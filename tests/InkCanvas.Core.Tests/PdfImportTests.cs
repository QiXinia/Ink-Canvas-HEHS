using InkCanvas.Features.Board;
using InkCanvas.Ink;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PdfImportTests
{
    [Fact]
    public void ImportCommitsAllPagesAndNotifiesOnce()
    {
        var board = new BoardService(); var original = board.Current;
        using var first = new InkController(); using var second = new InkController();
        var image = new BoardImageAsset { Data = new byte[] { 1 }, Width = 100, Height = 200 };
        int changes = 0; board.PageChanged += () => changes++;
        Assert.True(board.InsertPagesAfterCurrent(new[] { (first, (IReadOnlyList<BoardImageAsset>)new[] { image }), (second, (IReadOnlyList<BoardImageAsset>)Array.Empty<BoardImageAsset>()) }));
        Assert.Equal(1, changes); Assert.Equal(3, board.PageCount); Assert.Same(original, board.Pages[0]); Assert.Same(first, board.Current);
        Assert.Single(board.GetSelectionElements()); original.Dispose();
    }
    [Fact]
    public void ImportRejectsDuplicateControllersBeforeMutatingPages()
    {
        var board = new BoardService(); var original = board.Current;
        using var extra = new InkController();
        var batch = Enumerable.Range(0, 99).Select(_ => (extra, (IReadOnlyList<BoardImageAsset>)Array.Empty<BoardImageAsset>())).ToArray();
        Assert.Throws<ArgumentException>(() => board.InsertPagesAfterCurrent(batch));
        Assert.Single(board.Pages); Assert.Same(original, board.Current); original.Dispose();
    }
    [Fact]
    public void ImportRejectsReusingALiveControllerBeforeMutatingPages()
    {
        var board = new BoardService();
        Assert.Throws<ArgumentException>(() => board.InsertPagesAfterCurrent(new[] { (board.Current, (IReadOnlyList<BoardImageAsset>)Array.Empty<BoardImageAsset>()) }));
        Assert.Single(board.Pages); board.Current.Dispose();
    }
}
