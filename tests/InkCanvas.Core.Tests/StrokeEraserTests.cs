using System.Numerics;
using InkCanvas.Core.Ink;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class StrokeEraserTests
{
    [Fact]
    public void ErasingMiddleKeepsBothOutsideFragmentsAndTheirWidths()
    {
        var stroke = Line();
        Assert.True(StrokeEraser.EraseDisc(stroke, new Vector2(100, 50), 10, out var fragments));
        Assert.Equal(2, fragments.Count);
        Assert.Equal(0f, fragments[0].Points[0].X);
        Assert.Equal(88f, fragments[0].Points[^1].X, 3);
        Assert.Equal(112f, fragments[1].Points[0].X, 3);
        Assert.Equal(200f, fragments[1].Points[^1].X);
        Assert.All(fragments, f =>
        {
            Assert.True(f.IsCompleted);
            Assert.All(f.Points, p => Assert.Equal(4f, p.Width));
        });
        Assert.Equal(200f, stroke.Points[^1].X);
        Assert.Equal(2, stroke.Count);
    }

    [Fact]
    public void ErasingEndpointKeepsRestOfStroke()
    {
        Assert.True(StrokeEraser.EraseDisc(Line(), new Vector2(0, 50), 10, out var fragments));
        var fragment = Assert.Single(fragments);
        Assert.Equal(12f, fragment.Points[0].X, 3);
        Assert.Equal(200f, fragment.Points[^1].X);
    }

    [Fact]
    public void MissLeavesOriginalStrokeUntouched()
    {
        var stroke = Line();
        Assert.False(StrokeEraser.EraseDisc(stroke, new Vector2(100, 100), 10, out var fragments));
        Assert.Same(stroke, Assert.Single(fragments));
    }

    [Fact]
    public void CoveringWholeStrokeRemovesIt()
    {
        Assert.True(StrokeEraser.EraseDisc(Line(), new Vector2(100, 50), 110, out var fragments));
        Assert.Empty(fragments);
    }

    [Fact]
    public void EraserAlsoRemovesSinglePointInk()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
        stroke.AddPoint(100, 50);
        stroke.Complete();
        Assert.True(StrokeEraser.EraseDisc(stroke, new Vector2(100, 50), 10, out var fragments));
        Assert.Empty(fragments);
    }

    [Fact]
    public void FragmentReplacementPreservesLayerOrderAndNotifiesOnce()
    {
        var document = new InkDocument();
        var original = Line();
        var overlay = Line();
        document.AddRange(new[] { original, overlay });
        StrokeEraser.EraseDisc(original, new Vector2(100, 50), 10, out var fragments);
        int notifications = 0;
        document.Changed += () =>
        {
            notifications++;
            Assert.Equal(3, document.Count);
            Assert.Same(overlay, document.Strokes[^1]);
        };
        Assert.True(document.Replace(original, fragments));
        Assert.Equal(1, notifications);
        Assert.Same(fragments[0], document.Strokes[0]);
        Assert.Same(fragments[1], document.Strokes[1]);
    }

    private static InkStroke Line()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
        stroke.AddPoint(0, 50);
        stroke.AddPoint(200, 50);
        stroke.Complete();
        return stroke;
    }
}
