using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class SelectionTouchTests
{
    private static InkStroke Rectangle()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
        foreach (var point in new[] { new Vector2(100, 100), new Vector2(400, 100), new Vector2(400, 320),
            new Vector2(100, 320), new Vector2(100, 100) }) stroke.AddPoint(point.X, point.Y);
        stroke.Complete();
        return stroke;
    }

    private static OverlayPointer Touch(uint id, Vector2 point, long surface = 10) =>
        new(id, point.X, point.Y, .5f, 4, 4, OverlayPointerKind.Touch, 1) { InputSurface = surface };

    private static void Near(Vector2 expected, Vector2 actual) =>
        Assert.True(Vector2.Distance(expected, actual) < .015f, $"Expected {expected}, actual {actual}");

    [Fact]
    public void PresentationGatePreventsMultiFingerSelectionScaleAndRotation()
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true, TouchGesturesEnabled = false };
        var source = Rectangle(); controller.Document.Add(source); controller.SelectAll();
        var before = source.ObjectFrame;
        var first = Touch(1, new(180, 210)); var second = Touch(2, new(320, 210));
        controller.OnPointerDown(first); controller.OnPointerDown(second);
        controller.OnPointerMove(second with { X = 370, Y = 270 });
        Assert.Equal(Matrix3x2.Identity, controller.SelectionPreviewTransform);
        controller.OnPointerUp(second with { X = 370, Y = 270 }); controller.OnPointerUp(first);
        Assert.Equal(before, source.ObjectFrame); Assert.False(controller.HasActiveInteraction);
    }

    [Fact]
    public void PresentationGestureGateRetainsTouchStrokeSamples()
    {
        using var controller = new InkController { MultiTouchEnabled = false, TouchGesturesEnabled = false, PalmErases = false, RecognizeShapes = false };
        var first = Touch(1, new(100, 100)); var second = Touch(2, new(300, 100));
        controller.OnPointerDown(first); controller.OnPointerDown(second);
        controller.OnPointerMove(first with { X = 180, Y = 130, TimeMs = 30 });
        controller.OnPointerMove(second with { X = 380, Y = 130, TimeMs = 30 });
        Assert.False(controller.IsViewManipulating);
        controller.OnPointerUp(first with { X = 180, Y = 130, TimeMs = 40 });
        controller.OnPointerUp(second with { X = 380, Y = 130, TimeMs = 40 });
        Assert.Equal(2, controller.Document.Count);
        Assert.All(controller.Document.Strokes, stroke => Assert.True(stroke.Points.Count > 1 && stroke.Bounds.Width > 60));
        Assert.Equal(Matrix3x2.Identity, controller.ViewTransform); Assert.False(controller.HasActiveInteraction);
    }

    [Theory]
    [InlineData(.5f)]
    [InlineData(1)]
    [InlineData(2)]
    public void MultiFingerWritingAllowsSelectionPinchAndFreeRotationWithCanvasGesturesOff(float zoom)
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true,
            ViewTransform = Matrix3x2.CreateRotation(.3f) * Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateTranslation(-500, 70),
            TransformCenterOption = MatrixTransformCenterPointOptions.CanvasCenterPoint };
        controller.ConfigureGestures(false, false, false);
        var source = Rectangle();
        controller.Document.Add(source); controller.SelectAll();
        var before = source.Points.ToArray();
        var view = controller.ViewTransform;
        var a = Touch(1, Vector2.Transform(new Vector2(180, 210), view));
        var b = Touch(2, Vector2.Transform(new Vector2(320, 210), view));
        var midpoint = new Vector2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var screenTransform = Matrix3x2.CreateTranslation(-midpoint) * Matrix3x2.CreateScale(1.5f)
            * Matrix3x2.CreateRotation(37 * MathF.PI / 180) * Matrix3x2.CreateTranslation(midpoint + new Vector2(50, 30));
        var movedA = Vector2.Transform(new Vector2(a.X, a.Y), screenTransform);
        var movedB = Vector2.Transform(new Vector2(b.X, b.Y), screenTransform);
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        controller.OnPointerMove(a with { X = movedA.X, Y = movedA.Y });
        controller.OnPointerMove(b with { X = movedB.X, Y = movedB.Y });
        Assert.Equal(before, source.Points);
        Assert.Equal(view, controller.ViewTransform);
        Assert.Single(controller.PreviewStrokes);
        Assert.Equal(0, controller.History.UndoCount);
        Near(movedA, Vector2.Transform(new Vector2(180, 210), controller.SelectionPreviewTransform * view));
        Near(movedB, Vector2.Transform(new Vector2(320, 210), controller.SelectionPreviewTransform * view));
        controller.OnPointerUp(a with { X = movedA.X, Y = movedA.Y });
        Assert.Equal(0, controller.History.UndoCount);
        controller.OnPointerUp(b with { X = movedB.X, Y = movedB.Y });
        Assert.Equal(1, controller.History.UndoCount);
        Assert.False(controller.HasActiveInteraction);
        foreach (var point in before)
        {
            int index = Array.IndexOf(before, point);
            Near(Vector2.Transform(point.Position, view * screenTransform), Vector2.Transform(source.Points[index].Position, view));
        }
        var after = source.Points.ToArray();
        controller.Undo(); Assert.Equal(before, source.Points);
        controller.Redo(); Assert.Equal(after, source.Points);
    }

    [Fact]
    public void OneFingerDragPinchAndRemainingFingerFormOneContinuousUndoStep()
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true };
        var source = Rectangle(); controller.Document.Add(source); controller.SelectAll();
        var a = Touch(1, new Vector2(180, 210));
        var b = Touch(2, new Vector2(350, 230));
        controller.OnPointerDown(a);
        a = a with { X = 210, Y = 230 };
        controller.OnPointerMove(a);
        var beforePinch = controller.SelectionPreviewTransform;
        controller.OnPointerDown(b);
        Assert.Equal(beforePinch, controller.SelectionPreviewTransform);
        Assert.Equal(0, controller.History.UndoCount);
        a = a with { X = 190, Y = 230 };
        b = b with { X = 370, Y = 230 };
        controller.OnPointerMove(a); controller.OnPointerMove(b);
        var afterPinch = controller.SelectionPreviewTransform;
        controller.OnPointerUp(a);
        Assert.Equal(afterPinch, controller.SelectionPreviewTransform);
        b = b with { X = b.X + 25, Y = b.Y + 15 };
        controller.OnPointerMove(b);
        Near(Vector2.Transform(Vector2.Zero, afterPinch) + new Vector2(25, 15),
            Vector2.Transform(Vector2.Zero, controller.SelectionPreviewTransform));
        controller.OnPointerUp(b);
        Assert.Equal(1, controller.History.UndoCount);
        controller.Undo(); Near(new Vector2(100, 100), source.Points[0].Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedInkImagesAndVideoShareTheGestureAndOneUndoStep(bool clone)
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true };
        var store = new BoardService(controller);
        controller.GetSelectableElements = store.GetSelectionElements;
        var source = Rectangle(); controller.Document.Add(source);
        var image = new BoardImageAsset { Left = 130, Top = 130, Width = 100, Height = 80, Data = new byte[] { 1 } };
        var media = new BoardMediaAsset { Left = 260, Top = 140, Width = 100, Height = 80, Data = new byte[] { 2 } };
        store.AddImage(image); store.AddMedia(media); controller.History.Clear();
        controller.SelectAll(); controller.CloneMode = clone;
        var original = source.Points.ToArray();
        var a = Touch(1, new Vector2(180, 210));
        var b = Touch(2, new Vector2(320, 210));
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        a = a with { X = 250, Y = 105 };
        b = b with { X = 250, Y = 315 };
        controller.OnPointerMove(a); controller.OnPointerMove(b);
        Assert.Equal(original, source.Points);
        Assert.Equal(clone ? 2 : 1, controller.Document.Count);
        Assert.Equal(clone ? 2 : 1, store.CurrentImages.Count);
        Assert.Equal(clone ? 2 : 1, store.CurrentMedia.Count);
        controller.OnPointerUp(a); controller.OnPointerUp(b);
        Assert.Equal(1, controller.History.UndoCount);
        var expected = Matrix3x2.CreateScale(1.5f, new Vector2(250, 210))
            * Matrix3x2.CreateRotation(MathF.PI / 2, new Vector2(250, 210));
        var movedImage = store.CurrentImages.Last();
        var movedMedia = store.CurrentMedia.Last();
        Near(Vector2.Transform(new Vector2(130, 130), expected),
            Vector2.Transform(new Vector2(130, 130), BoardElementMath.Read(movedImage.Transform)));
        Near(Vector2.Transform(new Vector2(260, 140), expected),
            Vector2.Transform(new Vector2(260, 140), BoardElementMath.Read(movedMedia.Transform)));
        if (clone) { Assert.Null(image.Transform); Assert.Null(media.Transform); }
        controller.Undo(); Assert.Equal(original, source.Points);
        Assert.Single(store.CurrentImages); Assert.Single(store.CurrentMedia);
        Assert.Null(image.Transform); Assert.Null(media.Transform);
        controller.Redo();
        Assert.Equal(clone ? 2 : 1, store.CurrentImages.Count);
        Assert.Equal(clone ? 2 : 1, store.CurrentMedia.Count);
    }

    [Fact]
    public void RotationSwitchStillControlsTheSelectionWhilePinchRemainsAvailable()
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = false };
        controller.ConfigureGestures(false, false, false);
        controller.Document.Add(Rectangle()); controller.SelectAll();
        var a = Touch(1, new Vector2(180, 210)); var b = Touch(2, new Vector2(320, 210));
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        a = a with { X = 250, Y = 105 }; b = b with { X = 250, Y = 315 };
        controller.OnPointerMove(a); controller.OnPointerMove(b);
        var preview = controller.SelectionPreviewTransform;
        Assert.InRange(preview.M11, 1.499f, 1.501f);
        Assert.Equal(0, preview.M12); Assert.Equal(0, preview.M21);
        controller.OnPointerUp(a); controller.OnPointerUp(b);
        Assert.Equal(1, controller.History.UndoCount);
    }

    [Fact]
    public void GestureModeKeepsItsConfiguredZoomAndTranslationSwitches()
    {
        using var controller = new InkController { MultiTouchEnabled = false, GesturesTargetSelection = false };
        controller.ConfigureGestures(false, true, false);
        controller.Document.Add(Rectangle()); controller.SelectAll();
        var a = Touch(1, new Vector2(180, 210)); var b = Touch(2, new Vector2(320, 210));
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        a = a with { X = 150, Y = 240 }; b = b with { X = 390, Y = 240 };
        controller.OnPointerMove(a); controller.OnPointerMove(b);
        Assert.Equal(Matrix3x2.CreateTranslation(20, 30), controller.SelectionPreviewTransform);
        controller.OnPointerUp(a); controller.OnPointerUp(b);
        Assert.Equal(1, controller.History.UndoCount);
    }

    [Fact]
    public void ThirdFingerAndContactsOnAnotherDisplayCannotChangeTheActivePair()
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true };
        var source = Rectangle(); controller.Document.Add(source); controller.SelectAll();
        var a = Touch(1, new Vector2(180, 210)); var b = Touch(2, new Vector2(320, 210));
        var extra = Touch(3, new Vector2(250, 250));
        var remote = Touch(4, new Vector2(200, 180), surface: 20);
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        controller.OnPointerDown(extra); controller.OnPointerDown(remote);
        controller.OnPointerMove(extra with { Y = 280 });
        controller.OnPointerMove(remote with { X = 350 });
        Assert.Null(controller.PreviewStrokes);
        a = a with { X = 200, Y = 220 }; b = b with { X = 340, Y = 220 };
        controller.OnPointerMove(a); controller.OnPointerMove(b);
        var before = controller.SelectionPreviewTransform;
        controller.OnPointerUp(remote);
        Assert.Equal(before, controller.SelectionPreviewTransform);
        controller.OnPointerUp(a);
        Assert.Equal(before, controller.SelectionPreviewTransform);
        controller.OnPointerUp(extra with { Y = 280 });
        controller.OnPointerUp(b);
        Assert.False(controller.HasActiveInteraction);
        Assert.Equal(1, controller.History.UndoCount);
        controller.Undo(); Near(new Vector2(100, 100), source.Points[0].Position);
    }

    [Fact]
    public void BlankSpaceStillSupportsSimultaneousWritingAndMarqueeSelection()
    {
        using var controller = new InkController { MultiTouchEnabled = true, PalmErases = false, RecognizeShapes = false };
        var a = Touch(1, new Vector2(500, 400)); var b = Touch(2, new Vector2(650, 400));
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        controller.OnPointerMove(a with { X = 520 }); controller.OnPointerMove(b with { X = 670 });
        Assert.Equal(2, controller.LiveStrokes.Count);
        controller.OnPointerUp(a with { X = 520 }); controller.OnPointerUp(b with { X = 670 });
        Assert.Equal(2, controller.Document.Count);
        controller.Tool = InkTool.Select;
        controller.OnPointerDown(Touch(3, new Vector2(450, 350)));
        controller.OnPointerMove(Touch(3, new Vector2(700, 450)));
        controller.OnPointerDown(Touch(4, new Vector2(550, 400)));
        Assert.True(controller.IsMarqueeSelecting);
        Assert.Equal(2, controller.Selection.Count);
        controller.OnPointerUp(Touch(3, new Vector2(700, 450)));
        controller.OnPointerUp(Touch(4, new Vector2(550, 400)));
        Assert.False(controller.HasActiveInteraction);
        Assert.Equal(2, controller.Selection.Count);
        Assert.Equal(Matrix3x2.Identity, controller.ViewTransform);
    }

    [Fact]
    public void SelectionTouchesOnDifferentDisplaysDoNotStartAPinch()
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true };
        var source = Rectangle(); controller.Document.Add(source); controller.SelectAll();
        var a = Touch(1, new Vector2(180, 210));
        var b = Touch(2, new Vector2(320, 210), surface: 20);
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        controller.OnPointerMove(b with { X = 390, Y = 290 });
        Assert.Null(controller.PreviewStrokes);
        controller.OnPointerUp(b with { X = 390, Y = 290 }); controller.OnPointerUp(a);
        Assert.Equal(0, controller.History.UndoCount);
        Near(new Vector2(100, 100), source.Points[0].Position);
        Assert.False(controller.HasActiveInteraction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoFingerTapAtARotatedViewDoesNotTransformOrClone(bool clone)
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true,
            ViewTransform = Matrix3x2.CreateScale(1.25f) * Matrix3x2.CreateRotation(.37f) * Matrix3x2.CreateTranslation(80, 60) };
        var source = Rectangle(); controller.Document.Add(source); controller.SelectAll(); controller.CloneMode = clone;
        var a = Touch(1, Vector2.Transform(new Vector2(180, 210), controller.ViewTransform));
        var b = Touch(2, Vector2.Transform(new Vector2(320, 210), controller.ViewTransform));
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        controller.OnPointerUp(a); controller.OnPointerUp(b);
        Assert.Single(controller.Document.Strokes);
        Assert.Equal(0, controller.History.UndoCount);
        Assert.False(controller.HasActiveInteraction);
        Assert.Null(controller.PreviewStrokes);
    }

    [Fact]
    public void ResizingWithOneFingerCanPromoteToAPinchWithoutCommittingTwice()
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true };
        var source = Rectangle(); controller.Document.Add(source); controller.SelectAll();
        var original = source.Points.ToArray();
        var corner = controller.SelectionFrame.HandlePosition(Core.Selection.SelectionHandle.TopRight, controller.ViewTransform);
        var a = Touch(1, corner);
        controller.OnPointerDown(a);
        a = a with { X = a.X + 40, Y = a.Y - 20 };
        controller.OnPointerMove(a);
        var resized = source.Points.ToArray();
        var b = Touch(2, new Vector2(200, 220));
        controller.OnPointerDown(b);
        Assert.Equal(original, source.Points);
        Assert.Equal(0, controller.History.UndoCount);
        var preview = controller.SelectionPreviewTransform;
        for (int i = 0; i < original.Length; i++) Near(resized[i].Position, Vector2.Transform(original[i].Position, preview));
        controller.OnPointerUp(a); controller.OnPointerUp(b);
        Assert.Equal(1, controller.History.UndoCount);
        for (int i = 0; i < resized.Length; i++) Near(resized[i].Position, source.Points[i].Position);
        controller.Undo(); Assert.Equal(original, source.Points);
    }

    [Fact]
    public void CancelAndToolSwitchFinishTheGestureWithoutDuplicatingInkOrHistory()
    {
        using var controller = new InkController { MultiTouchEnabled = true, GesturesTargetSelection = true, RecognizeShapes = false };
        controller.Document.Add(Rectangle()); controller.SelectAll();
        var a = Touch(1, new Vector2(180, 210)); var b = Touch(2, new Vector2(320, 210));
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        a = a with { Y = 240 }; controller.OnPointerMove(a);
        controller.OnPointerCanceled(a);
        controller.Tool = InkTool.Pen;
        controller.OnPointerCanceled(b); controller.OnPointerUp(b);
        Assert.False(controller.HasActiveInteraction);
        Assert.Null(controller.PreviewStrokes);
        Assert.Equal(1, controller.History.UndoCount);
        controller.OnPointerDown(Touch(5, new Vector2(500, 400)));
        controller.OnPointerUp(Touch(5, new Vector2(500, 400)));
        Assert.Equal(2, controller.Document.Count);
    }
}
