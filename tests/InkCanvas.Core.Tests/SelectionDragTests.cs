using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class SelectionDragTests
{
    private static OverlayPointer Pointer(Vector2 point, bool control = false) =>
        new(718, point.X, point.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 0, Control: control);

    private static InkStroke Stroke(float y)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
        stroke.AddPoint(100, y); stroke.AddPoint(300, y); stroke.Complete();
        return stroke;
    }

    [Theory]
    [InlineData(.5f)]
    [InlineData(1f)]
    [InlineData(2f)]
    public void ControlDragPreviewsTheCopiesAndSelectsThemAfterOneUndoableCommit(float zoom)
    {
        using var controller = new InkController { ViewTransform = Matrix3x2.CreateScale(zoom) };
        var sources = new[] { Stroke(100), Stroke(200) };
        controller.Document.AddRange(sources); controller.SelectAll();
        var originalCenter = controller.SelectionFrame.Center(controller.ViewTransform);
        var from = new Vector2(200, 150) * zoom;
        controller.OnPointerDown(Pointer(from, control: true));
        for (int step = 1; step <= 4; step++)
        {
            var offset = new Vector2(step * 20, step * 15);
            controller.OnPointerMove(Pointer(from + offset * zoom, control: true));
            Assert.Equal(4, controller.Document.Count);
            Assert.Equal(2, controller.PreviewStrokes.Count);
            Assert.Equal(Matrix3x2.CreateTranslation(offset), controller.SelectionPreviewTransform);
            Assert.Equal(originalCenter + offset * zoom, controller.SelectionFrame.Center(controller.ViewTransform));
            Assert.All(sources, source => Assert.False(controller.IsPreviewStroke(source)));
            Assert.All(controller.PreviewStrokes, copy => Assert.True(controller.IsPreviewStroke(copy)));
            Assert.Equal(0, controller.History.UndoCount);
        }
        controller.OnPointerUp(Pointer(from + new Vector2(80, 60) * zoom, control: true));
        Assert.Equal(1, controller.History.UndoCount);
        Assert.Equal(new Vector2(100, 100), sources[0].Points[0].Position);
        Assert.Equal(new Vector2(180, 160), controller.Selection[0].Points[0].Position);
        Assert.DoesNotContain(sources[0], controller.Selection);
        Assert.False(controller.CloneMode);
        controller.Undo();
        Assert.Equal(2, controller.Document.Count);
        Assert.Equal(sources, controller.Selection);
        controller.Redo();
        Assert.Equal(4, controller.Document.Count);
        Assert.Equal(controller.Document.Strokes.Skip(2), controller.Selection);
    }

    [Fact]
    public void ControlClickWithContactJitterTogglesSelectionWithoutCreatingCopies()
    {
        using var controller = new InkController();
        var source = Stroke(100);
        controller.Document.Add(source); controller.SelectAll();
        controller.OnPointerDown(Pointer(new(200, 100), control: true));
        controller.OnPointerMove(Pointer(new(201, 101), control: true));
        controller.OnPointerUp(Pointer(new(201, 101), control: true));
        Assert.False(controller.HasSelection);
        Assert.Single(controller.Document.Strokes);
        Assert.Equal(0, controller.History.UndoCount);
        Assert.Null(controller.PreviewStrokes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyStrokesStayImmutableThroughoutDragAndCloneCallbacksSeeTheCorrectPreview(bool clone)
    {
        using var controller = new InkController();
        var strokes = Enumerable.Range(0, 600).Select(i => Stroke(100 + i)).ToArray();
        controller.Document.AddRange(strokes); controller.SelectAll(); controller.CloneMode = clone;
        var versions = strokes.Select(stroke => stroke.GeometryVersion).ToArray();
        int changes = 0;
        controller.Document.Changed += () =>
        {
            changes++;
            if (controller.Document.Count == 1200 && controller.PreviewStrokes is not null)
            {
                Assert.Equal(600, controller.PreviewStrokes.Count);
                Assert.All(controller.Document.Strokes.Skip(600), copy => Assert.True(controller.IsPreviewStroke(copy)));
                Assert.NotEqual(Matrix3x2.Identity, controller.SelectionPreviewTransform);
            }
        };
        var from = new Vector2(200, 350);
        controller.OnPointerDown(Pointer(from));
        for (int step = 1; step <= 80; step++) controller.OnPointerMove(Pointer(from + new Vector2(step, step)));
        Assert.Equal(versions, strokes.Select(stroke => stroke.GeometryVersion));
        Assert.Equal(clone ? 1 : 0, changes);
        Assert.Equal(0, controller.History.UndoCount);
        controller.OnPointerUp(Pointer(from + new Vector2(80, 80)));
        Assert.Equal(1, controller.History.UndoCount);
        Assert.Equal(clone ? new Vector2(100, 100) : new Vector2(180, 180), strokes[0].Points[0].Position);
        controller.Undo();
        Assert.Equal(600, controller.Document.Count);
        Assert.Equal(new Vector2(100, 100), strokes[0].Points[0].Position);
    }

    [Fact]
    public void PressingControlDuringAMoveCreatesOneCopyAtTheCurrentPreviewPosition()
    {
        using var controller = new InkController();
        var source = Stroke(100);
        controller.Document.Add(source); controller.SelectAll();
        controller.OnPointerDown(Pointer(new(200, 100)));
        controller.OnPointerMove(Pointer(new(230, 130)));
        controller.OnPointerMove(Pointer(new(250, 140), control: true));
        controller.OnPointerMove(Pointer(new(270, 150), control: true));
        Assert.Equal(2, controller.Document.Count);
        Assert.Equal(Matrix3x2.CreateTranslation(70, 50), controller.SelectionPreviewTransform);
        controller.OnPointerUp(Pointer(new(270, 150)));
        Assert.Equal(new Vector2(100, 100), source.Points[0].Position);
        Assert.Equal(new Vector2(170, 150), controller.Selection.Single().Points[0].Position);
        Assert.Equal(1, controller.History.UndoCount);
    }

    [Fact]
    public void UndoingAControlClonePreservesAnUnrelatedSelection()
    {
        using var controller = new InkController();
        var source = Stroke(100);
        controller.Document.Add(source); controller.SelectAll();
        controller.OnPointerDown(Pointer(new(200, 100), control: true));
        controller.OnPointerUp(Pointer(new(300, 180), control: true));
        var other = Stroke(400);
        controller.Document.Add(other);
        controller.ClearSelection(); controller.Selection.Add(other);
        var frame = controller.SelectionFrame;
        controller.Undo();
        Assert.Same(other, Assert.Single(controller.Selection));
        Assert.Equal(frame, controller.SelectionFrame);
        controller.Redo();
        Assert.Same(other, Assert.Single(controller.Selection));
        Assert.Equal(frame, controller.SelectionFrame);
    }
}
