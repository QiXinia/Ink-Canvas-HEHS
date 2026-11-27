using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;

namespace InkCanvas.Core.Shapes;

public readonly record struct PolygonAngle(Vector2 Vertex, Vector2 NextEdge, float Degrees, int Winding);

public static class PolygonGeometry
{
    public const int MaximumVertices = 16;

    public static bool IsSimple(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 3 || MathF.Abs(StrokeGeometry.SignedArea(points)) <= .0001f) return false;
        for (int i = 0; i < points.Count; i++)
        {
            int next = (i + 1) % points.Count;
            var edge = points[next] - points[i];
            var following = points[(next + 1) % points.Count] - points[next];
            if (edge.LengthSquared() < .0001f || MathF.Abs(Cross(edge, following)) < .0001f && Vector2.Dot(edge, following) < 0)
                return false;
            for (int j = i + 2; j < points.Count; j++)
            {
                int end = (j + 1) % points.Count;
                if (end != i && LassoGeometry.SegmentsIntersect(points[i], points[next], points[j], points[end])) return false;
            }
        }
        return true;
    }

    public static PolygonAngle[] Measure(IReadOnlyList<Vector2> points)
    {
        if (!IsSimple(points)) return [];
        int winding = Math.Sign(StrokeGeometry.SignedArea(points));
        var angles = new PolygonAngle[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var vertex = points[i];
            var a = Vector2.Normalize(points[(i + 1) % points.Count] - vertex);
            var b = Vector2.Normalize(points[(i + points.Count - 1) % points.Count] - vertex);
            float angle = MathF.Atan2(winding * Cross(a, b), Vector2.Dot(a, b));
            if (angle < 0) angle += MathF.Tau;
            angles[i] = new(vertex, a, angle * 180 / MathF.PI, winding);
        }
        return angles;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
