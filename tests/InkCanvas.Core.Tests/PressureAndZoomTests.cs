using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Ink.Manipulation;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PressureAndZoomTests
{
    [Theory]
    [InlineData(TaperKind.SmoothPen)]
    [InlineData(TaperKind.SoftPen)]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.Calligraphy)]
    public void PressureWithoutDeviceMetadataKeepsTheSimulatedWidth(TaperKind kind)
    {
        var neutral = new InkStroke(new StrokeAttributes { TaperKind = kind, BaseWidth = 8 });
        var pressured = new InkStroke(new StrokeAttributes { TaperKind = kind, BaseWidth = 8 });
        for (int i = 0; i < 180; i++)
        {
            float pressure = i % 11 / 10f;
            var stable = pressured.Points.Take(pressured.StablePointCount).ToArray();
            var previousWidths = pressured.Points.Select(p => p.Width).ToArray();
            neutral.AddPoint(i * 2, 100 + MathF.Sin(i * .1f) * 10, .5f, i * 8);
            pressured.AddPoint(i * 2, 100 + MathF.Sin(i * .1f) * 10, pressure, i * 8);
            Assert.Equal(stable, pressured.Points.Take(stable.Length));
            for (int j = 0; j < previousWidths.Length; j++)
                Assert.True(pressured.Points[j].Width >= previousWidths[j]);
        }
        for (int i = 0; i < pressured.Count; i++)
        {
            Assert.False(pressured.Points[i].HasPressure);
            Assert.Equal(neutral.Points[i].Width, pressured.Points[i].Width, 4);
        }
        var live = pressured.Points.ToArray();
        var outline = StrokeGeometry.BuildUnionContours(pressured).SelectMany(p => p).ToArray();
        pressured.Complete();
        Assert.Equal(live, pressured.Points);
        Assert.Equal(outline, StrokeGeometry.BuildUnionContours(pressured).SelectMany(p => p));
    }

    [Theory]
    [InlineData(TaperKind.None)]
    [InlineData(TaperKind.Tail)]
    [InlineData(TaperKind.Velocity)]
    public void DevicePressurePreservesTheOriginalThreeStyles(TaperKind kind)
    {
        var neutral = new InkStroke(new StrokeAttributes { TaperKind = kind });
        var pressured = new InkStroke(new StrokeAttributes { TaperKind = kind });
        for (int i = 0; i < 120; i++)
        {
            neutral.AddPoint(i * 2, 100, .5f, i * 8);
            pressured.AddPoint(i * 2, 100, i % 11 / 10f, i * 8);
        }
        Assert.Equal(neutral.Points.Select(p => p.Width), pressured.Points.Select(p => p.Width));
    }

    [Theory]
    [InlineData(TaperKind.SmoothPen)]
    [InlineData(TaperKind.SoftPen)]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.Calligraphy)]
    public void DevicePressureDoesNotTaperHighlighters(TaperKind kind)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = kind, BaseWidth = 8, IsHighlighter = true });
        for (int i = 0; i < 120; i++) stroke.AddPoint(i * 2, 100, i % 11 / 10f, i * 8);
        Assert.All(stroke.Points, p => Assert.Equal(8, p.Width));
    }

    [Theory]
    [InlineData(false, .88f, 2f, 100f, 200f, 1.8403753f)]
    [InlineData(true, .88f, 2f, 100f, 200f, 1.8403753f)]
    [InlineData(false, .88f, 150f, 100f, 200f, 1f)]
    [InlineData(true, .88f, 150f, 100f, 200f, 1f)]
    [InlineData(false, 1f, 1f, 100f, 200f, 2f)]
    [InlineData(true, 1f, 1f, 100f, 200f, 2f)]
    public void CanvasAndSelectionUseTheConfiguredZoom(bool selection, float damping, float deadZone,
        float initialLength, float finalLength, float expectedScale)
    {
        using var controller = new InkController
        {
            RecognizeShapes = false, PalmErases = false, MultiTouchEnabled = false,
            ZoomDamping = damping, ZoomDeadZone = deadZone, PanZoomEnabled = true
        };
        controller.ConfigureGestures(true, false, false);
        if (selection)
        {
            var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None });
            stroke.AddPoint(100, 100); stroke.AddPoint(400, 200); stroke.Complete();
            controller.Document.Add(stroke);
            controller.Tool = InkTool.Select;
            controller.SelectAll();
        }
        else controller.Tool = InkTool.Pan;
        OverlayPointer Touch(uint id, float x) => new(id, x, 150, .5f, 0, 0, OverlayPointerKind.Touch, 0);
        controller.OnPointerDown(Touch(1, 200));
        controller.OnPointerDown(Touch(2, 200 + initialLength));
        controller.OnPointerMove(Touch(2, 200 + finalLength));
        var transform = selection ? controller.SelectionPreviewTransform : controller.ViewTransform;
        Assert.Equal(expectedScale, transform.M11, 4);
        Assert.Equal(expectedScale, transform.M22, 4);
    }

    [Fact]
    public void ReversingAPinchReturnsToTheOriginalScale()
    {
        var manipulator = new TwoFingerManipulator { ZoomDamping = .88f, ZoomDeadZone = 2, AllowTranslate = false };
        var transform = Matrix3x2.Identity;
        manipulator.Begin(new(-5, 0), new(5, 0));
        manipulator.Update(new(-10, 0), new(10, 0), ref transform, Vector2.Zero);
        manipulator.Update(new(-5, 0), new(5, 0), ref transform, Vector2.Zero);
        Assert.Equal(1, transform.M11, 5);
        Assert.Equal(1, transform.M22, 5);
    }

    [Fact]
    public void PinchScaleDoesNotDependOnTheNumberOfInputEvents()
    {
        float Pinch(int samples)
        {
            var manipulator = new TwoFingerManipulator { ZoomDamping = .88f, ZoomDeadZone = 2, AllowTranslate = false };
            var transform = Matrix3x2.Identity;
            manipulator.Begin(new(-5, 0), new(5, 0));
            for (int i = 1; i <= samples; i++)
            {
                float halfLength = 5 + i * 5f / samples;
                manipulator.Update(new(-halfLength, 0), new(halfLength, 0), ref transform, Vector2.Zero);
            }
            return transform.M11;
        }
        Assert.Equal(Pinch(1), Pinch(100), 4);
    }
}
