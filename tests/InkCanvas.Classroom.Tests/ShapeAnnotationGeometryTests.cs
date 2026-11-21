using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.AiAssistant;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>图形释义矢量标注的归一化几何与元数据往返。</summary>
public sealed class ShapeAnnotationGeometryTests
{
    [Fact]
    public void Normalize_UnitPoints_ArePreserved()
    {
        var points = ShapeAnnotationGeometry.Normalize(new[] { 0.1, 0.2, 0.9, 0.8 }, 400, 300);

        Assert.Equal(2, points.Count);
        Assert.Equal(0.1, points[0].X, 6);
        Assert.Equal(0.2, points[0].Y, 6);
        Assert.Equal(0.9, points[1].X, 6);
        Assert.Equal(0.8, points[1].Y, 6);
    }

    [Fact]
    public void Normalize_PixelPoints_ScalesByAssetSize()
    {
        var points = ShapeAnnotationGeometry.Normalize(new[] { 100d, 200d, 300d, 400d }, 400, 500);

        Assert.Equal(2, points.Count);
        Assert.Equal(0.25, points[0].X, 6);
        Assert.Equal(0.4, points[0].Y, 6);
        Assert.Equal(0.75, points[1].X, 6);
        Assert.Equal(0.8, points[1].Y, 6);
    }

    [Fact]
    public void Normalize_OutOfRangePoints_AreClampedToUnitSquare()
    {
        var points = ShapeAnnotationGeometry.Normalize(new[] { -100d, -50d, 600d, 900d }, 400, 500);

        Assert.Equal(0, points[0].X, 6);
        Assert.Equal(0, points[0].Y, 6);
        Assert.Equal(1, points[1].X, 6);
        Assert.Equal(1, points[1].Y, 6);
    }

    [Fact]
    public void Normalize_EmptyOrSingleCoordinate_ReturnsNoPoints()
    {
        Assert.Empty(ShapeAnnotationGeometry.Normalize(null, 10, 10));
        Assert.Empty(ShapeAnnotationGeometry.Normalize(Array.Empty<double>(), 10, 10));
        Assert.Empty(ShapeAnnotationGeometry.Normalize(new[] { 0.5 }, 10, 10));
    }

    [Fact]
    public void Project_OffsetsNormalizedPointsByAssetPosition()
    {
        var normalized = new List<ShapeAnnotationPoint> { new(0.5, 0.5) };
        var projected = ShapeAnnotationGeometry.Project(normalized, 10, 20, 100, 50);

        Assert.Single(projected);
        Assert.Equal(60, projected[0].X, 6);
        Assert.Equal(45, projected[0].Y, 6);
    }

    [Fact]
    public void Project_AfterAssetMove_PointsFollowNewLeftTop()
    {
        var normalized = new List<ShapeAnnotationPoint> { new(0.5, 0.5) };
        var before = ShapeAnnotationGeometry.Project(normalized, 10, 20, 100, 50);
        var after = ShapeAnnotationGeometry.Project(normalized, 40, 30, 100, 50);

        Assert.Equal(before[0].X + 30, after[0].X, 6);
        Assert.Equal(before[0].Y + 10, after[0].Y, 6);
    }

    [Fact]
    public void ShapeMetadata_RoundTripsAnnotationPolylines()
    {
        var metadata = new AiPasteMetadata
        {
            Kind = AiPasteKind.Shape,
            Text = "三角形的高",
            Annotations = new List<List<double>>
            {
                new() { 0.1, 0.2, 0.5, 0.6 },
                new() { 0.0, 0.0, 1.0, 1.0 },
            },
        };

        var parsed = AiPasteMetadata.TryParse(metadata.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal(AiPasteKind.Shape, parsed.Kind);
        Assert.Equal("三角形的高", parsed.Text);
        Assert.Equal(2, parsed.Annotations.Count);
        Assert.Equal(0.5, parsed.Annotations[0][2], 6);
        Assert.Equal(1.0, parsed.Annotations[1][3], 6);
    }
}