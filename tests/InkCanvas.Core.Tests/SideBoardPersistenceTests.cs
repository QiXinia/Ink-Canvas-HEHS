using System.IO.Compression;
using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class SideBoardPersistenceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("inkcanvas-side-board-persistence-").FullName;
    private readonly List<BoardService> _boards = new();
    private string FilePath(string name = "board.icink") => Path.Combine(_directory, name);
    private BoardService Board()
    {
        var board = new BoardService(); _boards.Add(board); return board;
    }

    private static InkStroke Stroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 5 });
        stroke.AddPoint(x, 20); stroke.AddPoint(x + 10, 30); stroke.Complete();
        return stroke;
    }

    [Fact]
    public async Task V4RoundTripKeepsMainAndSideBoardContentSeparately()
    {
        var main = Board(); var side = Board(); main.SideBoardSource = side;
        main.Background = BoardBackground.Grid; main.GridSpacing = 56;
        main.Current.Document.Add(Stroke(1));
        Assert.True(main.InsertPageAfterCurrent());
        main.Background = BoardBackground.White;
        main.Current.Document.Add(Stroke(2));

        Assert.True(side.InsertPageAfterCurrent());
        side.Background = BoardBackground.Grid;
        side.Current.Document.Add(Stroke(9));
        side.GoTo(0);
        side.Background = BoardBackground.Lined; side.GridSpacing = 24;
        side.CurrentState.Name = "草稿";
        side.Current.ViewTransform = Matrix3x2.CreateScale(1.5f);
        side.Current.Document.Add(Stroke(7));
        side.AddImage(new BoardImageAsset { Data = new byte[] { 1, 2, 3 }, Width = 100, Height = 80 });

        string path = FilePath();
        await main.SaveBoardAsync(path);

        using (var archive = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(archive.GetEntry("board.json")!.Open());
            string manifest = await reader.ReadToEndAsync();
            Assert.Contains("\"Version\":4", manifest);
            Assert.Contains("\"SideBoard\"", manifest);
            Assert.Contains("\"Scope\":\"side\"", manifest);
            Assert.DoesNotContain("\"Data\"", manifest);
            Assert.DoesNotContain("\"PosterData\"", manifest);
        }

        var loadedMain = Board(); var loadedSide = Board();
        loadedMain.SideBoardSource = loadedSide;
        await loadedMain.LoadBoardAsync(path);

        Assert.Equal(2, loadedMain.PageCount);
        Assert.Equal(BoardBackground.Grid, loadedMain.PageBackground(0));
        Assert.Equal(56, loadedMain.PageGridSpacing(0));
        Assert.Equal(BoardBackground.White, loadedMain.PageBackground(1));
        Assert.Equal(1f, Assert.Single(loadedMain.Pages[0].Document.Strokes).Points[0].X);

        Assert.Equal(2, loadedSide.PageCount);
        Assert.Equal(0, loadedSide.CurrentIndex);
        Assert.Equal("草稿", loadedSide.CurrentState.Name);
        Assert.Equal(BoardBackground.Lined, loadedSide.Background);
        Assert.Equal(24, loadedSide.GridSpacing);
        Assert.Equal(Matrix3x2.CreateScale(1.5f), loadedSide.Current.ViewTransform);
        Assert.Equal(7f, Assert.Single(loadedSide.Current.Document.Strokes).Points[0].X);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(loadedSide.CurrentImages).Data);

        loadedSide.GoTo(1);
        Assert.Equal(BoardBackground.Grid, loadedSide.Background);
        Assert.Equal(9f, Assert.Single(loadedSide.Current.Document.Strokes).Points[0].X);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacyPackagedFileWithoutSideBoardNodeClearsSideBoardAndKeepsMain(int version)
    {
        string path = FilePath();
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("board.json").Open());
            await writer.WriteAsync(
                $"{{\"Format\":\"inkcanvas-board\",\"Version\":{version},\"Pages\":[{{\"Background\":\"Grid\",\"GridSpacing\":56,\"Strokes\":[]}}]}}");
        }

        var main = Board(); var side = Board();
        side.Current.Document.Add(Stroke(5));
        side.AddImage(new BoardImageAsset { Data = new byte[] { 4, 5, 6 }, Width = 60, Height = 40 });
        Assert.True(side.HasContent);
        main.SideBoardSource = side;

        await main.LoadBoardAsync(path);

        Assert.Equal(1, main.PageCount);
        Assert.Equal(BoardBackground.Grid, main.Background);
        Assert.Equal(56, main.GridSpacing);
        Assert.Equal(1, side.PageCount);
        Assert.False(side.HasContent);
        Assert.Empty(side.Current.Document.Strokes);
        Assert.Empty(side.CurrentImages);
        Assert.Empty(side.CurrentMedia);
    }

    [Fact]
    public async Task LegacyStrokeOnlyFileOpensAndLeavesSideBoardEmpty()
    {
        string path = FilePath();
        await File.WriteAllTextAsync(path, "{\"Strokes\":[{\"Width\":5,\"Points\":[[12,34,0.5,5,0]]}]}");

        var main = Board(); var side = Board();
        side.Current.Document.Add(Stroke(8));
        side.AddImage(new BoardImageAsset { Data = new byte[] { 7 }, Width = 30, Height = 20 });
        Assert.True(side.HasContent);
        main.SideBoardSource = side;

        await main.LoadBoardAsync(path);

        Assert.Equal(1, main.PageCount);
        Assert.Equal(5f, Assert.Single(main.Current.Document.Strokes).Points.Single().Width);
        Assert.False(side.HasContent);
        Assert.Equal(1, side.PageCount);
        Assert.Empty(side.CurrentImages);
    }

    [Fact]
    public async Task SideBoardMediaSharesOneBlobAndLoadsOnDemand()
    {
        var main = Board(); var side = Board(); main.SideBoardSource = side;
        var imageData = new byte[] { 1, 2, 3, 4 };
        main.AddImage(new BoardImageAsset { Data = imageData, Width = 200, Height = 100 });
        side.AddImage(new BoardImageAsset { Data = imageData, Width = 200, Height = 100 });
        var mediaData = new byte[] { 9, 8, 7 };
        var posterData = new byte[] { 6, 5 };
        // 主展板视频放在第二页，单页打开第一页时才真正验证“按需加载、不读其它页媒体”。
        Assert.True(main.InsertPageAfterCurrent());
        main.AddMedia(new BoardMediaAsset
        {
            Data = mediaData, PosterData = posterData, NaturalWidth = 640, NaturalHeight = 360,
        });
        side.AddMedia(new BoardMediaAsset
        {
            Data = mediaData, PosterData = posterData, NaturalWidth = 640, NaturalHeight = 360,
        });

        string path = FilePath();
        await main.SaveBoardAsync(path);

        using (var archive = ZipFile.OpenRead(path))
        {
            Assert.Equal(3, archive.Entries.Count(entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal)));
            using var reader = new StreamReader(archive.GetEntry("board.json")!.Open());
            string manifest = await reader.ReadToEndAsync();
            Assert.DoesNotContain("\"Data\"", manifest);
            Assert.DoesNotContain("\"PosterData\"", manifest);
        }

        var loadedMain = Board(); var loadedSide = Board();
        loadedMain.SideBoardSource = loadedSide;
        await loadedMain.LoadBoardAsync(path);

        Assert.Equal(imageData, Assert.Single(loadedMain.CurrentImages).Data);
        Assert.Equal(imageData, Assert.Single(loadedSide.CurrentImages).Data);
        Assert.Same(loadedMain.CurrentImages[0].Data, loadedSide.CurrentImages[0].Data);

        var target = Board();
        await target.LoadPageAsync(path);
        Assert.Equal(1, target.PageCount);
        Assert.Empty(target.CurrentMedia);
        Assert.True(side.HasContent);
        Assert.Equal(imageData, Assert.Single(side.CurrentImages).Data);
    }

    [Fact]
    public async Task ClearedSideBoardSavesAsEmptyInsteadOfStaleContent()
    {
        var main = Board(); var side = Board(); main.SideBoardSource = side;
        side.Current.Document.Add(Stroke(3));
        side.AddImage(new BoardImageAsset { Data = new byte[] { 1 }, Width = 10, Height = 10 });
        side.AddMedia(new BoardMediaAsset { Data = new byte[] { 2 }, PosterData = new byte[] { 3 } });
        Assert.True(side.InsertPageAfterCurrent());
        side.Current.Document.Add(Stroke(4));
        side.AddImage(new BoardImageAsset { Data = new byte[] { 4 }, Width = 11, Height = 11 });
        side.AddMedia(new BoardMediaAsset { Data = new byte[] { 5 }, PosterData = new byte[] { 6 } });

        string path = FilePath();
        await main.SaveBoardAsync(path);

        side.GoTo(1);
        side.RemoveCurrentPage();
        side.ClearCurrentPage();
        Assert.Equal(1, side.PageCount);
        Assert.False(side.HasContent);

        await main.SaveBoardAsync(path);
        var loadedMain = Board(); var loadedSide = Board();
        loadedMain.SideBoardSource = loadedSide;
        await loadedMain.LoadBoardAsync(path);

        Assert.Equal(1, loadedSide.PageCount);
        Assert.False(loadedSide.HasContent);
        Assert.Empty(loadedSide.CurrentImages);
        Assert.Empty(loadedSide.CurrentMedia);
        Assert.Empty(loadedSide.Current.Document.Strokes);
    }

    [Fact]
    public async Task SinglePageSaveDoesNotEmbedSideBoard()
    {
        var main = Board(); var side = Board(); main.SideBoardSource = side;
        side.Current.Document.Add(Stroke(5));

        string path = FilePath();
        await main.SavePageAsync(path);

        byte[] head = new byte[2];
        using (var stream = File.OpenRead(path)) await stream.ReadExactlyAsync(head);
        Assert.False(head[0] == (byte)'P' && head[1] == (byte)'K');
        string text = await File.ReadAllTextAsync(path);
        Assert.StartsWith("{", text.TrimStart());
        Assert.DoesNotContain("SideBoard", text);

        var loadedMain = Board(); var loadedSide = Board();
        loadedSide.Current.Document.Add(Stroke(99));
        loadedMain.SideBoardSource = loadedSide;
        await loadedMain.LoadPageAsync(path);

        Assert.Equal(1, loadedMain.PageCount);
        Assert.Equal(99f, Assert.Single(loadedSide.Current.Document.Strokes).Points[0].X);
    }

    [Fact]
    public async Task SideTargetChangedAfterPrepareIsNotOverwritten()
    {
        var main = Board(); var side = Board(); main.SideBoardSource = side;
        side.Current.Document.Add(Stroke(7));
        string path = FilePath();
        await main.SaveBoardAsync(path);

        var other = Board();
        other.Current.Document.Add(Stroke(99));

        var apply = await main.PrepareOpenAsync(path, true);
        main.SideBoardSource = other;
        apply();

        Assert.Equal(99f, Assert.Single(other.Current.Document.Strokes).Points[0].X);
        Assert.Equal(7f, Assert.Single(side.Current.Document.Strokes).Points[0].X);
    }

    public void Dispose()
    {
        foreach (var board in _boards) foreach (var page in board.Pages) page.Dispose();
        Directory.Delete(_directory, true);
    }
}