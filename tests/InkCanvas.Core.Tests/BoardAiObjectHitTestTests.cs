using System.Numerics;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>
/// Pure native-object hit tests: world/view coordinate conversion, tolerance, boundary and stacking.
/// </summary>
public sealed class BoardAiObjectHitTestTests
{
    private static BoardTextObject Text(float left, float top, float width, float height,
        long? layer = 0, float[] transform = null) => new()
    {
        Content = "文本",
        Left = left,
        Top = top,
        Width = width,
        Height = height,
        LayerOrder = layer,
        TransformMatrix = transform,
    };

    private static BoardFormulaObject Formula(float left, float top, float width, float height, long? layer = 0) => new()
    {
        Latex = "x^2",
        Left = left,
        Top = top,
        Width = width,
        Height = height,
        LayerOrder = layer,
    };

    [Fact]
    public void HitTest_ReturnsTextAndFormulaInsideTheirBounds()
    {
        var text = Text(100, 100, 200, 100, 1);
        var formula = Formula(400, 100, 120, 80, 2);

        Assert.Same(text, BoardAiObjectHitTest.HitTest(new[] { text }, new[] { formula }, 150, 150));
        Assert.Same(formula, BoardAiObjectHitTest.HitTest(new[] { text }, new[] { formula }, 450, 140));
        Assert.Null(BoardAiObjectHitTest.HitTest(new[] { text }, new[] { formula }, 350, 500));
    }

    [Fact]
    public void HitTest_BoundaryIsInclusiveAndToleranceIsBounded()
    {
        var text = Text(100, 100, 200, 100);

        Assert.Same(text, BoardAiObjectHitTest.HitTest(new[] { text }, null, 100, 100));
        Assert.Same(text, BoardAiObjectHitTest.HitTest(new[] { text }, null, 300, 200));
        Assert.Same(text, BoardAiObjectHitTest.HitTest(new[] { text }, null, 300 + BoardAiObjectHitTest.HitTolerance, 150));
        Assert.Null(BoardAiObjectHitTest.HitTest(new[] { text }, null,
            300 + BoardAiObjectHitTest.HitTolerance + 1, 150));
    }

    [Fact]
    public void HitTest_PrefersTopLayerAndCarriesTransformMatrix()
    {
        var lower = Text(100, 100, 200, 100, 1);
        var upper = Formula(120, 120, 80, 60, 9);

        Assert.Same(upper, BoardAiObjectHitTest.HitTest(new[] { lower }, new[] { upper }, 150, 140));

        var translated = Text(0, 0, 100, 50, 1,
            BoardElementMath.Write(Matrix3x2.CreateTranslation(200, 100)));
        Assert.Same(translated, BoardAiObjectHitTest.HitTest(new[] { translated }, null, 250, 120));
        Assert.Null(BoardAiObjectHitTest.HitTest(new[] { translated }, null, 50, 50));
    }

    [Fact]
    public void HitTestView_InvertsScaledAndTranslatedViewTransform()
    {
        var text = Text(100, 100, 200, 100);
        Matrix3x2 view = Matrix3x2.CreateScale(2f) * Matrix3x2.CreateTranslation(30f, 40f);

        ISelectionElement hit = BoardAiObjectHitTest.HitTestView(
            new[] { text }, null, 330, 280, view,
            out Vector2 world, out int objectCount, out float nearestDistance);

        Assert.Same(text, hit);
        Assert.Equal(150f, world.X, 2);
        Assert.Equal(120f, world.Y, 2);
        Assert.Equal(1, objectCount);
        Assert.Equal(0f, nearestDistance, 2);

        Assert.Null(BoardAiObjectHitTest.HitTestView(
            new[] { text }, null, 0, 0, view,
            out _, out _, out _));
    }

    [Fact]
    public void HitTest_MissReportsObjectCountAndNearestBoundsDistance()
    {
        var text = Text(100, 100, 200, 100);
        var formula = Formula(500, 100, 100, 100);

        ISelectionElement hit = BoardAiObjectHitTest.HitTest(
            new[] { text }, new[] { formula }, 0, 0,
            out int objectCount, out float nearestDistance);

        Assert.Null(hit);
        Assert.Equal(2, objectCount);
        Assert.Equal(MathF.Sqrt(100 * 100 + 100 * 100), nearestDistance, 2);
    }
}