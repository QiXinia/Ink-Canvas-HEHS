using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Selection;

public static class FloatingToolbarLayout
{
    /// <summary>Constant-size candidate search in one coordinate space, including negative monitor origins.</summary>
    public static Rect Place(Rect work, Rect selection, Rect obstacle, Vector2 size, float gap = 10,
        Vector2? firstReference = null, Vector2? secondReference = null)
    {
        float width = Math.Min(size.X, work.Width), height = Math.Min(size.Y, work.Height);
        float x = selection.Center.X - width / 2;
        var firstHandle = ReferenceBounds(firstReference, gap * 1.6f);
        var secondHandle = ReferenceBounds(secondReference, gap * 1.6f);
        Span<Vector2> candidates = stackalloc Vector2[]
        {
            new(x, selection.Bottom + gap), new(x, selection.Top - height - gap),
            new(selection.Right + gap, selection.Center.Y - height / 2),
            new(selection.Left - width - gap, selection.Center.Y - height / 2),
            new(x, obstacle.Top - height - gap), new(x, obstacle.Bottom + gap),
            new(work.Left + gap, work.Top + gap), new(work.Right - width - gap, work.Top + gap),
            new(work.Left + gap, work.Bottom - height - gap), new(work.Right - width - gap, work.Bottom - height - gap),
        };
        Rect best = Rect.Empty;
        double bestScore = double.MaxValue;
        for (int i = 0; i < candidates.Length; i++)
        {
            float left = Math.Clamp(candidates[i].X, work.Left, work.Right - width);
            float top = Math.Clamp(candidates[i].Y, work.Top, work.Bottom - height);
            var rect = new Rect(left, top, left + width, top + height);
            double score = Overlap(rect, obstacle.IsEmpty ? Rect.Empty : obstacle.Inflate(gap)) * 10000
                + (Overlap(rect, firstHandle) + Overlap(rect, secondHandle)) * 1000
                + Overlap(rect, selection.Inflate(gap)) * 100
                + Vector2.DistanceSquared(rect.Center, selection.Center) * .001 + i * .01;
            if (score < bestScore) { bestScore = score; best = rect; }
        }
        return best;
    }

    private static Rect ReferenceBounds(Vector2? reference, float radius) => reference is { } point
        ? new Rect(point.X - radius, point.Y - radius, point.X + radius, point.Y + radius)
        : Rect.Empty;

    private static float Overlap(Rect a, Rect b) => b.IsEmpty ? 0 :
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left))
        * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}
