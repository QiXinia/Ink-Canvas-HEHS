using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Shapes;

public static partial class ShapeGenerator
{
    internal const float DefaultFrustumTopScale = .55f;
    internal const float DefaultSectorSweep = MathF.PI / 2;

    internal static (Vector2 Center, Vector2 Radius) FrustumTop(Vector2 start, Vector2 end, float scale)
    {
        var low = Vector2.Min(start, end);
        var high = Vector2.Max(start, end);
        float rx = (high.X - low.X) / 2;
        float ry = MathF.Min(rx / SolidEllipseRatio, (high.Y - low.Y) / 4);
        return (new((low.X + high.X) / 2, end.Y < start.Y ? high.Y - ry * scale : low.Y + ry * scale),
            new(rx * scale, ry * scale));
    }

    internal static List<InkStroke> GenerateSector(Vector2 center, Vector2 first, float sweep, StrokeAttributes attributes)
    {
        var radial = first - center;
        float radius = radial.Length();
        if (!float.IsFinite(radius) || radius < .01f) return [];
        float heading = MathF.Atan2(radial.Y, radial.X);
        int segments = Math.Max(3, (int)MathF.Ceiling(sweep * FullEllipseSegments / MathF.Tau));
        var outline = new Vector2[segments + 3];
        outline[0] = center;
        outline[1] = first;
        for (int i = 1; i <= segments; i++)
        {
            var (sin, cos) = MathF.SinCos(heading + sweep * i / segments);
            outline[i + 1] = center + radius * new Vector2(cos, sin);
        }
        outline[^1] = center;
        return [Stroke(attributes, outline)];
    }

    internal static List<InkStroke> GenerateRoundedRectangle(ShapeKind kind, Vector2 start, Vector2 end,
        float radius, float inset, StrokeAttributes attributes)
    {
        var low = Vector2.Min(start, end);
        var high = Vector2.Max(start, end);
        if (MathF.Min(high.X - low.X, high.Y - low.Y) < .01f) return [];
        var outline = RoundedOutline(low, high, radius, kind == ShapeKind.SuperRoundedRectangle);
        var result = new List<InkStroke> { Stroke(attributes, outline) };
        if (kind == ShapeKind.RoundedRectangleRing)
        {
            var padding = new Vector2(inset);
            var inner = RoundedOutline(low + padding, high - padding, MathF.Max(0, radius - inset), false);
            Array.Reverse(inner);
            result.Add(Stroke(attributes, inner));
        }
        return result;
    }

    private static Vector2[] RoundedOutline(Vector2 low, Vector2 high, float radius, bool superellipse)
    {
        if (radius < .001f) return [low, new(high.X, low.Y), high, new(low.X, high.Y), low];
        const int arcSegments = 16;
        Vector2[] centers = [new(high.X - radius, high.Y - radius), new(low.X + radius, high.Y - radius),
            new(low.X + radius, low.Y + radius), new(high.X - radius, low.Y + radius)];
        Vector2[] directions = [Vector2.UnitX, Vector2.UnitY, -Vector2.UnitX, -Vector2.UnitY];
        var outline = new Vector2[centers.Length * (arcSegments + 1) + 1];
        for (int corner = 0; corner < centers.Length; corner++)
            for (int step = 0; step <= arcSegments; step++)
            {
                var (sin, cos) = MathF.SinCos(step * (MathF.PI / 2) / arcSegments);
                if (step == arcSegments) cos = 0;
                if (superellipse) { sin = MathF.Sqrt(sin); cos = MathF.Sqrt(cos); }
                var direction = directions[corner] * cos + directions[(corner + 1) % 4] * sin;
                outline[corner * (arcSegments + 1) + step] = centers[corner] + radius * direction;
            }
        outline[^1] = outline[0];
        return outline;
    }
}
