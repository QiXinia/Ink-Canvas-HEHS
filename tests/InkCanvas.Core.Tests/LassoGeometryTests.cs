using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class LassoGeometryTests
{
    private static readonly Vector2[] Concave = { new(0, 0), new(100, 0), new(100, 30), new(30, 30), new(30, 100), new(0, 100) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcaveLassoExcludesItsEmptyBoundingBoxCorner(bool reverse)
    {
        var lasso = new LassoGeometry(reverse ? Concave.Reverse() : Concave);
        Assert.True(lasso.IsValid);
        Assert.True(lasso.Contains(new Vector2(15, 80)));
        Assert.True(lasso.Contains(new Vector2(80, 15)));
        Assert.True(lasso.Contains(new Vector2(30, 60))); // boundary counts
        Assert.False(lasso.Contains(new Vector2(70, 70)));
        Assert.False(lasso.IntersectsSegment(new Vector2(60, 70), new Vector2(90, 70)));
    }

    [Fact]
    public void SparseStrokeCrossingLassoIsHitWithoutSamplesInside()
    {
        var lasso = new LassoGeometry(Concave);
        var stroke = Stroke(new(-20, 15), new(120, 15));
        Assert.True(lasso.IntersectsStroke(stroke, Matrix3x2.Identity));
        Assert.False(lasso.IntersectsStroke(Stroke(new(-20, 120), new(120, 120)), Matrix3x2.Identity));
    }

    [Fact]
    public void SinglePointInkCanBeCircled()
    {
        var lasso = new LassoGeometry(Concave);
        Assert.True(lasso.IntersectsStroke(Stroke(new Vector2(10, 10)), Matrix3x2.Identity));
        Assert.False(lasso.IntersectsStroke(Stroke(new Vector2(80, 80)), Matrix3x2.Identity));
    }

    [Fact]
    public void ViewRotationZoomAndPanAreAppliedToInkBeforeHitTesting()
    {
        var view = Matrix3x2.CreateRotation(0.7f) * Matrix3x2.CreateScale(2) * Matrix3x2.CreateTranslation(200, 100);
        var lasso = new LassoGeometry(Concave.Select(p => Vector2.Transform(p, view)));
        Assert.True(lasso.IntersectsStroke(Stroke(new(10, 10), new(80, 10)), view));
        Assert.False(lasso.IntersectsStroke(Stroke(new(70, 70), new(90, 90)), view));
    }

    [Fact]
    public void RotatedImageUsesItsActualFrameRatherThanAxisAlignedBounds()
    {
        var frame = new SelectionFrame(new Rect(100, 100, 200, 200), Matrix3x2.CreateRotation(MathF.PI / 4, new Vector2(150, 150)));
        Assert.False(new LassoGeometry(Box(80, 80, 95, 95)).IntersectsFrame(frame, Matrix3x2.Identity));
        Assert.True(new LassoGeometry(Box(145, 145, 155, 155)).IntersectsFrame(frame, Matrix3x2.Identity));
        Assert.True(new LassoGeometry(Box(0, 0, 300, 300)).IntersectsFrame(frame, Matrix3x2.Identity));
    }

    [Fact]
    public void TapStraightSwipeAndTinyLoopAreIgnored()
    {
        Assert.False(new LassoGeometry(new[] { new Vector2(0, 0) }).IsValid);
        Assert.False(new LassoGeometry(new[] { new Vector2(0, 0), new Vector2(50, 50), new Vector2(100, 100) }).IsValid);
        Assert.False(new LassoGeometry(Box(0, 0, 1, 1)).IsValid);
    }

    [Fact]
    public void SelfIntersectingLassoUsesEvenOddInterior()
    {
        var lasso = new LassoGeometry(new[] { new Vector2(0, 0), new Vector2(100, 100), new Vector2(0, 100), new Vector2(100, 0) });
        Assert.True(lasso.IsValid);
        Assert.True(lasso.Contains(new Vector2(50, 10)));
        Assert.False(lasso.Contains(new Vector2(10, 50)));
    }

    private static Vector2[] Box(float left, float top, float right, float bottom)
        => new[] { new Vector2(left, top), new Vector2(right, top), new Vector2(right, bottom), new Vector2(left, bottom) };

    private static InkStroke Stroke(params Vector2[] points)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None });
        foreach (var point in points) stroke.AddPoint(point.X, point.Y);
        stroke.Complete(); return stroke;
    }
}
