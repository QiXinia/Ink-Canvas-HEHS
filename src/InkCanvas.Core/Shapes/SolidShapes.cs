using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Shapes;

public static partial class ShapeGenerator
{
    public static bool IsTwoStepSolid(ShapeKind kind) => kind is ShapeKind.Cuboid or ShapeKind.Cube
        or ShapeKind.TriangularPrism or ShapeKind.PentagonalPrism or ShapeKind.HexagonalPrism or ShapeKind.SquarePyramid;

    public static Vector2 SolidBaseEnd(ShapeKind kind, Vector2 start, Vector2 end)
    {
        if (kind != ShapeKind.Cube) return end;
        float side = MathF.Max(MathF.Abs(end.X - start.X), MathF.Abs(end.Y - start.Y));
        return start + new Vector2(end.X < start.X ? -side : side, end.Y < start.Y ? -side : side);
    }

    private static Vector2[] SolidBase(ShapeKind kind, Vector2 start, Vector2 end)
    {
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(end.X) || !float.IsFinite(end.Y)) return Array.Empty<Vector2>();
        end = SolidBaseEnd(kind, start, end);
        var low = Vector2.Min(start, end);
        var high = Vector2.Max(start, end);
        if (high.X - low.X < .01f || high.Y - low.Y < .01f) return Array.Empty<Vector2>();
        int sides = kind switch { ShapeKind.TriangularPrism => 3, ShapeKind.PentagonalPrism => 5, ShapeKind.HexagonalPrism => 6, _ => 4 };
        if (kind == ShapeKind.SquarePyramid)
        {
            var center = (low + high) / 2;
            return new[] { new Vector2(center.X, low.Y), new Vector2(high.X, center.Y), new Vector2(center.X, high.Y), new Vector2(low.X, center.Y) };
        }
        if (sides == 4) return new[] { low, new Vector2(high.X, low.Y), high, new Vector2(low.X, high.Y) };
        var vertices = new Vector2[sides];
        var minimum = new Vector2(float.MaxValue);
        var maximum = new Vector2(float.MinValue);
        for (int i = 0; i < sides; i++)
        {
            float angle = -MathF.PI / 2 + i * MathF.Tau / sides;
            vertices[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            minimum = Vector2.Min(minimum, vertices[i]);
            maximum = Vector2.Max(maximum, vertices[i]);
        }
        // Fit the polygon to the actual drag bounds, including odd-sided polygons.
        for (int i = 0; i < sides; i++) vertices[i] = low + (vertices[i] - minimum) / (maximum - minimum) * (high - low);
        return vertices;
    }

    private static List<InkStroke> GenerateSolidBase(ShapeKind kind, Vector2 start, Vector2 end, StrokeAttributes attrs)
    {
        var polygon = SolidBase(kind, start, end);
        var strokes = new List<InkStroke>(polygon.Length);
        for (int i = 0; i < polygon.Length; i++) strokes.Add(Stroke(attrs, polygon[i], polygon[(i + 1) % polygon.Length]));
        return strokes;
    }

    /// <summary>Extrude a face or place a pyramid apex. Each shared edge is emitted once.</summary>
    public static List<InkStroke> GenerateSolid(ShapeKind kind, Vector2 start, Vector2 end, Vector2 offset, StrokeAttributes attrs)
        => ShapeDefinition.Attach(GenerateSolidContours(kind, start, end, offset, attrs), new ShapeDefinition
        {
            Kind = kind, Construction = ShapeConstruction.Solid,
            Parameters = new[] { start.X, start.Y, end.X, end.Y, offset.X, offset.Y },
        });

    internal static List<InkStroke> GenerateSolidContours(ShapeKind kind, Vector2 start, Vector2 end, Vector2 offset, StrokeAttributes attrs)
    {
        attrs ??= new StrokeAttributes();
        if (kind == ShapeKind.Cuboid) return GenerateCuboid(start, end, offset, attrs);
        var result = new List<InkStroke>();
        if (!IsTwoStepSolid(kind) || !float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || offset.LengthSquared() < .0001f) return result;
        var polygon = SolidBase(kind, start, end);
        int count = polygon.Length;
        if (count == 0) return result;
        if (kind == ShapeKind.Cube)
        {
            float side = Vector2.Distance(polygon[0], polygon[1]);
            offset = Vector2.Normalize(offset) * side * .55f;
        }
        bool pyramid = kind == ShapeKind.SquarePyramid;
        var vertices = new Vector3[pyramid ? count + 1 : count * 2];
        for (int i = 0; i < count; i++) vertices[i] = new Vector3(polygon[i], 0);
        if (pyramid) vertices[count] = new Vector3((Vector2.Min(start, end) + Vector2.Max(start, end)) / 2, 1);
        else for (int i = 0; i < count; i++) vertices[count + i] = new Vector3(polygon[i], 1);

        // This viewing ray projects to zero under p.xy + p.z * offset. Reversing the
        // depth direction can make the face drawn first either the near or the far face.
        float nearSign = offset.Y > 0 || offset.Y == 0 && offset.X < 0 ? 1 : -1;
        var towardViewer = new Vector3(-offset.X, -offset.Y, 1) * nearSign;
        var edges = new Dictionary<(int From, int To), bool>();
        AddFace(Enumerable.Range(0, count).Reverse().ToArray());
        if (!pyramid) AddFace(Enumerable.Range(count, count).ToArray());
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            AddFace(pyramid ? new[] { i, next, count } : new[] { i, next, next + count, i + count });
        }
        foreach (var (edge, visible) in edges)
        {
            Vector2 from = Project(vertices[edge.From]), to = Project(vertices[edge.To]);
            if (Vector2.DistanceSquared(from, to) < .0001f) continue;
            if (visible) result.Add(Stroke(attrs, from, to));
            else result.AddRange(Dashed(attrs, from, to, DashLength, DashPeriod - DashLength));
        }
        foreach (var stroke in result) { stroke.IsRecognizedShape = true; stroke.Complete(); }
        return result;

        Vector2 Project(Vector3 point) => new(point.X + point.Z * offset.X, point.Y + point.Z * offset.Y);
        void AddFace(int[] face)
        {
            var normal = Vector3.Cross(vertices[face[1]] - vertices[face[0]], vertices[face[2]] - vertices[face[0]]);
            bool visible = Vector3.Dot(normal, towardViewer) > .00001f;
            for (int i = 0; i < face.Length; i++)
            {
                int a = face[i], b = face[(i + 1) % face.Length];
                var key = a < b ? (a, b) : (b, a);
                edges.TryGetValue(key, out bool alreadyVisible);
                edges[key] = alreadyVisible || visible;
            }
        }
    }

    private static List<InkStroke> GenerateRoundSolid(ShapeKind kind, Vector2 start, Vector2 end, StrokeAttributes attrs)
    {
        var result = new List<InkStroke>();
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(end.X) || !float.IsFinite(end.Y)) return result;
        var low = Vector2.Min(start, end);
        var high = Vector2.Max(start, end);
        float width = high.X - low.X, height = high.Y - low.Y;
        if (width < .01f || height < .01f) return result;
        if (kind == ShapeKind.Sphere)
        {
            float radius = MathF.Min(width, height) / 2;
            var center = (start + end) / 2;
            result.Add(EllipseStroke(attrs, center, radius, radius));
            result.Add(Stroke(attrs, EllipsePoints(center, radius, radius * .3f, 0, MathF.PI, FullEllipseSegments / 2)));
            AddDashedArc(result, attrs, center, radius, radius * .3f, MathF.PI, MathF.Tau);
            return result;
        }
        if (kind == ShapeKind.Hemisphere)
        {
            float rx = width / 2, ry = height / 1.3f;
            bool downward = end.Y < start.Y;
            var center = new Vector2((low.X + high.X) / 2, downward ? low.Y + ry * .3f : high.Y - ry * .3f);
            result.Add(Stroke(attrs, EllipsePoints(center, rx, ry, downward ? 0 : MathF.PI, downward ? MathF.PI : MathF.Tau, FullEllipseSegments / 2)));
            result.Add(Stroke(attrs, EllipsePoints(center, rx, ry * .3f, 0, MathF.PI, FullEllipseSegments / 2)));
            AddDashedArc(result, attrs, center, rx, ry * .3f, MathF.PI, MathF.Tau);
            return result;
        }

        const float topScale = .55f;
        float baseRx = width / 2, baseRy = MathF.Min(baseRx / SolidEllipseRatio, height / 4);
        bool inverted = end.Y < start.Y;
        var largeCenter = new Vector2((low.X + high.X) / 2, inverted ? low.Y + baseRy : high.Y - baseRy);
        var smallCenter = new Vector2(largeCenter.X, inverted ? high.Y - baseRy * topScale : low.Y + baseRy * topScale);
        // Common tangents of homothetic ellipses. The side edges meet the rims tangentially,
        // including short, wide frustums and inverted drags.
        float normalY = (1 - topScale) * baseRy / (smallCenter.Y - largeCenter.Y);
        float tangent = MathF.Asin(Math.Clamp(normalY, -1, 1));
        AddRim(largeCenter, baseRx, baseRy, inverted);
        AddRim(smallCenter, baseRx * topScale, baseRy * topScale, !inverted);
        foreach (float angle in new[] { tangent, MathF.PI - tangent })
        {
            var radial = new Vector2(MathF.Cos(angle) * baseRx, MathF.Sin(angle) * baseRy);
            result.Add(Stroke(attrs, largeCenter + radial, smallCenter + radial * topScale));
        }
        return result;

        void AddRim(Vector2 center, float rx, float ry, bool near)
        {
            if (near) result.Add(EllipseStroke(attrs, center, rx, ry));
            else
            {
                result.Add(Stroke(attrs, EllipsePoints(center, rx, ry, tangent, MathF.PI - tangent, FullEllipseSegments / 2)));
                AddDashedArc(result, attrs, center, rx, ry, MathF.PI - tangent, MathF.Tau + tangent);
            }
        }
    }

    private static void AddDashedArc(List<InkStroke> strokes, StrokeAttributes attrs, Vector2 center, float rx, float ry, float from, float to)
    {
        // Keep dash lengths stable in canvas units as the shape grows.
        float radius = MathF.Max(rx, ry);
        float period = MathF.Max(.01f, DashPeriod / radius);
        for (float angle = from; angle < to; angle += period)
        {
            float end = MathF.Min(to, angle + DashLength / radius);
            int segments = Math.Max(1, (int)MathF.Ceiling((end - angle) * radius / 2));
            strokes.Add(Stroke(attrs, EllipsePoints(center, rx, ry, angle, end, segments)));
        }
    }
}
