using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using System.Numerics;

namespace InkCanvas.Ink;

public sealed partial class InkController
{
    private DrawingObject[] _drawingObjects = Array.Empty<DrawingObject>();
    private bool _drawingOrderDirty = true;
    public long LayerVersion { get; private set; }
    public event Action LayersChanged;
    private int _activeInkLayer;
    public int ActiveInkLayer => _activeInkLayer < Document.InkLayerCount ? _activeInkLayer : 0;
    public event Action ActiveInkLayerChanged;
    public bool HasInkAboveActiveLayer => Document.InkLayerCount > 1
        && DrawingObjects.Count > 0 && DrawingObjects[^1].InkLayerIndex > ActiveInkLayer;

    public bool TryCreateInkLayer()
    {
        int layer = Document.InkLayerCount;
        if (layer >= InkStroke.MaximumInkLayers || HasActiveInteraction || Playback is not null) return false;
        using (Document.DeferNotifications())
        {
            Document.RestoreInkLayers(layer + 1);
            return TrySetActiveInkLayer(layer);
        }
    }

    public bool TrySetActiveInkLayer(int layer)
    {
        if (layer < 0 || layer >= Document.InkLayerCount || HasActiveInteraction || Playback is not null) return false;
        if (_activeInkLayer == layer) return true;
        FinishDotAnimations();
        _activeInkLayer = layer;
        ClearSelection();
        ActiveInkLayerChanged?.Invoke();
        return true;
    }

    private bool InActiveInkLayer(InkStroke stroke) => stroke.InkLayerIndex == ActiveInkLayer;

    public void ClearCurrentInkLayer()
    {
        if (Document.InkLayerCount == 1) { Clear(); return; }
        if (HasActiveInteraction) return;
        EndPlayback();
        ClearSelection();
        var strokes = Document.Strokes.Select((stroke, index) => (Index: index, Stroke: stroke))
            .Where(item => InActiveInkLayer(item.Stroke)).ToArray();
        if (strokes.Length == 0) return;
        var targets = strokes.Select(item => item.Stroke).ToArray();
        Document.RemoveAll(targets);
        History.Push(new DelegateAction("clear ink layer", () => Document.RemoveAll(targets), () => Document.InsertRange(strokes)));
        Cleared?.Invoke();
        Invalidated?.Invoke();
    }

    public IReadOnlyList<DrawingObject> DrawingObjects
    {
        get
        {
            if (_drawingOrderDirty)
            {
                var elements = GetSelectableElements?.Invoke().Where(element => element.IsPresent)
                    .Select(element => new DrawingObject(null, element)) ?? Enumerable.Empty<DrawingObject>();
                _drawingObjects = elements.Concat(Document.Strokes.Select(stroke => new DrawingObject(stroke, null)))
                    .OrderBy(item => item.InkLayerIndex).ThenBy(item => item.SortOrder).ToArray();
                _drawingOrderDirty = false;
            }
            return _drawingObjects;
        }
    }

    public long NextLayerOrder()
    {
        var objects = DrawingObjects;
        if (objects.Count == 0) return 0;
        long highestOrder = objects.Max(item => item.LayerOrder ?? long.MaxValue);
        if (highestOrder >= long.MaxValue - objects.Count - 1)
        {
            for (int i = 0; i < objects.Count; i++)
            {
                var item = objects[i];
                item.LayerOrder = i;
            }
            return objects.Count;
        }
        return highestOrder + 1;
    }

    private bool IsSelectedObject(DrawingObject item) => item.Stroke is not null
        ? Selection.Contains(item.Stroke) : SelectedElements.Contains(item.Element);

    private DrawingObject? HitTestDrawingObject(Vector2 world)
    {
        float radius = 6f / MathF.Max(.1f, MathF.Sqrt(MathF.Abs(ViewTransform.GetDeterminant())));
        for (int i = DrawingObjects.Count - 1; i >= 0; i--)
        {
            var item = DrawingObjects[i];
            if (item.InkLayerIndex != ActiveInkLayer) continue;
            if (item.Element is not null && item.Element.Frame.Contains(world, Matrix3x2.Identity)) return item;
            if (item.Stroke is { } stroke && stroke.Bounds.Inflate(radius).Contains(world)
                && stroke.DistanceTo(world) <= radius + stroke.HitRadius) return item;
        }
        return null;
    }

    public bool CanArrangeSelection(LayerAction action)
    {
        if (!HasSelection || !Enum.IsDefined(action)) return false;
        var selectedStrokes = Selection.ToHashSet();
        var selectedElements = SelectedElements.ToHashSet();
        bool sawSelected = false, sawOther = false;
        bool forward = action is LayerAction.BringToFront or LayerAction.MoveForward;
        foreach (var item in DrawingObjects)
        {
            if (item.InkLayerIndex != ActiveInkLayer) continue;
            bool selected = item.Stroke is not null ? selectedStrokes.Contains(item.Stroke) : selectedElements.Contains(item.Element);
            if (forward && !selected && sawSelected || !forward && selected && sawOther) return true;
            sawSelected |= selected;
            sawOther |= !selected;
        }
        return false;
    }

    public void ArrangeSelection(LayerAction action)
    {
        if (_selectionGesture) EndSelectionGesture();
        else if (_activeHandle != InkCanvas.Core.Selection.SelectionHandle.None) EndSelectionDrag();
        if (!TryArrange(action, out var before, out var after)) return;
        var previous = before.Select(item => item.LayerOrder).ToArray();
        var next = new long?[before.Length];
        var indices = after.Select((item, index) => (item, index)).ToDictionary(pair => pair.item, pair => pair.index);
        bool forward = action is LayerAction.BringToFront or LayerAction.MoveForward;
        var groupedRanks = before.Where(item => item.Stroke?.ShapeSource is not null)
            .GroupBy(item => item.Stroke.ShapeSource.GroupId)
            .ToDictionary(group => group.Key, group => forward ? group.Min(item => indices[item]) : group.Max(item => indices[item]));
        for (int i = 0; i < before.Length; i++) next[i] = before[i].Stroke?.ShapeSource is { } source ? groupedRanks[source.GroupId] : indices[before[i]];
        Apply(next);
        History.Push(new DelegateAction("arrange selection", () => Apply(next), () => Apply(previous)));

        void Apply(long?[] orders)
        {
            var present = DrawingObjects.ToHashSet();
            for (int i = 0; i < before.Length; i++)
            {
                var item = before[i];
                if (present.Contains(item)) item.LayerOrder = orders[i];
            }
            _drawingOrderDirty = true;
            LayerVersion++;
            LayersChanged?.Invoke();
            SelectionChanged?.Invoke();
            Invalidated?.Invoke();
        }
    }

    private bool TryArrange(LayerAction action, out DrawingObject[] before, out DrawingObject[] after)
    {
        before = DrawingObjects.Where(item => item.InkLayerIndex == ActiveInkLayer).ToArray();
        after = before.ToArray();
        if (!HasSelection || !Enum.IsDefined(action)) return false;
        var selectedStrokes = Selection.ToHashSet();
        var selectedElements = SelectedElements.ToHashSet();
        var selected = before.Where(item => item.Stroke is not null ? selectedStrokes.Contains(item.Stroke)
            : selectedElements.Contains(item.Element)).ToHashSet();
        if (selected.Count == 0 || selected.Count == before.Length) return false;
        if (action is LayerAction.BringToFront or LayerAction.SendToBack)
        {
            var chosen = before.Where(selected.Contains);
            var other = before.Where(item => !selected.Contains(item));
            after = (action == LayerAction.BringToFront ? other.Concat(chosen) : chosen.Concat(other)).ToArray();
        }
        else if (action == LayerAction.MoveForward)
        {
            for (int i = after.Length - 2; i >= 0; i--)
                if (selected.Contains(after[i]) && !selected.Contains(after[i + 1]))
                    (after[i], after[i + 1]) = (after[i + 1], after[i]);
        }
        else
        {
            for (int i = 1; i < after.Length; i++)
                if (selected.Contains(after[i]) && !selected.Contains(after[i - 1]))
                    (after[i], after[i - 1]) = (after[i - 1], after[i]);
        }
        return !before.SequenceEqual(after);
    }

    private void SetCloneLayerOrder()
    {
        long next = NextLayerOrder();
        var copies = new Dictionary<DrawingObject, DrawingObject>();
        for (int i = 0; i < Selection.Count; i++) copies[new(Selection[i], null)] = new(_dragTargets[i], null);
        for (int i = 0; i < SelectedElements.Count; i++) copies[new(null, SelectedElements[i])] = new(null, _dragElements[i]);
        foreach (var item in DrawingObjects)
            if (copies.TryGetValue(item, out var copy)) copy.LayerOrder = next++;
    }
}
