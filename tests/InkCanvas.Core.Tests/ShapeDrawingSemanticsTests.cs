using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using System.Numerics;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>
/// WPF parity tests for the shape generator (upstream MW_ShapeDrawing.cs): center+radius
/// semantics, snapped 4-line parallel lines, two-step cuboid/hyperbola geometry, split
/// cylinder/cone bottoms, endpoint-exact parabolas and the original dash/dot/arrow constants.
/// </summary>
public class ShapeDrawingSemanticsTests
{
    private static readonly StrokeAttributes Attrs = new() { BaseWidth = 3, Color = InkColor.Black };

    private static void AssertPoint(Vector2 expected, Vector2 actual, float tolerance = 0.01f)
        => Assert.True(Vector2.Distance(expected, actual) <= tolerance,
            $"expected {expected}, got {actual}");

    private static bool OnSegment(Vector2 p, Vector2 a, Vector2 b, float tolerance = 0.01f)
        => InkStroke.DistanceToSegment(p, a, b) <= tolerance;

    // ---- center + radius semantics -----------------------------------------

    [Fact]
    public void CircleUsesPointerDownAsCenterAndDragDistanceAsRadius()
    {
        var start = new Vector2(100, 100);
        var s = ShapeGenerator.Generate(ShapeKind.CircleFromCenter, start, new Vector2(130, 140), Attrs).Single();
        // drag (30,40) -> R = 50, centered at the pointer-down point.
        Assert.All(s.Points, p => Assert.Equal(50f, Vector2.Distance(p.Position, start), 2));
        AssertPoint(new Vector2(150, 100), s.Points[0].Position);
    }

    [Fact]
    public void DashedCircleUsesCenterAndRadiusWithAngularDashes()
    {
        var start = new Vector2(0, 0);
        var strokes = ShapeGenerator.Generate(ShapeKind.DashedCircleFromCenter, start, new Vector2(30, 40), Attrs);
        // 0.05π dash / 0.083π period -> 13 dashes per half, 26 total (upstream:1329-1373).
        Assert.Equal(26, strokes.Count);
        Assert.All(strokes, s => Assert.All(s.Points, p =>
            Assert.Equal(50f, Vector2.Distance(p.Position, start), 1)));
    }

    [Fact]
    public void RectangleFromCenterUsesFullDeltaAndNoCenterDot()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.RectangleFromCenter,
            new Vector2(100, 100), new Vector2(140, 130), Attrs);
        var s = Assert.Single(strokes); // one closed stroke, no center-dot stroke
        Assert.Equal(5, s.Count);
        AssertPoint(new Vector2(60, 70), s.Points[0].Position);
        AssertPoint(new Vector2(60, 130), s.Points[1].Position);
        AssertPoint(new Vector2(140, 130), s.Points[2].Position);
        AssertPoint(new Vector2(140, 70), s.Points[3].Position);
        AssertPoint(new Vector2(60, 70), s.Points[4].Position);
    }

    [Fact]
    public void EllipseFromCenterUsesFullDeltaAndNoCenterDot()
    {
        var start = new Vector2(100, 100);
        var s = ShapeGenerator.Generate(ShapeKind.EllipseFromCenter, start, new Vector2(160, 120), Attrs).Single();
        AssertPoint(new Vector2(160, 100), s.Points[0].Position);
        Assert.Equal(160f, s.Points.Max(p => p.X), 2);
        Assert.Equal(40f, s.Points.Min(p => p.X), 2);
        Assert.Equal(120f, s.Points.Max(p => p.Y), 2);
        Assert.Equal(80f, s.Points.Min(p => p.Y), 2);
    }

    [Fact]
    public void EllipseWithFociPlacesPressureOneDotsOnTheMajorAxis()
    {
        var start = new Vector2(100, 100);
        float c = MathF.Sqrt(60f * 60f - 20f * 20f);

        var wide = ShapeGenerator.Generate(ShapeKind.EllipseWithFociFromCenter, start, new Vector2(160, 120), Attrs);
        Assert.Equal(3, wide.Count);
        Assert.Equal(1, wide[1].Count);
        AssertPoint(new Vector2(100 + c, 100), wide[1].Points[0].Position);
        AssertPoint(new Vector2(100 - c, 100), wide[2].Points[0].Position);
        Assert.Equal(1f, wide[1].Points[0].Pressure);
        // WPF sizes a pressure-1 dot at twice the nominal (pressure-0.5) width.
        Assert.Equal(Attrs.BaseWidth * 2f, wide[1].Points[0].Width, 3);

        var tall = ShapeGenerator.Generate(ShapeKind.EllipseWithFociFromCenter, start, new Vector2(120, 160), Attrs);
        Assert.Equal(3, tall.Count);
        AssertPoint(new Vector2(100, 100 - c), tall[1].Points[0].Position);
        AssertPoint(new Vector2(100, 100 + c), tall[2].Points[0].Position);

        // a == b: upstream draws no foci at all.
        var round = ShapeGenerator.Generate(ShapeKind.EllipseWithFociFromCenter, start, new Vector2(150, 150), Attrs);
        Assert.Single(round);
    }

    // ---- parallel lines ------------------------------------------------------

    [Fact]
    public void ParallelLineDrawsFourLinesAtOffsets25And75()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.ParallelLine,
            new Vector2(100, 100), new Vector2(300, 100), Attrs);
        Assert.Equal(4, strokes.Count);
        float[] offsets = { -75, -25, 25, 75 };
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(2, strokes[i].Count);
            AssertPoint(new Vector2(100, 100 + offsets[i]), strokes[i].Points[0].Position);
            AssertPoint(new Vector2(300, 100 + offsets[i]), strokes[i].Points[1].Position);
        }
    }

    [Fact]
    public void ParallelLineSnapsNear30Degrees()
    {
        var start = new Vector2(100, 100);
        var end = new Vector2(300, -20); // tan = 0.6, inside (0.52, 0.63)
        float d = Vector2.Distance(start, end);
        var snapped = ShapeGenerator.SnapParallelEnd(start, end);
        AssertPoint(new Vector2(start.X + d * 0.866f, start.Y - d * 0.5f), snapped, 0.05f);

        var strokes = ShapeGenerator.Generate(ShapeKind.ParallelLine, start, end, Attrs);
        Assert.Equal(4, strokes.Count);
        var n = new Vector2(0.5f, 0.866f);
        float[] offsets = { -75, -25, 25, 75 };
        for (int i = 0; i < 4; i++)
        {
            AssertPoint(start + n * offsets[i], strokes[i].Points[0].Position, 0.05f);
            AssertPoint(snapped + n * offsets[i], strokes[i].Points[1].Position, 0.05f);
        }
    }

    [Fact]
    public void ParallelLineSnapsNearVertical()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.ParallelLine,
            new Vector2(100, 100), new Vector2(102, 300), Attrs);
        Assert.Equal(4, strokes.Count);
        float[] offsets = { -75, -25, 25, 75 };
        for (int i = 0; i < 4; i++)
        {
            AssertPoint(new Vector2(100 + offsets[i], 100), strokes[i].Points[0].Position);
            AssertPoint(new Vector2(100 + offsets[i], 300), strokes[i].Points[1].Position);
        }
    }

    [Theory]
    [InlineData(100, -80)]   // tan 0.8: no snap band
    [InlineData(100, -5)]    // tan 0.05: horizontal band keeps X, flattens Y
    public void SnapParallelEndRespectsBands(float dx, float dy)
    {
        var start = new Vector2(0, 0);
        var end = new Vector2(dx, dy);
        var snapped = ShapeGenerator.SnapParallelEnd(start, end);
        if (MathF.Abs(dy / dx) < 1f / 12)
            AssertPoint(new Vector2(dx, 0), snapped);
        else
            AssertPoint(end, snapped);
    }

    [Fact]
    public void ParallelLineSnaps45And60()
    {
        float d45 = MathF.Sqrt(2) * 100;
        AssertPoint(new Vector2(d45 * 0.707f, -d45 * 0.707f),
            ShapeGenerator.SnapParallelEnd(new Vector2(0, 0), new Vector2(100, -100)), 0.05f);
        float d60 = MathF.Sqrt(100 * 100 + 180 * 180);
        AssertPoint(new Vector2(d60 * 0.5f, -d60 * 0.866f),
            ShapeGenerator.SnapParallelEnd(new Vector2(0, 0), new Vector2(100, -180)), 0.05f);
    }

    // ---- two-step cuboid ------------------------------------------------------

    [Fact]
    public void CuboidFirstStepIsFourSeparateEdges()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.Cuboid,
            new Vector2(100, 100), new Vector2(200, 160), Attrs);
        Assert.Equal(4, strokes.Count);
        Assert.All(strokes, s => Assert.Equal(2, s.Count));
        AssertPoint(new Vector2(100, 100), strokes[0].Points[0].Position);
        AssertPoint(new Vector2(100, 160), strokes[0].Points[1].Position);
        AssertPoint(new Vector2(100, 160), strokes[1].Points[0].Position);
        AssertPoint(new Vector2(200, 160), strokes[1].Points[1].Position);
        AssertPoint(new Vector2(200, 160), strokes[2].Points[0].Position);
        AssertPoint(new Vector2(200, 100), strokes[2].Points[1].Position);
        AssertPoint(new Vector2(100, 100), strokes[3].Points[0].Position);
        AssertPoint(new Vector2(200, 100), strokes[3].Points[1].Position);
    }

    [Fact]
    public void CuboidDepthHasFiveSolidEdgesAndThreeDashedHiddenEdges()
    {
        var strokes = ShapeGenerator.GenerateCuboidDepth(
            new Vector2(100, 100), new Vector2(200, 160), 40f, Attrs);

        // Solid edges span whole edges (>= 56px); dashes are <= 5px fragments.
        var solid = strokes.Where(s => s.ArcLength() > 10f).ToList();
        Assert.Equal(5, solid.Count);
        var expectedSolid = new (Vector2 A, Vector2 B)[]
        {
            (new Vector2(140, 60), new Vector2(240, 60)),   // top-back horizontal
            (new Vector2(100, 100), new Vector2(140, 60)),  // top-left connector
            (new Vector2(200, 100), new Vector2(240, 60)),  // top-right connector
            (new Vector2(200, 160), new Vector2(240, 120)), // bottom-right connector
            (new Vector2(240, 60), new Vector2(240, 120)),  // back-right vertical
        };
        foreach (var (a, b) in expectedSolid)
            Assert.Contains(solid, s =>
                Vector2.Distance(s.Points[0].Position, a) < 0.01f &&
                Vector2.Distance(s.Points[1].Position, b) < 0.01f);

        // Hidden edges: bottom-back horizontal, bottom-left connector, back-left vertical.
        var hidden = new (Vector2 A, Vector2 B)[]
        {
            (new Vector2(140, 120), new Vector2(240, 120)),
            (new Vector2(100, 160), new Vector2(140, 120)),
            (new Vector2(140, 60), new Vector2(140, 120)),
        };
        var dashed = strokes.Where(s => s.ArcLength() <= 10f).ToList();
        Assert.Equal(18, dashed.Count); // 8 + 5 + 5 dashes at period 13.8
        Assert.All(dashed, s => Assert.All(s.Points, p =>
            Assert.True(hidden.Any(h => OnSegment(p.Position, h.A, h.B)),
                $"dash point {p.Position} off the hidden edges")));
        Assert.Equal(23, strokes.Count);
    }

    // ---- two-step hyperbola ----------------------------------------------------

    [Fact]
    public void HyperbolaFirstStepIsTwoDashedAsymptotesThroughTheCenter()
    {
        var start = new Vector2(100, 100);
        var end = new Vector2(160, 140);
        var strokes = ShapeGenerator.Generate(ShapeKind.Hyperbola, start, end, Attrs);
        var lineA = (new Vector2(40, 60), end);
        var lineB = (new Vector2(40, 140), new Vector2(160, 60));
        Assert.Equal(22, strokes.Count); // 11 dashes per asymptote (len 144.22 / 13.8)
        Assert.All(strokes, s => Assert.All(s.Points, p =>
            Assert.True(OnSegment(p.Position, lineA.Item1, lineA.Item2) ||
                        OnSegment(p.Position, lineB.Item1, lineB.Item2),
                $"asymptote point {p.Position} off both lines")));
    }

    [Fact]
    public void HyperbolaBranchesLockToKWithFociOnXAxis()
    {
        var center = new Vector2(0, 0);
        float k = 1f;
        var end = new Vector2(100, 30); // |dy/dx| = 0.3 < k
        float a = MathF.Sqrt(100 * 100 - 30 * 30);
        float b = a * k;

        var strokes = ShapeGenerator.GenerateHyperbolaBranches(center, k, end, withFoci: false, Attrs);
        Assert.Equal(4, strokes.Count);
        // Vertex of the first arc sits at (a, 0).
        AssertPoint(new Vector2(a, 0), strokes[0].Points[0].Position, 0.05f);
        // Every sample satisfies y² = k²x² - b² on the right-upper branch.
        Assert.All(strokes[0].Points, p =>
        {
            Assert.True(p.X > 0 && p.Y <= 0.01f);
            Assert.Equal(p.Y * p.Y, k * k * p.X * p.X - b * b, 1);
        });
    }

    [Fact]
    public void HyperbolaBranchesPutFociOnYAxisWhenDragIsSteeperThanK()
    {
        var center = new Vector2(0, 0);
        var end = new Vector2(30, 100); // |dy/dx| = 3.33 > k=1
        float a = MathF.Sqrt(100 * 100 - 30 * 30);

        var strokes = ShapeGenerator.GenerateHyperbolaBranches(center, 1f, end, withFoci: true, Attrs);
        Assert.Equal(6, strokes.Count);
        AssertPoint(new Vector2(0, a), strokes[0].Points[0].Position, 0.05f);
        float c = MathF.Sqrt(a * a + a * a);
        Assert.Equal(1, strokes[4].Count);
        AssertPoint(new Vector2(0, c), strokes[4].Points[0].Position, 0.05f);
        AssertPoint(new Vector2(0, -c), strokes[5].Points[0].Position, 0.05f);
        Assert.Equal(1f, strokes[4].Points[0].Pressure);
    }

    // ---- cylinder / cone bottoms ------------------------------------------------

    [Fact]
    public void CylinderHasSolidTopAndFrontSolidBackDashedBottom()
    {
        // Upward drag: normalized top-first, Y swap only (upstream:1088-1097).
        var strokes = ShapeGenerator.Generate(ShapeKind.Cylinder,
            new Vector2(100, 300), new Vector2(200, 100), Attrs);
        Assert.Equal(17, strokes.Count); // top + front half + 13 back dashes + 2 sides

        float halfW = 50f, halfH = 50f / 2.646f;
        var topCenter = new Vector2(150, 100);
        var bottomCenter = new Vector2(150, 300);

        // Top ellipse: full, solid, minor/major = 1/2.646 of the width.
        Assert.Equal(100f, strokes[0].Points.Max(p => p.X) - strokes[0].Points.Min(p => p.X), 1);
        Assert.Equal(2 * halfH, strokes[0].Points.Max(p => p.Y) - strokes[0].Points.Min(p => p.Y), 1);
        Assert.All(strokes[0].Points, p => AssertPoint(topCenter, p.Position, halfW + 0.01f));

        // Front half of the bottom: solid, screen-down half (r 0..π).
        var front = strokes[1];
        AssertPoint(new Vector2(200, 300), front.Points[0].Position);
        AssertPoint(new Vector2(100, 300), front.Points[^1].Position);
        Assert.All(front.Points, p => Assert.True(p.Y >= 300f - 0.01f));

        // Back half: 13 angular dashes on the same ellipse.
        for (int i = 2; i < 15; i++)
            Assert.All(strokes[i].Points, p =>
                Assert.Equal(1f, MathF.Pow((p.X - bottomCenter.X) / halfW, 2) +
                                 MathF.Pow((p.Y - bottomCenter.Y) / halfH, 2), 2));

        // Sides run between the ellipse centers, not their rims.
        AssertPoint(new Vector2(100, 100), strokes[15].Points[0].Position);
        AssertPoint(new Vector2(100, 300), strokes[15].Points[1].Position);
        AssertPoint(new Vector2(200, 100), strokes[16].Points[0].Position);
        AssertPoint(new Vector2(200, 300), strokes[16].Points[1].Position);
    }

    [Fact]
    public void ConeHasApexAtTopCenterAndSplitBottom()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.Cone,
            new Vector2(100, 100), new Vector2(200, 300), Attrs);
        Assert.Equal(16, strokes.Count); // front half + 13 back dashes + 2 slants

        var front = strokes[0];
        Assert.All(front.Points, p => Assert.True(p.Y >= 300f - 0.01f));
        AssertPoint(new Vector2(200, 300), front.Points[0].Position);

        AssertPoint(new Vector2(150, 100), strokes[14].Points[0].Position);
        AssertPoint(new Vector2(100, 300), strokes[14].Points[1].Position);
        AssertPoint(new Vector2(150, 100), strokes[15].Points[0].Position);
        AssertPoint(new Vector2(200, 300), strokes[15].Points[1].Position);
    }

    [Fact]
    public void ConeCanPointDownFromItsFirstApexWithBaseAbove()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.Cone, new(100, 300), new(200, 100), Attrs);
        Assert.Equal(3, strokes.Count);
        Assert.Equal(new Vector2(150, 300), strokes[1].Points[0].Position);
        Assert.Equal(new Vector2(100, 100), strokes[1].Points[1].Position);
        Assert.Equal(new Vector2(200, 100), strokes[2].Points[1].Position);
        Assert.Contains(strokes[0].Points, p => p.Y < 100);
        Assert.Contains(strokes[0].Points, p => p.Y > 100);
    }

    // ---- constants: dashes, dots, arrow heads ------------------------------------

    [Fact]
    public void DashedLineUsesDash5AndPeriod13Point8()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.DashedLine,
            new Vector2(0, 0), new Vector2(100, 0), Attrs);
        Assert.Equal(8, strokes.Count); // ceil(100 / 13.8)
        AssertPoint(new Vector2(0, 0), strokes[0].Points[0].Position);
        AssertPoint(new Vector2(5, 0), strokes[0].Points[1].Position);
        AssertPoint(new Vector2(13.8f, 0), strokes[1].Points[0].Position);
        AssertPoint(new Vector2(18.8f, 0), strokes[1].Points[1].Position);
        AssertPoint(new Vector2(100, 0), strokes[^1].Points[1].Position); // last dash clipped
    }

    [Fact]
    public void DotLinePlacesPressure0Point8DotsEvery8Point28()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.DotLine,
            new Vector2(0, 0), new Vector2(80, 0), Attrs);
        Assert.Equal(10, strokes.Count); // i = 0 .. 74.52 < 80
        for (int i = 0; i < strokes.Count; i++)
        {
            Assert.Equal(1, strokes[i].Count);
            AssertPoint(new Vector2(i * 8.28f, 0), strokes[i].Points[0].Position);
            Assert.Equal(0.8f, strokes[i].Points[0].Pressure);
            Assert.Equal(Attrs.BaseWidth * 1.6f, strokes[i].Points[0].Width, 3);
        }
    }

    [Fact]
    public void ArrowHeadIsFixed30By10()
    {
        var s = ShapeGenerator.Generate(ShapeKind.Arrow,
            new Vector2(0, 0), new Vector2(100, 0), Attrs).Single();
        Assert.Equal(5, s.Count);
        AssertPoint(new Vector2(0, 0), s.Points[0].Position);
        AssertPoint(new Vector2(100, 0), s.Points[1].Position);
        AssertPoint(new Vector2(70, -10), s.Points[2].Position);
        AssertPoint(new Vector2(100, 0), s.Points[3].Position);
        AssertPoint(new Vector2(70, 10), s.Points[4].Position);
    }

    [Fact]
    public void CoordinateAxisArrowHeadIsFixed20By7()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.CoordinateFirstQuadrant,
            new Vector2(0, 0), new Vector2(100, -80), Attrs);
        Assert.Equal(2, strokes.Count);
        // x axis: 25px tail behind the origin, 20x7 barbs at the drag end.
        AssertPoint(new Vector2(-25, 0), strokes[0].Points[0].Position);
        AssertPoint(new Vector2(80, -7), strokes[0].Points[2].Position);
        AssertPoint(new Vector2(80, 7), strokes[0].Points[4].Position);
        // y axis: 25px tail below the origin, 20x7 barbs.
        AssertPoint(new Vector2(0, 25), strokes[1].Points[0].Position);
        AssertPoint(new Vector2(-7, -60), strokes[1].Points[2].Position);
        AssertPoint(new Vector2(7, -60), strokes[1].Points[4].Position);
    }

    [Fact]
    public void TwoPointCoordinateSystemUsesPointerDownAsOriginAndReleaseAsAxisEnds()
    {
        var start = new Vector2(100, 200);
        var end = new Vector2(300, 80);
        var strokes = ShapeGenerator.Generate(ShapeKind.CoordinateTwoPoint, start, end, Attrs);
        Assert.Equal(2, strokes.Count);
        AssertPoint(start, strokes[0].Points[0].Position);
        AssertPoint(new Vector2(end.X, start.Y), strokes[0].Points[1].Position);
        AssertPoint(start, strokes[1].Points[0].Position);
        AssertPoint(new Vector2(start.X, end.Y), strokes[1].Points[1].Position);

        var reverse = ShapeGenerator.Generate(ShapeKind.CoordinateTwoPoint, end, start, Attrs);
        Assert.Equal(2, reverse.Count);
        AssertPoint(end, reverse[0].Points[0].Position);
        AssertPoint(new Vector2(start.X, end.Y), reverse[0].Points[1].Position);
        AssertPoint(end, reverse[1].Points[0].Position);
        AssertPoint(new Vector2(end.X, start.Y), reverse[1].Points[1].Position);
        Assert.Empty(ShapeGenerator.Generate(ShapeKind.CoordinateTwoPoint, start, new(start.X, 80), Attrs));
    }

    // ---- endpoint-exact parabolas -------------------------------------------------

    [Fact]
    public void ParabolaUpPassesExactlyThroughTheDragEnd()
    {
        var end = new Vector2(100, -50);
        var strokes = ShapeGenerator.Generate(ShapeKind.ParabolaUp, new Vector2(0, 0), end, Attrs);
        Assert.Equal(2, strokes.Count);
        AssertPoint(new Vector2(0, 0), strokes[0].Points[0].Position); // vertex at pointer-down
        AssertPoint(end, strokes[0].Points[^1].Position);
        AssertPoint(new Vector2(-100, -50), strokes[1].Points[^1].Position);

        // Dragging downward opens the parabola downward, still endpoint-exact.
        var down = new Vector2(100, 50);
        var downStrokes = ShapeGenerator.Generate(ShapeKind.ParabolaUp, new Vector2(0, 0), down, Attrs);
        AssertPoint(down, downStrokes[0].Points[^1].Position);
    }

    [Fact]
    public void ParabolaRightOpensTowardTheDragAndIsEndpointExact()
    {
        var end = new Vector2(50, 100);
        var strokes = ShapeGenerator.Generate(ShapeKind.ParabolaRight, new Vector2(0, 0), end, Attrs);
        Assert.Equal(2, strokes.Count);
        AssertPoint(end, strokes[0].Points[^1].Position);
        AssertPoint(new Vector2(50, -100), strokes[1].Points[^1].Position);
    }

    [Fact]
    public void ParabolaWithFociPlacesFocusAtMinusPHalf()
    {
        var end = new Vector2(50, 100);
        var strokes = ShapeGenerator.Generate(ShapeKind.ParabolaWithFoci, new Vector2(0, 0), end, Attrs);
        Assert.Equal(3, strokes.Count);
        AssertPoint(end, strokes[0].Points[^1].Position);
        // p = dy²/(2·(ini.X-end.X)) = -100 -> focus at (ini.X - p/2, ini.Y) = (50, 0).
        Assert.Equal(1, strokes[2].Count);
        AssertPoint(new Vector2(50, 0), strokes[2].Points[0].Position);
        Assert.Equal(1f, strokes[2].Points[0].Pressure);
    }
}
