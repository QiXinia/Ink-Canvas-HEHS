using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class WritingStyleTests
{
    [Theory]
    [InlineData(TaperKind.Natural, 3f, 24)]
    [InlineData(TaperKind.Natural, 12f, 120)]
    [InlineData(TaperKind.Natural, 30f, 24)]
    [InlineData(TaperKind.Calligraphy, 3f, 24)]
    [InlineData(TaperKind.Calligraphy, 12f, 120)]
    [InlineData(TaperKind.Calligraphy, 30f, 24)]
    [InlineData(TaperKind.InkWash, 3f, 24)]
    [InlineData(TaperKind.InkWash, 12f, 120)]
    [InlineData(TaperKind.InkWash, 30f, 24)]
    public void PausingThenStartingSlowlyDoesNotLeaveAWiderStartingDot(TaperKind kind, float width, int pauseSamples)
    {
        foreach (var nib in new[] { PenNib.Round, PenNib.Ellipse45 })
        {
            var stroke = new InkStroke(new StrokeAttributes { TaperKind = kind, BaseWidth = width, Nib = nib });
            for (int i = 0; i < pauseSamples; i++) stroke.AddPoint(100, 100, .5f, i * 8);
            for (int i = 1; i <= 100; i++) stroke.AddPoint(100 + i * .25f, 100, .5f, (pauseSamples + i) * 8);
            float firstMovingWidth = stroke.Points[pauseSamples].Width;
            Assert.All(stroke.Points.Take(pauseSamples), point => Assert.True(point.Width <= firstMovingWidth * 1.01f));
            var live = stroke.Points.ToArray();
            stroke.Complete();
            Assert.Equal(live, stroke.Points);
        }
    }

    [Theory]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.Calligraphy)]
    [InlineData(TaperKind.InkWash)]
    public void StationaryStartupJitterDoesNotAccumulateAFullWidthHead(TaperKind kind)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = kind, BaseWidth = 12 });
        stroke.AddPoint(100, 100, .5f, 0);
        for (int i = 1; i < 400; i++) stroke.AddPoint(100 + (i % 2 == 0 ? .5f : -.5f), 100, .5f, i * 8);
        Assert.All(stroke.Points, point => Assert.InRange(point.Width, .001f, 3f));
    }

    [Theory]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.Calligraphy)]
    [InlineData(TaperKind.InkWash)]
    public void IntentionalTapRemainsVisibleWithTheSmoothStartingWidth(TaperKind kind)
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.TaperKind = kind;
        var sample = new OverlayPointer(1, 100, 100, .5f, 0, 0, OverlayPointerKind.Pen, 0);
        controller.OnPointerDown(sample);
        controller.OnPointerUp(sample with { TimeMs = 150 });
        var stroke = Assert.Single(controller.Document.Strokes);
        Assert.Single(stroke.Points);
        Assert.True(MathF.Abs(StrokeGeometry.SignedArea(Assert.Single(StrokeGeometry.BuildUnionContours(stroke)))) > .1f);
        controller.Undo();
        Assert.Empty(controller.Document.Strokes);
        controller.Redo();
        Assert.Single(controller.Document.Strokes);
    }

    [Theory]
    [InlineData(TaperKind.SmoothPen)]
    [InlineData(TaperKind.SoftPen)]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.Calligraphy)]
    [InlineData(TaperKind.InkWash)]
    public void CompletionPreservesTheLastLivePointsAndOutline(TaperKind style)
    {
        var stroke = Draw(style);
        var points = stroke.Points.ToArray();
        var outline = StrokeGeometry.BuildUnionContours(stroke);
        stroke.Complete();
        Assert.Equal(points, stroke.Points);
        Assert.Equal(outline.SelectMany(part => part), StrokeGeometry.BuildUnionContours(stroke).SelectMany(part => part));
        Assert.All(stroke.Points, point => Assert.InRange(point.Width, .01f, 20));
    }

    [Theory]
    [InlineData(TaperKind.SmoothPen)]
    [InlineData(TaperKind.SoftPen)]
    public void RealElapsedTimeMakesFastWritingThinner(TaperKind style)
    {
        var fast = Draw(style, 4);
        var slow = Draw(style, 25);
        Assert.True(fast.Points.Skip(30).Average(p => p.Width) < slow.Points.Skip(30).Average(p => p.Width));
    }

    [Theory]
    [InlineData(TaperKind.SmoothPen)]
    [InlineData(TaperKind.SoftPen)]
    public void SameSpeedAtDifferentSamplingRatesProducesSimilarWidths(TaperKind style)
    {
        float Width(int interval)
        {
            var stroke = new InkStroke(new StrokeAttributes { TaperKind = style, BaseWidth = 8 });
            for (int time = 0; time <= 1000; time += interval) stroke.AddPoint(time * .6f, 100, .5f, time);
            return stroke.Points[^1].Width;
        }
        Assert.InRange(MathF.Abs(Width(4) - Width(16)), 0, .1f);
    }

    [Theory]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.Calligraphy)]
    public void CubicNibIsThinAtBothEndsAndFullerInTheMiddle(TaperKind style)
    {
        var stroke = Draw(style, 12);
        Assert.True(stroke.Points[^1].Width < stroke.Points[40].Width * .4f);
        Assert.True(stroke.Points[0].Width < stroke.Points[40].Width);
    }

    [Fact]
    public void FilteringReducesStationaryInfraredJitterAndBoundsFastMotionLag()
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.SmoothPen });
        var raw = new List<Vector2>();
        for (int i = 0; i < 60; i++)
        {
            var point = new Vector2(100 + (i % 2 == 0 ? 1 : -1), 100);
            raw.Add(point);
            stroke.AddPoint(point.X, point.Y, .5f, i * 8);
        }
        float filteredMovement = 0, rawMovement = 0;
        for (int i = 20; i < 60; i++)
        {
            filteredMovement += Vector2.Distance(stroke.Points[i].Position, stroke.Points[i - 1].Position);
            rawMovement += Vector2.Distance(raw[i], raw[i - 1]);
        }
        Assert.True(filteredMovement < rawMovement * .3f);
        for (int i = 60; i < 100; i++)
        {
            var target = new Vector2(100 + (i - 59) * 12, 100 + (i - 59) * 5);
            stroke.AddPoint(target.X, target.Y, .5f, i * 8);
            Assert.InRange(Vector2.Distance(target, stroke.Points[^1].Position), 0, 2.501f);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(1000000f)]
    public void MissingRepeatedAndDiscontinuousTimesProduceFiniteInk(float time)
    {
        foreach (var kind in NewStyles())
        {
            var stroke = new InkStroke(new StrokeAttributes { TaperKind = kind });
            for (int i = 0; i < 150; i++) stroke.AddPoint(i, MathF.Sin(i), .5f, i % 3 == 0 ? 0 : time);
            Assert.All(stroke.Points, point =>
            {
                Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Width));
                Assert.True(point.Width > 0);
            });
        }
    }

    [Fact]
    public void EachStrokeOwnsItsFilterAndPressureState()
    {
        foreach (var style in NewStyles())
        {
            var baseline = Draw(style);
            var left = new InkStroke(new StrokeAttributes { TaperKind = style, BaseWidth = 8 });
            var right = new InkStroke(new StrokeAttributes { TaperKind = style, BaseWidth = 8 });
            for (int i = 0; i < 120; i++)
            {
                left.AddPoint(i * 2, 100 + MathF.Sin(i * .1f) * 10, .5f, i * 8);
                right.AddPoint(-i * 9, 900, .5f, i * 3);
            }
            Assert.Equal(baseline.Points, left.Points);
        }
    }

    [Fact]
    public void HighlighterKeepsUniformWidthWithAnyPenStyle()
    {
        foreach (var style in NewStyles())
        {
            var stroke = new InkStroke(new StrokeAttributes { TaperKind = style, BaseWidth = 8, IsHighlighter = true });
            for (int i = 0; i < 100; i++) stroke.AddPoint(i, 10, .5f, i * 8);
            Assert.All(stroke.Points, point => Assert.Equal(8, point.Width));
            Assert.Equal(TaperKind.None, stroke.TaperKind);
        }
    }

    [Fact]
    public void StylesPersistTheirExactAppearance()
    {
        string path = Path.Combine(Path.GetTempPath(), "ink-styles-" + Guid.NewGuid().ToString("N") + ".icink");
        try
        {
            var strokes = NewStyles().Select(style => { var stroke = Draw(style); stroke.Complete(); return stroke; }).ToArray();
            InkFileStore.Save(path, strokes);
            var restored = InkFileStore.Load(path);
            Assert.Equal(strokes.Length, restored.Count);
            for (int i = 0; i < strokes.Length; i++)
            {
                Assert.Equal(strokes[i].TaperKind, restored[i].TaperKind);
                Assert.Equal(strokes[i].Points, restored[i].Points);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void EveryStyleHasARoundTripMenuIndex()
    {
        for (int index = 0; index < OriginalMenuCatalog.PenStyles.Count; index++)
            Assert.Equal(index, OriginalMenuCatalog.ToPenStyle(OriginalMenuCatalog.ToTaperKind(index)));
    }

    [Fact]
    public void InkWashUsesFourBoundedLayersAndKeepsHighlighterSingleLayer()
    {
        var wash = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.InkWash, BaseWidth = 12 });
        for (int i = 0; i < 3000; i++) wash.AddPoint(i * 1.5f, 100 + MathF.Sin(i * .03f) * 24, .5f, i * 8);
        var layers = InkBrush.Layers(wash);
        Assert.Equal(4, layers.Length);
        Assert.Equal(1, layers[0].WidthScale);
        Assert.True(layers[1].Opacity < layers[0].Opacity);
        Assert.True(layers[3].Opacity > layers[0].Opacity);
        Assert.Equal(4, InkBrush.Layers(wash.Clone()).Length);
        var highlighter = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.InkWash, IsHighlighter = true });
        Assert.Equal(1, InkBrush.Layers(highlighter).Length);
    }

    [Fact]
    public void PointerTimesKeepMillisecondPrecisionAfterLongSystemUptime()
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.CurrentAttributes.TaperKind = TaperKind.SoftPen;
        uint start = uint.MaxValue - 40;
        OverlayPointer Sample(uint time, float x) => new(1, x, 100, .5f, 0, 0, OverlayPointerKind.Touch, time);
        controller.OnPointerDown(Sample(start, 0));
        for (uint i = 1; i <= 20; i++) controller.OnPointerMove(Sample(unchecked(start + i * 8), i * 3));
        controller.OnPointerUp(Sample(unchecked(start + 168), 60));
        var stroke = Assert.Single(controller.Document.Strokes);
        Assert.Equal(0, stroke.Points[0].TimeMs);
        Assert.Equal(160, stroke.Points[^1].TimeMs);
    }

    private static IEnumerable<TaperKind> NewStyles() => new[] { TaperKind.SmoothPen, TaperKind.SoftPen, TaperKind.Natural, TaperKind.Calligraphy, TaperKind.InkWash };
    private static InkStroke Draw(TaperKind kind, int interval = 8)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = kind, BaseWidth = 8 });
        for (int i = 0; i < 120; i++) stroke.AddPoint(i * 2, 100 + MathF.Sin(i * .1f) * 10, .5f, i * interval);
        return stroke;
    }
}
