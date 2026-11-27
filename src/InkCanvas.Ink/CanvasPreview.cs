using InkCanvas.Core.Ink;

namespace InkCanvas.Ink;

public sealed partial class InkController
{
    private object _previewOwner;
    public IReadOnlyList<InkStroke> ExtensionPreview { get; private set; } = Array.Empty<InkStroke>();
    public void SetExtensionPreview(object owner, IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _previewOwner = owner; ExtensionPreview = strokes ?? Array.Empty<InkStroke>(); Invalidated?.Invoke();
    }
    public void ClearExtensionPreview(object owner)
    {
        if (!ReferenceEquals(owner, _previewOwner)) return;
        _previewOwner = null; ExtensionPreview = Array.Empty<InkStroke>(); Invalidated?.Invoke();
    }
    private IEnumerable<InkStroke> ShapeMembers(InkStroke stroke) => stroke.ShapeSource is { } source
        ? Document.Strokes.Where(item => item.InkLayerIndex == stroke.InkLayerIndex && item.ShapeSource?.GroupId == source.GroupId) : new[] { stroke };
    private void ExpandShapeSelection()
    {
        var groups = Selection.Where(stroke => stroke.ShapeSource is not null).Select(stroke => stroke.ShapeSource.GroupId).ToHashSet();
        if (groups.Count == 0) return;
        foreach (var stroke in Document.Strokes)
            if (InActiveInkLayer(stroke) && stroke.ShapeSource is { } source && groups.Contains(source.GroupId) && !Selection.Contains(stroke)) Selection.Add(stroke);
    }
}
