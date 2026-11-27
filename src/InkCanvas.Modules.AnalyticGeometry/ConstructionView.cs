using System.Numerics;
using System.Globalization;
using InkCanvas.AnalyticGeometry.Construction;
using InkCanvas.Controls.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>Native Win2D GeoGebra-style geometry view with crisp major/minor grid, axes arrowheads, and cached geometry.</summary>
public sealed class ConstructionView : UserControl, IDisposable
{
    private sealed record Drawing(ConstructionObject Object, GeometryValue Value, CanvasGeometry Lines,
        CanvasGeometry Fill, Vector2[] Points, Windows.Foundation.Rect? HitBounds);
    private readonly CanvasControl _canvas = new() { ClearColor = Microsoft.UI.Colors.White };
    private readonly Canvas _widgets = new();
    private readonly Dictionary<string, FrameworkElement> _controls = new();
    private readonly Dictionary<string, Grid> _controlHosts = new();
    private readonly Dictionary<string, Button> _controlGrips = new();
    private readonly CanvasTextFormat _font = new() { FontFamily = "Segoe UI", FontSize = 12 };
    private readonly CanvasTextFormat _labelFont = new() { FontFamily = "Cambria Math", FontSize = 14 };
    private readonly List<Drawing> _drawings = new();
    private readonly Dictionary<string, Queue<GeoVector>> _traces = new();
    private ConstructionSnapshot _state = new();
    private ConstructionEvaluation _evaluation;
    private GeometryProjection _projection;
    private string _dragging;
    private uint _dragPointerId;
    private bool _pointDragged;
    private Vector2 _pressed;
    private GeoVector _dragOrigin;
    private GeoVector _pendingPoint;
    private string _pendingPointLabel;
    private string _draggingPath;
    private bool _draggingInRegion;
    private bool _pointMoveQueued;
    private string _dragControlLabel;
    private Vector2 _dragControlOffset;
    private uint _dragControlPointer;
    private string _hoveredLabel;
    private readonly HashSet<uint> _pointers = new();
    private bool _multiplePointers;
    private bool _dirty = true, _disposed;
    private string _cameraSignature;
    public string SelectedLabel { get; set; }
    public bool ConstructionToolActive { get; set; }
    internal sealed record ObjectPick(string Label, GeometryValue Value, int Output = 0, bool Inside = false)
    {
        public string Reference => Output == 0 ? Label : $"ConstructionOutput({Label},{Output})";
    }

    internal Func<ObjectPick, bool> AcceptToolObject { get; set; }
    private readonly HashSet<string> _toolHighlights = new(StringComparer.Ordinal);
    private GeoVector[] _toolMarkers = [];

    internal void ShowToolSelection(IEnumerable<string> highlights, GeoVector[] markers)
    {
        _toolHighlights.Clear();
        _toolHighlights.UnionWith(highlights);
        _toolMarkers = markers;
        _canvas.Invalidate();
    }
    public bool MoveAlongZAxis { get; set; }
    internal event Action<GeoVector, ObjectPick> Clicked;
    public event Action<string, GeoVector> PointMoved;
    public event Action<ConstructionSnapshot> ViewChanged;
    public event Action GestureStarted;
    public event Action GestureEnded;
    public event Action<string, string> ControlChanged;
    public event Action<string> ControlClicked;
    public event Action<string> SelectionRequested;
    public event Action<string, string> ObjectEventTriggered;
    public event Action<string, string, IReadOnlyDictionary<string, double>> ClientEventTriggered;
    public bool TrackClientPointer { get; set; }

    public ConstructionView()
    {
        Content = new Grid { Children = { _canvas, _widgets } };
        _canvas.Draw += Draw;
        SizeChanged += (_, _) => InvalidateGeometry();
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
        ManipulationStarted += (_, _) => GestureStarted?.Invoke();
        ManipulationDelta += (_, args) =>
        {
            if (_dragging is not null || ActualWidth < 1 || ActualHeight < 1) return;
            double scale = Math.Clamp(args.Delta.Scale, .5, 2);
            var delta = args.Delta.Translation;
            if (_state.ThreeDimensional && Math.Abs(scale - 1) < .001)
                SetView(_state with { Yaw = _state.Yaw + delta.X * .008, Pitch = Math.Clamp(_state.Pitch + delta.Y * .008, -1.5, 1.5) });
            else
            {
                double units = Math.Max((_state.XMax - _state.XMin) / ActualWidth, (_state.YMax - _state.YMin) / ActualHeight);
                double dx = delta.X * units;
                double dy = delta.Y * units;
                SetView(Zoom(_state with { XMin = _state.XMin - dx, XMax = _state.XMax - dx, YMin = _state.YMin + dy, YMax = _state.YMax + dy },
                    scale, new((float)args.Position.X, (float)args.Position.Y)));
            }
            args.Handled = true;
        };
        ManipulationCompleted += (_, _) => GestureEnded?.Invoke();
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerExited += (_, _) => UpdateHovered(null);
        PointerCanceled += (_, args) => ClearPointer(args);
        PointerCaptureLost += (_, args) => ClearPointer(args);
        PointerWheelChanged += (_, args) =>
        {
            var point = args.GetCurrentPoint(this);
            GestureStarted?.Invoke();
            SetView(Zoom(_state, Math.Pow(1.15, point.Properties.MouseWheelDelta / 120d), new((float)point.Position.X, (float)point.Position.Y)));
            GestureEnded?.Invoke();
            args.Handled = true;
        };
    }

    public void ZoomIn()
    {
        if (_disposed || ActualWidth < 1 || ActualHeight < 1) return;
        GestureStarted?.Invoke();
        SetView(Zoom(_state, 1.25, new((float)(ActualWidth * 0.5), (float)(ActualHeight * 0.5))));
        GestureEnded?.Invoke();
    }

    public void ZoomOut()
    {
        if (_disposed || ActualWidth < 1 || ActualHeight < 1) return;
        GestureStarted?.Invoke();
        SetView(Zoom(_state, 0.8, new((float)(ActualWidth * 0.5), (float)(ActualHeight * 0.5))));
        GestureEnded?.Invoke();
    }

    public void ResetView()
    {
        if (_disposed) return;
        GestureStarted?.Invoke();
        SetView(_state with { XMin = -10, XMax = 10, YMin = -7.5, YMax = 7.5, Yaw = -.6, Pitch = .65 });
        GestureEnded?.Invoke();
    }

    public void SetConstruction(ConstructionSnapshot state, ConstructionEvaluation evaluation)
    {
        _state = state;
        _evaluation = evaluation;
        UpdateWidgets();
        foreach (var item in state.Objects.Where(item => item.Trace && evaluation.IsVisible(item)))
        {
            if (!evaluation.Values.TryGetValue(item.Label, out var value) || value.Kind != GeometryKind.Point || !value.Defined) continue;
            if (!_traces.TryGetValue(item.Label, out var queue)) _traces[item.Label] = queue = new();
            if (queue.Count == 0 || (queue.Last() - value.Origin).Length > 1e-5) queue.Enqueue(value.Origin);
            while (queue.Count > 2048) queue.Dequeue();
        }
        foreach (string label in _traces.Keys.Where(label => !state.Objects.Any(item => item.Label == label && item.Trace)).ToArray()) _traces.Remove(label);
        InvalidateGeometry();
    }

    private void UpdateWidgets()
    {
        int index = 0;
        var retained = new HashSet<string>();
        foreach (var item in _state.Objects.Where(_evaluation.IsVisible))
        {
            if (!_evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            string kind = item.ShowSlider && value.Kind == GeometryKind.Scalar
                && double.TryParse(item.Definition, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? "slider" : value.ControlKind;
            if (kind.Length == 0) continue;
            string key = item.Label + ":" + kind;
            retained.Add(key);
            if (!_controls.TryGetValue(key, out var element))
            {
                string label = item.Label;
                switch (kind)
                {
                    case "slider":
                        var slider = new PointerSlider { Header = label, MinHeight = 44, Width = 180 };
                        slider.ValueChanged += (_, _) =>
                        {
                            if (slider.Tag is not true) ControlChanged?.Invoke(label, slider.Value.ToString("G15", CultureInfo.InvariantCulture));
                        };
                        slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => GestureStarted?.Invoke()), true);
                        slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => GestureEnded?.Invoke()), true);
                        slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => GestureEnded?.Invoke()), true);
                        element = slider; break;
                    case "checkbox":
                        var checkbox = new CheckBox { Content = label, MinHeight = 44 };
                        checkbox.Click += (_, _) => ControlChanged?.Invoke(label, "Checkbox(" + (checkbox.IsChecked == true ? "1" : "0") + ")");
                        element = checkbox; break;
                    case "button":
                        var button = new Button { Content = value.Display, MinHeight = 44 };
                        button.Click += (_, _) => ControlClicked?.Invoke(label); element = button; break;
                    case "input":
                        var input = new TextBox { Header = value.ControlTarget, Text = value.Display, MinHeight = 44, Width = 170 };
                        input.LostFocus += (_, _) => ControlChanged?.Invoke(value.ControlTarget, input.Text);
                        input.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { ControlChanged?.Invoke(value.ControlTarget, input.Text); args.Handled = true; } };
                        element = input; break;
                    case "dropdown":
                        var dropdown = new ComboBox { MinHeight = 44, MinWidth = 140, ItemsSource = value.Options };
                        dropdown.SelectionChanged += (_, _) =>
                        {
                            if (dropdown.Tag is true || dropdown.SelectedIndex < 0) return;
                            if (_evaluation.Values.TryGetValue(label, out var current) && current.ControlKind == "dropdown")
                                ControlChanged?.Invoke(label, "Dropdown(" + current.ControlTarget + "," + (dropdown.SelectedIndex + 1) + ")");
                        };
                        dropdown.DropDownOpened += (_, _) => ClientEventTriggered?.Invoke(label, "OnDropdownOpened", null);
                        dropdown.DropDownClosed += (_, _) => ClientEventTriggered?.Invoke(label, "OnDropdownClosed",
                            new Dictionary<string, double> { ["index"] = dropdown.SelectedIndex });
                        element = dropdown; break;
                    default: continue;
                }
                element.PointerPressed += (_, args) => args.Handled = true;
                element.PointerReleased += (_, args) => args.Handled = true;
                element.ManipulationMode = ManipulationModes.System;
                var grip = new Button { Content = "⋮", Padding = new Thickness(0), MinWidth = 24, MinHeight = 36 };
                AutomationProperties.SetName(grip, "拖动控件位置");
                grip.AddHandler(PointerPressedEvent, new PointerEventHandler(ControlGripPressed), true);
                grip.AddHandler(PointerMovedEvent, new PointerEventHandler(ControlGripMoved), true);
                grip.AddHandler(PointerReleasedEvent, new PointerEventHandler(ControlGripReleased), true);
                grip.PointerCanceled += (_, args) => FinishControlGrip(args.Pointer.PointerId);
                grip.PointerCaptureLost += (_, args) => FinishControlGrip(args.Pointer.PointerId);
                var controlHost = new Grid { Width = 240, MinHeight = 44 };
                controlHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                controlHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                controlHost.Children.Add(grip);
                Grid.SetColumn(element, 1);
                controlHost.Children.Add(element);
                _controls[key] = element;
                _controlHosts[key] = controlHost;
                _controlGrips[key] = grip;
                _widgets.Children.Add(controlHost);
            }
            if (element is CheckBox check) check.IsChecked = value.Number != 0;
            if (element is Button action) action.Content = value.Display;
            if (element is PointerSlider numeric && !numeric.IsAdjusting)
            {
                numeric.Tag = true;
                numeric.Minimum = item.SliderMin;
                numeric.Maximum = item.SliderMax;
                numeric.StepFrequency = item.SliderStep;
                numeric.Value = Math.Clamp(value.Number, item.SliderMin, item.SliderMax);
                numeric.Tag = false;
            }
            if (element is TextBox box && box.FocusState == FocusState.Unfocused) box.Text = value.Display;
            if (element is ComboBox combo)
            {
                combo.Tag = true;
                combo.ItemsSource = value.Options;
                if (InkCanvas.AnalyticGeometry.Algebra.MathSyntax.Call(item.Definition, out _, out var args) && args.Length > 1
                    && int.TryParse(args[1], out int selected)) combo.SelectedIndex = selected - 1;
                else combo.SelectedIndex = 0;
                combo.Tag = false;
            }
            var host = _controlHosts[key];
            if (_dragControlLabel != item.Label)
            {
                Canvas.SetLeft(host, item.ControlPositionSet ? item.ControlX : 16);
                Canvas.SetTop(host, item.ControlPositionSet ? item.ControlY : 12 + index * 72);
            }
            index++;
        }
        foreach (string key in _controls.Keys.Where(key => !retained.Contains(key)).ToArray())
        {
            _widgets.Children.Remove(_controlHosts[key]);
            _controls.Remove(key);
            _controlHosts.Remove(key);
            _controlGrips.Remove(key);
        }
    }

    private void ControlGripPressed(object sender, PointerRoutedEventArgs args)
    {
        string key = _controlGrips.FirstOrDefault(pair => ReferenceEquals(pair.Value, sender)).Key;
        if (key is null || !_controlHosts.TryGetValue(key, out var host)) return;
        _dragControlLabel = key[..key.IndexOf(':')];
        _dragControlPointer = args.Pointer.PointerId;
        var point = args.GetCurrentPoint(_widgets).Position;
        _dragControlOffset = new((float)point.X - (float)Canvas.GetLeft(host), (float)point.Y - (float)Canvas.GetTop(host));
        ((Button)sender).CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void ControlGripMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragControlLabel is null || args.Pointer.PointerId != _dragControlPointer) return;
        string key = _controlHosts.Keys.FirstOrDefault(candidate => candidate.StartsWith(_dragControlLabel + ":", StringComparison.Ordinal));
        if (key is null || !_controlHosts.TryGetValue(key, out var host)) return;
        var point = args.GetCurrentPoint(_widgets).Position;
        Canvas.SetLeft(host, Math.Max(0, point.X - _dragControlOffset.X));
        Canvas.SetTop(host, Math.Max(0, point.Y - _dragControlOffset.Y));
        args.Handled = true;
    }

    private void ControlGripReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_dragControlLabel is null || args.Pointer.PointerId != _dragControlPointer) return;
        string key = _controlHosts.Keys.FirstOrDefault(candidate => candidate.StartsWith(_dragControlLabel + ":", StringComparison.Ordinal));
        string label = _dragControlLabel;
        FinishControlGrip(args.Pointer.PointerId);
        if (key is not null && _controlHosts.TryGetValue(key, out var host))
        {
            double x = Canvas.GetLeft(host), y = Canvas.GetTop(host);
            ControlChanged?.Invoke(label, $"SetControlPosition({x.ToString("G15", CultureInfo.InvariantCulture)},{y.ToString("G15", CultureInfo.InvariantCulture)})");
        }
        args.Handled = true;
    }

    private void FinishControlGrip(uint pointerId)
    {
        if (_dragControlLabel is null || pointerId != _dragControlPointer) return;
        _dragControlLabel = null;
        _dragControlPointer = 0;
    }

    public void InvalidateGeometry()
    {
        if (_disposed) return;
        _dirty = true;
        _projection = new(_state, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        _canvas.Invalidate();
    }

    private void SetView(ConstructionSnapshot state)
    {
        _state = state;
        InvalidateGeometry();
        ViewChanged?.Invoke(state);
    }

    private ConstructionSnapshot Zoom(ConstructionSnapshot state, double scale, Vector2 center)
    {
        double units = Math.Max((state.XMax - state.XMin) / Math.Max(1, ActualWidth), (state.YMax - state.YMin) / Math.Max(1, ActualHeight));
        double x = (state.XMin + state.XMax) / 2 + (center.X - ActualWidth / 2) * units;
        double y = (state.YMin + state.YMax) / 2 - (center.Y - ActualHeight / 2) * units;
        double span = (state.XMax - state.XMin) / scale;
        if (span is < 1e-4 or > 1e6) return state;
        return state with { XMin = x + (state.XMin - x) / scale, XMax = x + (state.XMax - x) / scale,
            YMin = y + (state.YMin - y) / scale, YMax = y + (state.YMax - y) / scale };
    }

    private void Pressed(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || _projection is null) return;
        var point = args.GetCurrentPoint(this);
        _pointers.Add(args.Pointer.PointerId);
        if (_pointers.Count > 1) { _multiplePointers = true; EndDrag(); return; }
        _pressed = new((float)point.Position.X, (float)point.Position.Y);
        if (TrackClientPointer)
            ClientEventTriggered?.Invoke("", "OnMouseDown", new Dictionary<string, double> { ["x"] = _pressed.X, ["y"] = _pressed.Y });
        string hit = ConstructionToolActive ? null : HitTest(_pressed);
        if (hit is not null)
        {
            if (SelectedLabel != hit) SelectionRequested?.Invoke(hit);
            SelectedLabel = hit;
            var item = _state.Objects.First(item => item.Label == hit);
            bool freePoint = ConstructionDocument.IsFreePoint(item);
            _draggingPath = ConstructionDocument.AttachedPath(item, _evaluation.Values[hit]);
            if (!item.Fixed && _evaluation.Values[hit].Kind == GeometryKind.Point
                && (freePoint || _draggingPath is not null))
            {
                _dragging = hit;
                _draggingInRegion = ConstructionDocument.IsRegionPoint(item);
                _dragPointerId = args.Pointer.PointerId;
                _pointDragged = false;
                _dragOrigin = _evaluation.Values[hit].Origin;
                ManipulationMode = ManipulationModes.None;
                CapturePointer(args.Pointer);
                args.Handled = true;
            }
        }
        _canvas.Invalidate();
    }

    private void Moved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragging is null)
        {
            var hover = args.GetCurrentPoint(this).Position;
            UpdateHovered(HitTest(new((float)hover.X, (float)hover.Y)));
            return;
        }
        if (args.Pointer.PointerId != _dragPointerId) return;
        var point = args.GetCurrentPoint(this).Position;
        var screen = new Vector2((float)point.X, (float)point.Y);
        if (!_pointDragged)
        {
            if (Vector2.DistanceSquared(screen, _pressed) < 9) return;
            _pointDragged = true;
            GestureStarted?.Invoke();
            ObjectEventTriggered?.Invoke(_dragging, "OnDragStart");
        }
        try
        {
            GeoVector position;
            if (_state.ThreeDimensional && MoveAlongZAxis)
            {
                var axis = _projection.Project(_dragOrigin + new GeoVector(0, 0, 1)) - _projection.Project(_dragOrigin);
                double dz = Vector2.Dot(screen - _pressed, axis) / Math.Max(.01f, axis.LengthSquared());
                position = _dragOrigin + new GeoVector(0, 0, dz);
            }
            else position = _projection.Unproject(screen, _dragOrigin.Z);
            QueuePointMove(_dragging, position);
        }
        catch (InvalidOperationException) { }
        args.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs args)
    {
        var position = args.GetCurrentPoint(this).Position;
        var point = new Vector2((float)position.X, (float)position.Y);
        uint pointerId = args.Pointer.PointerId;
        bool pointPress = _dragging is not null && pointerId == _dragPointerId;
        string clickedPoint = pointPress ? _dragging : null;
        bool click = pointPress ? !_pointDragged : _dragging is null && Vector2.DistanceSquared(point, _pressed) < 144;
        if (pointPress) EndDrag();
        if (!_multiplePointers && click && _projection is not null)
        {
            try
            {
                if (ConstructionToolActive) PickConstructionAt(point);
                else
                {
                    var hit = clickedPoint is null
                        ? PickObject(point)
                        : new ObjectPick(clickedPoint, _evaluation.Values[clickedPoint]);
                    Clicked?.Invoke(_projection.Unproject(point), hit);
                }
            }
            catch (InvalidOperationException) { }
        }
        _pointers.Remove(args.Pointer.PointerId);
        if (_pointers.Count == 0) _multiplePointers = false;
        ReleasePointerCapture(args.Pointer);
    }

    private void EndDrag()
    {
        if (_dragging is null) return;
        FlushPointMove();
        string label = _dragging;
        bool moved = _pointDragged;
        _dragging = null;
        _draggingPath = null;
        _draggingInRegion = false;
        _dragPointerId = 0;
        _pointDragged = false;
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
        if (moved)
        {
            GestureEnded?.Invoke();
            ObjectEventTriggered?.Invoke(label, "OnDragEnd");
        }
    }

    private void UpdateHovered(string label)
    {
        if (label == _hoveredLabel) return;
        if (_hoveredLabel is not null) ObjectEventTriggered?.Invoke(_hoveredLabel, "OnMouseOut");
        _hoveredLabel = label;
        if (label is not null) ObjectEventTriggered?.Invoke(label, "OnMouseOver");
    }

    private void FlushPointMoveOnFrame(object sender, object args) => FlushPointMove();

    private void QueuePointMove(string label, GeoVector point)
    {
        _pendingPointLabel = label;
        _pendingPoint = point;
        if (_pointMoveQueued) return;
        _pointMoveQueued = true;
        CompositionTarget.Rendering += FlushPointMoveOnFrame;
    }

    private void FlushPointMove()
    {
        if (!_pointMoveQueued) return;
        CompositionTarget.Rendering -= FlushPointMoveOnFrame;
        _pointMoveQueued = false;
        if (_pendingPointLabel is not null && _dragging == _pendingPointLabel)
        {
            if (_draggingPath is null) PointMoved?.Invoke(_pendingPointLabel, _pendingPoint);
            else if (_evaluation.Values.GetValueOrDefault(_pendingPointLabel)?.AttachmentTarget is { } path)
            {
                if (_draggingInRegion)
                {
                    if (GeometryPath.TryGetRegionDefinition(_draggingPath, path, _pendingPoint, out string definition))
                        ControlChanged?.Invoke(_pendingPointLabel, definition);
                }
                else if (GeometryPath.TryGetParameter(path, _pendingPoint, out double parameter))
                    ControlChanged?.Invoke(_pendingPointLabel, $"PointOnObject({_draggingPath},{parameter.ToString("G15", CultureInfo.InvariantCulture)})");
            }
        }
        _pendingPointLabel = null;
    }

    public bool IsDraggingPoint => _dragging is not null;


    private void ClearPointer(PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerId == _dragPointerId) EndDrag();
        _pointers.Remove(args.Pointer.PointerId);
        if (_pointers.Count == 0) _multiplePointers = false;
    }

    private string HitTest(Vector2 point) => PickObject(point)?.Label;

    internal void PickConstructionAt(Vector2 point)
    {
        if (_evaluation is null || _projection is null) return;
        var hit = PickObject(point, AcceptToolObject);
        if (hit is null && AcceptToolObject is not null)
            hit = PickRegion(point, AcceptToolObject);
        Clicked?.Invoke(_projection.Unproject(point), hit);
    }

    internal Vector2 Project(GeoVector position) => _projection.Project(position);

    internal ObjectPick PickObject(Vector2 point, Func<ObjectPick, bool> accepts = null, bool includePoints = true)
    {
        if (_evaluation is null || _projection is null) return null;
        bool cached = !_dirty && !_state.Anaglyph;
        if (includePoints)
        {
            var hit = Find(points: true);
            if (hit is not null) return hit;
        }
        return Find(points: false);

        ObjectPick Find(bool points)
        {
            if (cached)
            {
                for (int i = _drawings.Count - 1; i >= 0; i--)
                {
                    var drawing = _drawings[i];
                    var hit = Match(drawing.Object.Label, drawing.Value, points, drawing);
                    if (hit is not null) return hit;
                }
                return null;
            }
            for (int i = _state.Objects.Count - 1; i >= 0; i--)
            {
                var item = _state.Objects[i];
                if (!_evaluation.IsVisible(item) || !_evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined)
                    continue;
                var hit = Match(item.Label, value, points, null);
                if (hit is not null) return hit;
            }
            return null;
        }

        ObjectPick Match(string label, GeometryValue value, bool points, Drawing drawing)
        {
            // Reject most objects by their cached bounds before inspecting any list outputs.
            if (drawing is not null && !points && (drawing.HitBounds is not { } bounds
                || !bounds.Contains(new Windows.Foundation.Point(point.X, point.Y)))) return null;
            if (value.Kind == GeometryKind.List && accepts is not null)
            {
                for (int i = value.Items.Count - 1; i >= 0; i--)
                {
                    var result = value.Items[i];
                    var pick = new ObjectPick(label, result, i + 1);
                    if (result.Defined && accepts(pick) && IsHit(result, points, null)) return pick;
                }
                return null;
            }
            if (points && value.Kind is not (GeometryKind.Point or GeometryKind.List)) return null;
            if (accepts is null) return IsHit(value, points, drawing) ? new ObjectPick(label, value) : null;
            var candidate = new ObjectPick(label, value);
            return accepts(candidate) && IsHit(value, points, drawing) ? candidate : null;
        }

        bool IsHit(GeometryValue value, bool points, Drawing drawing)
        {
            if (points)
            {
                if (value.Points.Count == 0 || value.Kind is not (GeometryKind.Point or GeometryKind.List)) return false;
                return drawing is not null
                    ? drawing.Points.Any(p => Vector2.DistanceSquared(p, point) < 256)
                    : value.Points.Any(p => Vector2.DistanceSquared(_projection.Project(p), point) < 256);
            }
            if (drawing is not null) return drawing.Lines.StrokeContainsPoint(point, 20);
            foreach (var path in _projection.Paths(value))
            for (int i = 1; i < path.Length; i++)
            {
                var direction = path[i] - path[i - 1];
                float t = Math.Clamp(Vector2.Dot(point - path[i - 1], direction) / Math.Max(.001f, direction.LengthSquared()), 0, 1);
                if (Vector2.DistanceSquared(path[i - 1] + t * direction, point) < 100) return true;
            }
            return false;
        }
    }

    private ObjectPick PickRegion(Vector2 point, Func<ObjectPick, bool> accepts)
    {
        for (int i = _state.Objects.Count - 1; i >= 0; i--)
        {
            var item = _state.Objects[i];
            if (!_evaluation.IsVisible(item) || !_evaluation.Values.TryGetValue(item.Label, out var region)
                || !region.Defined || !GeometryPath.IsAttachableRegion(region)) continue;
            var candidate = new ObjectPick(item.Label, region, Inside: true);
            if (!accepts(candidate)) continue;
            foreach (var boundary in region.Paths)
            {
                if (boundary.Length < 3) continue;
                bool inside = false;
                var a = _projection.Project(boundary[^1]);
                foreach (var vertex in boundary)
                {
                    var b = _projection.Project(vertex);
                    if ((a.Y > point.Y) != (b.Y > point.Y)
                        && point.X < a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                        inside = !inside;
                    a = b;
                }
                if (inside) return candidate;
            }
        }
        return null;
    }

    private void Rebuild(CanvasControl sender)
    {
        string camera = $"{ActualWidth},{ActualHeight},{_state.XMin},{_state.XMax},{_state.YMin},{_state.YMax},{_state.Yaw},{_state.Pitch},{_state.ThreeDimensional},{_state.Perspective},{_state.Oblique},{_state.Anaglyph}";
        bool sameCamera = camera == _cameraSignature;
        _cameraSignature = camera;
        bool stereo = _state.ThreeDimensional && _state.Anaglyph;
        RebuildDrawings(sender, _drawings, stereo ? new GeometryProjection(_state, ActualWidth, ActualHeight, -1) : _projection, sameCamera);
        RebuildDrawings(sender, _rightEye, stereo ? new GeometryProjection(_state, ActualWidth, ActualHeight, 1) : null, sameCamera);
        _dirty = false;
    }

    private readonly List<Drawing> _rightEye = new();

    private void RebuildDrawings(CanvasControl sender, List<Drawing> drawings, GeometryProjection projection, bool sameCamera)
    {
        var previous = drawings.ToDictionary(drawing => drawing.Object.Label);
        drawings.Clear();
        if (_evaluation is null || projection is null)
        {
            foreach (var drawing in previous.Values) { drawing.Lines?.Dispose(); drawing.Fill?.Dispose(); }
            return;
        }
        foreach (var item in _state.Objects.Where(_evaluation.IsVisible))
        {
            if (!_evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            if (sameCamera && previous.TryGetValue(item.Label, out var cached) && ReferenceEquals(cached.Value, value))
            {
                drawings.Add(cached with { Object = item }); previous.Remove(item.Label); continue;
            }
            if (value.Paths.Count == 0 && value.Faces.Count == 0 && value.Kind is not (GeometryKind.Line or GeometryKind.Ray))
            {
                var points = value.Points.Select(projection.Project).Where(Valid).ToArray();
                if (points.Length > 0 || value.TextAnchor is not null) drawings.Add(new(item, value, null, null, points, null));
                continue;
            }

            using var lineBuilder = new CanvasPathBuilder(sender);
            bool hasLines = false;
            var paths = projection.Paths(value).ToArray();
            foreach (var path in paths)
            {
                lineBuilder.BeginFigure(path[0]);
                foreach (var point in path.Skip(1)) lineBuilder.AddLine(point);
                lineBuilder.EndFigure(CanvasFigureLoop.Open);
                hasLines = true;
            }
            using var fillBuilder = new CanvasPathBuilder(sender);
            fillBuilder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
            bool hasFill = false;
            foreach (var face in value.Faces)
            {
                var a = projection.Project(face.A);
                var b = projection.Project(face.B);
                var c = projection.Project(face.C);
                if (!Valid(a) || !Valid(b) || !Valid(c)) continue;
                if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) (b, c) = (c, b);
                fillBuilder.BeginFigure(a); fillBuilder.AddLine(b); fillBuilder.AddLine(c);
                fillBuilder.EndFigure(CanvasFigureLoop.Closed);
                hasFill = true;
            }
            if (value.Kind == GeometryKind.Region && !hasFill)
            foreach (var path in paths)
            {
                fillBuilder.BeginFigure(path[0]);
                foreach (var point in path.Skip(1)) fillBuilder.AddLine(point);
                fillBuilder.EndFigure(CanvasFigureLoop.Closed);
                hasFill = true;
            }
            var lines = hasLines ? CanvasGeometry.CreatePath(lineBuilder) : null;
            var hitBounds = lines?.ComputeStrokeBounds(20);
            drawings.Add(new(item, value, lines, hasFill ? CanvasGeometry.CreatePath(fillBuilder) : null,
                value.Points.Select(projection.Project).Where(Valid).ToArray(), hitBounds));
        }
        foreach (var drawing in previous.Values) { drawing.Lines?.Dispose(); drawing.Fill?.Dispose(); }
    }

    private static bool Valid(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y)
        && Math.Abs(point.X) < 1e7 && Math.Abs(point.Y) < 1e7;

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || ActualWidth < 2 || ActualHeight < 2) return;
        if (_dirty) Rebuild(sender);
        var ds = args.DrawingSession;
        DrawGrid(ds);
        if (_state.ThreeDimensional && _state.Anaglyph)
        {
            ds.Blend = CanvasBlend.Min;
            try
            {
                DrawEye(ds, _drawings, Color.FromArgb(255, 255, 0, 0));
                DrawEye(ds, _rightEye, Color.FromArgb(255, 0, _state.AnaglyphBlue ? (byte)0 : (byte)255, 255));
            }
            finally { ds.Blend = CanvasBlend.SourceOver; }
            foreach (var drawing in _drawings)
                if (drawing.Value.TextAnchor is { } textAnchor)
                    ds.DrawText(drawing.Value.Display, _projection.Project(textAnchor), Microsoft.UI.Colors.Black, _labelFont);
                else if (drawing.Object.ShowLabel && drawing.Value.Points.Count > 0)
                    ds.DrawText(drawing.Object.Label, _projection.Project(drawing.Value.Points[0]) + new Vector2(9, -22),
                        Microsoft.UI.Colors.Black, _labelFont);
            return;
        }
        foreach (var drawing in _drawings)
        {
            uint packed = drawing.Object.Color;
            var color = Color.FromArgb(255, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
            if (drawing.Value.TextAnchor is { } textAnchor)
                ds.DrawText(drawing.Value.Display, _projection.Project(textAnchor), color, _labelFont);
            if (drawing.Fill is not null)
                ds.FillGeometry(drawing.Fill, Color.FromArgb((byte)(255 * Math.Clamp(drawing.Object.Opacity, 0, 1)), color.R, color.G, color.B));
            if (_traces.TryGetValue(drawing.Object.Label, out var trace))
                foreach (var point in trace) ds.FillCircle(_projection.Project(point), 1.5f, color);
            if (drawing.Lines is not null)
            {
                if (drawing.Object.Label == SelectedLabel || _toolHighlights.Contains(drawing.Object.Label))
                    ds.DrawGeometry(drawing.Lines, Color.FromArgb(64, color.R, color.G, color.B), (float)drawing.Object.LineWidth + 7);
                ds.DrawGeometry(drawing.Lines, color, (float)drawing.Object.LineWidth);
            }
            foreach (var point in drawing.Points)
            {
                if (drawing.Object.Label == SelectedLabel || _toolHighlights.Contains(drawing.Object.Label))
                    ds.FillCircle(point, 11, Color.FromArgb(56, color.R, color.G, color.B));
                ds.FillCircle(point, 4.8f, color);
                ds.DrawCircle(point, 4.8f, Microsoft.UI.Colors.White, 1.4f);
            }
            if (drawing.Object.ShowLabel && drawing.Points.Length > 0)
                ds.DrawText(drawing.Object.Label, drawing.Points[0] + new Vector2(9, -22), color, _labelFont);
        }
        foreach (var marker in _toolMarkers)
        {
            var center = _projection.Project(marker);
            if (!Valid(center)) continue;
            ds.FillCircle(center, 6, Color.FromArgb(255, 34, 105, 222));
            ds.DrawCircle(center, 6, Microsoft.UI.Colors.White, 1.6f);
        }
    }

    private void DrawEye(CanvasDrawingSession ds, List<Drawing> drawings, Color color)
    {
        foreach (var drawing in drawings)
        {
            if (drawing.Fill is not null)
            {
                double opacity = drawing.Object.Opacity;
                byte tint = (byte)Math.Round(255 * (1 - opacity));
                ds.FillGeometry(drawing.Fill, Color.FromArgb(255, color.R == 0 ? tint : color.R,
                    color.G == 0 ? tint : color.G, color.B == 0 ? tint : color.B));
            }
            if (drawing.Lines is not null)
                ds.DrawGeometry(drawing.Lines, color, (float)drawing.Object.LineWidth + (drawing.Object.Label == SelectedLabel ? 2 : 0));
            foreach (var point in drawing.Points)
                ds.FillCircle(point, drawing.Object.Label == SelectedLabel ? 6.8f : 4.8f, color);
        }
    }

    private void DrawGrid(CanvasDrawingSession ds)
    {
        if (_projection is null) return;
        var majorGrid = Color.FromArgb(255, 224, 229, 237);
        var minorGrid = Color.FromArgb(255, 241, 244, 249);
        var axisColor = Color.FromArgb(255, 71, 85, 105);
        var labelColor = Color.FromArgb(255, 100, 116, 139);
        float w = (float)ActualWidth;
        float h = (float)ActualHeight;

        if (_state.ThreeDimensional)
        {
            if (_state.Grid)
            for (int i = -10; i <= 10; i++)
            {
                var c = i % 5 == 0 ? majorGrid : minorGrid;
                ds.DrawLine(_projection.Project(new(i, -10)), _projection.Project(new(i, 10)), c);
                ds.DrawLine(_projection.Project(new(-10, i)), _projection.Project(new(10, i)), c);
            }
            if (_state.Axes)
            {
                var origin = _projection.Project(default);
                ds.DrawLine(origin, _projection.Project(new(8, 0, 0)), Microsoft.UI.Colors.IndianRed, 1.8f);
                ds.DrawLine(origin, _projection.Project(new(0, 8, 0)), Microsoft.UI.Colors.SeaGreen, 1.8f);
                ds.DrawLine(origin, _projection.Project(new(0, 0, 8)), Microsoft.UI.Colors.RoyalBlue, 1.8f);
            }
            return;
        }

        double step = Math.Pow(10, Math.Floor(Math.Log10((_state.XMax - _state.XMin) / 8)));
        while ((_state.XMax - _state.XMin) / step > 18) step *= 2;
        var origin2D = _projection.Project(default);

        if (_state.PolarGrid && _state.Grid)
        {
            float scale = Vector2.Distance(origin2D, _projection.Project(new(1, 0)));
            double limit = Math.Max(Math.Abs(_state.XMin), Math.Abs(_state.XMax)) + Math.Max(Math.Abs(_state.YMin), Math.Abs(_state.YMax));
            for (double radius = step; radius < limit; radius += step) ds.DrawCircle(origin2D, (float)radius * scale, majorGrid);
            for (int i = 0; i < 24; i++)
            { double angle = i * Math.Tau / 24; ds.DrawLine(origin2D, _projection.Project(new(limit * Math.Cos(angle), limit * Math.Sin(angle))), i % 2 == 0 ? majorGrid : minorGrid); }
        }
        else if (_state.Grid)
        {
            // Minor gridlines (5 subdivisions, GeoGebra-style)
            double minorStep = step / 5.0;
            for (double x = Math.Ceiling(_state.XMin / minorStep) * minorStep; x < _state.XMax; x += minorStep)
            {
                float px = _projection.Project(new(x, 0)).X;
                ds.DrawLine(px, 0, px, h, minorGrid, 0.8f);
            }
            for (double y = Math.Ceiling(_state.YMin / minorStep) * minorStep; y < _state.YMax; y += minorStep)
            {
                float py = _projection.Project(new(0, y)).Y;
                ds.DrawLine(0, py, w, py, minorGrid, 0.8f);
            }
            // Major gridlines
            for (double x = Math.Ceiling(_state.XMin / step) * step; x < _state.XMax; x += step)
            {
                float px = _projection.Project(new(x, 0)).X;
                ds.DrawLine(px, 0, px, h, majorGrid, 1.0f);
            }
            for (double y = Math.Ceiling(_state.YMin / step) * step; y < _state.YMax; y += step)
            {
                float py = _projection.Project(new(0, y)).Y;
                ds.DrawLine(0, py, w, py, majorGrid, 1.0f);
            }
        }

        if (_state.Axes)
        {
            float axisY = Math.Clamp(origin2D.Y, 16, h - 24);
            float axisX = Math.Clamp(origin2D.X, 24, w - 16);

            // Axes lines
            if (origin2D.X >= 0 && origin2D.X <= w)
            {
                ds.DrawLine(origin2D.X, 6, origin2D.X, h, axisColor, 1.5f);
                // Y-axis arrowhead
                ds.DrawLine(origin2D.X, 4, origin2D.X - 5, 13, axisColor, 1.5f);
                ds.DrawLine(origin2D.X, 4, origin2D.X + 5, 13, axisColor, 1.5f);
            }
            if (origin2D.Y >= 0 && origin2D.Y <= h)
            {
                ds.DrawLine(0, origin2D.Y, w - 6, origin2D.Y, axisColor, 1.5f);
                // X-axis arrowhead
                ds.DrawLine(w - 4, origin2D.Y, w - 13, origin2D.Y - 5, axisColor, 1.5f);
                ds.DrawLine(w - 4, origin2D.Y, w - 13, origin2D.Y + 5, axisColor, 1.5f);
            }

            // Tick marks and GeoGebra-style numbers right along the axes
            for (double x = Math.Ceiling(_state.XMin / step) * step; x < _state.XMax; x += step)
            {
                if (Math.Abs(x) < step * 0.05) continue;
                float px = _projection.Project(new(x, 0)).X;
                if (px < 12 || px > w - 24) continue;
                if (origin2D.Y >= 0 && origin2D.Y <= h)
                    ds.DrawLine(px, origin2D.Y - 3, px, origin2D.Y + 3, axisColor, 1.2f);
                ds.DrawText(x.ToString("G4"), px - 6, axisY + 5, labelColor, _font);
            }
            for (double y = Math.Ceiling(_state.YMin / step) * step; y < _state.YMax; y += step)
            {
                if (Math.Abs(y) < step * 0.05) continue;
                float py = _projection.Project(new(0, y)).Y;
                if (py < 18 || py > h - 18) continue;
                if (origin2D.X >= 0 && origin2D.X <= w)
                    ds.DrawLine(origin2D.X - 3, py, origin2D.X + 3, py, axisColor, 1.2f);
                ds.DrawText(y.ToString("G4"), axisX + 6, py - 8, labelColor, _font);
            }
            if (origin2D.X >= 10 && origin2D.X <= w - 20 && origin2D.Y >= 10 && origin2D.Y <= h - 22)
                ds.DrawText("0", origin2D.X - 13, origin2D.Y + 4, labelColor, _font);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= FlushPointMoveOnFrame;
        _pointMoveQueued = false;
        foreach (var drawing in _drawings.Concat(_rightEye)) { drawing.Lines?.Dispose(); drawing.Fill?.Dispose(); }
        _rightEye.Clear();
        _drawings.Clear(); _traces.Clear(); _controls.Clear(); _controlHosts.Clear(); _controlGrips.Clear(); _font.Dispose(); _labelFont.Dispose();
        _canvas.Draw -= Draw; _canvas.RemoveFromVisualTree(); Content = null;
    }
}
