using InkCanvas.Core.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace InkCanvas.Controls.Controls;

/// <summary>A sample generated with the same width, taper and nib geometry as canvas ink.</summary>
public sealed class PenStrokePreview : UserControl
{
    private readonly Canvas _surface = new() { Height = 80, IsHitTestVisible = false };
    private readonly Path _checks = new();
    private readonly List<Path> _ink = new();
    private StrokeAttributes _attributes = new();
    private double _previewHeight = 80;
    private bool _showChecks = true;
    private readonly record struct PreviewKey(double Width, double Height, float InkWidth, TaperKind Taper,
        PenNib Nib, float Rotation, bool Highlighter, InkWashBrushOptions InkWash);
    private PreviewKey? _strokeKey, _geometryKey;
    private InkStroke _stroke;
    private bool _renderPending;
    private bool _building;
    public long GeometryBuildCount { get; private set; }
    private double _checkWidth, _checkHeight;
    public double PreviewHeight
    {
        get => _previewHeight;
        set
        {
            if (_previewHeight == value) return;
            _previewHeight = value; _surface.Height = value; RequestRender();
        }
    }
    public bool ShowChecks
    {
        get => _showChecks;
        set
        {
            _showChecks = value;
            _checks.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value) _surface.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }
    public InkStroke Stroke { get { EnsureStroke(); return _stroke; } }

    public PenStrokePreview()
    {
        _surface.Children.Add(_checks);
        Content = new Border { CornerRadius = new CornerRadius(5), Child = _surface };
        AutomationProperties.SetName(this, "笔触预览");
        SizeChanged += (_, _) => RequestRender();
        Loaded += (_, _) => RequestRender();
        Unloaded += (_, _) => { CompositionTarget.Rendering -= OnRendering; _renderPending = false; };
        ApplyTheme(1);
    }

    public void Update(StrokeAttributes attributes, bool highlighter)
    {
        _attributes = attributes.Clone();
        _attributes.IsHighlighter = highlighter;
        RequestRender();
    }

    public void ApplyTheme(int theme)
    {
        bool dark = Theming.PopupBrushes.IsDark(theme);
        _surface.Background = new SolidColorBrush(!_showChecks ? Microsoft.UI.Colors.Transparent
            : dark ? Windows.UI.Color.FromArgb(255, 43, 51, 63) : Windows.UI.Color.FromArgb(255, 231, 235, 242));
        _checks.Fill = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 59, 69, 83) : Windows.UI.Color.FromArgb(255, 210, 217, 228));
    }

    private void RequestRender()
    {
        if (!IsLoaded || _renderPending) return;
        _renderPending = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object sender, object args)
    {
        CompositionTarget.Rendering -= OnRendering;
        _renderPending = false;
        if (IsLoaded && !_building) _ = RenderAsync();
    }

    private PreviewKey CurrentKey() => new(ActualWidth > 0 ? ActualWidth : 200, _previewHeight, _attributes.BaseWidth,
        _attributes.EffectiveTaper, _attributes.Nib, _attributes.NibRotation, _attributes.IsHighlighter, _attributes.InkWash);

    private void EnsureStroke()
    {
        var key = CurrentKey();
        double width = key.Width;
        float height = (float)key.Height;
        if (_strokeKey == key)
        {
            _stroke.Attributes.Color = _attributes.Color;
            _stroke.Attributes.HighlighterAlpha = _attributes.HighlighterAlpha;
            return;
        }
        _strokeKey = key;

        float inset = MathF.Max(8, _attributes.BaseWidth / 2 + 4);
        float amplitude = Math.Clamp(height / 2 - _attributes.BaseWidth / 2 - 8, 0, 12);
        _stroke = new InkStroke(_attributes.Clone());
        _stroke.Attributes.InkWashSeed = 137;
        var previous = System.Numerics.Vector2.Zero;
        float time = 0;
        for (int i = 0; i <= 40; i++)
        {
            float t = i / 40f;
            var point = new System.Numerics.Vector2(inset + ((float)width - 2 * inset) * t,
                height / 2 + MathF.Sin(t * MathF.PI * 2) * amplitude);
            if (i > 0) time += System.Numerics.Vector2.Distance(previous, point) / (.08f + 2.5f * t * t);
            _stroke.AddPoint(point.X, point.Y, .5f, time);
            previous = point;
        }
        _stroke.Complete();
    }

    private async Task RenderAsync()
    {
        EnsureStroke();
        var key = _strokeKey.Value;
        bool geometryChanged = _geometryKey != key;
        System.Numerics.Vector2[][][] contours = null;
        if (geometryChanged)
        {
            _building = true;
            try
            {
                var snapshot = _stroke.Clone();
                GeometryBuildCount++;
                contours = await Task.Run(() =>
                {
                    if (InkBrush.UsesInkWash(snapshot)) return InkWashPreviewGeometry.Build(snapshot);
                    var layers = InkBrush.Layers(snapshot);
                    var result = new System.Numerics.Vector2[layers.Length][][];
                    for (int layer = 0; layer < layers.Length; layer++)
                        result[layer] = StrokeGeometry.BuildUnionContours(snapshot, layers[layer].WidthScale);
                    return result;
                });
            }
            catch (Exception error)
            {
                Core.Diagnostics.FileLog.WriteLogToFile("笔触预览生成失败 " + error.Message);
                return;
            }
            finally { _building = false; }
            if (!IsLoaded || CurrentKey() != key)
            {
                RequestRender();
                return;
            }
            EnsureStroke();
        }
        if (_checkWidth != key.Width || _checkHeight != key.Height)
        {
            _checkWidth = key.Width; _checkHeight = key.Height;
            _surface.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, key.Width, key.Height) };
            var checks = new GeometryGroup();
            for (int y = 0; y < key.Height; y += 12)
            for (int x = y % 24; x < key.Width; x += 24)
                checks.Children.Add(new RectangleGeometry { Rect = new Windows.Foundation.Rect(x, y, 12, 12) });
            _checks.Data = checks;
        }
        var layers = InkBrush.Layers(_stroke);
        while (_ink.Count < layers.Length)
        {
            var path = new Path();
            _ink.Add(path); _surface.Children.Add(path);
        }
        for (int layer = 0; layer < _ink.Count; layer++)
        {
            var path = _ink[layer];
            path.Visibility = layer < layers.Length ? Visibility.Visible : Visibility.Collapsed;
            if (layer >= layers.Length) continue;
            if (geometryChanged) path.Data = PenStyleIcon.CreateGeometry(contours[layer]);
            var color = layers[layer].Color(_stroke.Attributes.EffectiveColor);
            var fill = Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
            if (path.Fill is SolidColorBrush brush) brush.Color = fill;
            else path.Fill = new SolidColorBrush(fill);
        }
        _geometryKey = key;
    }
}
