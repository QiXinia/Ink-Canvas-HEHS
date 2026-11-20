using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class HardwarePressureTests
{
    public static IEnumerable<object[]> Brushes() => Enum.GetValues<TaperKind>().Select(kind => new object[] { kind });

    private static InkStroke Stroke(TaperKind kind, bool highlighter = false)
        => new(new StrokeAttributes { BaseWidth = 10, TaperKind = kind, IsHighlighter = highlighter });

    private static OverlayPointer Pen(float pressure, uint time = 100, float x = 100)
        => new(1, x, 100, pressure, 0, 0, OverlayPointerKind.Pen, time) { HasPressure = true };

    [Theory]
    [MemberData(nameof(Brushes))]
    public void HardwarePressureControlsBrushWidthIndependentlyOfSpeed(TaperKind brush)
    {
        var slow = Stroke(brush); var fast = Stroke(brush);
        for (int i = 0; i < 80; i++)
        {
            slow.AddPoint(i * .5f, 10, .7f, i * 8, true);
            fast.AddPoint(i * 20f, 10, .7f, i * 8, true);
        }
        Assert.Equal(slow.Points.Select(p => p.Width), fast.Points.Select(p => p.Width));
        Assert.True(slow.Points[0].Width > 10);
        Assert.Equal(slow.Count, slow.StablePointCount);
    }

    [Theory]
    [MemberData(nameof(Brushes))]
    public void ReleasingPressureMakesANaturalTipWithoutRewritingHistory(TaperKind brush)
    {
        var stroke = Stroke(brush);
        for (int i = 0; i <= 100; i++)
        {
            var previous = stroke.Points.ToArray();
            float pressure = i < 30 ? .1f + i * .8f / 30 : .9f - (i - 30) * .88f / 70;
            stroke.AddPoint(i * 3, 20, pressure, i * 8, true);
            for (int j = 0; j < previous.Length; j++) Assert.Equal(previous[j].Width, stroke.Points[j].Width);
        }
        Assert.True(stroke.Points[32].Width > stroke.Points[0].Width * 2);
        Assert.True(stroke.Points[^1].Width < stroke.Points[32].Width / 3);
        var geometry = StrokeGeometry.BuildOutline(stroke);
        stroke.Complete(); Assert.Equal(geometry, StrokeGeometry.BuildOutline(stroke));
    }

    [Theory]
    [MemberData(nameof(Brushes))]
    public void PressureResponseStaysConsistentAcrossSamplingRates(TaperKind brush)
    {
        var at120 = Stroke(brush); var at240 = Stroke(brush);
        for (int i = 0; i <= 120; i++) at120.AddPoint(i * 2, 20, .1f + .8f * i / 120, i * (1000f / 120), true);
        for (int i = 0; i <= 240; i++) at240.AddPoint(i, 20, .1f + .8f * i / 240, i * (1000f / 240), true);
        for (int i = 5; i <= 120; i++)
            Assert.InRange(MathF.Abs(at120.Points[i].Width - at240.Points[i * 2].Width), 0, .12f);
    }

    [Fact]
    public void SmallPressureNoiseIsSmoothedWithoutDelayingALargeChange()
    {
        var stroke = Stroke(TaperKind.None);
        for (int i = 0; i < 100; i++) stroke.AddPoint(i, 20, .5f + (i % 2 == 0 ? .02f : -.02f), i * 4, true);
        var widths = stroke.Points.Skip(20).Select(p => p.Width).ToArray();
        Assert.True(widths.Max() - widths.Min() < .2f);
        stroke.AddPoint(100, 20, 1, 404, true);
        stroke.AddPoint(101, 20, 1, 412, true);
        Assert.True(stroke.Points[^1].Width > 15);
    }

    [Fact]
    public void MissingAndInvalidPacketsKeepTheLastUsablePressure()
    {
        var stroke = Stroke(TaperKind.Natural);
        stroke.AddPoint(0, 0, .2f, 0, true);
        float width = stroke.Points[0].Width;
        stroke.AddPoint(10, 0, .5f, 8);
        stroke.AddPoint(20, 0, float.NaN, 16, true);
        stroke.AddPoint(30, 0, float.PositiveInfinity, 24, true);
        Assert.All(stroke.Points, point => Assert.Equal(width, point.Width));
        Assert.All(stroke.Points, point => Assert.True(float.IsFinite(point.Pressure)));
        stroke.AddPoint(40, 0, .8f, 32, true);
        Assert.True(stroke.Points[^1].Width > width);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PenTapUsesPressureAndZeroOnLiftKeepsItsSize(bool highlighter)
    {
        using var light = new InkController { RecognizeShapes = false, Tool = highlighter ? InkTool.Highlighter : InkTool.Pen };
        using var heavy = new InkController { RecognizeShapes = false, Tool = highlighter ? InkTool.Highlighter : InkTool.Pen };
        light.CurrentAttributes.BaseWidth = heavy.CurrentAttributes.BaseWidth = 10;
        light.OnPointerDown(Pen(.1f)); heavy.OnPointerDown(Pen(.9f));
        float lightWidth = light.LiveStrokes.Single().Points[0].Width;
        float heavyWidth = heavy.LiveStrokes.Single().Points[0].Width;
        light.OnPointerUp(Pen(0, 120)); heavy.OnPointerUp(Pen(0, 120));
        Assert.Equal(lightWidth, Assert.Single(light.Document.Strokes).Points[0].Width);
        Assert.Equal(heavyWidth, Assert.Single(heavy.Document.Strokes).Points[0].Width);
        Assert.True(heavyWidth > lightWidth * 2);
    }

    [Fact]
    public void PointerUpExtendsToItsCoordinateWithTheLastContactPressure()
    {
        using var ink = new InkController { RecognizeShapes = false };
        ink.CurrentAttributes.BaseWidth = 10;
        ink.OnPointerDown(Pen(.8f, uint.MaxValue - 20));
        ink.OnPointerMove(Pen(.8f, uint.MaxValue - 10, 130));
        ink.OnPointerUp(Pen(0, 2, 160));
        var stroke = Assert.Single(ink.Document.Strokes);
        Assert.Equal(new Vector2(160, 100), stroke.Points[^1].Position);
        Assert.Equal(stroke.Points[1].Width, stroke.Points[^1].Width);
        Assert.Equal(23, stroke.Points[^1].TimeMs);
    }

    [Fact]
    public void HardwarePressureToggleDisablesPressureForPenAndHighlighter()
    {
        foreach (var tool in new[] { InkTool.Pen, InkTool.Highlighter })
        {
            using var ink = new InkController { RecognizeShapes = false, UseHardwarePressure = false, Tool = tool };
            ink.CurrentAttributes.BaseWidth = 10; ink.CurrentAttributes.TaperKind = TaperKind.None;
            ink.OnPointerDown(Pen(.1f)); ink.OnPointerMove(Pen(.9f, 108, 130));
            Assert.All(ink.LiveStrokes.Single().Points, point => Assert.Equal(10, point.Width));
        }
    }

    [Fact]
    public void PressureBrushHistorySurvivesPdfAndStyleChanges()
    {
        var stroke = Stroke(TaperKind.InkWash);
        for (int i = 0; i < 100; i++) stroke.AddPoint(i * 2, 20, .1f + i * .008f, i * 8, true, 30, 20);
        stroke.Complete();
        var restored = Assert.Single(PdfInkCodec.Decode(PdfInkCodec.Encode(new[] { stroke })));
        Assert.Equal(StrokeGeometry.BuildOutline(stroke), StrokeGeometry.BuildOutline(restored));
        restored.TaperKind = TaperKind.SoftPen; restored.TaperKind = TaperKind.InkWash;
        Assert.Equal(stroke.Points.Select(p => p.Width), restored.Points.Select(p => p.Width));
    }

    [Fact]
    public void ThinPressureDotsRetainTheirRenderedSizeAndHeavyStrokesCanBeSelectedAtTheEdge()
    {
        var thin = new InkStroke(new() { BaseWidth = .5f, TaperKind = TaperKind.Calligraphy });
        thin.AddPoint(20, 20, .01f, 0, true);
        var contour = StrokeGeometry.BuildUnionContours(thin).Single();
        Assert.InRange(contour.Max(p => p.X) - contour.Min(p => p.X), .049f, .051f);
        var heavy = new InkStroke(new() { BaseWidth = 30, TaperKind = TaperKind.InkWash });
        heavy.AddPoint(100, 100, 1, 0, true); heavy.AddPoint(300, 100, 1, 8, true); heavy.Complete();
        var document = new InkDocument(); document.Add(heavy);
        Assert.Contains(heavy, document.HitTest(new Vector2(200, 127), 1));
    }
}
