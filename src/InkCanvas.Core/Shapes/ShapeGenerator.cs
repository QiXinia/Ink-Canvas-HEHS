using InkCanvas.Core.Ink;
using System.Numerics;

namespace InkCanvas.Core.Shapes;

/// <summary>
/// The preset shape catalogue, ported from the original <c>drawingShapeMode</c> switch.
/// </summary>
public enum ShapeKind
{
    Line,
    DashedLine,
    DotLine,
    Arrow,
    ParallelLine,

    /// <summary>Both axes extend either side of the origin (original mode 11).</summary>
    CoordinateBoth,
    /// <summary>x one-way, y both (mode 12).</summary>
    CoordinateXOneWay,
    /// <summary>x both, y one-way (mode 13).</summary>
    CoordinateYOneWay,
    /// <summary>First quadrant only (mode 14).</summary>
    CoordinateFirstQuadrant,
    /// <summary>3D axes with a 45° z-axis (mode 17).</summary>
    Coordinate3D,

    Rectangle,
    RectangleFromCenter,
    Ellipse,
    Circle,
    EllipseFromCenter,
    EllipseWithFoci,
    DashedCircle,

    Hyperbola,
    HyperbolaWithFoci,
    ParabolaRight,
    ParabolaWithFoci,
    ParabolaUp,

    Cylinder,
    Cone,
    Cuboid,
    CircleFromCenter,
    DashedCircleFromCenter,
    HyperbolaWithAsymptotes,
    HyperbolaWithFociAndAsymptotes,
    EllipseWithFociFromCenter,
    ParabolaUpWithFoci,
    Tetrahedron,
    Square,
    Triangle,
    RightTriangle,
    Diamond,
    Parallelogram,
    Trapezoid,
    Cube,
    Sphere,
    Hemisphere,
    TriangularPrism,
    PentagonalPrism,
    HexagonalPrism,
    SquarePyramid,
    ConeFrustum,
    /// <summary>Pointer down sets the origin, pointer up sets the two axis extents.</summary>
    CoordinateTwoPoint,
    Polygon,
    Sector,
    SquareFrustum,
    RoundedRectangle,
    SuperRoundedRectangle,
    RoundedRectangleRing,
    TriangularFrustum,
    PentagonalFrustum,
    HexagonalFrustum,
}

/// <summary>
/// Builds renderable contours with a shared editable construction definition. Shapes retain
/// their type, parameters and control points through the existing ink persistence paths.
/// <para>
/// Rectangle, circle and ellipse use two-point bounds. Explicit center variants retain the
/// center-and-radius workflow. Cuboid and hyperbola use a second drag to set depth or curvature.
/// </para>
/// </summary>
public static partial class ShapeGenerator
{
    public static bool IsHyperbola(ShapeKind kind) => kind is ShapeKind.Hyperbola or ShapeKind.HyperbolaWithFoci
        or ShapeKind.HyperbolaWithAsymptotes or ShapeKind.HyperbolaWithFociAndAsymptotes;
    public static bool HasHyperbolaFoci(ShapeKind kind) => kind is ShapeKind.HyperbolaWithFoci or ShapeKind.HyperbolaWithFociAndAsymptotes;
    public static bool HasHyperbolaAsymptotes(ShapeKind kind) => kind is ShapeKind.HyperbolaWithAsymptotes or ShapeKind.HyperbolaWithFociAndAsymptotes;
    public static bool IsRoundedRectangle(ShapeKind kind) => kind is ShapeKind.RoundedRectangle
        or ShapeKind.SuperRoundedRectangle or ShapeKind.RoundedRectangleRing;
    /// <summary>Full-ellipse sampling density kept from the verified port (sub-pixel chord error).</summary>
    private const int FullEllipseSegments = 72;

    /// <summary>Dashed line: dash 5px, period 5*2.76 = 13.8px (upstream MW_ShapeDrawing.cs:1419-1443).</summary>
    public const float DashLength = 5f;
    public const float DashPeriod = 5f * 2.76f;

    /// <summary>Dot line: single-point dots every 3*2.76 = 8.28px at pressure 0.8 (upstream:1445-1467).</summary>
    public const float DotPeriod = 3f * 2.76f;
    public const float DotPressure = 0.8f;

    /// <summary>Angular dash on ellipses: width 0.05π, period 0.05π*1.66 = 0.083π (upstream:1329-1373).</summary>
    public const float EllipseDashStep = 0.05f;
    public const float EllipseDashPeriod = 0.05f * 1.66f;

    /// <summary>Cylinder/cone ellipse minor/major ratio = 1/2.646 of the WIDTH (upstream:1096-1097,1153-1154).</summary>
    public const float SolidEllipseRatio = 2.646f;

    public static List<InkStroke> Generate(ShapeKind kind, Vector2 start, Vector2 end, StrokeAttributes attrs)
    {
        if (kind == ShapeKind.Polygon) return GeneratePolygon(5, start, end, attrs);
        if (kind == ShapeKind.Sector || IsRoundedRectangle(kind))
            return ShapeDefinition.FromBounds(kind, start, end, []).Generate(attrs ?? new(), Guid.NewGuid());
        var contours = GenerateContours(kind, start, end, attrs);
        return ShapeDefinition.Attach(contours, ShapeDefinition.FromBounds(kind, start, end, contours));
    }

    public static List<InkStroke> GeneratePolygon(int count, Vector2 start, Vector2 end, StrokeAttributes attrs)
    {
        var radius = Vector2.Abs(end - start) / 2;
        if (radius.X < .01f || radius.Y < .01f) return new();
        var center = (start + end) / 2;
        count = Math.Clamp(count, 3, PolygonGeometry.MaximumVertices);
        var vertices = new Vector2[count];
        float phase = count == 4 ? -3 * MathF.PI / 4 : -MathF.PI / 2;
        for (int i = 0; i < vertices.Length; i++)
        {
            var (sin, cos) = MathF.SinCos(phase + MathF.Tau * i / vertices.Length);
            vertices[i] = center + new Vector2(cos, sin) * radius;
        }
        return ShapeDefinition.FromPolygon(ShapeKind.Polygon, vertices).Generate(attrs, Guid.NewGuid());
    }

    internal static List<InkStroke> GenerateContours(ShapeKind kind, Vector2 start, Vector2 end, StrokeAttributes attrs)
    {
        var result = new List<InkStroke>();
        var a = attrs ?? new StrokeAttributes();
        float w = end.X - start.X, h = end.Y - start.Y;
        var center = (start + end) * 0.5f;
        float rx = MathF.Abs(w) * 0.5f, ry = MathF.Abs(h) * 0.5f;

        switch (kind)
        {
            case ShapeKind.Cube:
            case ShapeKind.TriangularPrism:
            case ShapeKind.PentagonalPrism:
            case ShapeKind.HexagonalPrism:
            case ShapeKind.SquarePyramid:
            case ShapeKind.SquareFrustum:
            case ShapeKind.TriangularFrustum:
            case ShapeKind.PentagonalFrustum:
            case ShapeKind.HexagonalFrustum:
                result = GenerateSolidBase(kind, start, end, a);
                break;
            case ShapeKind.Sphere:
            case ShapeKind.Hemisphere:
            case ShapeKind.ConeFrustum:
                result = GenerateRoundSolid(kind, start, end, a);
                break;
            case ShapeKind.Line:
                result.Add(Stroke(a, start, end));
                break;

            case ShapeKind.DashedLine:
                result.AddRange(Dashed(a, start, end, DashLength, DashPeriod - DashLength));
                break;

            case ShapeKind.DotLine:
                result.AddRange(Dots(a, start, end));
                break;

            case ShapeKind.Arrow:
                result.Add(Arrow(a, start, end));
                break;

            case ShapeKind.ParallelLine:
                result.AddRange(ParallelLines(a, start, end));
                break;

            case ShapeKind.CoordinateBoth:
                result.Add(AxisArrow(a, new Vector2(2 * start.X - (end.X - 20), start.Y), new Vector2(end.X, start.Y)));
                result.Add(AxisArrow(a, new Vector2(start.X, 2 * start.Y - (end.Y + 20)), new Vector2(start.X, end.Y)));
                break;

            case ShapeKind.CoordinateTwoPoint:
                if (MathF.Abs(w) < 0.01f || MathF.Abs(h) < 0.01f) break;
                result.Add(AxisArrow(a, start, new Vector2(end.X, start.Y)));
                result.Add(AxisArrow(a, start, new Vector2(start.X, end.Y)));
                break;

            case ShapeKind.CoordinateXOneWay:
                if (MathF.Abs(w) < 0.01f) break;
                result.Add(AxisArrow(a, new Vector2(start.X + MathF.Sign(-w) * 25, start.Y), new Vector2(end.X, start.Y)));
                result.Add(AxisArrow(a, new Vector2(start.X, 2 * start.Y - (end.Y + 20)), new Vector2(start.X, end.Y)));
                break;

            case ShapeKind.CoordinateYOneWay:
                if (MathF.Abs(h) < 0.01f) break;
                result.Add(AxisArrow(a, new Vector2(2 * start.X - (end.X - 20), start.Y), new Vector2(end.X, start.Y)));
                result.Add(AxisArrow(a, new Vector2(start.X, start.Y + MathF.Sign(-h) * 25), new Vector2(start.X, end.Y)));
                break;

            case ShapeKind.CoordinateFirstQuadrant:
                if (MathF.Abs(w) < 0.01f || MathF.Abs(h) < 0.01f) break;
                result.Add(AxisArrow(a, new Vector2(start.X + MathF.Sign(-w) * 25, start.Y), new Vector2(end.X, start.Y)));
                result.Add(AxisArrow(a, new Vector2(start.X, start.Y + MathF.Sign(-h) * 25), new Vector2(start.X, end.Y)));
                break;

            case ShapeKind.Coordinate3D:
                {
                    result.Add(AxisArrow(a, start, new Vector2(start.X + MathF.Abs(w), start.Y)));
                    result.Add(AxisArrow(a, start, new Vector2(start.X, start.Y - MathF.Abs(h))));
                    float d = (MathF.Abs(w) + MathF.Abs(h)) * 0.5f;
                    result.Add(AxisArrow(a, start, new Vector2(start.X - d / 1.76f, start.Y + d / 1.76f)));
                    break;
                }

            case ShapeKind.Rectangle:
                result.Add(Stroke(a, start, new Vector2(start.X, end.Y), end, new Vector2(end.X, start.Y), start));
                break;

            case ShapeKind.Square:
                float side = MathF.Max(MathF.Abs(w), MathF.Abs(h));
                var opposite = start + new Vector2(w < 0 ? -side : side, h < 0 ? -side : side);
                result.Add(Stroke(a, start, new Vector2(start.X, opposite.Y), opposite, new Vector2(opposite.X, start.Y), start));
                break;
            case ShapeKind.Triangle:
                result.Add(Stroke(a, new Vector2(center.X, start.Y), end, new Vector2(start.X, end.Y), new Vector2(center.X, start.Y)));
                break;
            case ShapeKind.RightTriangle:
                result.Add(Stroke(a, start, new Vector2(start.X, end.Y), end, start));
                break;
            case ShapeKind.Diamond:
                result.Add(Stroke(a, new Vector2(center.X, start.Y), new Vector2(end.X, center.Y),
                    new Vector2(center.X, end.Y), new Vector2(start.X, center.Y), new Vector2(center.X, start.Y)));
                break;
            case ShapeKind.Parallelogram:
                result.Add(Stroke(a, new Vector2(start.X + w / 4, start.Y), new Vector2(end.X, start.Y),
                    new Vector2(end.X - w / 4, end.Y), new Vector2(start.X, end.Y), new Vector2(start.X + w / 4, start.Y)));
                break;
            case ShapeKind.Trapezoid:
                result.Add(Stroke(a, new Vector2(start.X + w / 4, start.Y), new Vector2(end.X - w / 4, start.Y),
                    end, new Vector2(start.X, end.Y), new Vector2(start.X + w / 4, start.Y)));
                break;
            case ShapeKind.Tetrahedron:
            {
                var tetraApex = new Vector2(center.X, start.Y);
                var left = new Vector2(start.X, end.Y - h / 5);
                var right = new Vector2(end.X, end.Y - h / 5);
                var front = new Vector2(center.X, end.Y);
                result.Add(Stroke(a, tetraApex, left, front, right, tetraApex));
                result.Add(Stroke(a, tetraApex, front));
                result.AddRange(Dashed(a, left, right, DashLength, DashPeriod - DashLength));
                break;
            }

            case ShapeKind.RectangleFromCenter:
                // Center = pointer-down, half-extents = FULL drag delta; no center dot in the
                // committed geometry (the menu icon's dot is decoration). Upstream:734-758.
                result.Add(Stroke(a,
                    new Vector2(2 * start.X - end.X, 2 * start.Y - end.Y),
                    new Vector2(2 * start.X - end.X, end.Y),
                    end,
                    new Vector2(end.X, 2 * start.Y - end.Y),
                    new Vector2(2 * start.X - end.X, 2 * start.Y - end.Y)));
                break;

            case ShapeKind.Ellipse:
                result.Add(EllipseStroke(a, center, rx, ry));
                break;

            case ShapeKind.Circle:
            case ShapeKind.DashedCircle:
                {
                    float r = MathF.Max(MathF.Abs(w), MathF.Abs(h)) / 2;
                    var circleCenter = start + new Vector2(w < 0 ? -r : r, h < 0 ? -r : r);
                    if (kind == ShapeKind.Circle) result.Add(EllipseStroke(a, circleCenter, r, r));
                    else result.AddRange(DashedEllipseArcs(a, circleCenter, r, r, true, true));
                    break;
                }
            case ShapeKind.CircleFromCenter:
                float radius = Vector2.Distance(start, end);
                result.Add(EllipseStroke(a, start, radius, radius));
                break;

            case ShapeKind.EllipseFromCenter:
                // Center = pointer-down, half-axes = |dx|,|dy|; no center dot. Upstream:794-812.
                result.Add(EllipseStroke(a, start, MathF.Abs(w), MathF.Abs(h)));
                break;

            case ShapeKind.EllipseWithFoci:
            case ShapeKind.EllipseWithFociFromCenter:
                {
                    // Center = pointer-down, a=|dx|, b=|dy|, c=sqrt(|a²-b²|); foci are
                    // single-point pressure-1 strokes on the major axis only. Upstream:813-872.
                    var origin = kind == ShapeKind.EllipseWithFoci ? center : start;
                    float aa = kind == ShapeKind.EllipseWithFoci ? rx : MathF.Abs(w);
                    float bb = kind == ShapeKind.EllipseWithFoci ? ry : MathF.Abs(h);
                    result.Add(EllipseStroke(a, origin, aa, bb));
                    float c = MathF.Sqrt(MathF.Abs(aa * aa - bb * bb));
                    if (aa > bb)
                    {
                        result.Add(Dot(a, new Vector2(origin.X + c, origin.Y), 1f));
                        result.Add(Dot(a, new Vector2(origin.X - c, origin.Y), 1f));
                    }
                    else if (aa < bb)
                    {
                        result.Add(Dot(a, new Vector2(origin.X, origin.Y - c), 1f));
                        result.Add(Dot(a, new Vector2(origin.X, origin.Y + c), 1f));
                    }
                    break;
                }

            case ShapeKind.DashedCircleFromCenter:
                {
                    float r = Vector2.Distance(start, end);
                    result.AddRange(DashedEllipseArcs(a, start, r, r, frontHalf: true, backHalf: true));
                    break;
                }

            case ShapeKind.Hyperbola:
            case ShapeKind.HyperbolaWithFoci:
            case ShapeKind.HyperbolaWithAsymptotes:
            case ShapeKind.HyperbolaWithFociAndAsymptotes:
                // Two-step shape, step 1: TWO DASHED asymptotes through the center.
                // The curve itself is GenerateHyperbolaBranches (step 2). Upstream:894-902.
                if (MathF.Abs(w) < 0.01f || MathF.Abs(h) < 0.01f) break;
                result.AddRange(Dashed(a, new Vector2(2 * start.X - end.X, 2 * start.Y - end.Y), end,
                    DashLength, DashPeriod - DashLength));
                result.AddRange(Dashed(a, new Vector2(2 * start.X - end.X, end.Y), new Vector2(end.X, 2 * start.Y - end.Y),
                    DashLength, DashPeriod - DashLength));
                break;

            case ShapeKind.ParabolaUp:
            case ShapeKind.ParabolaUpWithFoci:
                {
                    // y = a·x², vertex at pointer-down, a chosen so the curve passes EXACTLY
                    // through the drag end: a = (ini.Y-end.Y)/dx². Two half-branches. Upstream:980-1012.
                    if (MathF.Abs(w) < 0.01f || MathF.Abs(h) < 0.01f) break;
                    float pa = (start.Y - end.Y) / (w * w);
                    var right = new List<Vector2>();
                    var left = new List<Vector2>();
                    for (float i = 0; i <= MathF.Abs(w); i += 0.5f)
                    {
                        right.Add(new Vector2(start.X + i, start.Y - pa * i * i));
                        left.Add(new Vector2(start.X - i, start.Y - pa * i * i));
                    }
                    result.Add(Stroke(a, right.ToArray()));
                    result.Add(Stroke(a, left.ToArray()));
                    if (kind == ShapeKind.ParabolaUpWithFoci)
                        result.Add(Dot(a, new Vector2(start.X, start.Y - 1 / (4 * pa)), 1));
                    break;
                }

            case ShapeKind.ParabolaRight:
                {
                    // y² = a·x, a = (ini.X-end.X)/dy²; opens toward the drag direction. Upstream:1013-1045.
                    if (MathF.Abs(w) < 0.01f || MathF.Abs(h) < 0.01f) break;
                    float pa = (start.X - end.X) / (h * h);
                    result.AddRange(ParabolaBranches(a, start, pa, MathF.Abs(h)));
                    break;
                }

            case ShapeKind.ParabolaWithFoci:
                {
                    // y² = a·x with p = dy²/(2·(ini.X-end.X)), a = 0.5/p; focus dot at
                    // (ini.X - p/2, ini.Y), pressure 1. Upstream:1046-1087.
                    if (MathF.Abs(w) < 0.01f || MathF.Abs(h) < 0.01f) break;
                    float p = h * h / (2 * (start.X - end.X));
                    float pa = 0.5f / p;
                    result.AddRange(ParabolaBranches(a, start, pa, MathF.Abs(h)));
                    result.Add(Dot(a, new Vector2(start.X - p / 2, start.Y), 1f));
                    break;
                }

            case ShapeKind.Cylinder:
                {
                    float halfW = MathF.Abs(w) / 2, halfH = halfW / SolidEllipseRatio;
                    float midX = center.X;
                    float top = MathF.Min(start.Y, end.Y), bottom = MathF.Max(start.Y, end.Y);
                    result.Add(EllipseStroke(a, new Vector2(midX, top), halfW, halfH));
                    result.Add(Stroke(a, EllipsePoints(new Vector2(midX, bottom), halfW, halfH, 0, MathF.PI, SegmentsFor(0, MathF.PI))));
                    result.AddRange(DashedEllipseArcs(a, new Vector2(midX, bottom), halfW, halfH, false, true));
                    result.Add(Stroke(a, new Vector2(start.X, top), new Vector2(start.X, bottom)));
                    result.Add(Stroke(a, new Vector2(end.X, top), new Vector2(end.X, bottom)));
                    break;
                }

            case ShapeKind.Cone:
                {
                    float halfW = MathF.Abs(w) / 2, halfH = halfW / SolidEllipseRatio;
                    var apex = new Vector2(center.X, start.Y);
                    var baseCenter = new Vector2(center.X, end.Y);
                    if (h < 0) result.Add(EllipseStroke(a, baseCenter, halfW, halfH));
                    else
                    {
                        result.Add(Stroke(a, EllipsePoints(baseCenter, halfW, halfH, 0, MathF.PI, SegmentsFor(0, MathF.PI))));
                        result.AddRange(DashedEllipseArcs(a, baseCenter, halfW, halfH, false, true));
                    }
                    result.Add(Stroke(a, apex, new Vector2(start.X, end.Y)));
                    result.Add(Stroke(a, apex, end));
                    break;
                }

            case ShapeKind.Cuboid:
                // First pass records the front face; the second drag supplies signed depth.
                result.Add(Stroke(a, start, new Vector2(start.X, end.Y)));
                result.Add(Stroke(a, new Vector2(start.X, end.Y), end));
                result.Add(Stroke(a, end, new Vector2(end.X, start.Y)));
                result.Add(Stroke(a, start, new Vector2(end.X, start.Y)));
                break;
        }

        foreach (var s in result)
        {
            s.IsRecognizedShape = true;
            s.Complete();
        }
        return result;
    }

    // ---- two-step second passes -------------------------------------------

    /// <summary>The second drag sets a signed depth vector. Hidden edges follow the farther face.</summary>
    public static List<InkStroke> GenerateCuboid(Vector2 first, Vector2 second, Vector2 offset, StrokeAttributes attrs)
    {
        var corners = new[] { first, new Vector2(second.X, first.Y), second, new Vector2(first.X, second.Y) };
        bool secondIsNear = offset.Y > 0 || offset.Y == 0 && offset.X < 0;
        var towardNear = secondIsNear ? offset : -offset;
        int hidden = towardNear.Y >= 0 ? (towardNear.X >= 0 ? 2 : 3) : (towardNear.X >= 0 ? 1 : 0);
        var result = new List<InkStroke>();
        for (int face = 0; face < 2; face++)
        {
            bool far = secondIsNear ? face == 0 : face == 1;
            var shift = face == 0 ? Vector2.Zero : offset;
            for (int edge = 0; edge < 4; edge++)
            {
                int next = (edge + 1) % 4;
                AddEdge(corners[edge] + shift, corners[next] + shift, far && (edge == hidden || next == hidden));
            }
        }
        for (int corner = 0; corner < 4; corner++) AddEdge(corners[corner], corners[corner] + offset, corner == hidden);
        foreach (var stroke in result) { stroke.IsRecognizedShape = true; stroke.Complete(); }
        return result;

        void AddEdge(Vector2 from, Vector2 to, bool hiddenEdge)
        {
            if (hiddenEdge) result.AddRange(Dashed(attrs, from, to, DashLength, DashPeriod - DashLength));
            else result.Add(Stroke(attrs, from, to));
        }
    }

    /// <summary>
    /// Cuboid step 2: the 8 depth edges at 45° up-right, depth d = |frontTop.Y - dragEnd.Y|.
    /// 5 solid edges + 3 dashed hidden edges (bottom-back, bottom-left connector, back-left
    /// vertical). Front corners must already be min/max-normalized. Upstream:1216-1267.
    /// </summary>
    public static List<InkStroke> GenerateCuboidDepth(Vector2 frontTop, Vector2 frontBottom, float depth, StrokeAttributes attrs)
    {
        var a = attrs ?? new StrokeAttributes();
        var result = new List<InkStroke>();
        float d = MathF.Abs(depth);
        var off = new Vector2(d, -d);
        var ft = frontTop + off;               // back top-left
        var fb = new Vector2(frontBottom.X + d, frontBottom.Y - d); // back bottom-right
        var ftr = new Vector2(frontBottom.X, frontTop.Y);           // front top-right

        // 横上 (solid), 横下 (dashed)
        result.Add(Stroke(a, ft, new Vector2(fb.X, ft.Y)));
        result.AddRange(Dashed(a, new Vector2(ft.X, fb.Y), fb, DashLength, DashPeriod - DashLength));
        // 斜左上 / 斜右上 (solid), 斜左下 (dashed), 斜右下 (solid)
        result.Add(Stroke(a, frontTop, ft));
        result.Add(Stroke(a, ftr, new Vector2(ftr.X + d, ftr.Y - d)));
        result.AddRange(Dashed(a, new Vector2(frontTop.X, frontBottom.Y), new Vector2(ft.X, fb.Y), DashLength, DashPeriod - DashLength));
        result.Add(Stroke(a, frontBottom, fb));
        // 竖左 (dashed), 竖右 (solid)
        result.AddRange(Dashed(a, ft, new Vector2(ft.X, fb.Y), DashLength, DashPeriod - DashLength));
        result.Add(Stroke(a, new Vector2(fb.X, ft.Y), fb));

        foreach (var s in result)
        {
            s.IsRecognizedShape = true;
            s.Complete();
        }
        return result;
    }

    /// <summary>
    /// Hyperbola step 2: four branch arcs locked to the asymptote slope k from step 1.
    /// |dy/dx| &lt; k puts the foci on the x-axis (a=sqrt(|dx²-dy²/k²|), b=a·k), otherwise on
    /// the y-axis (a=sqrt(|dy²-dx²·k²|), b=a/k). Sampled at 0.5px from vertex a out to the
    /// drag extent; <paramref name="withFoci"/> adds pressure-1 dots at c=sqrt(a²+b²).
    /// Upstream:903-964.
    /// </summary>
    public static List<InkStroke> GenerateHyperbolaBranches(Vector2 center, float k, Vector2 end, bool withFoci, StrokeAttributes attrs)
    {
        var a = attrs ?? new StrokeAttributes();
        var result = new List<InkStroke>();
        float dx = end.X - center.X, dy = end.Y - center.Y;
        if (MathF.Abs(dx) < 0.01f || MathF.Abs(dy) < 0.01f || k <= 0) return result;

        bool fociOnX = MathF.Abs(dy / dx) < k;
        float aa, bb;
        var arcs = new List<Vector2>[4];
        for (int i = 0; i < 4; i++) arcs[i] = new List<Vector2>();

        if (fociOnX)
        {
            aa = MathF.Sqrt(MathF.Abs(dx * dx - dy * dy / (k * k)));
            bb = aa * k;
            int steps = Math.Max(0, (int)MathF.Floor((MathF.Abs(dx) - aa) / 0.5f));
            for (int j = 0; j <= steps; j++)
            {
                float i = aa + j * 0.5f;
                float rY = MathF.Sqrt(MathF.Abs(k * k * i * i - bb * bb));
                arcs[0].Add(new Vector2(center.X + i, center.Y - rY));
                arcs[1].Add(new Vector2(center.X + i, center.Y + rY));
                arcs[2].Add(new Vector2(center.X - i, center.Y - rY));
                arcs[3].Add(new Vector2(center.X - i, center.Y + rY));
            }
        }
        else
        {
            aa = MathF.Sqrt(MathF.Abs(dy * dy - dx * dx * k * k));
            bb = aa / k;
            int steps = Math.Max(0, (int)MathF.Floor((MathF.Abs(dy) - aa) / 0.5f));
            for (int j = 0; j <= steps; j++)
            {
                float i = aa + j * 0.5f;
                float rX = MathF.Sqrt(MathF.Abs(i * i / k / k - bb * bb));
                arcs[0].Add(new Vector2(center.X - rX, center.Y + i));
                arcs[1].Add(new Vector2(center.X + rX, center.Y + i));
                arcs[2].Add(new Vector2(center.X - rX, center.Y - i));
                arcs[3].Add(new Vector2(center.X + rX, center.Y - i));
            }
        }

        foreach (var arc in arcs)
            if (arc.Count > 0) result.Add(Stroke(a, arc.ToArray()));

        if (withFoci)
        {
            float c = MathF.Sqrt(aa * aa + bb * bb);
            if (fociOnX)
            {
                result.Add(Dot(a, new Vector2(center.X + c, center.Y), 1f));
                result.Add(Dot(a, new Vector2(center.X - c, center.Y), 1f));
            }
            else
            {
                result.Add(Dot(a, new Vector2(center.X, center.Y + c), 1f));
                result.Add(Dot(a, new Vector2(center.X, center.Y - c), 1f));
            }
        }

        foreach (var s in result)
        {
            s.IsRecognizedShape = true;
            s.Complete();
        }
        return result;
    }

    // ---- parallel lines -----------------------------------------------------

    /// <summary>
    /// Angle snapping for ParallelLine: |tan|&lt;1/12 → horizontal, tan∈(0.52,0.63) → 30°,
    /// (0.92,1.08) → 45°, (1.63,1.95) → 60°, |cot|&lt;1/12 → vertical. The snapped end keeps
    /// the drag distance d. Transcribed from upstream:587-627 (sin/cos use screen Y).
    /// </summary>
    public static Vector2 SnapParallelEnd(Vector2 start, Vector2 end)
        => SnapParallel(start, end).end;

    // Upstream snaps sin/cos AND endP together; the snapped sin/cos (not a re-derivation
    // from the snapped end) drive the offsets, e.g. vertical always snaps sin to +1.
    private static (Vector2 end, float sin, float cos) SnapParallel(Vector2 start, Vector2 end)
    {
        float d = Vector2.Distance(start, end);
        if (d <= 0) return (end, 0, 0);
        float sin = (start.Y - end.Y) / d;
        float cos = (end.X - start.X) / d;
        float tan = MathF.Abs(sin / cos);

        if (tan < 1f / 12)
        {
            sin = 0; cos = 1;
            end = new Vector2(end.X, start.Y);
        }
        if (tan > 0.52f && tan < 0.63f) // 30°
        {
            sin = MathF.Sign(sin) * 0.5f;
            cos = MathF.Sign(cos) * 0.866f;
            end = new Vector2(start.X + d * cos, start.Y - d * sin);
        }
        if (tan > 0.92f && tan < 1.08f) // 45°
        {
            sin = MathF.Sign(sin) * 0.707f;
            cos = MathF.Sign(cos) * 0.707f;
            end = new Vector2(start.X + d * cos, start.Y - d * sin);
        }
        if (tan > 1.63f && tan < 1.95f) // 60°
        {
            sin = MathF.Sign(sin) * 0.866f;
            cos = MathF.Sign(cos) * 0.5f;
            end = new Vector2(start.X + d * cos, start.Y - d * sin);
        }
        if (MathF.Abs(cos / sin) < 1f / 12)
        {
            end = new Vector2(start.X, end.Y);
            sin = 1; cos = 0;
        }
        return (end, sin, cos);
    }

    /// <summary>FOUR lines at perpendicular offsets ±25 and ±75 (upstream x=25, lines at ±x/±3x, :628-631).</summary>
    public static List<InkStroke> ParallelLines(StrokeAttributes attrs, Vector2 start, Vector2 end)
    {
        var result = new List<InkStroke>();
        var (snapped, sin, cos) = SnapParallel(start, end);
        if (Vector2.Distance(start, snapped) <= 0) return result;
        var n = new Vector2(sin, cos); // perpendicular to the (cos, -sin) drag direction
        const float x = 25f;
        foreach (float k in new[] { -3f, -1f, 1f, 3f })
            result.Add(Stroke(attrs, start + n * (x * k), snapped + n * (x * k)));
        return result;
    }

    // ---- primitives -------------------------------------------------------

    public static InkStroke Stroke(StrokeAttributes attrs, params Vector2[] pts)
    {
        var s = new InkStroke(attrs.Clone());
        s.Attributes.Nib = PenNib.Round;
        float t = 0;
        foreach (var p in pts)
        {
            s.AddPoint(p.X, p.Y, 0.5f, t);
            t += 8f;
        }
        return s;
    }

    /// <summary>
    /// A single-point dot sized by pressure like WPF (0.5 = nominal width): focus dots use
    /// pressure 1, dot-line dots 0.8. Width is stored directly so the taper pipeline, which
    /// is for freehand samples, does not shrink the dot.
    /// </summary>
    public static InkStroke Dot(StrokeAttributes attrs, Vector2 center, float pressure)
    {
        var s = new InkStroke(attrs.Clone());
        s.Attributes.Nib = PenNib.Round;
        float width = attrs.BaseWidth * (pressure / 0.5f);
        s.RestorePoints(new[] { new InkPoint(center.X, center.Y, pressure, width, 0f) });
        return s;
    }

    /// <summary>A line with the fixed 30x10 solid arrow head (upstream mode 2, :558-572).</summary>
    public static InkStroke Arrow(StrokeAttributes attrs, Vector2 start, Vector2 end)
        => ArrowWithHead(attrs, start, end, 30f, 10f);

    /// <summary>Coordinate-axis arrow with the fixed 20x7 head (GenerateArrowLineStroke, upstream:1392-1417).</summary>
    public static InkStroke AxisArrow(StrokeAttributes attrs, Vector2 start, Vector2 end)
        => ArrowWithHead(attrs, start, end, 20f, 7f);

    private static InkStroke ArrowWithHead(StrokeAttributes attrs, Vector2 start, Vector2 end, float w, float h)
    {
        var dir = end - start;
        if (dir.LengthSquared() < 1e-4f) return Stroke(attrs, start, end);
        // theta points from the tip back toward the start, exactly as upstream.
        double theta = Math.Atan2(start.Y - end.Y, start.X - end.X);
        float sint = (float)Math.Sin(theta), cost = (float)Math.Cos(theta);
        var barb1 = new Vector2(end.X + (w * cost - h * sint), end.Y + (w * sint + h * cost));
        var barb2 = new Vector2(end.X + (w * cost + h * sint), end.Y - (h * cost - w * sint));
        return Stroke(attrs, start, end, barb1, end, barb2);
    }

    public static List<InkStroke> Dashed(StrokeAttributes attrs, Vector2 start, Vector2 end, float dash, float gap)
    {
        if (attrs.GeneratorSolidLines) return new() { Stroke(attrs, start, end) };
        var result = new List<InkStroke>();
        var delta = end - start;
        float len = delta.Length();
        if (len < 0.5f) return result;
        var dir = delta / len;
        float pos = 0;
        while (pos < len)
        {
            float stop = MathF.Min(pos + dash, len);
            result.Add(Stroke(attrs, start + dir * pos, start + dir * stop));
            pos = stop + gap;
        }
        return result;
    }

    /// <summary>Single-point dots every 8.28px at pressure 0.8 (GenerateDotLineStrokeCollection, upstream:1445-1467).</summary>
    public static List<InkStroke> Dots(StrokeAttributes attrs, Vector2 start, Vector2 end)
    {
        if (attrs.GeneratorSolidLines) return new() { Stroke(attrs, start, end) };
        var result = new List<InkStroke>();
        var delta = end - start;
        float len = delta.Length();
        if (len <= 0) return result;
        var dir = delta / len;
        for (float i = 0; i < len; i += DotPeriod)
            result.Add(Dot(attrs, start + dir * i, DotPressure));
        return result;
    }

    private static List<InkStroke> ParabolaBranches(StrokeAttributes attrs, Vector2 vertex, float a, float extent)
    {
        var down = new List<Vector2>();
        var up = new List<Vector2>();
        for (float i = 0; i <= extent; i += 0.5f)
        {
            down.Add(new Vector2(vertex.X - a * i * i, vertex.Y + i));
            up.Add(new Vector2(vertex.X - a * i * i, vertex.Y - i));
        }
        return new List<InkStroke> { Stroke(attrs, down.ToArray()), Stroke(attrs, up.ToArray()) };
    }

    public static InkStroke EllipseStroke(StrokeAttributes attrs, Vector2 center, float rx, float ry)
        => Stroke(attrs, EllipsePoints(center, MathF.Max(rx, 0.5f), MathF.Max(ry, 0.5f), 0, 2 * MathF.PI, FullEllipseSegments));

    public static Vector2[] EllipsePoints(Vector2 center, float rx, float ry, float from, float to, int segments)
    {
        var pts = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float a = from + (to - from) * i / segments;
            pts[i] = center + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry);
        }
        return pts;
    }

    private static int SegmentsFor(float from, float to)
        => Math.Max(2, (int)MathF.Ceiling(FullEllipseSegments * (to - from) / (2 * MathF.PI)));

    /// <summary>
    /// Angular dashes on an ellipse: dash width 0.05π, period 0.083π, sampled at 0.01 rad.
    /// <paramref name="frontHalf"/> is r∈[0,π] (screen-down half, upstream isDrawBottom),
    /// <paramref name="backHalf"/> is r∈[π,2π] (upstream isDrawTop). Transcribed from
    /// GenerateDashedLineEllipseStrokeCollection, upstream:1329-1373.
    /// </summary>
    public static List<InkStroke> DashedEllipseArcs(StrokeAttributes attrs, Vector2 center, float rx, float ry, bool frontHalf, bool backHalf)
    {
        var result = new List<InkStroke>();
        rx = MathF.Max(rx, 0.5f); ry = MathF.Max(ry, 0.5f);
        if (attrs.GeneratorSolidLines)
        {
            if (frontHalf) result.Add(Stroke(attrs, EllipsePoints(center, rx, ry, 0, MathF.PI, 36)));
            if (backHalf) result.Add(Stroke(attrs, EllipsePoints(center, rx, ry, MathF.PI, MathF.Tau, 36)));
            return result;
        }
        if (frontHalf) AddHalf(0f, 1f);
        if (backHalf) AddHalf(1f, 2f);

        void AddHalf(float from, float to)
        {
            for (float i = from; i < to; i += EllipseDashPeriod)
            {
                float a0 = MathF.PI * i, a1 = MathF.PI * (i + EllipseDashStep);
                int segments = Math.Max(2, (int)MathF.Ceiling((a1 - a0) / 0.01f));
                result.Add(Stroke(attrs, EllipsePoints(center, rx, ry, a0, a1, segments)));
            }
        }
        return result;
    }
}
