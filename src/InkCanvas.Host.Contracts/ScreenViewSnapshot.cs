using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.ScreenTracking;

public enum TrackedApplication { None, Word, PowerPoint, Edge, Diagnostic }

public sealed record ScreenRegionAnchor(string Key, Rect Bounds);

/// <summary>Physical screen pixels from the target application, independent of overlay DPI.</summary>
public sealed record ScreenViewSnapshot
{
    public TrackedApplication Application { get; init; }
    public long WindowHandle { get; init; }
    public string ContextKey { get; init; }
    public Rect Viewport { get; init; }
    public float Scale { get; init; } = 1;
    public Vector2 ScrollPixels { get; init; }
    public bool HasScrollMetrics { get; init; }
    public bool HasZoomMetrics { get; init; }
    public IReadOnlyList<Rect> Occlusions { get; init; } = Array.Empty<Rect>();
    public IReadOnlyList<ScreenRegionAnchor> Anchors { get; init; } = Array.Empty<ScreenRegionAnchor>();
    public IReadOnlyList<ScreenRegionAnchor> StableContentRegions { get; init; } = Array.Empty<ScreenRegionAnchor>();
    public string Status { get; init; }
    public bool AlignmentReliable { get; init; } = true;
    public int ObservationEpoch { get; init; }
    public bool IsValid => !string.IsNullOrWhiteSpace(ContextKey) && !Viewport.IsEmpty
        && float.IsFinite(Viewport.Left) && float.IsFinite(Viewport.Top) && float.IsFinite(Viewport.Right) && float.IsFinite(Viewport.Bottom)
        && float.IsFinite(Scale) && Scale is >= .05f and <= 20
        && float.IsFinite(ScrollPixels.X) && float.IsFinite(ScrollPixels.Y);

    public Matrix3x2 ContentToScreen(float? scale = null, Vector2? scroll = null) =>
        Matrix3x2.CreateScale(scale ?? Scale) * Matrix3x2.CreateTranslation(
            new Vector2(Viewport.Left, Viewport.Top) - (scroll ?? ScrollPixels));

    public static float ScrollOffset(float viewportSize, double viewPercent, double scrollPercent)
    {
        if (!double.IsFinite(viewPercent) || !double.IsFinite(scrollPercent) || viewPercent <= 0 || scrollPercent < 0 || viewportSize <= 0) return 0;
        return (float)(Math.Max(0, viewportSize * (100 / Math.Min(100, viewPercent) - 1)) * Math.Clamp(scrollPercent, 0, 100) / 100);
    }
}

/// <summary>Only broad stationary headers and side panes become masks after confirmed scrolling.</summary>
public sealed class FixedScreenRegions
{
    private readonly Dictionary<string, Rect> _fixed = new();
    public IReadOnlyList<Rect> Update(ScreenViewSnapshot previous, ScreenViewSnapshot current)
    {
        if (previous?.ContextKey != current.ContextKey) _fixed.Clear();
        var currentAnchors = current.Anchors.GroupBy(anchor => anchor.Key).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Bounds);
        foreach (string key in _fixed.Keys.Where(key => !currentAnchors.ContainsKey(key)).ToArray()) _fixed.Remove(key);
        if (previous is not null && previous.ContextKey == current.ContextKey && previous.HasScrollMetrics && current.HasScrollMetrics
            && Math.Abs(previous.Scale - current.Scale) < .002f && Vector2.Distance(previous.ScrollPixels, current.ScrollPixels) > 8)
        {
            foreach (var anchor in previous.Anchors)
            {
                if (!currentAnchors.TryGetValue(anchor.Key, out var next)) continue;
                float area = next.Width * next.Height / (current.Viewport.Width * current.Viewport.Height);
                bool header = next.Width > current.Viewport.Width * .45f && next.Height < current.Viewport.Height * .3f;
                bool sidebar = next.Height > current.Viewport.Height * .5f && next.Width < current.Viewport.Width * .3f;
                if (area is > .002f and < .35f && (header || sidebar)
                    && Vector2.Distance(anchor.Bounds.Center, next.Center) < 2
                    && Math.Abs(anchor.Bounds.Width - next.Width) < 2 && Math.Abs(anchor.Bounds.Height - next.Height) < 2)
                    _fixed[anchor.Key] = next;
                else _fixed.Remove(anchor.Key);
            }
        }
        return _fixed.Keys.Where(currentAnchors.ContainsKey).Select(key => currentAnchors[key]).ToArray();
    }
    public void Clear() => _fixed.Clear();
}
