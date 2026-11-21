using System.Numerics;
using InkCanvas.Core.Ink;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class StrokeCacheVersionTests
{
    [Fact]
    public void GeometryEditsChangeVersionButCompletionAndColorKeepIt()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 8, TaperKind = TaperKind.Tail });
        long original = stroke.GeometryVersion;
        stroke.AddPoint(10, 20);
        Assert.True(stroke.GeometryVersion > original);
        long beforeComplete = stroke.GeometryVersion;
        stroke.Complete();
        stroke.Attributes.Color = InkColor.Red;
        Assert.Equal(beforeComplete, stroke.GeometryVersion);
        stroke.Transform(Matrix3x2.CreateTranslation(30, 10));
        Assert.True(stroke.GeometryVersion > beforeComplete);
        long beforeRestore = stroke.GeometryVersion;
        stroke.RestorePoints(new[] { new InkPoint(50, 60, 0.5f, 8, 0) });
        Assert.True(stroke.GeometryVersion > beforeRestore);
        long beforeTaper = stroke.GeometryVersion;
        stroke.TaperKind = TaperKind.None;
        Assert.True(stroke.GeometryVersion > beforeTaper);
    }

    [Fact]
    public void GeometryVersionIsRuntimeOnly()
    {
        var stroke = new InkStroke(new StrokeAttributes());
        Assert.DoesNotContain("GeometryVersion", System.Text.Json.JsonSerializer.Serialize(stroke));
    }
}
