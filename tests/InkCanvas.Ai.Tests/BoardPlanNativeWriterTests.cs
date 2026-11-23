using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>
/// BoardPlanWriter 只生成原生文本/公式对象：四类块映射、词语多列、LaTeX 保留，
/// 写入展板后 Images 不增加（禁止 AI 文字转 PNG）。
/// </summary>
public sealed class BoardPlanNativeWriterTests
{
    private static AssistantBoardPlan Plan() => new()
    {
        Schema = 1,
        Intent = "write",
        TargetBoard = "main",
        TargetPageIndex = 0,
        Blocks = new[]
        {
            new AssistantBoardBlock
            {
                Id = "b1", Kind = "formula", Latex = "\\frac{1}{2}x^2-3x+2=0",
                Style = new AssistantBoardStyle { FontSize = 28, Align = "center", Columns = 1, LineHeight = 1.4 },
            },
            new AssistantBoardBlock
            {
                Id = "b2", Kind = "text", Title = "一元二次方程", Text = "配方法与判别式",
                Style = new AssistantBoardStyle { FontSize = 24, Align = "left", Columns = 1, LineHeight = 1.4 },
            },
            new AssistantBoardBlock
            {
                Id = "b3", Kind = "wordlist", Title = "关键词",
                Lines = new[] { "判别式", "配方法", "因式分解", "顶点式" },
                Style = new AssistantBoardStyle { FontSize = 24, Align = "left", Columns = 2, LineHeight = 1.5 },
            },
            new AssistantBoardBlock
            {
                Id = "b4", Kind = "poem", Title = "静夜思", Author = "唐·李白",
                Lines = new[] { "床前明月光", "疑是地上霜", "举头望明月", "低头思故乡" },
                Style = new AssistantBoardStyle { FontSize = 26, Align = "center", Columns = 1, LineHeight = 1.6 },
            },
        },
    };

    [Fact]
    public void Create_MapsFourBlocksToNativeObjectsWithoutPng()
    {
        BoardPlanNativeResult result = BoardPlanWriter.Create(Plan(), "整理当前页板书", 1_730_000_000_000);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Texts.Count);
        Assert.Single(result.Formulas);
        Assert.Equal(4, result.Count);

        Assert.Equal(new[] { "text", "wordList", "poem" }, result.Texts.Select(text => text.Type));
        Assert.All(result.Texts, text =>
        {
            Assert.Equal("ai", text.Source);
            Assert.Equal("整理当前页板书", text.Instruction);
            Assert.Equal(1_730_000_000_000, text.CreatedAt);
        });

        var wordList = result.Texts.Single(text => text.Type == "wordList");
        Assert.Equal(2, wordList.Columns);
        Assert.Equal("判别式\n配方法\n因式分解\n顶点式", wordList.Content);

        var poem = result.Texts.Single(text => text.Type == "poem");
        Assert.Equal("静夜思", poem.Title);
        Assert.Equal("唐·李白", poem.Author);
        Assert.Equal(4, poem.Content.Split('\n').Length);

        Assert.Equal("\\frac{1}{2}x^2-3x+2=0", result.Formulas[0].Latex);
        Assert.Equal("ai", result.Formulas[0].Source);
    }

    [Fact]
    public void Create_ThenAddAiObjects_DoesNotIncreaseImages()
    {
        var board = new BoardService();
        int imagesBefore = board.CurrentImages.Count;
        BoardPlanNativeResult result = BoardPlanWriter.Create(Plan(), "整理当前页板书");

        IReadOnlyList<Guid> ids = board.AddAiObjectsAtomic(
            result.Texts, result.Formulas, "AI 板书", "native-group");

        Assert.Equal(4, ids.Count);
        Assert.Equal(3, board.CurrentTextObjects.Count);
        Assert.Single(board.CurrentFormulaObjects);
        Assert.Equal(imagesBefore, board.CurrentImages.Count);
        Assert.Empty(board.CurrentImages);
        Assert.Equal(1, board.Current.History.UndoCount);
    }
}