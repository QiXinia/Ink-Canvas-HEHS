namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private readonly record struct TriangleBounds(GeoTriangle Triangle, GeoVector Min, GeoVector Max);
    private GeometryValue SurfaceIntersection(GeometryValue first, GeometryValue second)
    {
        TriangleBounds Bounds(GeoTriangle triangle) => new(triangle,
            new(Math.Min(triangle.A.X, Math.Min(triangle.B.X, triangle.C.X)), Math.Min(triangle.A.Y, Math.Min(triangle.B.Y, triangle.C.Y)), Math.Min(triangle.A.Z, Math.Min(triangle.B.Z, triangle.C.Z))),
            new(Math.Max(triangle.A.X, Math.Max(triangle.B.X, triangle.C.X)), Math.Max(triangle.A.Y, Math.Max(triangle.B.Y, triangle.C.Y)), Math.Max(triangle.A.Z, Math.Max(triangle.B.Z, triangle.C.Z))));
        var candidates = second.Faces.Select(Bounds).OrderBy(box => box.Min.X).ToArray();
        var paths = new List<GeoVector[]>();
        foreach (var triangle in first.Faces)
        {
            var box = Bounds(triangle);
            foreach (var other in candidates)
            {
                if (other.Min.X > box.Max.X + 1e-9) break;
                if (other.Max.X < box.Min.X - 1e-9 || other.Min.Y > box.Max.Y + 1e-9 || other.Max.Y < box.Min.Y - 1e-9
                    || other.Min.Z > box.Max.Z + 1e-9 || other.Max.Z < box.Min.Z - 1e-9) continue;
                Sample();
                var points = new List<GeoVector>();
                Cut(triangle, other.Triangle, points);
                Cut(other.Triangle, triangle, points);
                if (points.Count < 2) continue;
                var pairs = points.SelectMany((point, index) => points.Skip(index + 1).Select(next => (Point: point, Next: next, Distance: (point - next).Length)));
                var longest = pairs.MaxBy(pair => pair.Distance);
                if (longest.Distance > 1e-8) paths.Add(new[] { longest.Point, longest.Next });
            }
        }
        return new() { Kind = GeometryKind.Curve, Paths = paths, Display = "曲面交线 · 网格近似" };
    }

    private static void Cut(GeoTriangle source, GeoTriangle target, List<GeoVector> intersections)
    {
        var normal = (target.B - target.A).Cross(target.C - target.A);
        if (normal.Length < 1e-12) return;
        normal = normal.Unit();
        var corners = new[] { source.A, source.B, source.C, source.A };
        for (int i = 0; i < 3; i++)
        {
            double start = (corners[i] - target.A).Dot(normal), end = (corners[i + 1] - target.A).Dot(normal);
            if (Math.Abs(start - end) < 1e-12 || start * end > 1e-12) continue;
            double t = start / (start - end);
            if (t < -1e-8 || t > 1 + 1e-8) continue;
            var point = corners[i] + (corners[i + 1] - corners[i]) * t;
            bool inside = (target.B - target.A).Cross(point - target.A).Dot(normal) >= -1e-8
                && (target.C - target.B).Cross(point - target.B).Dot(normal) >= -1e-8
                && (target.A - target.C).Cross(point - target.C).Dot(normal) >= -1e-8;
            if (inside && intersections.All(existing => (point - existing).Length > 1e-8)) intersections.Add(point);
        }
    }
}
