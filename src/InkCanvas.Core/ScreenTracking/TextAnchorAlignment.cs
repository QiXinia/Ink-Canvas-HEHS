using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.ScreenTracking;

/// <summary>Fits the scale and displacement of the same visible characters in one observation.</summary>
public static class TextAnchorAlignment
{
    private const float Tolerance = 3;

    public static bool TryFit(IReadOnlyList<(Rect Reference, Rect Current)> anchors, out Matrix3x2 transform)
    {
        transform = Matrix3x2.Identity;
        int bestCount = 0;
        float bestError = float.MaxValue;
        var best = Matrix3x2.Identity;
        for (int i = 0; i < anchors.Count; i++)
        {
            for (int j = i + 1; j < anchors.Count; j++)
            {
                var a = anchors[i];
                var b = anchors[j];
                var before = Position(b.Reference) - Position(a.Reference);
                var after = Position(b.Current) - Position(a.Current);
                float distance = before.LengthSquared();
                if (distance < 64 * 64) continue;
                float scale = Vector2.Dot(before, after) / distance;
                if (!float.IsFinite(scale) || scale is < .0025f or > 400) continue;
                var offset = (Position(a.Current) + Position(b.Current)
                    - (Position(a.Reference) + Position(b.Reference)) * scale) / 2;
                var candidate = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offset);
                int count = 0;
                float error = 0;
                foreach (var anchor in anchors)
                {
                    if (!Matches(anchor, candidate, out float residual)) continue;
                    count++;
                    error += residual;
                }
                if (count > bestCount || count == bestCount && error < bestError)
                {
                    bestCount = count;
                    bestError = error;
                    best = candidate;
                }
            }
        }
        if (bestCount < 2 || bestCount * 2 <= anchors.Count) return false;
        var referenceCenter = Vector2.Zero;
        var currentCenter = Vector2.Zero;
        foreach (var anchor in anchors)
        {
            if (!Matches(anchor, best, out _)) continue;
            referenceCenter += Position(anchor.Reference);
            currentCenter += Position(anchor.Current);
        }
        referenceCenter /= bestCount;
        currentCenter /= bestCount;
        float numerator = 0, denominator = 0;
        foreach (var anchor in anchors)
        {
            if (!Matches(anchor, best, out _)) continue;
            var before = Position(anchor.Reference) - referenceCenter;
            numerator += Vector2.Dot(before, Position(anchor.Current) - currentCenter);
            denominator += before.LengthSquared();
        }
        if (denominator < 64 * 64 / 2) return false;
        float fittedScale = numerator / denominator;
        if (!float.IsFinite(fittedScale) || fittedScale is < .0025f or > 400) return false;
        transform = Matrix3x2.CreateScale(fittedScale)
            * Matrix3x2.CreateTranslation(currentCenter - referenceCenter * fittedScale);
        return true;
    }

    private static Vector2 Position(Rect rect) => new(rect.Left, rect.Top);

    private static bool Matches((Rect Reference, Rect Current) anchor, Matrix3x2 transform, out float error)
    {
        error = Vector2.DistanceSquared(Vector2.Transform(Position(anchor.Reference), transform), Position(anchor.Current));
        return !anchor.Reference.IsEmpty && !anchor.Current.IsEmpty && error <= Tolerance * Tolerance
            && Math.Abs(anchor.Current.Height - anchor.Reference.Height * transform.M11) <= Math.Max(3, anchor.Current.Height * .12f);
    }
}
