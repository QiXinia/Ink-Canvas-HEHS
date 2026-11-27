using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Recognition;
using InkCanvas.Core.Shapes;
using InkCanvas.Extensibility;
using InkCanvas.Ink;

namespace InkCanvas.Modules.ShapeExpansion;

public sealed class ShapeExpansionModule : IApplicationModule, IPolygonExtension
{
    public IReadOnlyList<int> PresetVertexCounts { get; } = Array.AsReadOnly(new[] { 3, 4, 5, 6 });
    public bool SupportsAdvancedSelectionTransforms => true;
    public IReadOnlyList<ShapeKind> AdditionalShapes { get; } = Array.AsReadOnly(new[]
        { ShapeKind.RoundedRectangle, ShapeKind.SuperRoundedRectangle, ShapeKind.RoundedRectangleRing });

    public void Start(IModuleContext context) => context.Export<IPolygonExtension>(this);
    public void Stop() { }

    public Vector2[] RecognizeClosedStroke(InkStroke stroke, float viewScale)
    {
        if (stroke.Count < 4 || !float.IsFinite(viewScale) || viewScale <= 0) return [];
        float diagonal = MathF.Sqrt(stroke.Bounds.Width * stroke.Bounds.Width + stroke.Bounds.Height * stroke.Bounds.Height) * viewScale;
        if (!float.IsFinite(diagonal) || diagonal < ShapeRecognizer.MinSize) return [];
        var origin = stroke.Points[0].Position;
        float closure = Vector2.Distance(origin, stroke.Points[^1].Position) * viewScale;
        if (closure > Math.Clamp(diagonal * .075f, 8, 20)) return [];

        var points = ShapeRecognizer.Resample(stroke.Points.Select(point => (point.Position - origin) * viewScale).ToList(), 2);
        if (points.Count is < 20 or >= 4096) return [];
        if (points[^1] == points[0]) points.RemoveAt(points.Count - 1);
        if (points.Count < 4) return [];

        int farthest = 1;
        for (int i = 2; i < points.Count; i++)
            if (points[i].LengthSquared() > points[farthest].LengthSquared()) farthest = i;
        float tolerance = Math.Clamp(diagonal * .006f, 1.4f, 3);
        var firstHalf = ShapeRecognizer.Simplify(points.GetRange(0, farthest + 1), tolerance);
        var secondHalf = ShapeRecognizer.Simplify(points.Skip(farthest).Append(points[0]).ToList(), tolerance);
        var outline = firstHalf.Take(firstHalf.Count - 1).Concat(secondHalf.Take(secondHalf.Count - 1)).ToArray();
        if (outline.Length is < 4 or > 32) return [];
        var corners = new List<Vector2>();
        for (int i = 0; i < outline.Length; i++)
        {
            var before = outline[i] - outline[(i + outline.Length - 1) % outline.Length];
            var after = outline[(i + 1) % outline.Length] - outline[i];
            if (Vector2.Dot(before, after) < .9511f * before.Length() * after.Length()) corners.Add(outline[i]);
        }
        if (corners.Count is < 4 or > PolygonGeometry.MaximumVertices || !PolygonGeometry.IsSimple(corners)
            || MathF.Abs(StrokeGeometry.SignedArea(corners)) < 100) return [];

        // Measure fit over each original edge interval. Chord shortcuts on curved strokes
        // and jitter that happens to simplify to corners must still pass the line fit.
        var indices = corners.Select(point => points.IndexOf(point)).ToArray();
        var directions = new Vector2[corners.Count];
        var centers = new Vector2[corners.Count];
        for (int i = 0; i < corners.Count; i++)
        {
            int next = (i + 1) % corners.Count;
            var a = corners[i];
            var b = corners[next];
            float length = Vector2.Distance(a, b);
            int samples = (indices[next] - indices[i] + points.Count) % points.Count;
            if (length < 12 || samples < 3) return [];
            float error = 0;
            var center = Vector2.Zero;
            int trim = Math.Max(1, samples / 5);
            int end = samples - trim;
            for (int n = 1; n < samples; n++)
            {
                var point = points[(indices[i] + n) % points.Count];
                float distance = InkStroke.DistanceToSegment(point, a, b);
                if (distance > MathF.Max(2.9f, length * .085f)) return [];
                error += distance;
                if (n >= trim && n <= end) center += point;
            }
            if (error / (samples - 1) > MathF.Max(1.3f, length * .035f)) return [];
            center /= end - trim + 1;
            float xx = 0, xy = 0, yy = 0;
            for (int n = trim; n <= end; n++)
            {
                var offset = points[(indices[i] + n) % points.Count] - center;
                xx += offset.X * offset.X; xy += offset.X * offset.Y; yy += offset.Y * offset.Y;
            }
            float heading = .5f * MathF.Atan2(2 * xy, xx - yy);
            directions[i] = new(MathF.Cos(heading), MathF.Sin(heading));
            centers[i] = center;
        }

        // A true corner has two straight sides whose fitted lines meet at the turn.
        // Partitions of a rounded curve have a virtual intersection well outside the stroke.
        var fitted = new Vector2[corners.Count];
        for (int i = 0; i < corners.Count; i++)
        {
            int previous = (i + corners.Count - 1) % corners.Count;
            float determinant = Cross(directions[previous], directions[i]);
            if (MathF.Abs(determinant) < .15f) return [];
            var crossing = centers[previous] + directions[previous]
                * (Cross(centers[i] - centers[previous], directions[i]) / determinant);
            float distance = float.MaxValue;
            for (int sample = -5; sample <= 5; sample++)
            {
                int index = (indices[i] + sample + points.Count) % points.Count;
                distance = MathF.Min(distance, InkStroke.DistanceToSegment(crossing, points[index], points[(index + 1) % points.Count]));
            }
            if (distance > 2.2f || Vector2.Distance(crossing, corners[i]) > 10) return [];
            fitted[i] = crossing;
        }
        return PolygonGeometry.IsSimple(fitted) ? fitted.Select(point => origin + point / viewScale).ToArray() : [];
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
