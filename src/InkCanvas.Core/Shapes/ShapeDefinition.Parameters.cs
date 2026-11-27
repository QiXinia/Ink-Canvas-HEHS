using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Shapes;

public sealed partial record ShapeDefinition
{
    private const float MinimumSweep = MathF.PI / 180;

    private float SectorSweep => Math.Clamp(Parameters.Length > 4 ? Parameters[4] : ShapeGenerator.DefaultSectorSweep,
        MinimumSweep, MathF.Tau - MinimumSweep);

    private float FrustumScale => Math.Clamp(Parameters.Length > (Construction == ShapeConstruction.Solid ? 6 : 4)
        ? Parameters[Construction == ShapeConstruction.Solid ? 6 : 4] : ShapeGenerator.DefaultFrustumTopScale, .08f, .98f);

    private (Vector2 Low, Vector2 High, float Radius, float Inset) RoundedBounds()
    {
        var low = Vector2.Min(Point(0), Point(2));
        var high = Vector2.Max(Point(0), Point(2));
        float half = MathF.Min(high.X - low.X, high.Y - low.Y) / 2;
        float radius = Math.Clamp(Parameters.Length > 4 ? Parameters[4] : half * (Kind == ShapeKind.SuperRoundedRectangle ? .75f : .48f), 0, half);
        float inset = Math.Clamp(Parameters.Length > 5 ? Parameters[5] : half * .24f, half * .04f, half * .9f);
        return (low, high, radius, inset);
    }

    private Vector2[] ParameterizedControls()
    {
        var start = Point(0);
        var end = Point(2);
        if (Kind == ShapeKind.Sector)
            return [start, end, Vector2.Transform(end, Matrix3x2.CreateRotation(SectorSweep, start))];
        if (ShapeGenerator.IsRoundedRectangle(Kind))
        {
            var (low, high, radius, inset) = RoundedBounds();
            var corner = low + new Vector2(radius, 0);
            // Keep a zero-radius handle distinct from the top-left sizing handle.
            corner.X = MathF.Max(corner.X, low.X + (high.X - low.X) * .055f);
            return Kind == ShapeKind.RoundedRectangleRing
                ? [start, end, corner, new((low.X + high.X) / 2, high.Y - inset)]
                : [start, end, corner];
        }
        if (Kind == ShapeKind.ConeFrustum && Construction == ShapeConstruction.Bounds)
        {
            var top = ShapeGenerator.FrustumTop(start, end, FrustumScale);
            return [start, end, top.Center + new Vector2(top.Radius.X, 0)];
        }
        if (ShapeGenerator.IsPolygonalFrustum(Kind) && Construction == ShapeConstruction.Solid)
        {
            var top = (start + end) / 2 + Point(4);
            return [start, end, top, top + new Vector2(MathF.Abs(end.X - start.X) / 2 * FrustumScale, 0)];
        }
        return null;
    }

    private ShapeDefinition MoveParameterizedControl(int index, Vector2 local)
    {
        var start = Point(0);
        var end = Point(2);
        if (Kind == ShapeKind.Sector)
        {
            if (index == 0) { end += local - start; start = local; }
            if (index == 1)
            {
                if (Vector2.DistanceSquared(start, local) < .0001f) return this;
                end = local;
            }
            float sweep = SectorSweep;
            if (index == 2)
            {
                var from = end - start;
                var to = local - start;
                if (to.LengthSquared() < .0001f) return this;
                float next = MathF.Atan2(from.X * to.Y - from.Y * to.X, Vector2.Dot(from, to));
                if (next < 0) next += MathF.Tau;
                sweep = Math.Clamp(next, MinimumSweep, MathF.Tau - MinimumSweep);
            }
            return this with { Parameters = [start.X, start.Y, end.X, end.Y, sweep] };
        }
        if (ShapeGenerator.IsRoundedRectangle(Kind))
        {
            var (low, high, radius, inset) = RoundedBounds();
            if (index == 0) start = local;
            if (index == 1) end = local;
            if (index == 2) radius = local.X - low.X;
            if (index == 3) inset = high.Y - local.Y;
            float half = MathF.Min(MathF.Abs(end.X - start.X), MathF.Abs(end.Y - start.Y)) / 2;
            if (half < .005f) return this;
            return this with { Parameters = [start.X, start.Y, end.X, end.Y, Math.Clamp(radius, 0, half),
                Math.Clamp(inset, half * .04f, half * .9f)] };
        }
        if (Kind == ShapeKind.ConeFrustum && Construction == ShapeConstruction.Bounds)
        {
            if (index == 0) start = local;
            if (index == 1) end = local;
            if (MathF.Min(MathF.Abs(end.X - start.X), MathF.Abs(end.Y - start.Y)) < .01f) return this;
            float scale = index == 2 ? TopScale(start, end, local.X) : FrustumScale;
            return this with { Parameters = [start.X, start.Y, end.X, end.Y, scale] };
        }
        if (ShapeGenerator.IsPolygonalFrustum(Kind) && Construction == ShapeConstruction.Solid)
        {
            var offset = Point(4);
            if (index == 0) start = local;
            if (index == 1) end = local;
            if (index == 2) offset = local - (start + end) / 2;
            if (MathF.Min(MathF.Abs(end.X - start.X), MathF.Abs(end.Y - start.Y)) < .01f || offset.LengthSquared() < .0001f) return this;
            float scale = index == 3 ? TopScale(start + offset, end + offset, local.X) : FrustumScale;
            return this with { Parameters = [start.X, start.Y, end.X, end.Y, offset.X, offset.Y, scale] };
        }
        return null;
    }

    private static float TopScale(Vector2 start, Vector2 end, float x)
        => Math.Clamp((2 * x - start.X - end.X) / MathF.Abs(end.X - start.X), .08f, .98f);

    private List<InkStroke> GenerateParameterized(StrokeAttributes attributes)
    {
        if (Kind == ShapeKind.Sector) return ShapeGenerator.GenerateSector(Point(0), Point(2), SectorSweep, attributes);
        if (ShapeGenerator.IsRoundedRectangle(Kind))
        {
            var (low, high, radius, inset) = RoundedBounds();
            return ShapeGenerator.GenerateRoundedRectangle(Kind, low, high, radius, inset, attributes);
        }
        if (Kind == ShapeKind.ConeFrustum && Construction == ShapeConstruction.Bounds)
            return ShapeGenerator.GenerateFrustum(Point(0), Point(2), attributes, FrustumScale);
        if (ShapeGenerator.IsPolygonalFrustum(Kind) && Construction == ShapeConstruction.Solid)
            return ShapeGenerator.GenerateSolidContours(Kind, Point(0), Point(2), Point(4), attributes, FrustumScale);
        return null;
    }
}
