using InkCanvas.Core.Ink;
using System.Numerics;

namespace InkCanvas.Core.Recognition;

public enum RecognizedShape { None, Line, Circle, Ellipse, Triangle, Rectangle, Parallelogram, Cube, CoordinateAxes }

public readonly record struct RecognitionResult(
    RecognizedShape Shape,
    Vector2 Center,
    float RadiusX,
    float RadiusY,
    float Confidence,
    Vector2[] Corners)
{
    public static readonly RecognitionResult None =
        new(RecognizedShape.None, Vector2.Zero, 0, 0, 0, Array.Empty<Vector2>());
}

/// <summary>
/// Managed geometric shape recognition.
/// <para>
/// Replaces the original <c>IACore / IALoader / IAWinFX</c> (Tablet PC <c>InkAnalyzer</c>)
/// dependency, which was tied to .NET Framework and forced the whole app to stay x86.
/// Doing the fitting here removes that lock and makes recognition unit-testable.
/// </para>
/// <para>
/// The decision boundaries are our own, so results will not match InkAnalyzer exactly.
/// </para>
/// </summary>
public static partial class ShapeRecognizer
{
    /// <summary>Minimum shape size in pixels; below this we do not guess.</summary>
    public const float MinSize = 40f;

    public static RecognitionResult Recognize(IReadOnlyList<InkStroke> strokes)
    {
        if (strokes is null || strokes.Count == 0) return RecognitionResult.None;

        var pts = new List<Vector2>();
        float arc = 0;
        foreach (var s in strokes)
        {
            arc += s.ArcLength();
            foreach (var p in s.Points) pts.Add(p.Position);
        }
        if (pts.Count < 8 || arc < MinSize) return RecognitionResult.None;

        var bounds = BoundsOf(pts);
        if (bounds.Width < MinSize && bounds.Height < MinSize) return RecognitionResult.None;

        var centroid = Centroid(pts);

        // Straight line first: a very anisotropic point cloud with tiny perpendicular spread.
        if (strokes.Count == 1 && TryLine(pts, out var lineResult)) return lineResult;

        bool closed = Vector2.Distance(pts[0], pts[^1]) < MathF.Max(12f, arc * 0.18f);
        if (!closed) return RecognitionResult.None;

        if (TryCircle(pts, centroid, out float radius, out float circleResidual))
        {
            float aspect = bounds.Height <= 0.01f ? 1f : bounds.Width / bounds.Height;
            if (circleResidual < 0.10f && aspect > 0.82f && aspect < 1.22f)
                return new RecognitionResult(RecognizedShape.Circle, centroid, radius, radius,
                    1f - circleResidual, Array.Empty<Vector2>());
        }

        if (TryEllipse(pts, centroid, bounds, out float erx, out float ery, out float ellipseResidual)
            && ellipseResidual < 0.14f)
        {
            return new RecognitionResult(RecognizedShape.Ellipse, centroid, erx, ery,
                1f - ellipseResidual, Array.Empty<Vector2>());
        }

        if (TryPolygon(pts, out var corners))
        {
            if (corners.Length == 3)
                return new RecognitionResult(RecognizedShape.Triangle, centroid, 0, 0, 0.8f, corners);
            if (corners.Length == 4)
                return new RecognitionResult(RecognizedShape.Rectangle, centroid, 0, 0, 0.8f, corners);
        }

        return RecognitionResult.None;
    }

    // ---- fits -------------------------------------------------------------

    private static bool TryLine(List<Vector2> pts, out RecognitionResult result)
    {
        result = RecognitionResult.None;
        var c = Centroid(pts);
        float sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts)
        {
            float dx = p.X - c.X, dy = p.Y - c.Y;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        float trace = sxx + syy;
        if (trace < 1e-6f) return false;
        float det = sxx * syy - sxy * sxy;
        float disc = MathF.Sqrt(MathF.Max(0, trace * trace * 0.25f - det));
        float major = trace * 0.5f + disc;
        float minor = trace * 0.5f - disc;
        if (major < 1e-6f) return false;

        // Perpendicular RMS spread relative to length.
        float spread = MathF.Sqrt(MathF.Max(0, minor) / pts.Count);
        float length = 2 * MathF.Sqrt(major / pts.Count);
        if (length < MinSize) return false;
        if (spread / length > 0.035f) return false;

        var axis = Vector2.Normalize(new Vector2(sxy, major - sxx));
        if (float.IsNaN(axis.X) || axis.LengthSquared() < 0.5f) axis = new Vector2(1, 0);
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in pts)
        {
            float t = Vector2.Dot(p - c, axis);
            if (t < min) min = t;
            if (t > max) max = t;
        }
        var corners = new[] { c + axis * min, c + axis * max };
        float directLength = Vector2.Distance(pts[0], pts[^1]);
        float pathLength = 0;
        float maxDeviation = MathF.Max(2.5f, (max - min) * 0.02f);
        foreach (var point in pts)
            if (MathF.Abs((point.X - c.X) * axis.Y - (point.Y - c.Y) * axis.X) > maxDeviation) return false;
        var lastProgress = pts[0];
        for (int i = 1; i < pts.Count; i++)
        {
            float distance = Vector2.Distance(lastProgress, pts[i]);
            // Stationary pen packets can jitter while the user holds the endpoint.
            if (distance < 3) continue;
            pathLength += distance;
            lastProgress = pts[i];
        }
        pathLength += Vector2.Distance(lastProgress, pts[^1]);
        // Hooks and a stroke drawn back over itself are handwriting, even if RMS spread is small.
        if (pathLength <= 0 || directLength / pathLength < 0.97f) return false;
        result = new RecognitionResult(RecognizedShape.Line, c, 0, 0, 1f - spread / length, corners);
        return true;
    }

    /// <summary>Kåsa algebraic circle fit; residual is RMS radial error over radius.</summary>
    private static bool TryCircle(List<Vector2> pts, Vector2 guess, out float radius, out float residual)
    {
        radius = 0; residual = float.MaxValue;
        double sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0, sx = 0, sy = 0, sz = 0;
        int n = pts.Count;
        foreach (var p in pts)
        {
            double x = p.X - guess.X, y = p.Y - guess.Y;
            double z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y;
            sxz += x * z; syz += y * z;
            sx += x; sy += y; sz += z;
        }
        // Solve [[sxx,sxy,sx],[sxy,syy,sy],[sx,sy,n]] * [a,b,c] = -[sxz,syz,sz]
        double[,] m = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        double[] rhs = { -sxz, -syz, -sz };
        if (!Solve3(m, rhs, out double a, out double b, out double cc)) return false;

        double cx = -a / 2, cy = -b / 2;
        double r2 = cx * cx + cy * cy - cc;
        if (r2 <= 1) return false;
        double r = Math.Sqrt(r2);

        double err = 0;
        var center = new Vector2((float)(cx + guess.X), (float)(cy + guess.Y));
        foreach (var p in pts)
        {
            double d = Vector2.Distance(p, center) - r;
            err += d * d;
        }
        radius = (float)r;
        residual = (float)(Math.Sqrt(err / n) / r);
        return radius > 1;
    }

    private static bool TryEllipse(List<Vector2> pts, Vector2 c, Rect bounds,
        out float rx, out float ry, out float residual)
    {
        rx = MathF.Max(bounds.Width * 0.5f, 1f);
        ry = MathF.Max(bounds.Height * 0.5f, 1f);
        double err = 0;
        foreach (var p in pts)
        {
            double u = (p.X - c.X) / rx, v = (p.Y - c.Y) / ry;
            double d = Math.Sqrt(u * u + v * v) - 1.0;
            err += d * d;
        }
        residual = (float)Math.Sqrt(err / pts.Count);
        return true;
    }

    /// <summary>Corner detection by turning angle over a smoothed polyline.</summary>
    private static bool TryPolygon(List<Vector2> pts, out Vector2[] corners)
    {
        corners = Array.Empty<Vector2>();
        var simplified = Simplify(pts, 6f);
        if (simplified.Count < 4) return false;

        var found = new List<Vector2>();
        for (int i = 1; i < simplified.Count - 1; i++)
        {
            var v1 = Vector2.Normalize(simplified[i] - simplified[i - 1]);
            var v2 = Vector2.Normalize(simplified[i + 1] - simplified[i]);
            float dot = Math.Clamp(Vector2.Dot(v1, v2), -1f, 1f);
            float angle = MathF.Acos(dot) * 180f / MathF.PI;
            if (angle > 45f)
            {
                if (found.Count == 0 || Vector2.Distance(found[^1], simplified[i]) > 20f)
                    found.Add(simplified[i]);
            }
        }
        // Close the loop: the seam between last and first sample is also a corner candidate.
        if (found.Count is 3 or 4)
        {
            corners = found.ToArray();
            return true;
        }
        return false;
    }

    /// <summary>Ramer–Douglas–Peucker polyline simplification.</summary>
    private static List<Vector2> Simplify(List<Vector2> pts, float epsilon)
    {
        if (pts.Count < 3) return new List<Vector2>(pts);
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            float maxDist = 0; int index = first;
            for (int i = first + 1; i < last; i++)
            {
                float d = InkStroke.DistanceToSegment(pts[i], pts[first], pts[last]);
                if (d > maxDist) { maxDist = d; index = i; }
            }
            if (maxDist > epsilon && index > first && index < last)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }
        var result = new List<Vector2>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    // ---- helpers ----------------------------------------------------------

    private static Vector2 Centroid(List<Vector2> pts)
    {
        var sum = Vector2.Zero;
        foreach (var p in pts) sum += p;
        return sum / pts.Count;
    }

    private static Rect BoundsOf(List<Vector2> pts)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return Rect.FromLTRB(minX, minY, maxX, maxY);
    }

    private static bool Solve3(double[,] m, double[] rhs, out double x, out double y, out double z)
    {
        x = y = z = 0;
        var a = (double[,])m.Clone();
        var b = (double[])rhs.Clone();
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 3; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-12) return false;
            if (pivot != col)
            {
                for (int k = 0; k < 3; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (int r = col + 1; r < 3; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int k = col; k < 3; k++) a[r, k] -= f * a[col, k];
                b[r] -= f * b[col];
            }
        }
        z = b[2] / a[2, 2];
        y = (b[1] - a[1, 2] * z) / a[1, 1];
        x = (b[0] - a[0, 1] * y - a[0, 2] * z) / a[0, 0];
        return !(double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z));
    }
}
