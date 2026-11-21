using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Recognition;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class LineRecognitionTests
{
    private static OverlayPointer Sample(float x, float y, uint time, uint id = 1)
        => new(id, x, y, .5f, 0, 0, OverlayPointerKind.Pen, time);

    private static OverlayPointer Draw(InkController controller, uint start = 0, uint id = 1, float y = 100)
    {
        controller.OnPointerDown(Sample(100, y, start, id));
        for (uint i = 1; i <= 30; i++)
            controller.OnPointerMove(Sample(100 + i * 5, y + MathF.Sin(i * .2f) * .5f, start + i * 8, id));
        return Sample(250, y, start + 240, id);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(200u, false)]
    [InlineData(449u, false)]
    [InlineData(450u, true)]
    [InlineData(800u, true)]
    public void LineRequiresHoldingAtItsEndpoint(uint hold, bool recognized)
    {
        using var controller = new InkController();
        var end = Draw(controller);
        controller.OnPointerUp(end with { TimeMs = end.TimeMs + hold });
        Assert.Equal(recognized, Assert.Single(controller.Document.Strokes).IsRecognizedShape);
    }

    [Fact]
    public void SlowWritingDoesNotCountAsAnEndpointHold()
    {
        using var controller = new InkController();
        controller.OnPointerDown(Sample(0, 100, 0));
        for (uint i = 1; i <= 30; i++) controller.OnPointerMove(Sample(i * 5, 100, i * 100));
        controller.OnPointerUp(Sample(150, 100, 3010));
        Assert.False(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
    }

    [Fact]
    public void HoldingBeforeMovingDoesNotStraightenTheStroke()
    {
        using var controller = new InkController();
        controller.OnPointerDown(Sample(100, 100, 0));
        controller.OnPointerMove(Sample(100, 100, 700));
        for (uint i = 1; i <= 30; i++) controller.OnPointerMove(Sample(100 + i * 5, 100, 700 + i * 8));
        controller.OnPointerUp(Sample(250, 100, 950));
        Assert.False(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
    }

    [Fact]
    public void EndpointJitterAllowsRecognitionAndUndoRestoresOriginalInk()
    {
        using var controller = new InkController();
        var end = Draw(controller);
        var original = Assert.Single(controller.LiveStrokes);
        for (uint i = 1; i <= 10; i++)
            controller.OnPointerMove(end with { X = 250 + (i % 2 == 0 ? 1 : -1), TimeMs = end.TimeMs + i * 50 });
        controller.OnPointerUp(end with { TimeMs = end.TimeMs + 520 });
        Assert.True(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
        controller.Undo();
        Assert.Same(original, Assert.Single(controller.Document.Strokes));
        controller.Redo();
        Assert.True(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
    }

    [Fact]
    public void ContinuingAfterAHoldRequiresANewHold()
    {
        using var controller = new InkController();
        var end = Draw(controller);
        controller.OnPointerMove(end with { TimeMs = 800 });
        controller.OnPointerMove(end with { X = 270, TimeMs = 820 });
        controller.OnPointerUp(end with { X = 270, TimeMs = 830 });
        Assert.False(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
    }

    [Fact]
    public void ChineseStrokesRemainSeparateAndHoldingOnlyReplacesCurrentStroke()
    {
        using var controller = new InkController();
        var first = Draw(controller);
        controller.OnPointerUp(first);
        var original = Assert.Single(controller.Document.Strokes);
        var second = Draw(controller, 300, 2, 103);
        controller.OnPointerUp(second with { TimeMs = second.TimeMs + 500 });
        Assert.Equal(2, controller.Document.Count);
        Assert.Contains(original, controller.Document.Strokes);
        Assert.False(original.IsRecognizedShape);
        Assert.True(controller.Document.Strokes[1].IsRecognizedShape);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HighlighterAndCancelledInputDoNotTriggerLines(bool highlighter, bool canceled)
    {
        using var controller = new InkController { Tool = highlighter ? InkTool.Highlighter : InkTool.Pen };
        var end = Draw(controller) with { TimeMs = 900 };
        if (canceled) controller.OnPointerCanceled(end);
        else controller.OnPointerUp(end);
        Assert.False(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
    }

    [Fact]
    public void PointerTimestampRolloverPreservesTheHoldDuration()
    {
        using var controller = new InkController();
        var end = Draw(controller, uint.MaxValue - 300);
        controller.OnPointerUp(end with { TimeMs = unchecked(end.TimeMs + 500) });
        Assert.True(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
    }

    [Fact]
    public void ExplicitLineToolDoesNotNeedAHold()
    {
        using var controller = new InkController { Tool = InkTool.Shape, ShapeKind = ShapeKind.Line };
        controller.OnPointerDown(Sample(100, 100, 0));
        controller.OnPointerUp(Sample(250, 100, 20));
        Assert.True(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
    }

    [Fact]
    public void AHookIsNotALineEvenWhenItsOverallSpreadIsSmall()
    {
        var stroke = new InkStroke(new StrokeAttributes());
        for (int i = 0; i <= 40; i++) stroke.AddPoint(i * 5, 100);
        stroke.AddPoint(199, 92); stroke.AddPoint(196, 85);
        Assert.NotEqual(RecognizedShape.Line, ShapeRecognizer.Recognize(new[] { stroke }).Shape);
    }

    [Fact]
    public void RetracingAStrokeIsNotALine()
    {
        var stroke = new InkStroke(new StrokeAttributes());
        for (int i = 0; i <= 40; i++) stroke.AddPoint(i * 5, 100);
        for (int i = 39; i >= 25; i--) stroke.AddPoint(i * 5, 100);
        Assert.NotEqual(RecognizedShape.Line, ShapeRecognizer.Recognize(new[] { stroke }).Shape);
    }

    [Fact]
    public void SeparateCollinearStrokesAreNotRecognizedTogether()
    {
        var strokes = new[] { new InkStroke(new StrokeAttributes()), new InkStroke(new StrokeAttributes()) };
        for (int i = 0; i < 20; i++)
        {
            strokes[0].AddPoint(i * 5, 100);
            strokes[1].AddPoint(100 + i * 5, 100);
        }
        Assert.NotEqual(RecognizedShape.Line, ShapeRecognizer.Recognize(strokes).Shape);
    }
}
