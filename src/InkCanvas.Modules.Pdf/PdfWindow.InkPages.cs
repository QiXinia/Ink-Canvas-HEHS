using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Pdf;
using Microsoft.UI.Dispatching;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly UndoStack _inkHistory = new(200);
    private readonly HashSet<int> _dirtyInkPages = new();
    private readonly Dictionary<int, Dictionary<InkStroke, long>> _routedGeometry = new();
    private bool _routingInk, _inkRoutingQueued;
    private sealed record InkPageChange(int Page, int Index, InkStroke Original,
        IReadOnlyDictionary<int, IReadOnlyList<InkStroke>> Fragments);

    internal InkController PageController(int index)
    {
        if (_controllers.TryGetValue(index, out var existing)) return existing;
        var controller = new InkController(_inkHistory);
        Configure(controller);
        controller.Document.AddRange(_file.GetInk(index).Select(stroke => stroke.Clone()));
        _controllers[index] = controller;
        controller.Document.Changed += () => OnInkChanged(index);
        controller.LayersChanged += () => OnInkChanged(index);
        controller.StrokeCommitCompleted += () => NormalizeCrossPageInk(allowActiveStrokes: true);
        return controller;
    }

    private void QueueInkRouting()
    {
        if (_closed || _inkRoutingQueued || _routingInk) return;
        _inkRoutingQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _inkRoutingQueued = false;
            if (!_closed) NormalizeCrossPageInk();
        });
    }

    internal void NormalizeCrossPageInk(bool allowActiveStrokes = false)
    {
        if (_routingInk || !allowActiveStrokes && _controllers.Values.Any(controller => controller.HasActiveInteraction)) return;
        var changes = new List<InkPageChange>();
        foreach (int page in _dirtyInkPages.ToArray())
        {
            var strokes = PageController(page).Document.Strokes;
            if (!_routedGeometry.TryGetValue(page, out var versions)) _routedGeometry[page] = versions = new();
            for (int index = 0; index < strokes.Count; index++)
            {
                var stroke = strokes[index];
                if (versions.TryGetValue(stroke, out long version) && version == stroke.GeometryVersion) continue;
                var fragments = PdfInkSplitter.Split(stroke, page, _layout);
                if (fragments.Count == 1 && fragments.TryGetValue(page, out var original)
                    && original.Count == 1 && ReferenceEquals(original[0], stroke)) continue;
                changes.Add(new(page, index, stroke, fragments));
            }
            versions.Clear();
            foreach (var stroke in strokes) versions[stroke] = stroke.GeometryVersion;
        }
        _dirtyInkPages.Clear();
        if (changes.Count == 0) return;
        var state = _inkHistory.Capture();
        ApplyChanges();
        Controller.ClearSelection();
        if (state.Undo.Count > 0)
        {
            var previous = state.Undo[^1];
            var combined = new DelegateAction(previous.Name, () => { previous.Apply(); ApplyChanges(); },
                () => { RevertChanges(); previous.Revert(); });
            _inkHistory.Restore(new(state.Undo.Take(state.Undo.Count - 1).Append(combined).ToArray(), state.Redo));
        }

        void ApplyChanges()
        {
            _routingInk = true;
            try
            {
                foreach (var change in changes)
                {
                    _routedGeometry[change.Page].Remove(change.Original);
                    PageController(change.Page).Document.Remove(change.Original);
                    foreach (var part in change.Fragments)
                    {
                        var document = PageController(part.Key).Document;
                        using var batch = document.DeferNotifications();
                        document.RemoveAll(part.Value);
                        document.AddRange(part.Value);
                    }
                }
            }
            finally { _routingInk = false; }
        }
        void RevertChanges()
        {
            _routingInk = true;
            try
            {
                foreach (var change in changes.AsEnumerable().Reverse())
                {
                    foreach (var part in change.Fragments) PageController(part.Key).Document.RemoveAll(part.Value);
                    PageController(change.Page).Document.Insert(change.Index, change.Original);
                }
            }
            finally { _routingInk = false; }
        }
    }
}
