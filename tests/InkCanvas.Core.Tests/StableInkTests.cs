using InkCanvas.Core.Ink;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class StableInkTests
{
    [Theory]
    [InlineData(TaperKind.None)]
    [InlineData(TaperKind.Tail)]
    [InlineData(TaperKind.Velocity)]
    [InlineData(TaperKind.SmoothPen)]
    [InlineData(TaperKind.SoftPen)]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.Calligraphy)]
    public void AppendingSamplesPreservesEveryReportedStablePoint(TaperKind kind)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = kind, BaseWidth = 10 });
        for (int i = 0; i < 300; i++)
        {
            var stable = stroke.Points.Take(stroke.StablePointCount).ToArray();
            stroke.AddPoint(i, MathF.Sin(i * .1f) * 40, .5f, i * 8);
            Assert.Equal(stable, stroke.Points.Take(stable.Length));
        }
        stroke.Complete(); Assert.Equal(stroke.Count, stroke.StablePointCount);
    }
}
