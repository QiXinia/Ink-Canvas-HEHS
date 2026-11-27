using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Recognition;

public static partial class ShapeRecognizer
{
    /// <summary>Pause recognition uses distance sampling so stationary packets cannot bias the fitted center.</summary>
    public static RecognitionResult RecognizeHeld(IReadOnlyList<InkStroke> strokes)
    {
        if (strokes is null || strokes.Count == 0) return RecognitionResult.None;
        if (TryWireframe(strokes, out var wireframe)) return wireframe;
        var points = Resample(strokes.SelectMany(s => s.Points.Select(p => p.Position)).ToList(), 3);
        if (points.Count < 8) return RecognitionResult.None;
        var bounds = BoundsOf(points);
        if (MathF.Max(bounds.Width, bounds.Height) < MinSize) return RecognitionResult.None;
        if (strokes.Count == 1)
        {
            if (TryLine(points, out var line)) return line;
            // Rotate the exact vertical case away from the legacy PCA singularity.
            var rotated = points.Select(p => new Vector2(-p.Y, p.X)).ToList();
            if (TryLine(rotated, out line))
                return line with { Center = new(line.Center.Y, -line.Center.X),
                    Corners = line.Corners.Select(p => new Vector2(p.Y, -p.X)).ToArray() };
        }
        if (Vector2.Distance(points[0], points[^1]) > MathF.Max(10, (bounds.Width + bounds.Height) * .08f))
            return RecognitionResult.None;
        if (TryClosedPolygon(points, out var corners))
        {
            if (corners.Length == 3 && MathF.Abs(StrokeGeometry.SignedArea(corners)) > MinSize * MinSize / 4)
                return new(RecognizedShape.Triangle, bounds.Center, 0, 0, .9f, corners);
            if (corners.Length == 4 && TryQuadrilateral(corners, out var kind, out var fitted))
                return new(kind, bounds.Center, 0, 0, .9f, fitted);
        }
        var sampled = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None });
        sampled.RestorePoints(points.Select(p => new InkPoint(p.X, p.Y, .5f, 1, 0)));
        var result = Recognize(new[] { sampled });
        return result.Shape is RecognizedShape.Circle or RecognizedShape.Ellipse
            ? result with { Center = bounds.Center } : RecognitionResult.None;
    }

    private static bool TryClosedPolygon(List<Vector2> points, out Vector2[] corners)
    {
        corners = Array.Empty<Vector2>();
        int split = 0;
        for (int i = 1; i < points.Count; i++)
            if (Vector2.DistanceSquared(points[0], points[i]) > Vector2.DistanceSquared(points[0], points[split])) split = i;
        if (split == 0) return false;
        var bounds = BoundsOf(points);
        float epsilon = MathF.Max(3, (bounds.Width + bounds.Height) * .015f);
        var simplified = Simplify(points.GetRange(0, split + 1), epsilon);
        simplified.RemoveAt(simplified.Count - 1);
        simplified.AddRange(Simplify(points.GetRange(split, points.Count - split), epsilon));
        if (Vector2.Distance(simplified[0], simplified[^1]) < epsilon * 2) simplified.RemoveAt(simplified.Count - 1);
        if (simplified.Count is < 3 or > 10) return false;
        var found = new List<Vector2>();
        for (int i = 0; i < simplified.Count; i++)
        {
            var before = Vector2.Normalize(simplified[i] - simplified[(i + simplified.Count - 1) % simplified.Count]);
            var after = Vector2.Normalize(simplified[(i + 1) % simplified.Count] - simplified[i]);
            if (Vector2.Dot(before, after) < .866f) found.Add(simplified[i]);
        }
        if (found.Count is not (3 or 4)) return false;
        corners = found.ToArray();
        return true;
    }

    /// <summary>Uniform arc sampling prevents endpoint dwell and hardware packet rate from biasing fits.</summary>
    public static List<Vector2> Resample(List<Vector2> points, float spacing)
    {
        if (points.Count < 2 || !float.IsFinite(spacing) || spacing <= 0) return points;
        var result = new List<Vector2> { points[0] };
        float remaining = spacing;
        for (int i = 1; i < points.Count; i++)
        {
            var from = points[i - 1];
            float distance = Vector2.Distance(from, points[i]);
            while (distance >= remaining && result.Count < 4096)
            {
                from = Vector2.Lerp(from, points[i], remaining / distance);
                result.Add(from); distance -= remaining; remaining = spacing;
            }
            remaining -= distance;
            if (result.Count >= 4096) break;
        }
        if (Vector2.DistanceSquared(result[^1], points[^1]) > .01f) result.Add(points[^1]);
        return result;
    }

    private static bool TryQuadrilateral(Vector2[] corners, out RecognizedShape kind, out Vector2[] fitted)
    {
        kind = RecognizedShape.None; fitted = corners;
        var a = corners[1] - corners[0]; var b = corners[2] - corners[1];
        var c = corners[3] - corners[2]; var d = corners[0] - corners[3];
        if (MathF.Min(MathF.Min(a.Length(), b.Length()), MathF.Min(c.Length(), d.Length())) < 20) return false;
        if (Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(c)) > -.96f
            || Vector2.Dot(Vector2.Normalize(b), Vector2.Normalize(d)) > -.96f
            || MathF.Abs(StrokeGeometry.SignedArea(corners)) < MinSize * MinSize / 2) return false;
        var center = corners.Aggregate(Vector2.Zero, (sum, p) => sum + p) / 4;
        var u = (a - c) / 4; var v = (b - d) / 4;
        bool right = MathF.Abs(Vector2.Dot(Vector2.Normalize(u), Vector2.Normalize(v))) < .16f;
        if (right)
        {
            var normal = Vector2.Normalize(new Vector2(-u.Y, u.X));
            v = normal * Vector2.Dot(v, normal);
        }
        fitted = new[] { center - u - v, center + u - v, center + u + v, center - u + v };
        kind = right ? RecognizedShape.Rectangle : RecognizedShape.Parallelogram;
        return true;
    }

    private static bool TryWireframe(IReadOnlyList<InkStroke> strokes, out RecognitionResult result)
    {
        result = RecognitionResult.None;
        var segments = new List<(Vector2 A, Vector2 B)>();
        foreach (var stroke in strokes)
        {
            var simplified = Simplify(stroke.Points.Select(p => p.Position).ToList(), 4);
            if (simplified.Count > 30) return false;
            for (int i = 1; i < simplified.Count; i++)
                if (Vector2.Distance(simplified[i - 1], simplified[i]) >= MinSize)
                    segments.Add((simplified[i - 1], simplified[i]));
        }
        if (segments.Count is < 2 or > 30) return false;
        if (segments.Count == 2)
        {
            var a = segments[0]; var b = segments[1];
            var u = a.B - a.A; var v = b.B - b.A;
            if (MathF.Abs(Vector2.Dot(Vector2.Normalize(u), Vector2.Normalize(v))) > .15f) return false;
            float cross = u.X * v.Y - u.Y * v.X;
            var diff = b.A - a.A;
            float t = (diff.X * v.Y - diff.Y * v.X) / cross;
            float s = (diff.X * u.Y - diff.Y * u.X) / cross;
            if (t is < -.05f or > 1.05f || s is < -.05f or > 1.05f) return false;
            // A corner formed by two strokes is common handwriting. Axes must cross internally.
            if (t is < .12f or > .88f || s is < .12f or > .88f) return false;
            result = new(RecognizedShape.CoordinateAxes, a.A + t * u, 0, 0, .95f, new[] { a.A, a.B, b.A, b.B });
            return true;
        }
        var vertices = new List<Vector2>();
        var edges = new HashSet<(int A, int B)>();
        foreach (var segment in segments)
        {
            int a = Vertex(segment.A), b = Vertex(segment.B);
            if (a != b) edges.Add((Math.Min(a, b), Math.Max(a, b)));
        }
        if (vertices.Count != 8 || edges.Count != 12) return false;
        for (int i = 0; i < 8; i++) if (edges.Count(edge => edge.A == i || edge.B == i) != 3) return false;
        var neighbors = edges.Where(edge => edge.A == 0 || edge.B == 0)
            .Select(edge => vertices[edge.A == 0 ? edge.B : edge.A] - vertices[0]).ToArray();
        var ideal = new Vector2[8];
        for (int mask = 0; mask < 8; mask++)
        {
            var p = vertices[0];
            for (int axis = 0; axis < 3; axis++) if ((mask & (1 << axis)) != 0) p += neighbors[axis];
            ideal[mask] = p;
        }
        float tolerance = MathF.Max(7, neighbors.Min(v => v.Length()) * .12f);
        var mapping = ideal.Select(p => vertices.FindIndex(v => Vector2.Distance(v, p) < tolerance)).ToArray();
        if (mapping.Any(index => index < 0) || mapping.Distinct().Count() != 8) return false;
        for (int mask = 0; mask < 8; mask++)
        for (int axis = 0; axis < 3; axis++)
        {
            int other = mask ^ (1 << axis);
            var edge = (Math.Min(mapping[mask], mapping[other]), Math.Max(mapping[mask], mapping[other]));
            if (!edges.Contains(edge)) return false;
        }
        result = new(RecognizedShape.Cube, Centroid(vertices), 0, 0, .93f, ideal);
        return true;

        int Vertex(Vector2 point)
        {
            int index = vertices.FindIndex(v => Vector2.Distance(v, point) < 8);
            if (index >= 0) return index;
            vertices.Add(point); return vertices.Count - 1;
        }
    }
}
