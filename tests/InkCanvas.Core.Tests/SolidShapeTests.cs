using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class SolidShapeTests
{
    public static IEnumerable<object[]> NewSolids => Enum.GetValues<ShapeKind>()
        .Where(kind => kind is >= ShapeKind.Cube and <= ShapeKind.ConeFrustum).Select(kind => new object[] { kind });
    public static IEnumerable<object[]> TwoStepSolids => Enum.GetValues<ShapeKind>().Where(ShapeGenerator.IsTwoStepSolid).Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(NewSolids))]
    public void NewSolidsDrawInBothDragDirectionsAndRejectFlatBounds(ShapeKind kind)
    {
        foreach (var end in new[] { new Vector2(360, 300), new Vector2(40, 300), new Vector2(360, 20), new Vector2(40, 20) })
        {
            var strokes = ShapeGenerator.Generate(kind, new(180, 140), end, new());
            Assert.NotEmpty(strokes);
            Assert.All(strokes, stroke =>
            {
                Assert.True(stroke.IsCompleted && stroke.IsRecognizedShape);
                Assert.All(stroke.Points, point => Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y)));
            });
        }
        Assert.Empty(ShapeGenerator.Generate(kind, new(100, 100), new(100, 100), new()));
    }

    [Theory]
    [MemberData(nameof(TwoStepSolids))]
    public void EverySolidSupportsContinuousDrawingWithOneUndoPerObject(ShapeKind kind)
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.SelectShape(kind, sticky: true);
        for (int i = 0; i < 2; i++)
        {
            int previousCount = controller.Document.Count;
            Drag(controller, new(100 + i * 320, 100), new(260 + i * 320, 220));
            Assert.Equal(previousCount, controller.Document.Count);
            Assert.NotEmpty(controller.ShapePreview);
            Drag(controller, new(500, 400), new(555, 355));
            Assert.True(controller.Document.Count > previousCount);
            Assert.Empty(controller.ShapePreview);
            Assert.True(controller.StickyShape);
            Assert.Equal(InkTool.Shape, controller.Tool);
        }
        Assert.Equal(2, controller.History.UndoCount);
        int count = controller.Document.Count;
        controller.Undo();
        Assert.InRange(controller.Document.Count, 1, count - 1);
        controller.Undo(); Assert.Equal(0, controller.Document.Count);
        controller.Redo(); controller.Redo(); Assert.Equal(count, controller.Document.Count);
        controller.SelectShape(kind);
        Drag(controller, new(100, 400), new(260, 520)); Drag(controller, new(500, 400), new(555, 355));
        Assert.Equal(InkTool.Pen, controller.Tool);
        Assert.False(controller.StickyShape);
    }

    [Theory]
    [MemberData(nameof(TwoStepSolids))]
    public void SolidPreviewMatchesCommitForEveryDepthQuadrant(ShapeKind kind)
    {
        foreach (var offset in new[] { new Vector2(60, 45), new Vector2(-60, 45), new Vector2(60, -45), new Vector2(-60, -45) })
        {
            using var controller = new InkController { RecognizeShapes = false };
            controller.SelectShape(kind);
            Drag(controller, new(260, 220), new(100, 100));
            controller.OnPointerDown(Pointer(new(500, 400)));
            controller.OnPointerMove(Pointer(new Vector2(500, 400) + offset));
            var preview = controller.ShapePreview.Select(stroke => stroke.Points.Select(point => point.Position).ToArray()).ToArray();
            Assert.NotEmpty(preview);
            controller.OnPointerUp(Pointer(new Vector2(500, 400) + offset));
            Assert.Equal(preview.Length, controller.Document.Count);
            for (int i = 0; i < preview.Length; i++) Assert.Equal(preview[i], controller.Document.Strokes[i].Points.Select(point => point.Position).ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(TwoStepSolids))]
    public void TinyDepthCanBeRetriedWithoutLosingTheBase(ShapeKind kind)
    {
        using var controller = new InkController { RecognizeShapes = false, ViewTransform = Matrix3x2.CreateScale(.5f) };
        controller.SelectShape(kind, sticky: true);
        Drag(controller, new(100, 100), new(250, 200));
        int faceCount = controller.ShapePreview.Count;
        var retryPoint = kind == ShapeKind.SquarePyramid ? new Vector2(175, 150) : new Vector2(400, 300);
        Drag(controller, retryPoint, retryPoint + Vector2.UnitX);
        Assert.Equal(0, controller.Document.Count);
        Assert.Equal(faceCount, controller.ShapePreview.Count);
        Drag(controller, new(400, 300), new(460, 260));
        Assert.NotEmpty(controller.Document.Strokes);
        Assert.Equal(1, controller.History.UndoCount);
    }

    [Theory]
    [InlineData(ShapeKind.TriangularPrism)]
    [InlineData(ShapeKind.PentagonalPrism)]
    [InlineData(ShapeKind.HexagonalPrism)]
    [InlineData(ShapeKind.SquarePyramid)]
    public void HiddenEdgesChangeWhenTheFirstFaceBecomesTheBackFace(ShapeKind kind)
    {
        var first = new Vector2(100, 100); var second = new Vector2(260, 230);
        var face = ShapeGenerator.Generate(kind, first, second, new());
        var near = ShapeGenerator.GenerateSolid(kind, first, second, new(160, -120), new());
        var far = ShapeGenerator.GenerateSolid(kind, first, second, new(160, 120), new());
        bool WholeEdge(List<InkStroke> strokes, InkStroke edge) => strokes.Any(stroke => stroke.Count == 2
            && ((Vector2.Distance(stroke.Points[0].Position, edge.Points[0].Position) < .01f && Vector2.Distance(stroke.Points[1].Position, edge.Points[1].Position) < .01f)
            || (Vector2.Distance(stroke.Points[1].Position, edge.Points[0].Position) < .01f && Vector2.Distance(stroke.Points[0].Position, edge.Points[1].Position) < .01f)));
        Assert.All(face, edge => Assert.True(WholeEdge(near, edge)));
        Assert.Contains(face, edge => !WholeEdge(far, edge));
        Assert.All(far.Where(stroke => stroke.ArcLength() <= ShapeGenerator.DashLength + .01f), stroke => Assert.True(stroke.ArcLength() > 0));
    }

    [Fact]
    public void CubeKeepsSquareFacesAndFixedProjectedDepth()
    {
        var first = new Vector2(100, 100); var second = new Vector2(260, 180);
        var face = ShapeGenerator.Generate(ShapeKind.Cube, first, second, new());
        Assert.All(face, edge => Assert.Equal(160, edge.ArcLength(), 2));
        var a = ShapeGenerator.GenerateSolid(ShapeKind.Cube, first, second, new(60, -45), new());
        var b = ShapeGenerator.GenerateSolid(ShapeKind.Cube, first, second, new(120, -90), new());
        Assert.Equal(a.SelectMany(stroke => stroke.Points).Select(point => point.Position), b.SelectMany(stroke => stroke.Points).Select(point => point.Position));
    }

    [Fact]
    public void PyramidApexTracksTheSecondPointerWithoutAnOffsetJump()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.SelectShape(ShapeKind.SquarePyramid);
        Drag(controller, new(100, 200), new(300, 280));
        controller.OnPointerDown(Pointer(new(500, 400)));
        controller.OnPointerMove(Pointer(new(200, 60)));
        Assert.Contains(controller.ShapePreview.SelectMany(stroke => stroke.Points), point => Vector2.Distance(point.Position, new Vector2(200, 60)) < .01f);
        controller.OnPointerUp(Pointer(new(200, 60)));
        Assert.Contains(controller.Document.Strokes.SelectMany(stroke => stroke.Points), point => Vector2.Distance(point.Position, new Vector2(200, 60)) < .01f);
    }

    [Theory]
    [InlineData(35f)]
    [InlineData(180f)]
    [InlineData(-35f)]
    [InlineData(-180f)]
    public void FrustumSidesAreTangentAndStayInsideTheDragBounds(float height)
    {
        var first = new Vector2(100, 200); var second = new Vector2(340, 200 + height);
        var strokes = ShapeGenerator.Generate(ShapeKind.ConeFrustum, first, second, new());
        float rx = 120, ry = MathF.Min(rx / ShapeGenerator.SolidEllipseRatio, MathF.Abs(height) / 4);
        var baseCenter = new Vector2(220, height > 0 ? second.Y - ry : second.Y + ry);
        foreach (var side in strokes.TakeLast(2))
        {
            var radius = side.Points[0].Position - baseCenter;
            var normal = Vector2.Normalize(new Vector2(radius.X / (rx * rx), radius.Y / (ry * ry)));
            var direction = Vector2.Normalize(side.Points[1].Position - side.Points[0].Position);
            Assert.True(MathF.Abs(Vector2.Dot(normal, direction)) < .0001f);
        }
        Assert.All(strokes.SelectMany(stroke => stroke.Points), point =>
        {
            Assert.InRange(point.X, 99.99f, 340.01f);
            Assert.InRange(point.Y, MathF.Min(first.Y, second.Y) - .01f, MathF.Max(first.Y, second.Y) + .01f);
        });
    }

    [Fact]
    public void StickyHyperbolaAlsoKeepsItsToolAfterSecondDrag()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.SelectShape(ShapeKind.Hyperbola, sticky: true);
        Drag(controller, new(100, 100), new(200, 200)); Drag(controller, new(150, 150), new(200, 150));
        Assert.Equal(InkTool.Shape, controller.Tool);
        Assert.True(controller.StickyShape);
        Assert.Empty(controller.ShapePreview);
        controller.Tool = InkTool.Pen;
        Assert.False(controller.StickyShape);
    }

    private static OverlayPointer Pointer(Vector2 point) => new(1, point.X, point.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 1);
    private static void Drag(InkController controller, Vector2 from, Vector2 to)
    {
        controller.OnPointerDown(Pointer(from)); controller.OnPointerMove(Pointer(to)); controller.OnPointerUp(Pointer(to));
    }
}
