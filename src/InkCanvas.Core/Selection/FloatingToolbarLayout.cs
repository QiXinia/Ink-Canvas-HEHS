using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Selection;

/// <summary>
/// Places a floating selection toolbar outside the selected object.
/// Preference order is above → below → right → left → work-area corners.
/// Every candidate is clamped to <paramref name="work"/>; candidates that overlap the selection
/// (or the given obstacle, e.g. the dock) are rejected before any distance scoring, so a toolbar
/// is never positioned inside the selection when an outside slot exists.
/// </summary>
public static class FloatingToolbarLayout
{
    public static Rect Place(Rect work, Rect selection, Rect obstacle, Vector2 size, float gap = 10)
    {
        if (work.IsEmpty || size.X <= 0 || size.Y <= 0) return Rect.Empty;

        float width = Math.Min(size.X, work.Width);
        float height = Math.Min(size.Y, work.Height);
        if (width <= 0 || height <= 0) return Rect.Empty;

        Vector2 center = selection.IsEmpty ? work.Center : selection.Center;
        float x = center.X - width / 2;
        float y = center.Y - height / 2;
        float gapValue = MathF.Max(0, gap);

        Span<Vector2> candidates = stackalloc Vector2[]
        {
            new(x, selection.Top - height - gapValue),                 // above (preferred)
            new(x, selection.Bottom + gapValue),                       // below
            new(selection.Right + gapValue, y),                        // right
            new(selection.Left - width - gapValue, y),                 // left
            new(work.Left + gapValue, work.Top + gapValue),            // work corners (fallbacks)
            new(work.Right - width - gapValue, work.Top + gapValue),
            new(work.Left + gapValue, work.Bottom - height - gapValue),
            new(work.Right - width - gapValue, work.Bottom - height - gapValue),
        };

        // Keep a small breathing gap around the selection; touching the inflated edge is acceptable.
        Rect avoidSelection = selection.IsEmpty ? Rect.Empty : selection.Inflate(gapValue * 0.5f);
        Rect avoidObstacle = obstacle.IsEmpty ? Rect.Empty : obstacle;

        Rect best = Rect.Empty;
        double bestScore = double.MaxValue;
        for (int index = 0; index < candidates.Length; index++)
        {
            float left = Math.Clamp(candidates[index].X, work.Left, work.Right - width);
            float top = Math.Clamp(candidates[index].Y, work.Top, work.Bottom - height);
            var rect = new Rect(left, top, left + width, top + height);

            float selectionOverlap = Overlap(rect, avoidSelection);
            float obstacleOverlap = Overlap(rect, avoidObstacle);
            if (selectionOverlap <= 0 && obstacleOverlap <= 0)
                return rect;

            double score = selectionOverlap * 10000f
                + obstacleOverlap * 10000f
                + Vector2.DistanceSquared(rect.Center, center) * .001f
                + index * .01f;
            if (score < bestScore) { bestScore = score; best = rect; }
        }
        return best;
    }

    private static float Overlap(Rect a, Rect b) => b.IsEmpty ? 0 :
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left))
        * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}