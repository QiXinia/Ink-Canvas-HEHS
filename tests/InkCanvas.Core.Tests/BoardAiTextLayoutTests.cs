using System.Numerics;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>
/// Win2D-backed text layout checks plus a vector drawing smoke test proving native board text and
/// formulas are painted straight into a drawing session (no CanvasRenderTarget image asset per object).
/// </summary>
public sealed class BoardAiTextLayoutTests
{
    private static CanvasDevice CreateDevice()
    {
        // A dedicated software device keeps these layout checks isolated from the shared GPU
        // device used by concurrent handout/PPT rendering tests.
        return new CanvasDevice(forceSoftwareRenderer: true);
    }

    private static AiTextRenderSpec Spec(string content, string align, string type = "text") => new()
    {
        Type = type,
        Content = content,
        FontFamily = "Microsoft YaHei UI",
        FontSize = 32,
        LineHeight = 1.4f,
        Align = align,
        Color = "#FF202024",
    };

    [Fact]
    public void LongTextWrapsInsideTheRequestedWidth()
    {
        using var device = CreateDevice();
        const string content = "一元二次方程的判别式决定了方程实数根的个数，配方法与因式分解是常用解法。";
        using AiTextLayoutPlan narrow = AiTextLayoutBuilder.Build(device, Spec(content, "left"), 180);
        using AiTextLayoutPlan wide = AiTextLayoutBuilder.Build(device, Spec(content, "left"), 900);

        Assert.NotNull(narrow.Body);
        Assert.NotNull(wide.Body);
        Assert.True(narrow.BodyLineCount > wide.BodyLineCount,
            $"narrow={narrow.BodyLineCount}, wide={wide.BodyLineCount}");
        Assert.True(narrow.TotalHeight > wide.TotalHeight);
        Assert.True((float)narrow.Body.LayoutBounds.Width <= 180.5f);
    }

    [Theory]
    [InlineData("left", CanvasHorizontalAlignment.Left)]
    [InlineData("center", CanvasHorizontalAlignment.Center)]
    [InlineData("right", CanvasHorizontalAlignment.Right)]
    [InlineData("justify", CanvasHorizontalAlignment.Justified)]
    public void AlignmentIsMappedToWin2D(string align, CanvasHorizontalAlignment expected)
    {
        using var device = CreateDevice();
        using AiTextLayoutPlan plan = AiTextLayoutBuilder.Build(device, Spec("对齐检查", align), 300);

        Assert.Equal(expected, plan.Alignment);
        Assert.NotNull(plan.Body);
    }

    [Fact]
    public void PoemTitleAndAuthorHeaderAreMeasured()
    {
        using var device = CreateDevice();
        AiTextRenderSpec spec = new()
        {
            Type = "poem",
            Title = "静夜思",
            Author = "李白",
            Dynasty = "唐",
            Content = "床前明月光\n疑是地上霜\n举头望明月\n低头思故乡",
            FontFamily = "Microsoft YaHei UI",
            FontSize = 36,
            LineHeight = 1.8f,
            Align = "center",
        };

        using AiTextLayoutPlan plan = AiTextLayoutBuilder.Build(device, spec, 360);

        Assert.NotNull(plan.Title);
        Assert.NotNull(plan.Header);
        Assert.NotNull(plan.Body);
        Assert.True((float)plan.Title.LayoutBounds.Height > 0);
        Assert.True(plan.BodyLineCount >= 4);
        Assert.True(plan.TotalHeight > (float)plan.Title.LayoutBounds.Height);
    }

    [Fact]
    public void NativeObjectsAreDrawnAsVectorsIntoASession()
    {
        using var device = CreateDevice();
        var board = new BoardService();
        var text = new BoardTextObject
        {
            Content = "原生文本对象",
            Left = 20,
            Top = 20,
            Width = 260,
            Height = 100,
            FontSize = 36,
            Color = "#FF000000",
        };
        var formula = new BoardFormulaObject
        {
            Latex = "\\frac{a}{b}+\\sqrt{x}",
            Left = 20,
            Top = 140,
            Width = 260,
            Height = 100,
            FontSize = 32,
            Color = "#FF000000",
        };
        board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书");

        using var renderer = new InkSceneRenderer();
        using var target = new CanvasRenderTarget(device, 320, 280, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Windows.UI.Color.FromArgb(0, 255, 255, 255));
            renderer.DrawCommitted(session, board.Current, Matrix3x2.Identity);
        }

        Windows.UI.Color[] pixels = target.GetPixelColors();
        Assert.Contains(pixels, pixel => pixel.A > 0);
    }

    [Fact]
    public void WordListColumnsSplitTopToBottom()
    {
        IReadOnlyList<string> columns = AiTextLayoutBuilder.SplitColumns(
            "判别式\n配方法\n因式分解\n顶点式", 2);

        Assert.Equal(new[] { "判别式\n配方法", "因式分解\n顶点式" }, columns);
    }

    [Fact]
    public void WordListColumnsBuildSeparateBodyLayouts()
    {
        using var device = CreateDevice();
        var spec = new AiTextRenderSpec
        {
            Type = "wordList",
            Content = "判别式\n配方法\n因式分解\n顶点式",
            FontFamily = "Microsoft YaHei UI",
            FontSize = 32,
            LineHeight = 1.4f,
            Align = "left",
            Columns = 2,
        };

        using AiTextLayoutPlan plan = AiTextLayoutBuilder.Build(device, spec, 320);

        Assert.Equal(2, plan.Columns);
        Assert.Equal(2, plan.Bodies.Count);
        Assert.True(plan.ColumnWidth > 0);
        Assert.All(plan.Bodies, body =>
            Assert.True((float)body.LayoutBounds.Width <= plan.ColumnWidth + 0.5f));
        Assert.True(plan.TotalHeight > 0);
    }

    [Fact]
    public void CommittedInkLayerDrawsNativeObjectsOnTheRealBoardCanvasPath()
    {
        using var device = CreateDevice();
        var board = new BoardService();
        var text = new BoardTextObject
        {
            Content = "真实画布路径文本",
            Left = 20,
            Top = 20,
            Width = 280,
            Height = 100,
            FontSize = 36,
            Color = "#FF202024",
        };
        var formula = new BoardFormulaObject
        {
            Latex = "\\frac{a}{b}+\\sqrt{x}",
            Left = 20,
            Top = 150,
            Width = 280,
            Height = 100,
            FontSize = 32,
            Color = "#FF202024",
        };
        board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书");

        using var scene = new InkSceneRenderer();
        using var layer = new CommittedInkLayer();
        using var target = new CanvasRenderTarget(device, 360, 300, 96);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Windows.UI.Color.FromArgb(0, 255, 255, 255));
            layer.Draw(session, scene, board.Current, 360, 300, 96,
                objectsOverride: board.Current.DrawingObjects);
        }

        Windows.UI.Color[] pixels = target.GetPixelColors();
        Assert.Contains(pixels, pixel => pixel.A > 0);
    }
}