using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Modules.ShapeExpansion;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PolygonExpansionTests
{
    private static readonly Vector2[] Concave =
        [new(100, 100), new(260, 110), new(300, 230), new(215, 195), new(230, 310), new(120, 290), new(65, 200)];

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void KeepsTheCornersOfClosedPolygonsEvenWhenStartingOnAnEdge(int count)
    {
        var vertices = Regular(count);
        var middle = Vector2.Lerp(vertices[0], vertices[1], .6f);
        var samples = SampleEdges(new[] { middle }.Concat(vertices.Skip(1)).Append(vertices[0]).ToArray());
        var recognized = new ShapeExpansionModule().RecognizeClosedStroke(Stroke(samples), 1);
        Assert.Equal(count, recognized.Length);
        Assert.All(vertices, vertex => Assert.Contains(recognized, point => Vector2.Distance(point, vertex) < 4));
    }

    [Fact]
    public void RejectsCurvesOpenPathsAndCrossedPaths()
    {
        var extension = new ShapeExpansionModule();
        var round = Regular(240).Append(Regular(240)[0]);
        Assert.Empty(extension.RecognizeClosedStroke(Stroke(round), 1));
        var open = SampleEdges(Concave).Take(95);
        Assert.Empty(extension.RecognizeClosedStroke(Stroke(open), 1));
        var star = Regular(5);
        var crossing = SampleEdges([star[0], star[2], star[4], star[1], star[3]]);
        Assert.Empty(extension.RecognizeClosedStroke(Stroke(crossing), 1));
    }

    [Fact]
    public void StandardPauseCommitsAnEditablePolygonWithUndoAndPersistence()
    {
        using var controller = new InkController { GetPolygonExtension = () => new ShapeExpansionModule() };
        var samples = SampleEdges(Concave);
        FeedStroke(controller, samples);
        controller.AdvanceShapeHold(controller.RecognitionOptions.HoldMilliseconds - 1);
        Assert.Empty(controller.RecognitionPreview);
        controller.AdvanceShapeHold(1);
        var preview = Assert.Single(controller.RecognitionPreview);
        Assert.Equal(ShapeKind.Polygon, preview.ShapeSource.Geometry.Kind);
        Assert.Equal(Concave.Length, preview.ShapeSource.Geometry.ControlPoints().Length);
        controller.OnPointerUp(Pointer(samples[^1], 4000));
        var polygon = Assert.Single(controller.Document.Strokes);
        Assert.Equal(preview.ShapeSource.Geometry.Vertices, polygon.ShapeSource.Geometry.Vertices);
        controller.Undo();
        Assert.False(Assert.Single(controller.Document.Strokes).IsRecognizedShape);
        controller.Redo();
        Assert.Same(polygon, Assert.Single(controller.Document.Strokes));

        string path = Path.GetTempFileName();
        try
        {
            InkFileStore.Save(path, [polygon]);
            var reloaded = Assert.Single(InkFileStore.Load(path));
            Assert.Equal(polygon.ShapeSource.Geometry.Vertices, reloaded.ShapeSource.Geometry.Vertices);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void HonorsModuleAvailabilityPolygonSettingsAndThePause(bool plugin, bool polygons, bool hold)
    {
        var extension = new ShapeExpansionModule();
        using var controller = new InkController { GetPolygonExtension = () => plugin ? extension : null };
        controller.RecognitionOptions.RecognizePolygons = polygons;
        var points = SampleEdges(Regular(5));
        FeedStroke(controller, points);
        if (hold) controller.AdvanceShapeHold(controller.RecognitionOptions.HoldMilliseconds);
        controller.OnPointerUp(Pointer(points[^1], hold ? 4000u : (uint)points.Length * 8 + 1));
        var stroke = Assert.Single(controller.Document.Strokes);
        Assert.NotEqual(ShapeKind.Polygon, stroke.ShapeSource?.Geometry?.Kind);
        if (!hold) Assert.False(stroke.IsRecognizedShape);
    }

    [Fact]
    public void InteriorAnglesIncludeReflexAnglesAndAreInvariantUnderReversedVertexOrder()
    {
        Vector2[] points = [new(0, 0), new(100, 0), new(100, 100), new(50, 50), new(0, 100)];
        var angles = PolygonGeometry.Measure(points);
        Assert.Equal(new[] { 90, 90, 45, 270, 45 }, angles.Select(angle => (int)MathF.Round(angle.Degrees)));
        Assert.Equal(540, angles.Sum(angle => angle.Degrees), 3);
        Assert.Equal(angles.Select(angle => (int)MathF.Round(angle.Degrees)),
            PolygonGeometry.Measure(points.Reverse().ToArray()).Reverse().Select(angle => (int)MathF.Round(angle.Degrees)));
    }

    [Fact]
    public void PresetTriangleOffersThreeIndependentVerticesAndLiveAnglesAfterRotation()
    {
        var extension = new ShapeExpansionModule();
        using var controller = new InkController { GetPolygonExtension = () => extension,
            ViewTransform = Matrix3x2.CreateRotation(.4f) * Matrix3x2.CreateScale(1.5f) };
        controller.SelectPolygon(3);
        controller.OnPointerDown(Pointer(new(100, 100), 100));
        controller.OnPointerUp(Pointer(new(280, 260), 200));
        controller.Tool = InkTool.Select;
        controller.SelectAll();
        controller.ToggleShapeEditing();
        Assert.True(controller.ShowPolygonAngles);
        var before = controller.ShapeControlPoints.ToArray();
        Assert.Equal(3, before.Length);
        var first = Vector2.Transform(before[0], controller.ViewTransform);
        controller.OnPointerDown(Pointer(first, 300));
        controller.OnPointerMove(Pointer(first + new Vector2(18, -22), 340));
        controller.OnPointerUp(Pointer(first + new Vector2(18, -22), 350));
        var after = controller.ShapeControlPoints;
        Assert.Equal(before.Skip(1), after.Skip(1));
        Assert.NotEqual(PolygonGeometry.Measure(before)[0].Degrees, PolygonGeometry.Measure(after)[0].Degrees);
        controller.Undo();
        Assert.Equal(before, Assert.Single(controller.Document.Strokes).ShapeSource.Geometry.ControlPoints());
    }

    private static Vector2[] Regular(int count) => Enumerable.Range(0, count).Select(index =>
    {
        var (sin, cos) = MathF.SinCos(index * MathF.Tau / count);
        return new Vector2(300 + 150 * cos, 300 + 150 * sin);
    }).ToArray();

    private static Vector2[] SampleEdges(Vector2[] vertices)
    {
        var points = new List<Vector2>();
        for (int i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Length];
            var edge = Vector2.Normalize(b - a);
            int samples = (int)MathF.Ceiling(Vector2.Distance(a, b) / 3);
            for (int j = 0; j < samples; j++)
                points.Add(Vector2.Lerp(a, b, j / (float)samples) + new Vector2(-edge.Y, edge.X) * (.65f * MathF.Sin(j)));
        }
        points.Add(points[0]);
        return points.ToArray();
    }

    private static InkStroke Stroke(IEnumerable<Vector2> points)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None });
        foreach (var point in points) stroke.AddPoint(point.X, point.Y);
        return stroke;
    }

    private static void FeedStroke(InkController controller, Vector2[] samples)
    {
        controller.OnPointerDown(Pointer(samples[0], 0));
        for (int i = 1; i < samples.Length; i++) controller.OnPointerMove(Pointer(samples[i], (uint)i * 8));
    }

    private static OverlayPointer Pointer(Vector2 point, uint time) => new(1, point.X, point.Y, .5f, 1, 1, OverlayPointerKind.Mouse, time);
}
