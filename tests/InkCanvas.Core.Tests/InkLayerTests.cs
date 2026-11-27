using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Board;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class InkLayerTests
{
    [Theory]
    [InlineData(InkEraserMode.Area)]
    [InlineData(InkEraserMode.WholeStroke)]
    [InlineData(InkEraserMode.Lasso)]
    public void ErasersOnlyChangeTheActiveLayerAndUndoPreservesLayerMembership(InkEraserMode mode)
    {
        using var controller = new InkController { EraserRadius = 12, EraserMode = mode };
        var bottom = Line(0);
        var top = Line(1);
        controller.Document.AddRange([bottom, top]);
        Assert.True(controller.TrySetActiveInkLayer(1));
        controller.Tool = InkTool.Eraser;
        if (mode == InkEraserMode.Lasso)
        {
            controller.OnPointerDown(Mouse(40, 20));
            controller.OnPointerMove(Mouse(180, 20));
            controller.OnPointerMove(Mouse(180, 90));
            controller.OnPointerMove(Mouse(40, 90));
            controller.OnPointerUp(Mouse(40, 20));
        }
        else
        {
            controller.OnPointerDown(Mouse(100, 50));
            controller.OnPointerUp(Mouse(100, 50));
        }
        Assert.Contains(bottom, controller.Document.Strokes);
        Assert.DoesNotContain(top, controller.Document.Strokes);
        Assert.All(controller.Document.Strokes.Where(stroke => stroke != bottom), stroke => Assert.Equal(1, stroke.InkLayerIndex));
        if (mode != InkEraserMode.Area) Assert.Single(controller.Document.Strokes);
        controller.Undo();
        Assert.Equal(new[] { bottom, top }, controller.DrawingObjects.Select(item => item.Stroke));
    }

    [Fact]
    public void WritingSelectionAndClearFollowTheCurrentLayer()
    {
        using var controller = new InkController { RecognizeShapes = false };
        var bottom = Line(0);
        controller.Document.Add(bottom);
        Assert.True(controller.TryCreateInkLayer());
        controller.OnPointerDown(Mouse(20, 50));
        controller.OnPointerMove(Mouse(190, 50));
        controller.OnPointerUp(Mouse(200, 50));
        var upper = Assert.Single(controller.Document.Strokes, stroke => stroke != bottom);
        Assert.Equal(1, upper.InkLayerIndex);
        controller.SelectAll();
        Assert.Same(upper, Assert.Single(controller.Selection));
        controller.InvertSelection();
        Assert.Empty(controller.Selection);
        Assert.True(controller.TrySetActiveInkLayer(0));
        controller.OnPointerDown(Mouse(90, 50));
        controller.OnPointerUp(Mouse(90, 50));
        Assert.Same(bottom, Assert.Single(controller.Selection));
        controller.ClearCurrentInkLayer();
        Assert.Same(upper, Assert.Single(controller.Document.Strokes));
        controller.Undo();
        Assert.Equal(new[] { bottom, upper }, controller.DrawingObjects.Select(item => item.Stroke));
    }

    [Fact]
    public void ShapeConnectionsCannotAttachToAnotherLayer()
    {
        using var controller = new InkController();
        var node = ShapeGenerator.Generate(ShapeKind.Rectangle, new(40, 30), new(90, 80), new()).Single();
        var source = node.ShapeSource;
        var connector = new ShapeDefinition
        {
            Kind = ShapeKind.Arrow, Construction = ShapeConstruction.Connector,
            StartNode = source.GroupId, Parameters = [0, 0, 140, 50],
        }.Generate(new(), Guid.NewGuid()).Single();
        connector.InkLayerIndex = 1;
        controller.Document.AddRange([node, connector]);
        Assert.Equal(0, connector.Points[0].X);
        var sameLayer = node.Clone();
        sameLayer.InkLayerIndex = 1;
        controller.Document.Add(sameLayer);
        Assert.InRange(connector.Points[0].X, 89, 91);
    }

    [Fact]
    public void BoardPageAndStrokeSnapshotsPreserveLayers()
    {
        var board = new BoardService();
        var reopened = new BoardService();
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bottom = Line(0);
            bottom.LayerOrder = 99;
            var top = Line(1);
            top.LayerOrder = 1;
            board.Current.Document.AddRange([top, bottom]);
            Assert.True(board.Current.TryCreateInkLayer());
            string path = Path.Combine(root, "board.icink");
            board.SaveBoard(path);
            reopened.LoadBoard(path);
            Assert.Equal(3, reopened.Current.Document.InkLayerCount);
            Assert.Equal(new[] { bottom.Id, top.Id }, reopened.Current.DrawingObjects.Select(item => item.Id));
            string strokes = Path.Combine(root, "strokes.icink");
            InkFileStore.Save(strokes, board.Current.Document.Strokes);
            Assert.Equal(new[] { 1, 0 }, InkFileStore.Load(strokes).Select(stroke => stroke.InkLayerIndex));
        }
        finally
        {
            board.Current.Dispose();
            reopened.Current.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static InkStroke Line(int layer)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None }) { InkLayerIndex = layer };
        for (int x = 20; x <= 200; x += 5) stroke.AddPoint(x, 50);
        stroke.Complete();
        return stroke;
    }

    private static OverlayPointer Mouse(float x, float y) => new(1, x, y, .5f, 0, 0, OverlayPointerKind.Mouse, 0);
}
