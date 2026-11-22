using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>空间不足时“写入新的一页”：页面结构保留，卡片插入仍只占一个撤销步骤。</summary>
public sealed class BoardNewPageWriteTests
{
    private static BoardImageAsset Image(string name, byte fill) => new()
    {
        Name = name,
        Data = new[] { fill, (byte)1, (byte)2 },
        ContentType = "image/png",
        Left = 32,
        Top = 32,
        Width = 300,
        Height = 120,
    };

    [Fact]
    public void InsertPageAfterCurrent_ThenAddImagesAtomic_UndoKeepsNewPage()
    {
        var board = new BoardService();
        int originalPages = board.PageCount;
        int originalIndex = board.CurrentIndex;

        Assert.True(board.InsertPageAfterCurrent());
        Assert.Equal(originalPages + 1, board.PageCount);
        Assert.Equal(originalIndex + 1, board.CurrentIndex);
        Assert.Empty(board.Current.Document.Strokes);

        IReadOnlyList<int> indexes = board.AddImagesAtomic(
            new[] { Image("formula", 1), Image("poem", 2) }, "AI 板书（新页）");

        Assert.Equal(new[] { 0, 1 }, indexes);
        Assert.Equal(2, board.CurrentImages.Count);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Empty(board.CurrentImages);
        Assert.Equal(originalPages + 1, board.PageCount);
        Assert.Equal(originalIndex + 1, board.CurrentIndex);
        Assert.Equal(1, board.Current.History.RedoCount);

        board.Current.Redo();
        Assert.Equal(2, board.CurrentImages.Count);
        Assert.Equal(1, board.Current.History.UndoCount);
    }

    [Fact]
    public void InsertPageAfterCurrent_CarriesDrawingStateAndStartsEmpty()
    {
        var board = new BoardService();
        board.Current.CurrentAttributes.BaseWidth = 5;

        Assert.True(board.InsertPageAfterCurrent());

        Assert.Equal(0, board.Current.Document.Count);
        Assert.Empty(board.CurrentImages);
        Assert.Equal(5, board.Current.CurrentAttributes.BaseWidth, 3);
    }

    [Fact]
    public void RemoveNewPage_RestoresPreviousPageAndHistory()
    {
        var board = new BoardService();
        var originalPage = board.Current;
        int originalIndex = board.CurrentIndex;

        Assert.True(board.InsertPageAfterCurrent());
        int newIndex = board.CurrentIndex;
        var newPage = board.Current;
        board.AddImagesAtomic(new[] { Image("card", 3) }, "AI 板书（新页）");

        Assert.True(board.RemovePage(newIndex));
        Assert.Equal(originalIndex, board.CurrentIndex);
        Assert.Same(originalPage, board.Current);
        Assert.NotSame(newPage, board.Current);
        Assert.Empty(board.CurrentImages);
    }
}