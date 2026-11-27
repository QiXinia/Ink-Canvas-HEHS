using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;

namespace InkCanvas.Ink;

public sealed partial class InkController
{
    private InkSelectionMode _selectionMode;
    private bool _lassoToggle;
    public InkSelectionMode SelectionMode
    {
        get => _selectionMode;
        set
        {
            if (!Enum.IsDefined(value) || _selectionMode == value) return;
            FinishRegionSelection(canceled: true);
            _selectionMode = value;
        }
    }

    public bool IsLassoSelecting { get; private set; }
    public bool IsRegionSelecting => IsMarqueeSelecting || IsLassoSelecting;

    public void InvertSelection()
    {
        Tool = InkTool.Select;
        if (_selectionGesture) EndSelectionGesture();
        else if (_activeHandle != SelectionHandle.None) EndSelectionDrag();
        FinishRegionSelection(canceled: true);
        var selected = Selection.ToHashSet();
        var selectedGroups = Selection.Where(stroke => stroke.ShapeSource is not null)
            .Select(stroke => stroke.ShapeSource.GroupId).ToHashSet();
        var elements = SelectedElements.Select(element => element.Id).ToHashSet();
        ClearSelection();
        Selection.AddRange(Document.Strokes.Where(stroke => InActiveInkLayer(stroke) && !selected.Contains(stroke)
            && (stroke.ShapeSource is null || !selectedGroups.Contains(stroke.ShapeSource.GroupId))));
        if (ActiveInkLayer == 0 && GetSelectableElements is not null)
            SelectedElements.AddRange(GetSelectableElements().Where(element => element.IsPresent && !elements.Contains(element.Id)));
        ExpandShapeSelection();
        SelectionChanged?.Invoke(); Invalidated?.Invoke();
    }

    private void BeginRegionSelection(Vector2 viewPoint, bool additive, bool toggle = false)
    {
        _marqueeInitialStrokes = additive ? Selection.ToList() : new();
        _marqueeInitialElements = additive ? SelectedElements.ToList() : new();
        if (!additive) { Selection.Clear(); SelectedElements.Clear(); }
        _dragFromView = viewPoint;
        MarqueeBounds = Rect.Empty;
        IsLassoSelecting = SelectionMode == InkSelectionMode.Lasso;
        _lassoToggle = toggle;
        IsMarqueeSelecting = !IsLassoSelecting;
        if (IsLassoSelecting)
        {
            _lassoPath.Clear(); _lassoViewAtStart = ViewTransform;
            _lassoBounds = new Rect(viewPoint.X, viewPoint.Y, viewPoint.X, viewPoint.Y);
            AppendLassoPoint(viewPoint, final: true);
        }
        SelectionChanged?.Invoke(); Invalidated?.Invoke();
    }

    private void UpdateRegionSelection(Vector2 viewPoint)
    {
        if (IsLassoSelecting) AppendLassoPoint(viewPoint);
        else UpdateMarquee(viewPoint);
    }

    private void FinishRegionSelection(bool canceled = false)
    {
        if (!IsRegionSelecting) return;
        if (IsLassoSelecting || canceled)
        {
            Selection.Clear(); Selection.AddRange(_marqueeInitialStrokes);
            SelectedElements.Clear(); SelectedElements.AddRange(_marqueeInitialElements);
            if (IsLassoSelecting && !canceled)
            {
                var lasso = new LassoGeometry(_lassoPath);
                if (lasso.IsValid)
                {
                    var selected = Selection.ToHashSet();
                    foreach (var stroke in Document.Strokes)
                        if (InActiveInkLayer(stroke) && !selected.Contains(stroke) && lasso.IntersectsStroke(stroke, _lassoViewAtStart)) Selection.Add(stroke);
                    ExpandShapeSelection();
                    var selectedElements = SelectedElements.Select(element => element.Id).ToHashSet();
                    if (ActiveInkLayer == 0 && GetSelectableElements is not null)
                        foreach (var element in GetSelectableElements())
                            if (element.IsPresent && !selectedElements.Contains(element.Id) && lasso.IntersectsFrame(element.Frame, _lassoViewAtStart))
                                SelectedElements.Add(element);
                }
                else if (_lassoBounds.Width <= 4 && _lassoBounds.Height <= 4 && Matrix3x2.Invert(_lassoViewAtStart, out var inverse))
                {
                    var hit = HitTestDrawingObject(Vector2.Transform(_dragFromView, inverse));
                    if (hit?.Element is { } element)
                    {
                        int index = SelectedElements.FindIndex(selected => selected.Id == element.Id);
                        if (_lassoToggle && index >= 0) SelectedElements.RemoveAt(index);
                        else if (index < 0) SelectedElements.Add(element);
                    }
                    if (hit?.Stroke is { } stroke)
                    {
                        var members = ShapeMembers(stroke).ToHashSet();
                        if (_lassoToggle && Selection.Contains(stroke)) Selection.RemoveAll(members.Contains);
                        else foreach (var member in members) if (!Selection.Contains(member)) Selection.Add(member);
                    }
                }
            }
        }
        if (IsLassoSelecting) { _lassoPath.Clear(); _lassoBounds = Rect.Empty; }
        IsMarqueeSelecting = IsLassoSelecting = false;
        MarqueeBounds = Rect.Empty;
        _marqueeInitialStrokes = null; _marqueeInitialElements = null;
        SelectionChanged?.Invoke(); Invalidated?.Invoke();
    }
}
