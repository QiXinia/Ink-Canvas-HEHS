using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class DrawingLayerTests
{
    private static InkStroke Stroke(int value)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 12, TaperKind = TaperKind.None });
        stroke.AddPoint(value, 100); stroke.AddPoint(value + 200, 100); stroke.Complete();
        return stroke;
    }

    private static (BoardService Store, DrawingObject[] Objects) Scene(InkController controller)
    {
        var store = new BoardService(controller);
        var a = Stroke(10); var c = Stroke(30); var f = Stroke(60);
        controller.Document.AddRange(new[] { a, c, f });
        store.AddImage(new BoardImageAsset { Name = "b", Data = new byte[] { 1 }, Width = 300, Height = 250 });
        store.AddMedia(new BoardMediaAsset { Name = "d", Data = new byte[] { 2 }, Width = 300, Height = 250 });
        store.AddImage(new BoardImageAsset { Name = "e", Data = new byte[] { 3 }, Width = 300, Height = 250 });
        var elements = store.GetSelectionElements().ToDictionary(item => ((BoardSelectionElement)item).Image?.Name ?? ((BoardSelectionElement)item).Media.Name);
        var objects = new[] { new DrawingObject(a, null), new(null, elements["b"]), new(c, null),
            new(null, elements["d"]), new(null, elements["e"]), new(f, null) };
        for (int i = 0; i < objects.Length; i++) { var item = objects[i]; item.LayerOrder = i; }
        controller.NotifyElementsChanged(); controller.Tool = InkTool.Select; controller.History.Clear();
        return (store, objects);
    }

    [Theory]
    [InlineData(LayerAction.BringToFront, new[] { 0, 2, 4, 5, 1, 3 })]
    [InlineData(LayerAction.MoveForward, new[] { 0, 2, 1, 4, 3, 5 })]
    [InlineData(LayerAction.MoveBackward, new[] { 1, 0, 3, 2, 4, 5 })]
    [InlineData(LayerAction.SendToBack, new[] { 1, 3, 0, 2, 4, 5 })]
    public void MixedMultiselectionKeepsRelativeOrderAndRecordsOneUndo(LayerAction action, int[] expected)
    {
        using var controller = new InkController();
        var (_, objects) = Scene(controller);
        controller.SelectedElements.AddRange(new[] { objects[3].Element, objects[1].Element });
        var frame = controller.SelectionFrame;
        Assert.True(controller.CanArrangeSelection(action));
        controller.ArrangeSelection(action);
        Assert.Equal(expected.Select(index => objects[index]), controller.DrawingObjects);
        Assert.Equal(1, controller.History.UndoCount);
        Assert.Equal(frame, controller.SelectionFrame);
        controller.Undo(); Assert.Equal(objects, controller.DrawingObjects);
        controller.Redo(); Assert.Equal(expected.Select(index => objects[index]), controller.DrawingObjects);
        Assert.Equal(new[] { objects[3].Element, objects[1].Element }, controller.SelectedElements);
    }

    [Fact]
    public void AdjacentMixedObjectsMoveAsABlockWithoutReversingTheSelection()
    {
        using var controller = new InkController();
        var (_, objects) = Scene(controller);
        controller.Selection.Add(objects[2].Stroke); controller.SelectedElements.Add(objects[1].Element);
        controller.ArrangeSelection(LayerAction.MoveForward);
        Assert.Equal(new[] { 0, 3, 1, 2, 4, 5 }.Select(index => objects[index]), controller.DrawingObjects);
        controller.ArrangeSelection(LayerAction.MoveBackward);
        Assert.Equal(objects, controller.DrawingObjects);
    }

    [Fact]
    public void BoundaryOperationsAndWholePageSelectionAreNoOps()
    {
        using var controller = new InkController();
        var (_, objects) = Scene(controller);
        controller.Selection.Add(objects[^1].Stroke);
        Assert.False(controller.CanArrangeSelection(LayerAction.MoveForward));
        controller.ArrangeSelection(LayerAction.BringToFront);
        Assert.Equal(0, controller.History.UndoCount);
        controller.SelectAll();
        foreach (var action in Enum.GetValues<LayerAction>())
        {
            Assert.False(controller.CanArrangeSelection(action));
            controller.ArrangeSelection(action);
        }
        Assert.Equal(0, controller.History.UndoCount); Assert.Equal(objects, controller.DrawingObjects);
    }

    [Fact]
    public void TopmostHitTestMatchesTheLayerOrderEvenWhenTheLowerObjectIsSelected()
    {
        using var controller = new InkController();
        var (_, objects) = Scene(controller);
        controller.SelectElement(objects[1].Element);
        controller.ArrangeSelection(LayerAction.SendToBack);
        var pointer = new OverlayPointer(17, 100, 100, .5f, 0, 0, OverlayPointerKind.Mouse, 1);
        controller.OnPointerDown(pointer); controller.OnPointerUp(pointer);
        Assert.Same(objects[5].Stroke, Assert.Single(controller.Selection));
        controller.ArrangeSelection(LayerAction.SendToBack);
        controller.ClearSelection();
        controller.OnPointerDown(pointer); controller.OnPointerUp(pointer);
        Assert.Same(objects[4].Element, Assert.Single(controller.SelectedElements));
    }

    [Fact]
    public void MixedCloneStaysAtTheTopInSourceOrderAndUndoRestoresThePage()
    {
        using var controller = new InkController();
        var (store, objects) = Scene(controller);
        controller.Selection.AddRange(new[] { objects[5].Stroke, objects[0].Stroke });
        controller.SelectedElements.AddRange(new[] { objects[4].Element, objects[1].Element });
        controller.CloneMode = true;
        var from = controller.SelectionFrame.Center(Matrix3x2.Identity);
        var pointer = new OverlayPointer(18, from.X, from.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 1);
        controller.OnPointerDown(pointer); controller.OnPointerMove(pointer with { X = from.X + 400, Y = from.Y + 300 });
        controller.OnPointerUp(pointer with { X = from.X + 400, Y = from.Y + 300 });
        var clones = controller.DrawingObjects.Skip(6).ToArray();
        Assert.Equal(4, clones.Length);
        Assert.Equal(new[] { true, false, false, true }, clones.Select(item => item.Stroke is not null));
        Assert.Equal(new[] { "b", "e" }, clones.Where(item => item.Element is not null).Select(item => ((BoardSelectionElement)item.Element).Image.Name));
        controller.Undo(); Assert.Equal(objects, controller.DrawingObjects); Assert.Equal(2, store.CurrentImages.Count);
        controller.Redo(); Assert.Equal(10, controller.DrawingObjects.Count);
    }

    [Fact]
    public void CustomOrderSurvivesFilesAndOldFilesRetainTheirOriginalStacking()
    {
        using var controller = new InkController();
        var (store, objects) = Scene(controller);
        controller.Selection.Add(objects[2].Stroke); controller.SelectedElements.Add(objects[1].Element);
        controller.ArrangeSelection(LayerAction.BringToFront);
        var expected = controller.DrawingObjects.Select(Key).ToArray();
        string path = Path.Combine(Path.GetTempPath(), $"inkcanvas-layers-{Guid.NewGuid():N}.icink");
        try
        {
            store.SavePage(path);
            var loaded = new BoardService(); loaded.LoadPage(path);
            Assert.Equal(expected, loaded.Current.DrawingObjects.Select(Key));
            foreach (var item in loaded.Current.DrawingObjects) { var copy = item; copy.LayerOrder = null; }
            loaded.Current.NotifyElementsChanged();
            Assert.Equal(new[] { "b", "e", "d", "10", "30", "60" }, loaded.Current.DrawingObjects.Select(Key));
        }
        finally { File.Delete(path); }
        static string Key(DrawingObject item) => item.Stroke is not null ? item.Stroke.Points[0].X.ToString()
            : ((BoardSelectionElement)item.Element).Image?.Name ?? ((BoardSelectionElement)item.Element).Media.Name;
    }

    [Fact]
    public void ErasedFragmentsRetainTheirLayerAndLayerUndoNeverResurrectsDeletedObjects()
    {
        using var controller = new InkController();
        var (_, objects) = Scene(controller);
        var stroke = objects[0].Stroke;
        Assert.True(StrokeEraser.EraseDisc(stroke, new Vector2(110, 100), 10, out var fragments));
        Assert.All(fragments, fragment => Assert.Equal(stroke.LayerOrder, fragment.LayerOrder));
        controller.Selection.Add(stroke); controller.ArrangeSelection(LayerAction.BringToFront);
        controller.Document.Remove(stroke);
        controller.Undo(); Assert.DoesNotContain(controller.DrawingObjects, item => ReferenceEquals(item.Stroke, stroke));
    }

    [Fact]
    public void EveryMixedMultiselectionPreservesBothInternalOrdersAndCreatesAtMostOneUndoStep()
    {
        foreach (var action in Enum.GetValues<LayerAction>())
        for (int mask = 1; mask < 63; mask++)
        {
            using var controller = new InkController();
            var (_, objects) = Scene(controller);
            var selected = objects.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            foreach (var item in selected.Reverse())
            {
                if (item.Stroke is not null) controller.Selection.Add(item.Stroke);
                else controller.SelectedElements.Add(item.Element);
            }
            var chosen = selected.ToHashSet();
            bool available = controller.CanArrangeSelection(action);
            controller.ArrangeSelection(action);
            Assert.Equal(selected, controller.DrawingObjects.Where(chosen.Contains));
            Assert.Equal(objects.Where(item => !chosen.Contains(item)), controller.DrawingObjects.Where(item => !chosen.Contains(item)));
            Assert.Equal(objects.Length, controller.DrawingObjects.Distinct().Count());
            Assert.Equal(available ? 1 : 0, controller.History.UndoCount);
            controller.Undo(); Assert.Equal(objects, controller.DrawingObjects);
        }
    }

    [Fact]
    public void OverlappingUnselectedInkDoesNotBreakControlDraggingAMultiselection()
    {
        using var controller = new InkController();
        var (_, objects) = Scene(controller);
        controller.Selection.AddRange(new[] { objects[0].Stroke, objects[2].Stroke });
        var center = controller.SelectionFrame.Center(Matrix3x2.Identity);
        var pointer = new OverlayPointer(19, center.X, center.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 1, Control: true);
        controller.OnPointerDown(pointer);
        controller.OnPointerMove(pointer with { X = center.X + 400, Y = center.Y + 300 });
        Assert.Equal(2, controller.PreviewStrokes.Count);
        Assert.Equal(5, controller.Document.Count);
        controller.OnPointerUp(pointer with { X = center.X + 400, Y = center.Y + 300 });
        Assert.Equal(2, controller.Selection.Count);
        Assert.DoesNotContain(objects[0].Stroke, controller.Selection);
        Assert.Equal(1, controller.History.UndoCount);
    }
}
