using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>主展板/板中板共享的图片命中：轴对齐、旋转外接框与顶层优先。</summary>
public sealed class BoardImageHitTestTests
{
    private static BoardImageAsset Image(string name, float left, float top, float width, float height)
        => new()
        {
            Name = name,
            Data = new byte[] { 1, 2, 3 },
            ContentType = "image/png",
            Left = left,
            Top = top,
            Width = width,
            Height = height,
        };

    [Fact]
    public void HitTest_UsesAxisAlignedBounds()
    {
        var image = Image("card", 10, 20, 100, 50);

        Assert.Same(image, BoardImageHitTest.HitTest(new[] { image }, 15, 25));
        Assert.Same(image, BoardImageHitTest.HitTest(new[] { image }, 110, 70));
        Assert.Null(BoardImageHitTest.HitTest(new[] { image }, 111, 25));
        Assert.Null(BoardImageHitTest.HitTest(new[] { image }, 15, 71));
    }

    [Fact]
    public void HitTest_UsesTransformedBounds()
    {
        var rotated = Image("rotated", 10, 10, 100, 20);
        rotated.Transform = new float[] { 0, 1, -1, 0, 0, 0 };

        // p' = (x,y) * M = (-y, x)，外接框为 x[-30,-10]、y[10,110]。
        Assert.Same(rotated, BoardImageHitTest.HitTest(new[] { rotated }, -20, 50));
        Assert.Null(BoardImageHitTest.HitTest(new[] { rotated }, 60, 15));
    }

    [Fact]
    public void HitTest_PrefersTopmostImage()
    {
        var bottom = Image("bottom", 0, 0, 100, 100);
        var top = Image("top", 20, 20, 60, 60);

        Assert.Same(top, BoardImageHitTest.HitTest(new[] { bottom, top }, 30, 30));
        Assert.Same(bottom, BoardImageHitTest.HitTest(new[] { bottom, top }, 5, 5));
    }

    [Fact]
    public void HitTest_IgnoresInvalidAndNullEntries()
    {
        var invalid = new BoardImageAsset { Name = "invalid", Data = Array.Empty<byte>(), Width = 0, Height = 0 };
        Assert.Null(BoardImageHitTest.HitTest(new[] { invalid }, 0, 0));
        Assert.Null(BoardImageHitTest.HitTest(null, 0, 0));
    }
}