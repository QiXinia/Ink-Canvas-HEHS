using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

public sealed class SpotlightGeometryTests
{
    // ---- SpotlightGeometry ----------------------------------------------

    [Fact]
    public void ClampCenter_KeepsSpotInsideBounds()
    {
        var (x1, y1) = SpotlightGeometry.ClampCenter(-50, 50, 100, 100, 20);
        Assert.Equal(20, x1, 3);
        Assert.Equal(50, y1, 3);

        var (x2, y2) = SpotlightGeometry.ClampCenter(999, 999, 100, 100, 20);
        Assert.Equal(80, x2, 3);
        Assert.Equal(80, y2, 3);

        var (x3, y3) = SpotlightGeometry.ClampCenter(50, 50, 100, 100, 20);
        Assert.Equal(50, x3, 3);
        Assert.Equal(50, y3, 3);
    }

    [Fact]
    public void ClampCenter_RadiusLargerThanHalf_CentersSpot()
    {
        var (x, y) = SpotlightGeometry.ClampCenter(10, 10, 60, 60, 500);
        Assert.Equal(30, x, 3);
        Assert.Equal(30, y, 3);
    }

    [Fact]
    public void NormalizeRect_HandlesReversedCorners()
    {
        var (left, top, width, height) = SpotlightGeometry.NormalizeRect(100, 80, 20, 30);

        Assert.Equal(20, left, 3);
        Assert.Equal(30, top, 3);
        Assert.Equal(80, width, 3);
        Assert.Equal(50, height, 3);
    }

    [Fact]
    public void RadiusFrom_HalvesDiameter()
    {
        Assert.Equal(20, SpotlightGeometry.RadiusFrom(40), 3);
        Assert.Equal(0, SpotlightGeometry.RadiusFrom(-5), 3);
    }

    [Fact]
    public void ClampRadius_ClampsToHalfMinDimension()
    {
        Assert.Equal(30, SpotlightGeometry.ClampRadius(999, 120, 60), 3);
        Assert.Equal(0, SpotlightGeometry.ClampRadius(-10, 120, 60), 3);
        Assert.Equal(10, SpotlightGeometry.ClampRadius(10, 120, 60), 3);
    }
}