using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class EraserPreviewTests
{
    [Fact]
    public void PenCursorFollowsMouseAndPenWithoutChangingInk()
    {
        using var controller = new InkController { Tool = InkTool.Pen, ShowPenCursor = true };
        controller.OnPointerHover(Sample());
        Assert.True(controller.IsPenCursorVisible);
        Assert.False(controller.IsEraserPreviewVisible);
        Assert.True(controller.PenCursorVisualBounds.Contains(new(150, 100)));
        controller.HideEraserPreview();
        Assert.False(controller.IsPenCursorVisible);
        controller.OnPointerHover(Sample(OverlayPointerKind.Touch));
        Assert.False(controller.IsPenCursorVisible);
        controller.OnPointerHover(Sample(OverlayPointerKind.Pen));
        Assert.True(controller.IsPenCursorVisible);
        controller.ShowPenCursor = false;
        Assert.False(controller.IsPenCursorVisible);
        Assert.Empty(controller.Document.Strokes);
        Assert.False(controller.History.CanUndo);
    }
    private static OverlayPointer Sample(OverlayPointerKind kind = OverlayPointerKind.Mouse, uint id = 1)
        => new(id, 150, 100, .5f, 1, 1, kind, 100);

    [Theory]
    [InlineData(OverlayPointerKind.Mouse)]
    [InlineData(OverlayPointerKind.Pen)]
    public void HoverShowsAreaWithoutChangingInkOrHistory(OverlayPointerKind kind)
    {
        using var controller = new InkController { Tool = InkTool.Eraser, EraserRadius = 30 };
        controller.OnPointerHover(Sample(kind));
        Assert.True(controller.IsEraserPreviewVisible);
        Assert.Equal(new Vector2(150, 100), controller.EraserPreviewCenter);
        Assert.Equal(30, controller.EraserPreviewRadius);
        Assert.Empty(controller.Document.Strokes);
        Assert.Equal(0, controller.History.UndoCount);
    }

    [Theory]
    [InlineData(OverlayPointerKind.Mouse)]
    [InlineData(OverlayPointerKind.Pen)]
    [InlineData(OverlayPointerKind.Touch)]
    public void ContactMovesPreviewAndTouchLiftHidesIt(OverlayPointerKind kind)
    {
        using var controller = new InkController { Tool = InkTool.Eraser };
        var sample = Sample(kind);
        controller.OnPointerDown(sample);
        Assert.True(controller.IsEraserPreviewVisible);
        controller.OnPointerMove(sample with { X = 190 });
        Assert.Equal(new Vector2(190, 100), controller.EraserPreviewCenter);
        controller.OnPointerUp(sample with { X = 200 });
        Assert.Equal(kind != OverlayPointerKind.Touch, controller.IsEraserPreviewVisible);
    }

    [Fact]
    public void TouchHoverDoesNotShowAPreview()
    {
        using var controller = new InkController { Tool = InkTool.Eraser };
        controller.OnPointerHover(Sample(OverlayPointerKind.Touch));
        Assert.False(controller.IsEraserPreviewVisible);
    }

    [Fact]
    public void PreviewMatchesActualEraserAtZoomAndAfterSizeChanges()
    {
        using var controller = new InkController
        {
            Tool = InkTool.Eraser, EraserRadius = 10,
            ViewTransform = Matrix3x2.CreateScale(2) * Matrix3x2.CreateTranslation(50, 20),
        };
        var sample = Sample();
        controller.OnPointerHover(sample);
        Assert.Equal(new Vector2(50, 40), controller.EraserPreviewCenter);
        Assert.Equal(46, controller.EraserVisualBounds.Width);
        int invalidations = 0;
        controller.EraserPreviewChanged += () => invalidations++;
        controller.EraserRadius = 25;
        Assert.Equal(106, controller.EraserVisualBounds.Width);
        Assert.Equal(1, invalidations);
        controller.ViewTransform = Matrix3x2.CreateRotation(.6f) * Matrix3x2.CreateScale(2, 3);
        Assert.Equal(106, controller.EraserVisualBounds.Width, 3);
        Assert.Equal(156, controller.EraserVisualBounds.Height, 3);
    }

    [Fact]
    public void AnotherContactCannotMoveOrHideTheActiveEraser()
    {
        using var controller = new InkController { Tool = InkTool.Eraser };
        var first = Sample(OverlayPointerKind.Touch);
        var other = Sample(OverlayPointerKind.Touch, 2) with { X = 300 };
        controller.OnPointerDown(first);
        controller.OnPointerDown(other);
        controller.OnPointerMove(other);
        controller.OnPointerUp(other);
        Assert.True(controller.IsEraserPreviewVisible);
        Assert.Equal(new Vector2(150, 100), controller.EraserPreviewCenter);
        controller.OnPointerUp(first);
        Assert.False(controller.IsEraserPreviewVisible);
    }

    [Theory]
    [InlineData(InkEraserMode.WholeStroke)]
    [InlineData(InkEraserMode.Lasso)]
    public void OtherModesDoNotShowAreaPreview(InkEraserMode mode)
    {
        using var controller = new InkController { Tool = InkTool.Eraser };
        controller.OnPointerHover(Sample());
        controller.EraserMode = mode;
        controller.OnPointerHover(Sample());
        Assert.False(controller.IsEraserPreviewVisible);
        Assert.True(controller.EraserVisualBounds.IsEmpty);
    }

    [Fact]
    public void ExitingCancelingOrSwitchingToolsClearsPreview()
    {
        using var controller = new InkController { Tool = InkTool.Eraser };
        var sample = Sample();
        controller.OnPointerHover(sample);
        controller.HideEraserPreview(2);
        Assert.True(controller.IsEraserPreviewVisible);
        controller.HideEraserPreview(1);
        Assert.False(controller.IsEraserPreviewVisible);
        controller.OnPointerDown(sample);
        controller.OnPointerCanceled(sample);
        Assert.False(controller.IsEraserPreviewVisible);
        controller.OnPointerHover(sample);
        controller.Tool = InkTool.Pen;
        Assert.False(controller.IsEraserPreviewVisible);
    }

    [Fact]
    public void PreviewMovementInvalidatesWithoutEditingTheDocument()
    {
        using var controller = new InkController { Tool = InkTool.Eraser };
        int invalidations = 0, edits = 0;
        controller.EraserPreviewChanged += () => invalidations++;
        controller.Document.Changed += () => edits++;
        controller.OnPointerHover(Sample());
        controller.OnPointerHover(Sample() with { X = 160 });
        controller.OnPointerHover(Sample() with { X = 160, TimeMs = 110 });
        Assert.Equal(2, invalidations);
        Assert.Equal(0, edits);
    }
}
