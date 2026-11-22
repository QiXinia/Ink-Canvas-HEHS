using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>AI 板书整体写入：同一次撤销/重做覆盖全部卡片，且不影响既有图片。</summary>
public sealed class BoardImagesAtomicTests
{
    private static BoardImageAsset Image(string name, byte fill, float left) => new()
    {
        Name = name,
        Data = new[] { fill, (byte)1, (byte)2 },
        ContentType = "image/png",
        Left = left,
        Top = left,
        Width = 100,
        Height = 100,
    };

    [Fact]
    public void AddImagesAtomic_InsertsAllAndUndoesAsOneStep()
    {
        var board = new BoardService();
        var first = Image("first", 1, 10);
        var second = Image("second", 2, 120);

        IReadOnlyList<int> indexes = board.AddImagesAtomic(new[] { first, second }, "AI 板书");

        Assert.Equal(new[] { 0, 1 }, indexes);
        Assert.Equal(new[] { "first", "second" }, board.CurrentImages.Select(image => image.Name));
        Assert.Equal(1, board.Current.History.UndoCount);
        Assert.Equal(0, first.LayerOrder);
        Assert.Equal(1, second.LayerOrder);

        board.Current.Undo();
        Assert.Empty(board.CurrentImages);
        Assert.Equal(0, board.Current.History.UndoCount);
        Assert.Equal(1, board.Current.History.RedoCount);

        board.Current.Redo();
        Assert.Equal(new[] { "first", "second" }, board.CurrentImages.Select(image => image.Name));
        Assert.Equal(1, board.Current.History.UndoCount);
    }

    [Fact]
    public void AddImagesAtomic_SkipsInvalidImagesAndKeepsSingleStep()
    {
        var board = new BoardService();
        var valid = Image("valid", 1, 0);
        var invalid = new BoardImageAsset { Name = "bad", Data = Array.Empty<byte>(), Width = 0, Height = 0 };

        IReadOnlyList<int> indexes = board.AddImagesAtomic(new[] { invalid, valid }, "AI 板书");

        Assert.Equal(new[] { 0 }, indexes);
        Assert.Single(board.CurrentImages);
        Assert.Equal("valid", board.CurrentImages[0].Name);
        Assert.Equal(1, board.Current.History.UndoCount);
    }

    [Fact]
    public void AddImagesAtomic_EmptyInput_IsNoOp()
    {
        var board = new BoardService();

        Assert.Empty(board.AddImagesAtomic(Array.Empty<BoardImageAsset>(), "AI 板书"));
        Assert.Empty(board.AddImagesAtomic(null, "AI 板书"));
        Assert.Equal(0, board.Current.History.UndoCount);
        Assert.Empty(board.CurrentImages);
    }

    [Fact]
    public void GetContentBounds_IncludesImagesAndMedia()
    {
        var board = new BoardService();
        Assert.True(board.AddImage(Image("image", 1, 200)));
        Assert.True(board.AddMedia(new BoardMediaAsset
        {
            Name = "video",
            Data = new byte[] { 1 },
            Width = 50,
            Height = 40,
            Left = 400,
            Top = 300,
        }));

        Rect bounds = board.GetContentBounds();

        Assert.Equal(200f, bounds.Left);
        Assert.Equal(200f, bounds.Top);
        Assert.Equal(450f, bounds.Right);
        Assert.Equal(340f, bounds.Bottom);
    }

    [Fact]
    public void GetContentBounds_IncludesStrokesAndEmptyPageIsEmpty()
    {
        var board = new BoardService();
        Assert.True(board.GetContentBounds().IsEmpty);

        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None, BaseWidth = 3 });
        stroke.AddPoint(10, 20);
        stroke.AddPoint(60, 90);
        stroke.Complete();
        board.Current.Document.Add(stroke);

        Rect bounds = board.GetContentBounds();

        Assert.True(bounds.Left <= 10f);
        Assert.True(bounds.Top <= 20f);
        Assert.True(bounds.Right >= 60f);
        Assert.True(bounds.Bottom >= 90f);
    }
}