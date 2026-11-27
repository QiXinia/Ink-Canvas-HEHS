using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.History;
using InkCanvas.Core.Selection;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
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
    public void FilledShapeKeepsItsOutlineAndAnErasedHoleAcrossSaveAndReload()
    {
        using var controller = new InkController { EraserRadius = 12 };
        var original = new ShapeDefinition
        {
            Kind = ShapeKind.Rectangle, Parameters = [10, 10, 190, 90],
            Vertices = [10, 10, 190, 10, 190, 90, 10, 90], FillColor = 0xFF369AFF,
        }.Generate(new StrokeAttributes { BaseWidth = 4 }, Guid.NewGuid()).ToArray();
        foreach (var stroke in original) { stroke.InkLayerIndex = 1; stroke.LayerOrder = 7; }
        var lower = original[0].Clone(false);
        lower.InkLayerIndex = 0;
        controller.Document.AddRange([lower, ..original]);
        Assert.True(controller.TrySetActiveInkLayer(1));
        controller.Tool = InkTool.Eraser;
        var pointer = new OverlayPointer(1, 100, 50, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer);
        controller.OnPointerUp(pointer);

        var result = controller.Document.Strokes.ToArray();
        Assert.Equal(3, result.Length);
        Assert.Same(lower, result[0]);
        Assert.All(result.Skip(1), stroke =>
        {
            Assert.Equal(1, stroke.InkLayerIndex);
            Assert.Equal(7, stroke.LayerOrder);
            Assert.Equal(original[0].ShapeSource.GroupId, stroke.ShapeSource.GroupId);
            Assert.False(stroke.ShapeSource.Geometry.IsEditable);
        });
        Assert.Equal(original[1].Points, result[2].Points);
        AssertHole(result[1]);
        Assert.Empty(controller.Document.HitTest(new Rect(97, 47, 103, 53), Matrix3x2.Identity, 1));
        controller.Undo();
        Assert.Equal(new[] { lower }.Concat(original), controller.Document.Strokes);
        controller.Redo();
        Assert.Equal(result, controller.Document.Strokes);
        controller.SelectAll();
        Assert.False(controller.CanEditShape);
        controller.SetShapeLineStyle(ShapeLineStyle.Dashed);
        Assert.Equal(result, controller.Document.Strokes);

        string path = Path.GetTempFileName();
        try
        {
            InkFileStore.Save(path, result);
            AssertHole(InkFileStore.Load(path)[1]);
        }
        finally { File.Delete(path); }

        static void AssertHole(InkStroke stroke)
        {
            Assert.Equal(2, stroke.FillContours().Count());
            Assert.InRange(stroke.DistanceTo(new Vector2(100, 50)), 11.5f, 12f);
            Assert.Equal(0, stroke.DistanceTo(new Vector2(40, 50)));
        }
    }

    [Fact]
    public void DrawingToolRectangleCanBePartiallyErasedOrRemovedAsOneShape()
    {
        using var controller = new InkController { EraserRadius = 10, RecognizeShapes = false };
        controller.SelectShape(ShapeKind.Rectangle, sticky: true);
        var pointer = new OverlayPointer(1, 10, 10, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer);
        controller.OnPointerUp(pointer with { X = 190, Y = 90 });
        var original = Assert.Single(controller.Document.Strokes);

        controller.Tool = InkTool.Eraser;
        var cut = pointer with { X = 100 };
        controller.OnPointerDown(cut);
        controller.OnPointerUp(cut);
        var fragments = controller.Document.Strokes.ToArray();
        Assert.NotEmpty(fragments);
        Assert.All(fragments, stroke => Assert.True(stroke.DistanceTo(new Vector2(100, 10)) >= 9.9f));
        Assert.Contains(fragments, stroke => stroke.DistanceTo(new Vector2(100, 90)) < .01f);
        controller.EraserMode = InkEraserMode.WholeStroke;
        controller.OnPointerDown(cut with { Y = 90 });
        controller.OnPointerUp(cut with { Y = 90 });
        Assert.Empty(controller.Document.Strokes);
        controller.Undo();
        Assert.Equal(fragments, controller.Document.Strokes);
        controller.Undo();
        Assert.Same(original, Assert.Single(controller.Document.Strokes));
    }

    [Fact]
    public void CuttingDashedConnectorKeepsVisibleDashesWithoutRegeneratingTheLine()
    {
        var connector = new ShapeDefinition
        {
            Kind = ShapeKind.Line, Construction = ShapeConstruction.Connector, LineStyle = ShapeLineStyle.Dashed,
            Parameters = [0, 50, 200, 50],
        }.Generate(new StrokeAttributes { BaseWidth = 2 }, Guid.NewGuid()).Single();
        connector.InkLayerIndex = 2;
        Assert.False(StrokeEraser.EraseDisc(connector, new Vector2(10, 50), .5f, out var missed));
        Assert.Same(connector, Assert.Single(missed));
        Assert.True(StrokeEraser.EraseDisc(connector, new Vector2(4, 50), 5, out var remaining));
        Assert.NotEmpty(remaining);
        Assert.All(remaining, fragment =>
        {
            Assert.Equal(2, fragment.InkLayerIndex);
            Assert.Equal(ShapeConstruction.Contours, fragment.ShapeSource.Geometry.Construction);
            Assert.Same(fragment, Assert.Single(ShapeDefinition.RenderContours(fragment)));
            Assert.All(fragment.Points, point => Assert.True(point.X >= 10));
        });
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

    [Fact]
    public void AreaErasePreservesStrokeIdentityAcrossEarlierUndoAndRedo()
    {
        using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Area, EraserRadius = 10 };
        var original = Line();
        var other = Line();
        other.Transform(Matrix3x2.CreateTranslation(0, 400));
        controller.Document.Add(other);
        controller.Document.Add(original);
        controller.History.Push(new DelegateAction("add", () => controller.Document.Add(original), () => controller.Document.Remove(original)));
        var pointer = new OverlayPointer(1, 100, 50, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer);
        controller.OnPointerUp(pointer);
        var fragments = controller.Document.Strokes.Skip(1).ToArray();
        Assert.Equal(2, fragments.Length);
        Assert.Same(other, controller.Document.Strokes[0]);
        controller.Undo();
        Assert.Same(original, controller.Document.Strokes[1]);
        controller.Undo();
        Assert.Same(other, Assert.Single(controller.Document.Strokes));
        controller.Redo();
        controller.Redo();
        Assert.Equal(new[] { other }.Concat(fragments), controller.Document.Strokes);
        controller.SelectAll();
        controller.TransformSelectionUndoable(Matrix3x2.CreateTranslation(20, 30), "move");
        controller.Undo();
        controller.Undo();
        Assert.Same(original, controller.Document.Strokes[1]);
        Assert.Equal(0, original.Points[0].X);
    }

    [Fact]
    public void FastAreaEraseNotifiesOncePerPointerSample()
    {
        using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Area, EraserRadius = 20 };
        controller.Document.AddRange(Enumerable.Range(0, 20).Select(index =>
        {
            var stroke = Line();
            stroke.Transform(Matrix3x2.CreateTranslation(0, index));
            return stroke;
        }));
        var pointer = new OverlayPointer(1, -100, 50, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer);
        int changes = 0;
        controller.Document.Changed += () => changes++;
        controller.OnPointerMove(pointer with { X = 300 });
        Assert.Equal(1, changes);
        controller.OnPointerUp(pointer with { X = 300 });
        Assert.Empty(controller.Document.Strokes);
        controller.Undo();
        Assert.Equal(20, controller.Document.Count);
    }

    [Fact]
    public void EmptyAreaEraseDoesNotCopyDensePageIntoHistory()
    {
        using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Area, EraserRadius = 10 };
        controller.Document.AddRange(Enumerable.Range(0, 250).Select(index =>
        {
            var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None });
            for (int point = 0; point < 400; point++) stroke.AddPoint(point, index);
            stroke.Complete();
            return stroke;
        }));
        var pointer = new OverlayPointer(1, 1000, 1000, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer);
        controller.OnPointerUp(pointer);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        controller.OnPointerDown(pointer);
        controller.OnPointerUp(pointer);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 512 * 1024);
        Assert.False(controller.History.CanUndo);
    }
}
