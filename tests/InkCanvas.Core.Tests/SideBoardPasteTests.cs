using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class SideBoardPasteTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

    [Fact]
    public void ContentWithinSixtyPercentIsCenteredWithoutScaling()
    {
        var rect = BoardPasteLayout.FitCentered(400, 200, 1000, 800, Matrix3x2.Identity);

        Assert.Equal(new BoardPasteRect(300, 300, 400, 200, rect.Transform), rect);
        Assert.Equal(Matrix3x2.Identity, BoardElementMath.Read(rect.Transform));
    }

    [Fact]
    public void OversizedContentIsCappedAtSixtyPercentAndKeepsAspect()
    {
        var rect = BoardPasteLayout.FitCentered(1000, 500, 1000, 800, Matrix3x2.Identity);

        Assert.Equal(600f, rect.Width);
        Assert.Equal(300f, rect.Height);
        Assert.Equal(2f, rect.Width / rect.Height);
        Assert.Equal(200f, rect.Left);
        Assert.Equal(250f, rect.Top);
    }

    [Fact]
    public void LayoutUsesInverseOfTheMainBoardViewTransform()
    {
        var view = Matrix3x2.CreateScale(2f) * Matrix3x2.CreateTranslation(30, 40);
        var rect = BoardPasteLayout.FitCentered(1000, 500, 1000, 800, view);

        Assert.NotNull(rect.Transform);
        var actual = BoardElementMath.Read(rect.Transform);
        Assert.True(Matrix3x2.Invert(view, out var expected));
        Assert.Equal(expected.M11, actual.M11, 4);
        Assert.Equal(expected.M12, actual.M12, 4);
        Assert.Equal(expected.M21, actual.M21, 4);
        Assert.Equal(expected.M22, actual.M22, 4);
        Assert.Equal(expected.M31, actual.M31, 3);
        Assert.Equal(expected.M32, actual.M32, 3);
    }

    [Fact]
    public void CreateAssetCarriesPngDataAndPlacement()
    {
        var asset = BoardPasteLayout.CreateImageAsset("板中板.png", Png, 1000, 500, 1000, 800, Matrix3x2.Identity);

        Assert.NotNull(asset);
        Assert.Same(Png, asset!.Data);
        Assert.Equal("image/png", asset.ContentType);
        Assert.Equal(600f, asset.Width);
        Assert.Equal(300f, asset.Height);
        Assert.Equal(200f, asset.Left);
        Assert.Equal(250f, asset.Top);
    }

    [Fact]
    public void InvalidContentOrViewportProducesNoAsset()
    {
        Assert.Null(BoardPasteLayout.CreateImageAsset("x", Array.Empty<byte>(), 100, 100, 800, 600, Matrix3x2.Identity));
        Assert.Null(BoardPasteLayout.CreateImageAsset("x", Png, 0, 100, 800, 600, Matrix3x2.Identity));
        Assert.Null(BoardPasteLayout.CreateImageAsset("x", Png, 100, 100, 0, 600, Matrix3x2.Identity));
    }

    [Fact]
    public void EmptySidePageHasNoPasteableContent()
    {
        var side = new BoardService();
        Assert.False(side.CurrentPageHasPasteableContent);

        side.Current.Document.Add(new InkStroke(new StrokeAttributes { BaseWidth = 5 }));
        Assert.True(side.CurrentPageHasPasteableContent);

        side.Current.Clear();
        Assert.False(side.CurrentPageHasPasteableContent);

        side.AddImage(new BoardImageAsset { Data = Png, Width = 10, Height = 10 });
        Assert.True(side.CurrentPageHasPasteableContent);
    }

    [Fact]
    public void PastedImageIsOneUndoStepAndUndoRedoRestoresIt()
    {
        var main = new BoardService();
        var asset = BoardPasteLayout.CreateImageAsset("板中板.png", Png, 1000, 500, 1000, 800, Matrix3x2.Identity);
        Assert.NotNull(asset);
        Assert.Equal(0, main.Current.History.UndoCount);

        Assert.True(main.AddImage(asset!));
        Assert.Single(main.CurrentImages);
        Assert.Equal(1, main.Current.History.UndoCount);

        main.Current.Undo();
        Assert.Empty(main.CurrentImages);
        Assert.True(main.Current.History.CanRedo);

        main.Current.Redo();
        Assert.Single(main.CurrentImages);
        Assert.Same(asset, main.CurrentImages[0]);
    }
}