using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class BoardManagementTests : IDisposable
{
    private readonly List<BoardService> _boards = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("inkcanvas-page-management-").FullName;
    private BoardService Board(int count = 4)
    {
        var board = new BoardService(); _boards.Add(board);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) board.NextPage();
            board.Background = (BoardBackground)(i % 4); board.GridSpacing = 24 + i % 40;
            board.AddImage(new() { Name = "image-" + i, Data = new byte[] { (byte)i }, Width = 100, Height = 100 });
            board.AddMedia(new() { Name = "video-" + i, Data = new byte[] { (byte)i, 1 } });
        }
        return board;
    }

    [Theory]
    [InlineData(0, 3, 0)] [InlineData(0, 3, 1)] [InlineData(3, 0, 3)]
    [InlineData(3, 0, 0)] [InlineData(1, 2, 1)] [InlineData(2, 1, 3)]
    public void MovePreservesPageIdentityPaperMediaAndHistory(int source, int target, int current)
    {
        var board = Board(); board.GoTo(current);
        var selected = board.Current;
        var expected = board.Pages.ToList();
        var original = Enumerable.Range(0, board.PageCount).Select(board.GetPageInfo).ToArray();
        var moving = expected[source]; expected.RemoveAt(source); expected.Insert(target, moving);
        int changed = 0; board.PageChanged += () => changed++;
        Assert.True(board.MovePage(source, target)); Assert.Equal(1, changed);
        Assert.Equal(expected, board.Pages); Assert.Same(selected, board.Current);
        Assert.Equal(expected.IndexOf(selected), board.CurrentIndex);
        for (int i = 0; i < board.PageCount; i++)
        {
            var page = board.GetPageInfo(i); var previous = original.Single(item => ReferenceEquals(item.Controller, page.Controller));
            Assert.Equal(previous.Background, page.Background); Assert.Equal(previous.GridSpacing, page.GridSpacing);
            Assert.Same(previous.Images, page.Images); Assert.Same(previous.Media, page.Media);
            Assert.Equal(2, page.Controller.History.UndoCount);
        }
        var own = board.CurrentMedia.Single(); board.Current.Undo();
        Assert.Empty(board.CurrentMedia);
        Assert.DoesNotContain(board.GetPageInfo(board.CurrentIndex).Media, item => item.Id == own.Id);
    }

    [Fact]
    public void DuplicateCopiesTheWholePageWithIndependentIdsAndSharedImmutableMedia()
    {
        var board = Board(1); var source = board.GetPageInfo(0);
        board.Background = BoardBackground.Grid; board.GridSpacing = 56;
        source.Controller.ViewTransform = Matrix3x2.CreateScale(1.3f) * Matrix3x2.CreateTranslation(20, 30);
        var shapes = ShapeGenerator.Generate(ShapeKind.Triangle, new(100, 100), new(300, 220), new() { TaperKind = TaperKind.None });
        source.Controller.Document.AddRange(shapes); source.Controller.SelectAll();
        source.Controller.SetShapeFill(InkColor.Opaque(40, 80, 220));
        Assert.True(board.DuplicateCurrentPage());
        Assert.Equal(2, board.PageCount); Assert.NotSame(source.Controller, board.Current);
        Assert.Equal(source.Controller.ViewTransform, board.Current.ViewTransform);
        Assert.Equal(BoardBackground.Grid, board.Background); Assert.Equal(56, board.GridSpacing);
        Assert.Equal(source.Controller.Document.Count, board.Current.Document.Count);
        Assert.All(board.Current.Document.Strokes, stroke => Assert.DoesNotContain(source.Controller.Document.Strokes, original => original.Id == stroke.Id));
        Assert.NotEqual(source.Controller.Document.Strokes[0].ShapeSource.GroupId, board.Current.Document.Strokes[0].ShapeSource.GroupId);
        Assert.Same(source.Images[0].Data, board.CurrentImages[0].Data); Assert.NotEqual(source.Images[0].Id, board.CurrentImages[0].Id);
        Assert.Same(source.Media[0].Data, board.CurrentMedia[0].Data); Assert.NotEqual(source.Media[0].Id, board.CurrentMedia[0].Id);
        board.CurrentImages[0].Left = 999; Assert.Equal(0, source.Images[0].Left);
        Assert.Equal(0, board.Current.History.UndoCount);
    }

    [Fact]
    public async Task ReorderedLongBoardSavesAndReopensInTheVisibleOrder()
    {
        var board = Board(150); board.GoTo(75); var current = board.Current;
        Assert.True(board.MovePage(149, 0)); Assert.Same(current, board.Current);
        Assert.True(board.MovePage(76, 20)); Assert.Same(current, board.Current); Assert.Equal(20, board.CurrentIndex);
        var expected = Enumerable.Range(0, board.PageCount).Select(board.GetPageInfo).ToArray();
        string path = Path.Combine(_directory, "sorted.icink");
        await board.SaveBoardAsync(path);
        var loaded = Board(1); await loaded.LoadBoardAsync(path);
        Assert.Equal(150, loaded.PageCount);
        for (int i = 0; i < loaded.PageCount; i++)
        {
            var actual = loaded.GetPageInfo(i);
            Assert.Equal(expected[i].Background, actual.Background); Assert.Equal(expected[i].GridSpacing, actual.GridSpacing);
            Assert.Equal(expected[i].Images[0].Name, actual.Images[0].Name); Assert.Equal(expected[i].Media[0].Name, actual.Media[0].Name);
        }
    }

    [Fact]
    public void DeletingAnotherPageKeepsCurrentPageAndItsSelection()
    {
        var board = Board(); board.GoTo(2); var current = board.Current;
        current.SelectAll(); var selected = current.SelectedElements.ToArray();
        Assert.True(board.RemovePage(0)); Assert.Same(current, board.Current); Assert.Equal(1, board.CurrentIndex);
        Assert.Equal(selected, current.SelectedElements); Assert.Equal(BoardBackground.Grid, board.Background);
        Assert.True(board.RemovePage(2)); Assert.Same(current, board.Current);
        Assert.Equal("image-2", board.CurrentImages.Single().Name);
    }

    [Fact]
    public void InvalidMoveIsANoOpAndDeletingTheLastPageKeepsOneBlankPage()
    {
        var board = Board(1); long version = board.PageStructureVersion; int changes = 0;
        board.PageChanged += () => changes++;
        Assert.False(board.MovePage(0, 0)); Assert.False(board.MovePage(-1, 0)); Assert.False(board.MovePage(0, 10));
        Assert.False(board.RemovePage(-1)); Assert.Equal(version, board.PageStructureVersion); Assert.Equal(0, changes);
        Assert.True(board.RemovePage(0)); Assert.Single(board.Pages); Assert.Empty(board.CurrentImages); Assert.Empty(board.CurrentMedia);
    }

    public void Dispose()
    {
        foreach (var board in _boards) foreach (var page in board.Pages) page.Dispose();
        Directory.Delete(_directory, true);
    }
}
