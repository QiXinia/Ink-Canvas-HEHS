using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ImageSelectionTests
{
    private static BoardImageAsset Image() => new()
    {
        Data = new byte[] { 1, 2, 3 }, Left = 100, Top = 120, Width = 240, Height = 160,
    };

    private static BoardService Store(InkController controller)
    {
        var store = new BoardService(controller);
        controller.GetSelectableElements = store.GetSelectionElements;
        return store;
    }

    private static OverlayPointer Pointer(Vector2 point, OverlayPointerKind kind = OverlayPointerKind.Mouse, bool control = false) =>
        new(451, point.X, point.Y, .5f, 0, 0, kind, 0, Control: control);

    [Theory]
    [InlineData(OverlayPointerKind.Mouse, .5f)]
    [InlineData(OverlayPointerKind.Pen, 1f)]
    [InlineData(OverlayPointerKind.Touch, 2f)]
    public void ImageOnlySelectionSupportsMoveResizeAndUndoAtZoom(OverlayPointerKind kind, float zoom)
    {
        using var controller = new InkController { ViewTransform = Matrix3x2.CreateScale(zoom) };
        var store = Store(controller);
        var image = Image(); store.AddImage(image); controller.History.Clear();
        controller.Tool = InkTool.Select;
        var center = new Vector2(220, 200) * zoom;
        controller.OnPointerDown(Pointer(center, kind));
        controller.OnPointerUp(Pointer(center, kind));
        Assert.Empty(controller.Selection);
        Assert.Same(image, Assert.IsType<BoardSelectionElement>(Assert.Single(controller.SelectedElements)).Image);
        Assert.True(controller.HasSelection);
        controller.OnPointerDown(Pointer(center, kind));
        controller.OnPointerMove(Pointer(center + new Vector2(60, 40) * zoom, kind));
        Assert.Equal(new Vector2(280, 240) * zoom, controller.SelectionFrame.Center(controller.ViewTransform));
        controller.OnPointerUp(Pointer(center + new Vector2(60, 40) * zoom, kind));
        Assert.Equal(1, controller.History.UndoCount);
        controller.Undo();
        Assert.Equal(new Vector2(220, 200) * zoom, controller.SelectionFrame.Center(controller.ViewTransform));
        var frame = controller.SelectionFrame;
        var corner = frame.HandlePosition(SelectionHandle.BottomRight, controller.ViewTransform);
        var target = corner + new Vector2(120, 80) * zoom;
        controller.OnPointerDown(Pointer(corner, kind));
        controller.OnPointerMove(Pointer(target, kind));
        controller.OnPointerUp(Pointer(target, kind));
        Assert.True(controller.SelectionBounds.Width > frame.Bounds.Width);
        controller.Undo();
        Assert.Equal(frame, controller.SelectionFrame);
    }

    [Fact]
    public void ImageOpacityIsUndoableAndSurvivesCloneAndPersistence()
    {
        using var controller = new InkController();
        var store = Store(controller);
        var image = Image(); store.AddImage(image);
        var element = Assert.Single(store.GetSelectionElements());
        controller.SelectElement(element); controller.History.Clear();
        controller.SetSelectionImageOpacity(.5f);
        Assert.Equal(.5f, image.Opacity);
        Assert.Equal(.5f, Assert.IsType<BoardSelectionElement>(element.Duplicate()).Image.Opacity);
        controller.Undo(); Assert.Equal(1f, image.Opacity);
        controller.Redo(); Assert.Equal(.5f, image.Opacity);
        string path = Path.Combine(Path.GetTempPath(), $"inkcanvas-images-{Guid.NewGuid():N}.icink");
        try
        {
            store.SavePage(path);
            var loaded = new BoardService(); loaded.LoadPage(path);
            Assert.Equal(.5f, Assert.Single(loaded.CurrentImages).Opacity);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                Format = "inkcanvas-board", Version = 1,
                Pages = new[] { new { Images = new[] { new { image.Data, image.Left, image.Top, image.Width, image.Height } } } },
            }));
            loaded.LoadPage(path);
            Assert.Equal(1f, Assert.Single(loaded.CurrentImages).Opacity);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ControlCloneOfAnImageSelectsTheCopyAndLeavesTheSourceUnchanged()
    {
        using var controller = new InkController();
        var store = Store(controller);
        var image = Image(); store.AddImage(image);
        controller.SelectElement(Assert.Single(store.GetSelectionElements()));
        controller.History.Clear();
        controller.OnPointerDown(Pointer(new(220, 200), control: true));
        controller.OnPointerMove(Pointer(new(300, 250), control: true));
        Assert.Equal(2, store.CurrentImages.Count);
        Assert.Equal(Matrix3x2.Identity, BoardElementMath.Read(image.Transform));
        controller.OnPointerUp(Pointer(new(300, 250), control: true));
        var selected = Assert.IsType<BoardSelectionElement>(Assert.Single(controller.SelectedElements));
        Assert.NotSame(image, selected.Image);
        Assert.Equal(new Vector2(80, 50), selected.Frame.Transform.Translation);
        controller.Undo(); Assert.Single(store.CurrentImages);
        Assert.Same(image, Assert.IsType<BoardSelectionElement>(Assert.Single(controller.SelectedElements)).Image);
        controller.Redo(); Assert.Equal(2, store.CurrentImages.Count);
    }

    [Fact]
    public void ClearAndDeleteRestoreImageOrderAndKeepSelectionAdaptersStable()
    {
        using var controller = new InkController();
        var store = Store(controller);
        var images = new[] { Image(), Image(), Image() };
        foreach (var image in images) store.AddImage(image);
        var adapters = store.GetSelectionElements().ToArray();
        Assert.Equal(adapters, store.GetSelectionElements());
        controller.SelectElement(adapters[1]); controller.DeleteSelection();
        Assert.Equal(new[] { images[0], images[2] }, store.CurrentImages);
        controller.Undo(); Assert.Equal(images, store.CurrentImages);
        controller.Clear(); Assert.Empty(store.CurrentImages);
        controller.Undo(); Assert.Equal(images, store.CurrentImages);
        controller.Redo(); Assert.Empty(store.CurrentImages);
    }
}
