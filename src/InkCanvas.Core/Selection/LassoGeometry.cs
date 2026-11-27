using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Selection;

/// <summary>A closed freehand polygon in view DIPs, using even/odd fill for self-intersections.</summary>
public sealed class LassoGeometry
{
    private readonly Vector2[] _points;
    public Rect Bounds { get; }
    public bool IsValid { get; }

    public LassoGeometry(IEnumerable<Vector2> points)
    {
        _points = points.Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).ToArray();
        if (_points.Length < 3) return;
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        double area = 0;
        for (int i = 0; i < _points.Length; i++)
        {
            var p = _points[i];
            left = MathF.Min(left, p.X); top = MathF.Min(top, p.Y);
            right = MathF.Max(right, p.X); bottom = MathF.Max(bottom, p.Y);
            if (i > 0) area += Math.Abs(Cross(p - _points[0], _points[(i + 1) % _points.Length] - _points[0]));
        }
        Bounds = new Rect(left, top, right, bottom);
        // Ignore taps and straight swipes. Absolute triangle areas also admit figure-eight loops.
        IsValid = Bounds.Width >= 4 && Bounds.Height >= 4 && area >= 16;
    }

    public bool Contains(Vector2 point)
    {
        if (!IsValid || !Bounds.Contains(point)) return false;
        bool inside = false;
        for (int i = 0, j = _points.Length - 1; i < _points.Length; j = i++)
        {
            var a = _points[j]; var b = _points[i];
            if (OnSegment(point, a, b)) return true;
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) inside = !inside;
        }
        return inside;
    }

    public bool IntersectsSegment(Vector2 a, Vector2 b)
    {
        if (!IsValid || !Bounds.Intersects(new Rect(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y)))) return false;
        if (Contains(a) || Contains(b)) return true;
        for (int i = 0; i < _points.Length; i++)
            if (SegmentsIntersect(a, b, _points[i], _points[(i + 1) % _points.Length])) return true;
        return false;
    }

    public bool IntersectsStroke(InkStroke stroke, Matrix3x2 worldToView)
    {
        if (!IsValid || stroke.Count == 0 || !Bounds.Intersects(TransformBounds(stroke.Bounds, worldToView))) return false;
        if (stroke.ShapeSource?.Geometry?.IsFill == true)
            return (Matrix3x2.Invert(worldToView, out var inverse) && stroke.DistanceTo(Vector2.Transform(_points[0], inverse)) == 0)
                || stroke.FillContours().Any(range => IntersectsContour(range.Start, range.Count, closed: true));
        return IntersectsContour(0, stroke.Count, closed: false);

        bool IntersectsContour(int start, int count, bool closed)
        {
            var first = Vector2.Transform(stroke.Points[start].Position, worldToView);
            if (Contains(first)) return true;
            var previous = first;
            for (int i = start + 1; i < start + count; i++)
            {
                var current = Vector2.Transform(stroke.Points[i].Position, worldToView);
                if (IntersectsSegment(previous, current)) return true;
                previous = current;
            }
            return closed && IntersectsSegment(previous, first);
        }
    }

    public bool IntersectsFrame(SelectionFrame frame, Matrix3x2 worldToView)
    {
        if (!IsValid || frame.Bounds.IsEmpty) return false;
        var corners = frame.Corners(worldToView);
        for (int i = 0; i < corners.Length; i++)
            if (IntersectsSegment(corners[i], corners[(i + 1) % corners.Length])) return true;
        // The lasso can lie wholly inside a filled image/video rectangle.
        return frame.Contains(_points[0], worldToView);
    }

    private static Rect TransformBounds(Rect bounds, Matrix3x2 transform)
    {
        Span<Vector2> corners = stackalloc Vector2[] { new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top),
            new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom) };
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (var corner in corners)
        {
            var p = Vector2.Transform(corner, transform);
            left = MathF.Min(left, p.X); top = MathF.Min(top, p.Y);
            right = MathF.Max(right, p.X); bottom = MathF.Max(bottom, p.Y);
        }
        return new Rect(left, top, right, bottom);
    }

    private static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;

    private static bool OnSegment(Vector2 p, Vector2 a, Vector2 b)
        => Math.Abs(Cross(b - a, p - a)) <= 0.0001
            && p.X >= MathF.Min(a.X, b.X) - 0.0001f && p.X <= MathF.Max(a.X, b.X) + 0.0001f
            && p.Y >= MathF.Min(a.Y, b.Y) - 0.0001f && p.Y <= MathF.Max(a.Y, b.Y) + 0.0001f;

    internal static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        double abC = Cross(b - a, c - a), abD = Cross(b - a, d - a);
        double cdA = Cross(d - c, a - c), cdB = Cross(d - c, b - c);
        return ((abC > 0 && abD < 0) || (abC < 0 && abD > 0))
                && ((cdA > 0 && cdB < 0) || (cdA < 0 && cdB > 0))
            || OnSegment(c, a, b) || OnSegment(d, a, b) || OnSegment(a, c, d) || OnSegment(b, c, d);
    }
}
