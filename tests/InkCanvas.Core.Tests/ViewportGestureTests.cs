using System.Numerics;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ViewportGestureTests
{
    private static OverlayPointer Touch(uint id, float x, float y, uint time)
        => new(id, x, y, .5f, 12, 12, OverlayPointerKind.Touch, time);

    [Fact]
    public void PinchKeepsMovingMidpointAnchoredWhileZoomingAndRotating()
    {
        using var controller = new InkController
        {
            Tool = InkTool.Pan,
            PanZoomEnabled = true,
            MultiTouchEnabled = false,
            ViewTransform = Matrix3x2.CreateScale(1.7f) * Matrix3x2.CreateTranslation(38, -24),
        };
        controller.ConfigureGestures(true, true, true);
        var before = controller.ViewTransform;
        Assert.True(Matrix3x2.Invert(before, out var inverse));
        var worldAnchor = Vector2.Transform(new(150, 100), inverse);
        controller.OnPointerDown(Touch(1, 100, 100, 100));
        controller.OnPointerDown(Touch(2, 200, 100, 101));
        controller.OnPointerMove(Touch(2, 200, 150, 110));
        controller.OnPointerMove(Touch(1, 100, 90, 111));

        var after = controller.ViewTransform;
        Assert.InRange(ViewportScale(after), 1.9f, 2.1f);
        Assert.True(MathF.Abs(MathF.Atan2(after.M12, after.M11)) > .08f);
        Assert.True(Vector2.Distance(Vector2.Transform(worldAnchor, after), new(150, 120)) < .01f);
    }

    [Fact]
    public void ReplacingOneFingerRebasesThePairAndPromotesTheRemainingFingerToPan()
    {
        using var controller = new InkController
        {
            Tool = InkTool.Pan,
            PanZoomEnabled = true,
            MultiTouchEnabled = false,
        };
        controller.ConfigureGestures(true, true, true);
        controller.OnPointerDown(Touch(1, 100, 100, 100));
        controller.OnPointerDown(Touch(2, 200, 100, 101));
        controller.OnPointerDown(Touch(3, 200, 200, 102));
        controller.OnPointerMove(Touch(2, 220, 110, 110));
        controller.OnPointerMove(Touch(1, 80, 110, 111));
        controller.OnPointerUp(Touch(1, 80, 110, 112));

        var rebased = controller.ViewTransform;
        controller.OnPointerMove(Touch(2, 220, 110, 120));
        Assert.Equal(rebased, controller.ViewTransform);
        controller.OnPointerUp(Touch(3, 200, 200, 121));
        controller.OnPointerMove(Touch(2, 240, 125, 130));
        Assert.Equal(new Vector2(20, 15), controller.ViewTransform.Translation - rebased.Translation);
        controller.OnPointerUp(Touch(2, 240, 125, 131));
        Assert.False(controller.HasActiveInteraction);
    }

    [Fact]
    public void TouchSamplesCommitOncePerRequestedCompositionFrame()
    {
        using var controller = new InkController
        {
            Tool = InkTool.Pan,
            PanZoomEnabled = true,
            MultiTouchEnabled = false,
            ViewportReleaseMotionEnabled = true,
        };
        int frames = 0;
        controller.ViewportFrameRequested += () => frames++;
        controller.ConfigureGestures(true, true, false);
        controller.OnPointerDown(Touch(1, 100, 100, 100));
        controller.OnPointerDown(Touch(2, 200, 100, 101));
        controller.OnPointerMove(Touch(2, 220, 100, 105));
        controller.OnPointerMove(Touch(1, 90, 100, 106));

        Assert.Equal(Matrix3x2.Identity, controller.ViewTransform);
        Assert.Equal(1, frames);
        controller.CommitViewportFrame();
        Assert.NotEqual(Matrix3x2.Identity, controller.ViewTransform);
    }

    [Fact]
    public void ElasticZoomReturnsToTheConfiguredMaximumAfterRelease()
    {
        using var controller = new InkController
        {
            Tool = InkTool.Pan,
            PanZoomEnabled = true,
            MultiTouchEnabled = false,
            ViewportReleaseMotionEnabled = true,
        };
        controller.ViewportFrameRequested += () => { };
        controller.ConfigureGestures(true, true, false);
        controller.ConfigureViewScaleLimits(.2f, 2);
        controller.OnPointerDown(Touch(1, 100, 100, 100));
        controller.OnPointerDown(Touch(2, 200, 100, 101));
        controller.OnPointerMove(Touch(2, 600, 100, 120));
        controller.OnPointerUp(Touch(2, 600, 100, 121));
        controller.OnPointerUp(Touch(1, 100, 100, 122));
        Assert.True(ViewportScale(controller.ViewTransform) > 2);

        for (int frame = 0; frame < 60 && controller.AdvanceViewportFrame(1f / 60); frame++) { }

        Assert.InRange(ViewportScale(controller.ViewTransform), 1.998f, 2.002f);
    }

    [Fact]
    public void CanceledPinchRebasesTheRemainingFingerAndStillReturnsToTheScaleLimit()
    {
        using var controller = new InkController
        {
            Tool = InkTool.Pan,
            PanZoomEnabled = true,
            MultiTouchEnabled = false,
            ViewportReleaseMotionEnabled = true,
        };
        controller.ViewportFrameRequested += () => { };
        controller.ConfigureGestures(true, true, false);
        controller.ConfigureViewScaleLimits(.2f, 2);
        controller.OnPointerDown(Touch(1, 100, 100, 100));
        controller.OnPointerDown(Touch(2, 200, 100, 101));
        controller.OnPointerMove(Touch(2, 600, 100, 120));
        controller.OnPointerCanceled(Touch(2, 600, 100, 121));

        var rebased = controller.ViewTransform;
        Assert.True(ViewportScale(rebased) > 2);
        controller.OnPointerMove(Touch(1, 100, 100, 123));
        controller.CommitViewportFrame();
        Assert.Equal(rebased, controller.ViewTransform);
        controller.OnPointerMove(Touch(1, 115, 120, 130));
        controller.CommitViewportFrame();
        Assert.Equal(new Vector2(15, 20), controller.ViewTransform.Translation - rebased.Translation);
        controller.OnPointerCanceled(Touch(1, 115, 120, 132));
        for (int frame = 0; frame < 60 && controller.AdvanceViewportFrame(1f / 60); frame++) { }

        Assert.False(controller.HasActiveInteraction);
        Assert.InRange(ViewportScale(controller.ViewTransform), 1.998f, 2.002f);
    }

    [Fact]
    public void FastPanCoastsAndANewTouchInterruptsTheMotion()
    {
        using var controller = new InkController
        {
            Tool = InkTool.Pan,
            PanZoomEnabled = true,
            MultiTouchEnabled = false,
            ViewportReleaseMotionEnabled = true,
        };
        controller.ViewportFrameRequested += () => { };
        controller.ConfigureGestures(false, true, false);
        controller.OnPointerDown(Touch(1, 100, 100, 100));
        controller.OnPointerMove(Touch(1, 120, 100, 120));
        controller.OnPointerMove(Touch(1, 150, 100, 150));
        controller.OnPointerUp(Touch(1, 150, 100, 155));
        var released = controller.ViewTransform.Translation;

        Assert.True(controller.AdvanceViewportFrame(1f / 60));
        Assert.True(controller.ViewTransform.Translation.X > released.X);
        controller.OnPointerDown(Touch(2, 150, 100, 160));
        var interrupted = controller.ViewTransform.Translation;

        Assert.False(controller.AdvanceViewportFrame(.1f));
        Assert.Equal(interrupted, controller.ViewTransform.Translation);
        controller.OnPointerUp(Touch(2, 150, 100, 161));
    }

    private static float ViewportScale(Matrix3x2 view) => MathF.Sqrt(MathF.Abs(view.GetDeterminant()));
}
