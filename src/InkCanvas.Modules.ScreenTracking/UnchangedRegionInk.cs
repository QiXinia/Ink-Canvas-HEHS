using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.ScreenTracking;
using InkCanvas.Ink.Rendering;

namespace InkCanvas.Features.Tools;

/// <summary>Synchronizes only ink wholly contained in uniquely identified, unchanged paragraphs.</summary>
internal sealed class UnchangedRegionInk
{
    private sealed class Shared
    {
        public Rect Bounds;
        public List<InkStroke> Ink = new();
        public readonly HashSet<Guid> Seen = new();
    }
    private readonly Dictionary<string, Shared> _regions = new();

    public IReadOnlyList<InkStroke> Merge(ScreenViewSnapshot before, ScreenViewSnapshot after,
        IReadOnlyList<InkStroke> source, Matrix3x2 sourceToScreen,
        IReadOnlyList<InkStroke> target, Matrix3x2 targetToScreen, out bool synchronized)
    {
        synchronized = false;
        if (!Matrix3x2.Invert(targetToScreen, out var targetInverse)) return target;
        Capture(before, source, sourceToScreen);
        var result = target.ToList();
        foreach (var region in UniqueRegions(after))
        {
            if (region.Bounds.IsEmpty || !_regions.TryGetValue(after.WindowHandle + ":" + region.Key, out var shared) || shared.Bounds.IsEmpty) continue;
            float xRatio = region.Bounds.Width / shared.Bounds.Width, yRatio = region.Bounds.Height / shared.Bounds.Height;
            if (Math.Abs(xRatio - yRatio) > .08f * Math.Max(xRatio, yRatio)) continue;
            var targetBounds = Expand(region.Bounds, 8);
            var backToSourceRegion = RegionTransform(region.Bounds, shared.Bounds);
            foreach (var extra in target.Where(stroke => !shared.Seen.Contains(stroke.Id)
                && Contains(targetBounds, CommittedInkLayer.TransformBounds(stroke.Bounds, targetToScreen))))
            {
                shared.Ink.Add(Copy(extra, targetToScreen * backToSourceRegion)); shared.Seen.Add(extra.Id);
            }
            if (shared.Seen.Count == 0) continue;
            result.RemoveAll(stroke => shared.Seen.Contains(stroke.Id));
            var toTarget = RegionTransform(shared.Bounds, region.Bounds) * targetInverse;
            result.AddRange(shared.Ink.Select(stroke => Copy(stroke, toTarget)));
            synchronized = true;
        }
        return result;
    }

    public void Capture(ScreenViewSnapshot view, IReadOnlyList<InkStroke> strokes, Matrix3x2 toScreen)
    {
        var handled = new HashSet<Guid>();
        // Capture even when the next page has no matching paragraph, preserving deletions.
        foreach (var region in UniqueRegions(view))
        {
            if (region.Bounds.IsEmpty) continue;
            string key = view.WindowHandle + ":" + region.Key;
            if (!_regions.TryGetValue(key, out var shared)) _regions[key] = shared = new Shared();
            shared.Ink = strokes.Where(stroke => !handled.Contains(stroke.Id)
                && Contains(Expand(region.Bounds, 8), CommittedInkLayer.TransformBounds(stroke.Bounds, toScreen)))
                .Select(stroke => Copy(stroke, toScreen)).ToList();
            shared.Bounds = region.Bounds;
            foreach (var stroke in shared.Ink) { shared.Seen.Add(stroke.Id); handled.Add(stroke.Id); }
        }
    }

    private static InkStroke Copy(InkStroke source, Matrix3x2 transform)
    {
        var copy = source.Clone(); copy.Transform(transform); return copy;
    }
    private static Matrix3x2 RegionTransform(Rect source, Rect target) => Matrix3x2.CreateTranslation(-source.Left, -source.Top)
        * Matrix3x2.CreateScale(target.Width / source.Width, target.Height / source.Height)
        * Matrix3x2.CreateTranslation(target.Left, target.Top);
    private static Rect Expand(Rect rect, float amount) => new(rect.Left - amount, rect.Top - amount, rect.Right + amount, rect.Bottom + amount);
    private static bool Contains(Rect outer, Rect inner) => outer.Contains(new Vector2(inner.Left, inner.Top)) && outer.Contains(new Vector2(inner.Right, inner.Bottom));
    private static IEnumerable<ScreenRegionAnchor> UniqueRegions(ScreenViewSnapshot view) =>
        (view?.StableContentRegions ?? Array.Empty<ScreenRegionAnchor>()).GroupBy(region => region.Key).Where(group => group.Count() == 1).Select(group => group.First());
}
