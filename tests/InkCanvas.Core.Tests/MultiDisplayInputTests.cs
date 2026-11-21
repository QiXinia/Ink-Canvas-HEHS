using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using InkCanvas.Platform.Input;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class MultiDisplayInputTests
{
    private static OverlayPointer Touch(uint id, long screen, float x = 100) => new(id, x, 100, .5f, 4, 4,
        OverlayPointerKind.Touch, 1) { InputSurface = screen };

    [Fact]
    public void ContactsOnDifferentScreensWriteWithoutTriggeringPan()
    {
        using var controller = new InkController { MultiTouchEnabled = false, PalmErases = false, RecognizeShapes = false };
        controller.ConfigureGestures(false, true, false);
        var first = Touch(1, 100);
        var second = Touch(2, 200, 1000);
        controller.OnPointerDown(first); controller.OnPointerDown(second);
        controller.OnPointerMove(first with { X = 200, TimeMs = 30 });
        controller.OnPointerMove(second with { X = 1100, TimeMs = 30 });
        Assert.False(controller.IsViewManipulating);
        Assert.Equal(2, controller.LiveStrokes.Count);
        controller.OnPointerUp(first with { X = 200, TimeMs = 40 });
        controller.OnPointerUp(second with { X = 1100, TimeMs = 40 });
        Assert.Equal(2, controller.Document.Count);
        Assert.Equal(Matrix3x2.Identity, controller.ViewTransform);
        Assert.False(controller.HasActiveInteraction);
    }

    [Fact]
    public void GestureOnOneScreenPreservesLiveInkOnAnother()
    {
        using var controller = new InkController { MultiTouchEnabled = false, PalmErases = false, RecognizeShapes = false };
        controller.ConfigureGestures(false, true, false);
        var first = Touch(1, 100);
        var remote = Touch(2, 200, 1000);
        var sameScreen = Touch(3, 100, 300);
        controller.OnPointerDown(first); controller.OnPointerDown(remote); controller.OnPointerDown(sameScreen);
        Assert.Single(controller.LiveStrokes);
        controller.OnPointerMove(remote with { X = 1040, TimeMs = 30 });
        controller.OnPointerUp(remote with { X = 1040, TimeMs = 40 });
        Assert.Single(controller.Document.Strokes);
        Assert.Equal(1000, controller.Document.Strokes[0].Points[0].X);
        controller.OnPointerMove(first with { X = 120, TimeMs = 50 });
        controller.OnPointerMove(sameScreen with { X = 320, TimeMs = 50 });
        Assert.Equal(20, controller.ViewTransform.M31, 3);
    }

    [Fact]
    public void PenPriorityOnlyAppliesToItsOwnScreen()
    {
        var palm = new PalmRejector();
        var pen = Touch(9, 100) with { Kind = OverlayPointerKind.Pen };
        palm.OnDown(pen);
        Assert.False(palm.ShouldDraw(Touch(1, 100)));
        Assert.True(palm.ShouldDraw(Touch(2, 200)));
        palm.OnUp(pen);
        Assert.True(palm.ShouldDraw(Touch(1, 100)));
    }

    [Fact]
    public void OldSettingsEnableAllScreensAndSelectionSurvivesSerialization()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}");
        Assert.True(settings.Display.AllDisplays);
        settings.Display.AllDisplays = false;
        settings.Display.EnabledDevices.Add(@"\\.\DISPLAY2");
        var restored = settings.Clone();
        Assert.False(restored.Display.AllDisplays);
        Assert.Equal(settings.Display.EnabledDevices, restored.Display.EnabledDevices);
        restored.ResetToRecommendation(); Assert.True(restored.Display.AllDisplays);
    }
}
