using System;
using System.Collections.Generic;
using System.Numerics;

namespace InkCanvas.Core.Ink;

/// <summary>
/// Immutable contour bundle for one streamed chunk of a calligraphic ink brush stroke.
/// Designed for O(1) incremental live rendering and Win2D GPU geometry exclusion.
/// </summary>
public sealed class CalligraphyChunkContours
{
    public required Vector2[][] WetBody { get; init; }
    public required Vector2[][] GreyDryDrag { get; init; }
    public required Vector2[][] OuterClusters { get; init; }
    public required Vector2[][] CoreClusters { get; init; }
    public required Vector2[][] FeibaiCutouts { get; init; }
}

/// <summary>
/// Pure .NET geometry generator for Chinese calligraphy ink strokes:
/// - Configurable oblique flat-nib projection and recorded hardware-pressure/tilt input
/// - Sustainable ink reservoir floor (continuous long strokes never break into blank gaps)
/// - 5-cluster hierarchical longitudinal split + charcoal-grey dry-drag bands (zero sawtooths)
/// - Bilinear 64x64 Xuan-paper tooth LUT for negative-space feibai (飞白) cutout contours
/// </summary>
public static class CalligraphyBrushGeometry
{
    public const int ChunkPointCount = 24;
    private const int LutSize = 64;
    private const int LutMask = LutSize - 1;
    private static readonly float[] ToothLut = BuildPaperToothLut();

    private readonly record struct SpineSample(
        Vector2 Pos,
        Vector2 Tangent,
        Vector2 Normal,
        float Curvature,
        float ArcLen,
        float Velocity,
        float Radius,
        float InkReserve);

    private readonly record struct RibbonPoint(Vector2 Pos, float HalfWidth, Vector2 Tangent);

    private readonly record struct ClusterSpec(float Eta, float WidthMul, float DrySens, float Reach, bool IsCore);

    private static readonly ClusterSpec[] Clusters =
    {
        new(-0.68f, 0.24f, 0.92f, 0.76f, false),
        new(-0.32f, 0.34f, 0.50f, 0.98f, true),
        new( 0.02f, 0.38f, 0.35f, 1.15f, true),
        new( 0.34f, 0.32f, 0.56f, 0.90f, true),
        new( 0.69f, 0.22f, 1.00f, 0.66f, false),
    };

    private static readonly float[] GreyEtas = { -0.84f, -0.50f, -0.16f, 0.18f, 0.52f, 0.86f };

    /// <summary>
    /// Builds calligraphic contours for a point range [startPointIndex..endPointIndex] (inclusive).
    /// Set <paramref name="isStartChunk"/> / <paramref name="isEndChunk"/> to control entry/exit caps
    /// so consecutive streamed chunks stitch seamlessly with zero overlap seams.
    /// Live and finalized strokes intentionally share the same outline to avoid lift-off flicker.
    /// </summary>
    public static CalligraphyChunkContours BuildChunk(
        InkStroke stroke,
        int startPointIndex,
        int endPointIndex,
        bool isStartChunk,
        bool isEndChunk,
        bool isFinalized,
        float? dryness = null)
    {
        if (stroke is null) throw new ArgumentNullException(nameof(stroke));
        var points = stroke.Points;
        if (points.Count < 2) return null;

        int sIdx = Math.Clamp(startPointIndex, 0, points.Count - 2);
        int eIdx = Math.Clamp(endPointIndex, sIdx + 1, points.Count - 1);

        var attrs = stroke.Attributes;
        var options = attrs.InkWash.Normalize();
        float dry = dryness is float value && float.IsFinite(value) ? Math.Clamp(value, 0, 1) : options.Dryness;
        int seed = attrs.InkWashSeed != 0 ? Math.Abs(attrs.InkWashSeed % 10000) + 17 : DeterministicSeed(stroke.Id);
        float baseR = MathF.Max(attrs.BaseWidth, .05f) * 0.5f;
        float nibAngle = -options.Angle * MathF.PI / 180;
        Vector2 nibVec = new(MathF.Cos(nibAngle), MathF.Sin(nibAngle));
        var spine = BuildSpineSlice(stroke, sIdx, eIdx, baseR, seed, nibVec, options.Flatness);
        if (spine.Count < 2) return null;

        float entryLen = MathF.Min(26f, baseR * 1.9f);

        var wetList = new List<Vector2[]>(2);
        var greyList = new List<Vector2[]>(8);
        var outerList = new List<Vector2[]>(6);
        var coreList = new List<Vector2[]>(6);
        var feibaiList = new List<Vector2[]>(16);

        // 1. Velvety wet ink body (with oblique chisel entry on start chunk)
        var wetPts = new List<RibbonPoint>(spine.Count);
        for (int i = 0; i < spine.Count; i++)
        {
            var pt = spine[i];
            float wetRatio = MathF.Max(0.58f, 0.94f - dry * 0.26f * (0.4f + 0.6f * pt.Velocity));
            wetPts.Add(new RibbonPoint(pt.Pos, pt.Radius * wetRatio, pt.Tangent));
        }
        var wetPoly = BuildRibbonPolygon(wetPts, isStartChunk, isEndChunk, 0.95f, 0.76f, nibVec);
        if (wetPoly.Length >= 3) wetList.Add(wetPoly);

        // 2. Charcoal-grey dry-drag ribbons (strictly tangent-aligned)
        for (int g = 0; g < GreyEtas.Length; g++)
        {
            float gSeed = seed * 0.059f + g * 23.1f;
            float eta = GreyEtas[g] + (Hash1(gSeed) - 0.5f) * 0.12f;
            float absEta = MathF.Abs(eta);
            float reach = 0.58f + 0.42f * Hash1(gSeed + 11f);
            var seg = new List<RibbonPoint>(spine.Count);
            int segStart = 0;

            void FlushGrey(bool atEnd)
            {
                if (seg.Count >= 2)
                {
                    var poly = BuildRibbonPolygon(seg, segStart == 0 && isStartChunk, atEnd && isEndChunk, 1.3f, 0.28f, null);
                    if (poly.Length >= 3) greyList.Add(poly);
                }
                seg.Clear();
            }

            for (int i = 0; i < spine.Count; i++)
            {
                var pt = spine[i];
                var pos = pt.Pos + pt.Normal * (eta * pt.Radius * 0.96f - pt.Curvature * pt.Radius * 0.16f);
                float tooth = PaperToothSmooth(pt.ArcLen / baseR * 15, eta * 16 + seed);
                if (dry > 0 && (pt.Velocity > 0.24f || dry > 0.25f))
                {
                    if (seg.Count == 0) segStart = i;
                    float hw = pt.Radius * 0.125f * (1.12f - 0.44f * absEta) * (0.55f + 0.65f * tooth);
                    seg.Add(new RibbonPoint(pos, hw * dry, pt.Tangent));
                }
                else
                {
                    FlushGrey(false);
                }
            }
            if (isEndChunk && seg.Count >= 2)
            {
                var lastPt = spine[^1];
                var tip = seg[^1];
                float ext = MathF.Min(22f, baseR * 0.92f * reach * (0.5f + 0.5f * lastPt.Velocity));
                if (ext > 2f) seg.Add(new RibbonPoint(tip.Pos + lastPt.Tangent * ext, 0.16f, lastPt.Tangent));
            }
            FlushGrey(true);
        }

        // 3. 5-Cluster hierarchical longitudinal ink fingers (inner 3 core clusters never break)
        for (int c = 0; c < Clusters.Length; c++)
        {
            var cs = Clusters[c];
            float cSeed = seed * 0.031f + c * 17.3f;
            float eta = cs.Eta + (Hash1(cSeed) - 0.5f) * 0.09f;
            var seg = new List<RibbonPoint>(spine.Count);
            int segStart = 0;

            void FlushCluster(bool atEnd)
            {
                if (seg.Count >= 2)
                {
                    var poly = BuildRibbonPolygon(seg, segStart == 0 && isStartChunk, atEnd && isEndChunk, 1.18f, 0.48f, null);
                    if (poly.Length >= 3) (cs.IsCore ? coreList : outerList).Add(poly);
                }
                seg.Clear();
            }

            for (int i = 0; i < spine.Count; i++)
            {
                var pt = spine[i];
                float spread = 0.86f + 0.18f * (0.35f + 0.65f * pt.Velocity);
                var pos = pt.Pos + pt.Normal * (eta * pt.Radius * spread - pt.Curvature * pt.Radius * 0.20f);
                float tooth = PaperToothSmooth(pt.ArcLen / baseR * 15, eta * 16 + seed);
                float dryLoad = pt.InkReserve - dry * cs.DrySens * (0.34f + 0.50f * pt.Velocity) + (tooth - 0.5f) * 0.32f;
                bool touching = cs.IsCore || pt.ArcLen < entryLen || dryLoad >= .46f;

                if (touching)
                {
                    if (seg.Count == 0) segStart = i;
                    float hw = pt.Radius * cs.WidthMul * MathF.Max(0.38f, 1.04f - dry * 0.36f * pt.Velocity + (tooth - 0.5f) * 0.22f);
                    seg.Add(new RibbonPoint(pos, hw, pt.Tangent));
                }
                else
                {
                    FlushCluster(false);
                }
            }
            if (isEndChunk && seg.Count >= 2)
            {
                var lastPt = spine[^1];
                var tip = seg[^1];
                float ext = MathF.Min(20f, baseR * 0.82f * cs.Reach * (0.45f + 0.55f * lastPt.Velocity));
                if (ext > 2f) seg.Add(new RibbonPoint(tip.Pos + lastPt.Tangent * ext, 0.20f, lastPt.Tangent));
            }
            FlushCluster(true);
        }

        // 4. Negative-space longitudinal feibai (飞白) cutout contours for Win2D CombineWith(Exclude)
        if (dry > 0.08f)
        {
            int slitTracks = (int)MathF.Round(7f + dry * 8f);
            for (int k = 0; k < slitTracks; k++)
            {
                float kSeed = seed * 0.083f + k * 31.7f;
                float u = slitTracks == 1 ? 0f : (k / (float)(slitTracks - 1)) * 2f - 1f;
                float eta = u * 0.88f + (Hash1(kSeed) - 0.5f) * (1f / MathF.Max(6f, slitTracks));
                float absEta = MathF.Abs(eta);

                var slitSeg = new List<RibbonPoint>(spine.Count);
                bool carving = false;

                void FlushSlit()
                {
                    if (slitSeg.Count >= 2)
                    {
                        var poly = BuildRibbonPolygon(slitSeg, false, false, 1.35f, 0.16f, null);
                        if (poly.Length >= 3) feibaiList.Add(poly);
                    }
                    slitSeg.Clear();
                }

                for (int i = 0; i < spine.Count; i++)
                {
                    var pt = spine[i];
                    if (pt.ArcLen < entryLen * 0.95f)
                    {
                        if (carving) { carving = false; FlushSlit(); }
                        continue;
                    }
                    var pos = pt.Pos + pt.Normal * (eta * pt.Radius * 0.92f - pt.Curvature * pt.Radius * 0.18f);
                    float tooth = PaperToothSmooth(pt.ArcLen / baseR * 15, eta * 16 + seed);
                    float streakN = Noise1D(pt.ArcLen * 0.07f, kSeed + 19f);
                    float carveScore = dry * (0.36f + 0.58f * pt.Velocity + 0.24f * absEta + 0.18f * (1f - pt.InkReserve))
                        + (0.5f - tooth) * 0.56f + (streakN - 0.5f) * 0.28f;

                    bool nextCarving = carveScore > .48f;
                    if (carving && !nextCarving) FlushSlit();
                    carving = nextCarving;

                    if (carving)
                    {
                        float slitW = MathF.Min(pt.Radius * 0.17f,
                            pt.Radius * (0.045f + 0.10f * dry * (0.4f + 0.6f * pt.Velocity)) * (0.6f + 0.7f * (1f - tooth)));
                        slitSeg.Add(new RibbonPoint(pos, slitW, pt.Tangent));
                    }
                }
                FlushSlit();
            }
        }

        return new CalligraphyChunkContours
        {
            WetBody = wetList.ToArray(),
            GreyDryDrag = greyList.ToArray(),
            OuterClusters = outerList.ToArray(),
            CoreClusters = coreList.ToArray(),
            FeibaiCutouts = feibaiList.ToArray(),
        };
    }

    private static List<SpineSample> BuildSpineSlice(
        InkStroke stroke,
        int startIdx,
        int endIdx,
        float baseR,
        int seed,
        Vector2 nibVec,
        float flatness)
    {
        var points = stroke.Points;
        var spine = new List<SpineSample>(Math.Max(8, (endIdx - startIdx) * 3));
        float totalLength = stroke.ArcLength();
        float entryLen = MathF.Min(26f, baseR * 1.9f);
        float exitLen = MathF.Min(totalLength * .45f, MathF.Max(24f, baseR * 3.2f));
        // Every input interval has its own sampling lattice and analytic tangent. Adjacent
        // chunks consequently share the exact same cross-section, independent of draw history.
        for (int i = startIdx; i < endIdx; i++)
        {
            var p0 = points[i > 0 ? i - 1 : 0].Position;
            var p1 = points[i].Position;
            var p2 = points[i + 1].Position;
            var p3 = points[i + 2 < points.Count ? i + 2 : points.Count - 1].Position;

            float w1 = MathF.Max(points[i].Width, .05f);
            float w2 = MathF.Max(points[i + 1].Width, .05f);
            float v1 = EstimateSpeed(points, i);
            float v2 = EstimateSpeed(points, i + 1);
            float segDist = Vector2.Distance(p1, p2);
            float arcBefore = stroke.ArcLengthAt(i);
            int steps = Math.Clamp((int)MathF.Ceiling(segDist / 3f), 1, 128);
            for (int s = i == startIdx ? 0 : 1; s <= steps; s++)
            {
                float t = s / (float)steps;
                float t2 = t * t, t3 = t2 * t;
                var pos = 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
                var delta = .5f * ((-p0 + p2) + 2 * (2 * p0 - 5 * p1 + 4 * p2 - p3) * t
                    + 3 * (-p0 + 3 * p1 - 3 * p2 + p3) * t2);
                float mag = delta.Length();
                var tangent = mag > 1e-4f ? delta / mag : Vector2.Normalize(segDist > .0001f ? p2 - p1 : Vector2.UnitX);
                var normal = new Vector2(-tangent.Y, tangent.X);
                float k1 = CurvatureAt(points, i), k2 = CurvatureAt(points, i + 1);
                float kappa = k1 + (k2 - k1) * t;
                float arc = arcBefore + segDist * t;
                float dotN = Vector2.Dot(nibVec, normal);
                float dotT = Vector2.Dot(nibVec, tangent);
                float minor = 1 - flatness;
                float oblique = MathF.Sqrt(dotN * dotN + minor * minor * dotT * dotT);
                var tilt = Vector2.Lerp(new(points[i].TiltX, points[i].TiltY), new(points[i + 1].TiltX, points[i + 1].TiltY), t);
                float tiltStrength = Math.Clamp(tilt.Length() / 75, 0, 1);
                float tiltProjection = tilt.LengthSquared() > .0001f ? MathF.Abs(Vector2.Dot(Vector2.Normalize(tilt), normal)) : 0;
                oblique *= 1 + tiltStrength * .35f * tiltProjection;
                float env = arc < entryLen ? .56f + .44f * MathF.Sin(arc / MathF.Max(1, entryLen) * MathF.PI * .5f) : 1;
                if (totalLength > 12 && totalLength - arc < exitLen)
                    env *= MathF.Pow(MathF.Max(.05f, (totalLength - arc) / MathF.Max(1, exitLen)), .68f);
                float breath = 1 + (Noise1D(arc / baseR * .36f, seed * .01f + 3) - .5f) * .07f;
                float radius = MathF.Max(.025f, (w1 + (w2 - w1) * t) * .5f * oblique * env
                    * (1 + MathF.Min(.15f, MathF.Abs(kappa) * .30f)) * breath);
                float reserve = .76f + .28f * MathF.Exp(-arc / (baseR * 46.67f));
                spine.Add(new SpineSample(pos, tangent, normal, kappa, arc, v1 + (v2 - v1) * t, radius, reserve));
            }
        }
        return spine;
    }

    private static float EstimateSpeed(IReadOnlyList<InkPoint> points, int index)
    {
        float speed = 0, weight = 0;
        for (int offset = 0; offset < 4 && index - offset > 0; offset++)
        {
            int i = index - offset;
            float dist = Vector2.Distance(points[i - 1].Position, points[i].Position);
            float dt = MathF.Max(1f, points[i].TimeMs - points[i - 1].TimeMs);
            float w = 1f / (offset + 1);
            speed += MathF.Min(1.5f, dist / dt * .8f) * w;
            weight += w;
        }
        return weight > 0 ? speed / weight : .14f;
    }

    private static float CurvatureAt(IReadOnlyList<InkPoint> points, int index)
    {
        if (index <= 0 || index >= points.Count - 1) return 0;
        var a = points[index].Position - points[index - 1].Position;
        var b = points[index + 1].Position - points[index].Position;
        float denominator = MathF.Sqrt(a.LengthSquared() * b.LengthSquared());
        return denominator > .0001f ? Math.Clamp((a.X * b.Y - a.Y * b.X) / denominator * 1.6f, -.42f, .42f) : 0;
    }
    private static Vector2[] BuildRibbonPolygon(
        List<RibbonPoint> pts,
        bool capStart,
        bool capEnd,
        float tipExtend,
        float entryRound,
        Vector2? nibVec)
    {
        int n = pts.Count;
        if (n < 2) return Array.Empty<Vector2>();

        var left = new Vector2[n];
        var right = new Vector2[n];

        for (int i = 0; i < n; i++)
        {
            var t = pts[i].Tangent;
            var normal = new Vector2(-t.Y, t.X);

            float taper = 1f;
            if (capStart)
            {
                if (i == 0) taper *= entryRound;
                else if (i == 1 && n >= 4) taper *= MathF.Min(1f, entryRound + 0.44f);
            }
            if (capEnd)
            {
                if (i == n - 1) taper *= 0.08f;
                else if (i == n - 2 && n >= 4) taper *= 0.50f;
            }

            float hw = MathF.Max(0.001f, pts[i].HalfWidth * taper);
            float slant = (capStart && nibVec.HasValue && i <= 2)
                ? Vector2.Dot(nibVec.Value, t) * hw * 0.55f * ((3 - i) / 3f)
                : 0f;
            left[i] = pts[i].Pos + normal * hw + t * slant;
            right[i] = pts[i].Pos - normal * hw - t * slant;
        }

        var poly = new List<Vector2>(n * 2 + 4);
        if (capStart)
        {
            var fd = pts[1].Pos - pts[0].Pos;
            float fm = MathF.Max(1e-4f, fd.Length());
            poly.Add(pts[0].Pos - (fd / fm) * MathF.Min(5f, pts[0].HalfWidth * (0.50f + entryRound * 0.55f)));
        }
        for (int i = 0; i < n; i++) poly.Add(left[i]);
        if (capEnd)
        {
            var ld = pts[n - 1].Pos - pts[n - 2].Pos;
            float lm = MathF.Max(1e-4f, ld.Length());
            poly.Add(pts[n - 1].Pos + (ld / lm) * MathF.Min(9.5f, pts[n - 1].HalfWidth * 2.3f * tipExtend));
        }
        for (int i = n - 1; i >= 0; i--) poly.Add(right[i]);

        // Ensure consistent positive winding for Win2D Winding-rule union
        if (StrokeGeometry.SignedArea(poly) < 0f) poly.Reverse();
        return poly.ToArray();
    }

    private static int DeterministicSeed(Guid id)
    {
        int h = id.GetHashCode();
        return Math.Abs(h == int.MinValue ? 137 : h) % 10000 + 17;
    }

    private static float Hash1(float n)
    {
        float x = MathF.Sin(n * 127.1f + 311.7f) * 43758.5453f;
        return x - MathF.Floor(x);
    }

    private static float Noise1D(float x, float seed)
    {
        float i = MathF.Floor(x);
        float f = x - i;
        float u = f * f * (3f - 2f * f);
        return Hash1(i + seed * 57.3f) * (1f - u) + Hash1(i + 1f + seed * 57.3f) * u;
    }

    private static float[] BuildPaperToothLut()
    {
        var lut = new float[LutSize * LutSize];
        static float H2(int ix, int iy)
        {
            float x = MathF.Sin((ix & LutMask) * 127.1f + (iy & LutMask) * 311.7f) * 43758.5453f;
            return x - MathF.Floor(x);
        }
        static float Sample(float sx, float sy)
        {
            int ix = (int)MathF.Floor(sx), iy = (int)MathF.Floor(sy);
            float fx = sx - ix, fy = sy - iy;
            float ux = fx * fx * (3f - 2f * fx);
            float uy = fy * fy * (3f - 2f * fy);
            return (H2(ix, iy) * (1f - ux) + H2(ix + 1, iy) * ux) * (1f - uy)
                 + (H2(ix, iy + 1) * (1f - ux) + H2(ix + 1, iy + 1) * ux) * uy;
        }
        for (int y = 0; y < LutSize; y++)
            for (int x = 0; x < LutSize; x++)
                lut[(y << 6) | x] = 0.62f * Sample(x * 0.25f, y * 0.25f) + 0.38f * Sample(x * 0.5f, y * 0.5f);
        return lut;
    }

    private static float PaperToothSmooth(float x, float y)
    {
        float fx = x * 0.34f, fy = y * 0.34f;
        int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
        float tx = fx - ix, ty = fy - iy;
        int x0 = ix & LutMask, x1 = (ix + 1) & LutMask;
        int y0 = (iy & LutMask) << 6, y1 = ((iy + 1) & LutMask) << 6;
        float a = ToothLut[y0 | x0], b = ToothLut[y0 | x1];
        float c = ToothLut[y1 | x0], d = ToothLut[y1 | x1];
        return (a + (b - a) * tx) * (1f - ty) + (c + (d - c) * tx) * ty;
    }
}
