using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>阶段 3：AI 板书“替换原板书”的原子撤销（删笔画 + 插卡片）与默认保留原迹。</summary>
public sealed class BoardStrokeReplacementTests
{
    private static InkStroke Stroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None, BaseWidth = 3 });
        stroke.AddPoint(x, 10);
        stroke.AddPoint(x + 20, 40);
        stroke.Complete();
        return stroke;
    }

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
    public void ReplaceStrokesWithImagesAtomic_DeletesAndInsertsAsOneUndoStep()
    {
        var board = new BoardService();
        InkStroke first = Stroke(10);
        InkStroke second = Stroke(60);
        board.Current.Document.Add(first);
        board.Current.Document.Add(second);

        bool replaced = board.ReplaceStrokesWithImagesAtomic(
            new[] { first }, new[] { Image("card", 7, 200) }, "AI 板书替换");

        Assert.True(replaced);
        Assert.Same(second, Assert.Single(board.Current.Document.Strokes));
        Assert.Single(board.CurrentImages);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Equal(2, board.Current.Document.Strokes.Count);
        Assert.Same(first, board.Current.Document.Strokes[0]);
        Assert.Same(second, board.Current.Document.Strokes[1]);
        Assert.Empty(board.CurrentImages);
        Assert.Equal(1, board.Current.History.RedoCount);

        board.Current.Redo();
        Assert.Same(second, Assert.Single(board.Current.Document.Strokes));
        Assert.Equal("card", Assert.Single(board.CurrentImages).Name);
        Assert.Equal(1, board.Current.History.UndoCount);
    }

    [Fact]
    public void ReplaceStrokesWithImagesAtomic_RestoresOriginalOrderAfterUndo()
    {
        var board = new BoardService();
        InkStroke a = Stroke(0);
        InkStroke b = Stroke(30);
        InkStroke c = Stroke(60);
        board.Current.Document.Add(a);
        board.Current.Document.Add(b);
        board.Current.Document.Add(c);

        Assert.True(board.ReplaceStrokesWithImagesAtomic(
            new[] { a, c }, new[] { Image("card", 1, 0) }, "AI 板书替换"));
        Assert.Same(b, Assert.Single(board.Current.Document.Strokes));

        board.Current.Undo();
        Assert.Equal(3, board.Current.Document.Strokes.Count);
        Assert.Same(a, board.Current.Document.Strokes[0]);
        Assert.Same(b, board.Current.Document.Strokes[1]);
        Assert.Same(c, board.Current.Document.Strokes[2]);
    }

    [Fact]
    public void ReplaceStrokesWithImagesAtomic_NullSelectionRemovesAllPageStrokes()
    {
        var board = new BoardService();
        board.Current.Document.Add(Stroke(10));
        board.Current.Document.Add(Stroke(60));

        Assert.True(board.ReplaceStrokesWithImagesAtomic(
            null, new[] { Image("card", 2, 0) }, "AI 板书替换"));

        Assert.Empty(board.Current.Document.Strokes);
        Assert.Single(board.CurrentImages);

        board.Current.Undo();
        Assert.Equal(2, board.Current.Document.Strokes.Count);
        Assert.Empty(board.CurrentImages);
    }

    [Fact]
    public void ReplaceStrokesWithImagesAtomic_EmptyImages_IsNoOpAndKeepsStrokes()
    {
        var board = new BoardService();
        InkStroke stroke = Stroke(10);
        board.Current.Document.Add(stroke);

        Assert.False(board.ReplaceStrokesWithImagesAtomic(
            new[] { stroke }, Array.Empty<BoardImageAsset>(), "AI 板书替换"));
        Assert.Single(board.Current.Document.Strokes);
        Assert.Empty(board.CurrentImages);
        Assert.Equal(0, board.Current.History.UndoCount);
    }

    [Fact]
    public void ReplaceStrokesWithImagesAtomic_EmptyPage_IsNoOp()
    {
        var board = new BoardService();

        Assert.False(board.ReplaceStrokesWithImagesAtomic(
            null, new[] { Image("card", 3, 0) }, "AI 板书替换"));
        Assert.Empty(board.CurrentImages);
        Assert.Equal(0, board.Current.History.UndoCount);
    }

    [Fact]
    public void AddImagesAtomic_DefaultKeepsOriginalStrokes()
    {
        var board = new BoardService();
        InkStroke stroke = Stroke(10);
        board.Current.Document.Add(stroke);

        IReadOnlyList<int> indexes = board.AddImagesAtomic(
            new[] { Image("card", 4, 0) }, "AI 板书");

        Assert.Single(indexes);
        Assert.Same(stroke, Assert.Single(board.Current.Document.Strokes));
        Assert.Single(board.CurrentImages);

        board.Current.Undo();
        Assert.Same(stroke, Assert.Single(board.Current.Document.Strokes));
        Assert.Empty(board.CurrentImages);
    }
}