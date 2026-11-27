using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class RegionSelectionTests
{
    private static readonly Vector2[] Concave = { new(50, 50), new(200, 50), new(200, 90), new(90, 90), new(90, 200), new(50, 200) };

    [Theory]
    [InlineData(OverlayPointerKind.Mouse, .5f)]
    [InlineData(OverlayPointerKind.Pen, 1)]
    [InlineData(OverlayPointerKind.Touch, 2)]
    public void LassoUsesItsActualPolygonAcrossViewTransformsAndInputDevices(OverlayPointerKind kind, float scale)
    {
        using var controller = new InkController { Tool = InkTool.Select, SelectionMode = InkSelectionMode.Lasso,
            ViewTransform = Matrix3x2.CreateRotation(.2f) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(200, 100) };
        var inside = Stroke(new(65, 120), new(75, 175));
        var outside = Stroke(new(130, 130), new(170, 170));
        var crossing = Stroke(new(20, 70), new(230, 70));
        controller.Document.AddRange(new[] { inside, outside, crossing });
        DrawLasso(controller, Concave, kind);
        Assert.Equal(new[] { inside, crossing }, controller.Selection);
        Assert.False(controller.IsRegionSelecting);
        Assert.Empty(controller.LassoPath);
        Assert.False(controller.HasActiveInteraction);
        Assert.Equal(0, controller.History.UndoCount);
        Assert.Equal(3, controller.Document.Count);
    }

    [Fact]
    public void LassoIncludesMixedObjectsAndWholeShapeGroupsAndSupportsInverting()
    {
        using var controller = new InkController { Tool = InkTool.Select, SelectionMode = InkSelectionMode.Lasso };
        var store = new BoardService(controller);
        var inside = Stroke(new(60, 110), new(70, 160));
        var shapePart = Stroke(new(110, 65), new(130, 70));
        var shapeOtherPart = Stroke(new(250, 130), new(290, 140));
        var source = new ShapeSource(Guid.NewGuid(), "test", "group");
        shapePart.ShapeSource = shapeOtherPart.ShapeSource = source;
        var outside = Stroke(new(150, 140), new(180, 170));
        controller.Document.AddRange(new[] { inside, shapePart, shapeOtherPart, outside });
        var image = new BoardImageAsset { Data = new byte[] { 1 }, Left = 60, Top = 150, Width = 20, Height = 20 };
        var excluded = new BoardImageAsset { Data = new byte[] { 2 }, Left = 130, Top = 130, Width = 20, Height = 20 };
        var video = new BoardMediaAsset { Data = new byte[] { 1 }, Left = 150, Top = 55, Width = 25, Height = 25 };
        store.AddImage(image); store.AddImage(excluded); store.AddMedia(video);
        controller.GetSelectableElements = store.GetSelectionElements;
        controller.History.Clear();
        DrawLasso(controller, Concave);
        Assert.Contains(inside, controller.Selection);
        Assert.Contains(shapePart, controller.Selection);
        Assert.Contains(shapeOtherPart, controller.Selection);
        Assert.DoesNotContain(outside, controller.Selection);
        var selectedIds = controller.SelectedElements.Select(element => element.Id).ToHashSet();
        Assert.Equal(2, selectedIds.Count);
        controller.InvertSelection();
        Assert.Same(outside, Assert.Single(controller.Selection));
        Assert.DoesNotContain(Assert.Single(controller.SelectedElements).Id, selectedIds);
        controller.InvertSelection();
        Assert.Equal(3, controller.Selection.Count);
        Assert.Equal(selectedIds, controller.SelectedElements.Select(element => element.Id).ToHashSet());
        controller.SelectAll(); controller.InvertSelection();
        Assert.False(controller.HasSelection);
        controller.InvertSelection();
        Assert.Equal(4, controller.Selection.Count);
        Assert.Equal(3, controller.SelectedElements.Count);
        Assert.Equal(0, controller.History.UndoCount);
        Assert.Equal(4, controller.Document.Count);
    }

    [Fact]
    public void LassoCanAddToAnExistingSelectionWithoutMovingIt()
    {
        using var controller = new InkController { Tool = InkTool.Select, SelectionMode = InkSelectionMode.Lasso };
        var initial = Stroke(new(300, 300), new(320, 310));
        var inside = Stroke(new(70, 120), new(75, 160));
        controller.Document.AddRange(new[] { initial, inside });
        controller.Selection.Add(initial);
        DrawLasso(controller, Concave, shift: true);
        Assert.Equal(new[] { initial, inside }, controller.Selection);
        Assert.Equal(300, initial.Points[0].X);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("tool")]
    [InlineData("mode")]
    public void InterruptedLassoDropsItsPathAndNeverAppliesAnUnfinishedSelection(string interruption)
    {
        using var controller = new InkController { Tool = InkTool.Select, SelectionMode = InkSelectionMode.Lasso };
        controller.Document.Add(Stroke(new(70, 120), new(75, 160)));
        controller.OnPointerDown(Pointer(Concave[0]));
        foreach (var point in Concave.Skip(1)) controller.OnPointerMove(Pointer(point));
        Assert.True(controller.IsLassoSelecting);
        Assert.True(controller.HasActiveInteraction);
        if (interruption == "cancel") controller.OnPointerCanceled(Pointer(Concave[0]));
        else if (interruption == "tool") controller.Tool = InkTool.Pen;
        else controller.SelectionMode = InkSelectionMode.Rectangle;
        Assert.False(controller.IsRegionSelecting);
        Assert.Empty(controller.LassoPath);
        Assert.False(controller.HasSelection);
        Assert.Equal(1, controller.Document.Count);
    }

    [Fact]
    public void LassoCanStartOnUnselectedInkAndStillSupportsPointSelection()
    {
        using var controller = new InkController { Tool = InkTool.Select, SelectionMode = InkSelectionMode.Lasso };
        var stroke = Stroke(new(50, 50), new(110, 65)); controller.Document.Add(stroke);
        var points = stroke.Points.ToArray();
        controller.OnPointerDown(Pointer(new(50, 50)));
        Assert.True(controller.IsLassoSelecting);
        foreach (var point in Concave.Skip(1)) controller.OnPointerMove(Pointer(point));
        controller.OnPointerUp(Pointer(Concave[0]));
        Assert.Same(stroke, Assert.Single(controller.Selection));
        Assert.Equal(points, stroke.Points);
        controller.ClearSelection();
        controller.OnPointerDown(Pointer(new(90, 60)));
        controller.OnPointerUp(Pointer(new(90, 60)));
        Assert.Same(stroke, Assert.Single(controller.Selection));
        Assert.Equal(0, controller.History.UndoCount);
    }

    [Fact]
    public void RectangleRemainsTheDefaultAndLassoPreferenceRoundTrips()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}");
        Assert.Equal(InkSelectionMode.Rectangle, settings.Canvas.SelectionMode);
        using var controller = new InkController { Tool = InkTool.Select };
        var stroke = Stroke(new(100, 100), new(200, 200)); controller.Document.Add(stroke);
        controller.OnPointerDown(Pointer(new(70, 70)));
        controller.OnPointerMove(Pointer(new(230, 230)));
        Assert.True(controller.IsMarqueeSelecting);
        controller.OnPointerUp(Pointer(new(230, 230)));
        Assert.Same(stroke, Assert.Single(controller.Selection));
        settings.Canvas.SelectionMode = InkSelectionMode.Lasso;
        Assert.Equal(InkSelectionMode.Lasso, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)).Canvas.SelectionMode);
    }

    private static InkStroke Stroke(params Vector2[] points)
    {
        var stroke = new InkStroke(new() { BaseWidth = 2, TaperKind = TaperKind.None });
        foreach (var point in points) stroke.AddPoint(point.X, point.Y);
        stroke.Complete(); return stroke;
    }

    private static OverlayPointer Pointer(Vector2 point, OverlayPointerKind kind = OverlayPointerKind.Mouse, bool shift = false) =>
        new(450, point.X, point.Y, .5f, 4, 4, kind, 0, Shift: shift);

    private static void DrawLasso(InkController controller, Vector2[] points, OverlayPointerKind kind = OverlayPointerKind.Mouse, bool shift = false)
    {
        Vector2 View(Vector2 point) => Vector2.Transform(point, controller.ViewTransform);
        controller.OnPointerDown(Pointer(View(points[0]), kind, shift));
        foreach (var point in points.Skip(1)) controller.OnPointerMove(Pointer(View(point), kind, shift));
        controller.OnPointerUp(Pointer(View(points[0]), kind, shift));
    }
}
