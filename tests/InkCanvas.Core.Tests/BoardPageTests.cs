using System.IO.Compression;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Pdf;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class BoardPageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("inkcanvas-board-pages-").FullName;
    private readonly List<BoardService> _boards = new();
    private string FilePath => Path.Combine(_directory, "pages.icink");

    private BoardService Board()
    {
        var board = new BoardService();
        _boards.Add(board);
        return board;
    }

    [Fact]
    public void PaperFollowsItsPageAcrossInsertionDeletionAndClearing()
    {
        var board = Board();
        board.SetBackground(BoardBackground.Grid); board.GridSpacing = 56;
        var first = board.Current;
        Assert.True(board.InsertPageAfterCurrent());
        AssertPaper(board, BoardBackground.Grid, 56);
        board.SetBackground(BoardBackground.Black); board.GridSpacing = 32;
        var second = board.Current;
        board.GoTo(0);
        AssertPaper(board, BoardBackground.Grid, 56);
        Assert.True(board.InsertPageAfterCurrent());
        board.SetBackground(BoardBackground.Lined); board.GridSpacing = 24;
        board.RemoveCurrentPage();
        Assert.Same(second, board.Current);
        AssertPaper(board, BoardBackground.Black, 32);
        board.RemoveCurrentPage();
        Assert.Same(first, board.Current);
        AssertPaper(board, BoardBackground.Grid, 56);
        board.RemoveCurrentPage();
        Assert.Single(board.Pages);
        AssertPaper(board, BoardBackground.Grid, 56);
    }

    [Fact]
    public async Task HundredsOfPagesKeepTheirOwnPaperAndInkAfterSavingAndReopening()
    {
        var board = Board();
        for (int i = 0; i < 250; i++)
        {
            if (i > 0) board.NextPage();
            board.Background = (BoardBackground)(i % 4);
            board.GridSpacing = 20 + i % 100;
            var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 5 });
            stroke.AddPoint(i, 20); stroke.AddPoint(i + 10, 30); stroke.Complete();
            board.Current.Document.Add(stroke);
        }
        Assert.Equal(250, board.PageCount);
        await board.SaveBoardAsync(FilePath);
        var loaded = Board();
        await loaded.LoadBoardAsync(FilePath);
        Assert.Equal(250, loaded.PageCount);
        for (int i = 249; i >= 0; i--)
        {
            loaded.GoTo(i);
            AssertPaper(loaded, (BoardBackground)(i % 4), 20 + i % 100);
            Assert.Equal(i, Assert.Single(loaded.Current.Document.Strokes).Points[0].X);
        }
        loaded.GoTo(249); loaded.NextPage();
        Assert.Equal(251, loaded.PageCount);
        AssertPaper(loaded, (BoardBackground)(249 % 4), 69);
    }

    [Fact]
    public void BatchImportAndSelectionCloningWorkBeyondNinetyNinePages()
    {
        var board = Board();
        board.Background = BoardBackground.Grid; board.GridSpacing = 48;
        var batch = Enumerable.Range(0, 150)
            .Select(_ => (new InkController(), (IReadOnlyList<BoardImageAsset>)Array.Empty<BoardImageAsset>())).ToArray();
        int changes = 0;
        board.PageChanged += () => changes++;
        Assert.True(board.InsertPagesAfterCurrent(batch));
        Assert.Equal(1, changes); Assert.Equal(151, board.PageCount);
        board.GoTo(150);
        AssertPaper(board, BoardBackground.Grid, 48);
        board.AddImage(new() { Data = new byte[] { 1 }, Width = 100, Height = 100 });
        board.Current.SelectAll();
        Assert.True(board.CloneSelectionToNewPage());
        Assert.Equal(152, board.PageCount); Assert.Single(board.CurrentImages);
        AssertPaper(board, BoardBackground.Grid, 48);
        board.Background = BoardBackground.White;
        board.PreviousPage();
        AssertPaper(board, BoardBackground.Grid, 48);
        Assert.Equal(150, PdfPageRange.Parse("1-150", 200, 200).Count);
        Assert.False(board.CanAddPages(int.MaxValue));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyBoardPaperMigratesToEveryPage(int version)
    {
        string json = JsonSerializer.Serialize(new
        {
            Format = "inkcanvas-board", Version = version, Background = "Grid", GridSpacing = 56,
            Pages = Enumerable.Range(0, 120).Select(_ => new { Strokes = Array.Empty<object>() }).ToArray(),
        });
        await WriteManifest(json, version == 2);
        var board = Board();
        await board.LoadBoardAsync(FilePath);
        Assert.Equal(120, board.PageCount);
        for (int i = 0; i < board.PageCount; i++)
        {
            board.GoTo(i);
            AssertPaper(board, BoardBackground.Grid, 56);
        }
        board.Background = BoardBackground.Black;
        board.GoTo(0);
        AssertPaper(board, BoardBackground.Grid, 56);
        await board.SaveBoardAsync(FilePath);
        await board.LoadBoardAsync(FilePath);
        board.GoTo(119);
        AssertPaper(board, BoardBackground.Black, 56);
    }

    [Fact]
    public async Task PerPagePaperOverridesLegacyDefaultsAndInvalidValuesFallBackSafely()
    {
        await WriteManifest("""
            {"Format":"inkcanvas-board","Version":3,"Background":"White","GridSpacing":56,
             "Pages":[{"Background":"Lined","GridSpacing":24},{"Background":"999","GridSpacing":-1},
                      {"Background":"Grid","GridSpacing":1000}]}
            """, true);
        var board = Board();
        await board.LoadBoardAsync(FilePath);
        AssertPaper(board, BoardBackground.Lined, 24);
        board.NextPage(); AssertPaper(board, BoardBackground.White, 56);
        board.NextPage(); AssertPaper(board, BoardBackground.Grid, 160);
    }

    [Fact]
    public async Task SinglePageOpenRestoresPaperToTheCapturedTargetPage()
    {
        var source = Board();
        source.Background = BoardBackground.Lined; source.GridSpacing = 28;
        await source.SavePageAsync(FilePath);
        var target = Board();
        target.Background = BoardBackground.Grid; target.GridSpacing = 64;
        var apply = await target.PrepareOpenAsync(FilePath, false);
        target.InsertPageAfterCurrent();
        target.Background = BoardBackground.White;
        apply();
        AssertPaper(target, BoardBackground.White, 64);
        target.PreviousPage();
        AssertPaper(target, BoardBackground.Lined, 28);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PluginImportsRestorePagePaper(bool entireBoard)
    {
        var source = Board();
        source.Background = BoardBackground.Lined; source.GridSpacing = 28;
        await source.SavePageAsync(FilePath);
        var target = Board();
        var apply = await target.PrepareImportedPageAsync(FilePath, entireBoard);
        File.Delete(FilePath);
        apply();
        AssertPaper(target, BoardBackground.Lined, 28);
    }

    [Fact]
    public async Task PaperSnapshotIsStableWhileSavingAndEditingOtherPages()
    {
        var source = Board();
        source.Background = BoardBackground.Grid; source.GridSpacing = 56;
        source.InsertPageAfterCurrent(); source.Background = BoardBackground.Black;
        Task save = source.SaveBoardAsync(FilePath);
        source.Background = BoardBackground.White; source.GridSpacing = 24;
        source.PreviousPage(); source.Background = BoardBackground.Lined;
        await save;
        var loaded = Board(); await loaded.LoadBoardAsync(FilePath);
        AssertPaper(loaded, BoardBackground.Grid, 56);
        loaded.NextPage(); AssertPaper(loaded, BoardBackground.Black, 56);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InkOnlyImportInheritsTheTargetPaper(bool entireBoard)
    {
        await File.WriteAllTextAsync(FilePath, "{\"Strokes\":[]}");
        var board = Board(); board.Background = BoardBackground.Lined; board.GridSpacing = 28;
        var apply = await board.PrepareOpenAsync(FilePath, entireBoard);
        board.Background = BoardBackground.White;
        apply();
        AssertPaper(board, BoardBackground.Lined, 28);
    }

    [Fact]
    public void PaperEditsNotifyOnlyOnEffectiveChanges()
    {
        var board = Board();
        int changes = 0; board.BackgroundChanged += () => changes++;
        board.SetBackground(BoardBackground.Grid);
        board.GridSpacing = 56;
        board.SetBackground(BoardBackground.Grid);
        board.GridSpacing = 56;
        Assert.Equal(2, changes);
    }

    private async Task WriteManifest(string json, bool packaged)
    {
        if (!packaged) { await File.WriteAllTextAsync(FilePath, json); return; }
        using var archive = ZipFile.Open(FilePath, ZipArchiveMode.Create);
        await using var output = new StreamWriter(archive.CreateEntry("board.json").Open());
        await output.WriteAsync(json);
    }

    private static void AssertPaper(BoardService board, BoardBackground background, float spacing)
    {
        Assert.Equal(background, board.Background);
        Assert.Equal(spacing, board.GridSpacing);
    }

    public void Dispose()
    {
        foreach (var board in _boards)
            foreach (var page in board.Pages) page.Dispose();
        Directory.Delete(_directory, true);
    }
}
