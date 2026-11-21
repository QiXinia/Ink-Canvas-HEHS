using System.Numerics;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PdfTouchTests
{
    [Fact]
    public void ReaderPinchThenSingleFingerPanDoesNotJumpOrCreateInk()
    {
        using var controller = new InkController { Tool = InkTool.Pan, PanZoomEnabled = true };
        controller.ConfigureGestures(true, true, false);
        var a = new OverlayPointer(1, 100, 100, .5f, 4, 4, OverlayPointerKind.Touch, 100);
        var b = a with { Id = 2, X = 200 };
        controller.OnPointerDown(a); controller.OnPointerDown(b);
        controller.OnPointerMove(b with { X = 250 });
        Assert.True(controller.ViewTransform.GetDeterminant() > 1);
        controller.OnPointerUp(b with { X = 250 });
        var before = controller.ViewTransform;
        controller.OnPointerMove(a with { X = 120, Y = 130 });
        var translation = controller.ViewTransform.Translation - before.Translation;
        Assert.True(Vector2.Distance(translation, new(20, 30)) < .001f);
        controller.OnPointerUp(a with { X = 120, Y = 130 });
        Assert.False(controller.HasActiveInteraction);
        Assert.Empty(controller.Document.Strokes);
    }
    [Fact]
    public void SecondFingerCancelsProvisionalTouchInkAndMovesPdfView()
    {
        using var controller = new InkController { Tool = InkTool.Pen, MultiTouchEnabled = false, RecognizeShapes = false };
        controller.ConfigureGestures(true, true, false);
        var a = new OverlayPointer(1, 100, 100, .5f, 4, 4, OverlayPointerKind.Touch, 100);
        var b = a with { Id = 2, X = 200 };
        controller.OnPointerDown(a); controller.OnPointerMove(a with { X = 102, TimeMs = 110 });
        controller.OnPointerDown(b); controller.OnPointerMove(b with { X = 260, TimeMs = 120 });
        controller.OnPointerUp(b with { X = 260 }); controller.OnPointerUp(a with { X = 102 });
        Assert.Empty(controller.Document.Strokes);
        Assert.Empty(controller.LiveStrokes);
        Assert.NotEqual(Matrix3x2.Identity, controller.ViewTransform);
    }
}
