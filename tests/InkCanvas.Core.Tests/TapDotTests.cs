using InkCanvas.Core.Ink;
using System.Numerics;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class TapDotTests
{
    public static IEnumerable<object[]> StylesAndDevices() =>
        from style in Enum.GetValues<TaperKind>()
        from kind in Enum.GetValues<OverlayPointerKind>()
        select new object[] { style, kind };

    public static IEnumerable<object[]> StylesAndZooms() =>
        from style in Enum.GetValues<TaperKind>()
        from zoom in new[] { .5f, 1f, 2f }
        select new object[] { style, zoom };

    [Theory]
    [MemberData(nameof(StylesAndZooms))]
    public void InfraredJitterAroundATapStillProducesOneConfiguredWidthDot(TaperKind style, float zoom)
    {
        using var controller = new InkController { RecognizeShapes = false, ViewTransform = Matrix3x2.CreateScale(zoom) };
        controller.CurrentAttributes.TaperKind = style;
        controller.CurrentAttributes.BaseWidth = 3;
        var start = Sample(OverlayPointerKind.Touch) with { X = 102.8f };
        controller.OnPointerDown(start);
        for (uint index = 1; index <= 120; index++)
        {
            float angle = index * 2.3f;
            controller.OnPointerMove(start with
            {
                X = 100 + 2.8f * MathF.Cos(angle), Y = 100 + 2.8f * MathF.Sin(angle), TimeMs = index * 8,
            });
        }
        var provisional = Assert.Single(controller.LiveStrokes).Points.ToArray();
        controller.OnPointerUp(start with { X = 97.4f, TimeMs = 970 });
        var dot = Assert.Single(controller.Document.Strokes);
        Assert.Equal(3, Assert.Single(dot.Points).Width);
        var animation = Assert.Single(controller.DotAnimations);
        Assert.Equal(provisional, animation.Preview.Points);
        controller.AdvanceDotAnimations(60);
        for (int index = 0; index < provisional.Length; index++)
        {
            Assert.Equal(Vector2.Lerp(provisional[index].Position, dot.Points[0].Position, .5f), animation.Preview.Points[index].Position);
            float from = provisional[index].Width;
            Assert.InRange(animation.Preview.Points[index].Width, Math.Min(from, 3), Math.Max(from, 3));
        }
        controller.AdvanceDotAnimations(60);
        Assert.Empty(controller.DotAnimations);
        Assert.All(animation.Preview.Points, point => Assert.Equal(3, point.Width));
    }

    [Fact]
    public void AStrokeThatMovesBeyondTapToleranceKeepsItsPathEvenIfItReturnsToTheStart()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.TaperKind = TaperKind.Natural;
        var start = Sample(OverlayPointerKind.Touch);
        controller.OnPointerDown(start);
        for (uint index = 1; index <= 15; index++) controller.OnPointerMove(start with { X = 100 + index, TimeMs = index * 8 });
        for (uint index = 1; index <= 15; index++) controller.OnPointerMove(start with { X = 115 - index, TimeMs = 120 + index * 8 });
        controller.OnPointerUp(start with { TimeMs = 250 });
        Assert.True(Assert.Single(controller.Document.Strokes).Count > 1);
        Assert.Empty(controller.DotAnimations);
    }

    [Theory]
    [MemberData(nameof(StylesAndDevices))]
    public void TapCommitsConfiguredWidthAndSettlesSmoothly(TaperKind style, OverlayPointerKind kind)
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.BaseWidth = 12;
        controller.CurrentAttributes.TaperKind = style;
        var sample = Sample(kind);
        controller.OnPointerDown(sample);
        float initialWidth = Assert.Single(controller.LiveStrokes).Points[0].Width;
        controller.OnPointerUp(sample with { TimeMs = 15 });
        var dot = Assert.Single(controller.Document.Strokes);
        Assert.Equal(12, Assert.Single(dot.Points).Width);
        if (initialWidth < 12)
        {
            var animation = Assert.Single(controller.DotAnimations);
            Assert.Equal(initialWidth, animation.Preview.Points[0].Width);
            float previous = initialWidth;
            for (int step = 0; step < 7; step++)
            {
                controller.AdvanceDotAnimations(15);
                float width = animation.Preview.Points[0].Width;
                Assert.InRange(width, previous, 12);
                Assert.True(width - previous < (12 - initialWidth) * .2f);
                previous = width;
            }
            controller.AdvanceDotAnimations(15);
            Assert.Empty(controller.DotAnimations);
            Assert.Equal(12, animation.Preview.Points[0].Width);
        }
        else Assert.Empty(controller.DotAnimations);
    }

    [Fact]
    public void HoldingAtOnePointStillCreatesOneFullWidthDot()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.BaseWidth = 15;
        controller.CurrentAttributes.TaperKind = TaperKind.Calligraphy;
        var sample = Sample(OverlayPointerKind.Touch);
        controller.OnPointerDown(sample);
        for (uint index = 1; index <= 100; index++)
            controller.OnPointerMove(sample with { TimeMs = index * 8, X = 100 + (index % 2 == 0 ? .2f : -.2f) });
        controller.OnPointerUp(sample with { TimeMs = 810 });
        Assert.Equal(15, Assert.Single(Assert.Single(controller.Document.Strokes).Points).Width);
        Assert.Single(controller.DotAnimations);
    }

    [Fact]
    public void ContinuingToWriteDoesNotExpandTheStrokeStartIntoADot()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.BaseWidth = 12;
        controller.CurrentAttributes.TaperKind = TaperKind.Natural;
        var sample = Sample(OverlayPointerKind.Pen);
        controller.OnPointerDown(sample);
        for (uint index = 1; index <= 80; index++)
            controller.OnPointerMove(sample with { TimeMs = index * 10, X = 100 + index * .5f });
        controller.OnPointerUp(sample with { X = 140, TimeMs = 810 });
        var stroke = Assert.Single(controller.Document.Strokes);
        Assert.True(stroke.Count > 1);
        Assert.True(stroke.Points[0].Width < 3);
        Assert.Empty(controller.DotAnimations);
    }

    [Fact]
    public void UndoDuringTheVisualTransitionRemovesTheDotAndRedoKeepsFinalWidth()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.BaseWidth = 10;
        controller.CurrentAttributes.TaperKind = TaperKind.Calligraphy;
        var sample = Sample(OverlayPointerKind.Mouse);
        controller.OnPointerDown(sample);
        controller.OnPointerUp(sample with { TimeMs = 10 });
        Assert.Single(controller.DotAnimations);
        controller.Undo();
        Assert.Empty(controller.Document.Strokes);
        Assert.Empty(controller.DotAnimations);
        controller.Redo();
        Assert.Equal(10, Assert.Single(controller.Document.Strokes).Points[0].Width);
        Assert.Empty(controller.DotAnimations);
    }

    [Fact]
    public void SavingDuringTransitionStoresTheFullWidth()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.BaseWidth = 9;
        controller.CurrentAttributes.TaperKind = TaperKind.Calligraphy;
        var sample = Sample(OverlayPointerKind.Pen);
        controller.OnPointerDown(sample);
        controller.OnPointerUp(sample with { TimeMs = 10 });
        string path = Path.Combine(Path.GetTempPath(), "tap-dot-" + Guid.NewGuid().ToString("N") + ".icink");
        try
        {
            InkFileStore.Save(path, controller.Document.Strokes);
            Assert.Equal(9, Assert.Single(InkFileStore.Load(path)).Points[0].Width);
            Assert.True(Assert.Single(controller.DotAnimations).Preview.Points[0].Width < 9);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MultipleTapsSettleIndependently()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.TaperKind = TaperKind.Natural;
        var first = Sample(OverlayPointerKind.Touch);
        controller.OnPointerDown(first);
        controller.OnPointerUp(first);
        controller.AdvanceDotAnimations(90);
        var second = first with { Id = 2, X = 150 };
        controller.OnPointerDown(second);
        controller.OnPointerUp(second);
        Assert.Equal(2, controller.DotAnimations.Count);
        controller.AdvanceDotAnimations(30);
        Assert.Single(controller.DotAnimations);
        controller.AdvanceDotAnimations(90);
        Assert.Empty(controller.DotAnimations);
        Assert.Equal(2, controller.Document.Count);
    }

    private static OverlayPointer Sample(OverlayPointerKind kind) => new(1, 100, 100, .5f, 1, 1, kind, 0);
}
