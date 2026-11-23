using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>阶段 5：连续修改指令的本地应用（字号/列/过滤/移动/行距）与边界。</summary>
public sealed class BoardPlanEditPolicyTests
{
    private static AssistantBoardBlock Block(string id, string kind) => new()
    {
        Id = id,
        Kind = kind,
        Text = kind == "text" ? "一元二次方程" : "",
        Latex = kind == "formula" ? "\\frac{1}{2}x^2" : "",
        Lines = kind == "poem" ? new[] { "床前明月光", "疑是地上霜" }
            : kind == "wordlist" ? new[] { "判别式｜pàn bié shì｜判断根的情况" }
            : Array.Empty<string>(),
        Title = kind == "poem" ? "静夜思" : "",
        Author = kind == "poem" ? "唐·李白" : "",
        Style = new AssistantBoardStyle { FontSize = 24, Columns = 1, LineHeight = 1.4, Align = "left" },
        Confidence = 0.9,
    };

    private static AssistantBoardPlan Plan() => new()
    {
        Schema = 1,
        Intent = "organize",
        TargetBoard = "main",
        TargetPageIndex = 2,
        Blocks = new[] { Block("b1", "formula"), Block("b2", "text"), Block("b3", "wordlist"), Block("b4", "poem") },
        Notes = new[] { "备注" },
        Originals = new[] { "原文一", "原文二" },
        Confidence = 0.78,
        Warnings = new[] { "需复核" },
    };

    private static AssistantBoardPlan Apply(AssistantBoardPlan plan, string instruction, out string message)
    {
        Assert.True(BoardPlanEditPolicy.TryApply(plan, instruction, out AssistantBoardPlan updated, out message), message);
        Assert.NotNull(updated);
        return updated;
    }

    [Fact]
    public void FontGrow_ScalesAllBlocksWithinRange()
    {
        AssistantBoardPlan updated = Apply(Plan(), "字号大一点", out _);

        Assert.All(updated.Blocks, block => Assert.Equal(28.8, block.Style.FontSize, 3));
        Assert.All(updated.Blocks, block => Assert.Equal(1, block.Style.Columns));
        Assert.Equal("\\frac{1}{2}x^2", updated.Blocks[0].Latex);
    }

    [Fact]
    public void FontShrink_ScalesDown()
    {
        AssistantBoardPlan updated = Apply(Plan(), "字体小一点", out _);
        Assert.All(updated.Blocks, block => Assert.Equal(20.4, block.Style.FontSize, 3));
    }

    [Fact]
    public void Font_ClampsToDocumentedRange()
    {
        AssistantBoardPlan big = Plan();
        foreach (AssistantBoardBlock block in big.Blocks) block.Style.FontSize = 90;
        Assert.All(Apply(big, "字号放大", out _).Blocks, block => Assert.Equal(96, block.Style.FontSize, 3));

        AssistantBoardPlan small = Plan();
        foreach (AssistantBoardBlock block in small.Blocks) block.Style.FontSize = 13;
        Assert.All(Apply(small, "字号缩小", out _).Blocks, block => Assert.Equal(12, block.Style.FontSize, 3));
    }

    [Theory]
    [InlineData("字号 32", 32)]
    [InlineData("字体32", 32)]
    [InlineData("font size 40", 40)]
    [InlineData("字号 200", 96)]
    [InlineData("字号 8", 12)]
    public void ExplicitFontSize_IsAppliedAndClamped(string instruction, double expected)
    {
        AssistantBoardPlan updated = Apply(Plan(), instruction, out _);
        Assert.All(updated.Blocks, block => Assert.Equal(expected, block.Style.FontSize, 3));
    }

    [Fact]
    public void Columns_TwoAndOneAreAppliedAndRectsReassigned()
    {
        AssistantBoardPlan two = Apply(Plan(), "改成两列", out _);
        Assert.All(two.Blocks, block => Assert.Equal(2, block.Style.Columns));
        Assert.All(two.Blocks, block => Assert.False(block.Rect.IsEmpty));
        Assert.All(two.Blocks, block =>
        {
            Assert.InRange(block.Rect.Left, 0, 1);
            Assert.InRange(block.Rect.Right, 0, 1);
            Assert.True(block.Rect.Left < block.Rect.Right);
            Assert.True(block.Rect.Top < block.Rect.Bottom);
        });
        Assert.True(two.Blocks[0].Rect.Right <= two.Blocks[1].Rect.Left);
        Assert.True(two.Blocks[0].Rect.Bottom <= two.Blocks[2].Rect.Top);

        AssistantBoardPlan one = Apply(two, "改成一列", out _);
        Assert.All(one.Blocks, block => Assert.Equal(1, block.Style.Columns));
    }

    [Fact]
    public void Columns_ThreeIsClampedToThree()
    {
        AssistantBoardPlan updated = Apply(Plan(), "改成三列", out _);
        Assert.All(updated.Blocks, block => Assert.Equal(3, block.Style.Columns));
    }

    [Fact]
    public void FilterKeepFormula_RemovesOtherKindsWithoutChangingContent()
    {
        AssistantBoardPlan updated = Apply(Plan(), "只保留公式", out _);

        AssistantBoardBlock only = Assert.Single(updated.Blocks);
        Assert.Equal("formula", only.Kind);
        Assert.Equal("\\frac{1}{2}x^2", only.Latex);
        Assert.Equal("b1", only.Id);
    }

    [Fact]
    public void FilterKeepPoemAndRemoveFormula()
    {
        AssistantBoardPlan poem = Apply(Plan(), "只保留古诗", out _);
        Assert.Equal("b4", Assert.Single(poem.Blocks).Id);
        Assert.Equal(new[] { "床前明月光", "疑是地上霜" }, poem.Blocks[0].Lines);

        AssistantBoardPlan withoutFormula = Apply(Plan(), "去掉公式", out _);
        Assert.Equal(3, withoutFormula.Blocks.Count);
        Assert.DoesNotContain(withoutFormula.Blocks, block => block.Kind == "formula");
        Assert.Equal(new[] { "b2", "b3", "b4" }, withoutFormula.Blocks.Select(block => block.Id));
    }

    [Fact]
    public void FilterToEmpty_ReturnsFalse()
    {
        AssistantBoardPlan plan = Plan();
        plan.Blocks = plan.Blocks.Where(block => block.Kind != "wordlist").ToArray();

        Assert.False(BoardPlanEditPolicy.TryApply(plan, "只保留词语", out AssistantBoardPlan updated, out string message));
        Assert.Null(updated);
        Assert.Contains("过滤", message);
    }

    [Fact]
    public void MoveTarget_ChangesOnlyTargetBoard()
    {
        AssistantBoardPlan plan = Plan();
        AssistantBoardPlan side = Apply(plan, "移到板中板", out _);

        Assert.Equal("side", side.TargetBoard);
        Assert.Equal(plan.TargetPageIndex, side.TargetPageIndex);
        Assert.Equal(plan.Blocks.Count, side.Blocks.Count);
        Assert.Equal(plan.Blocks[0].Style.FontSize, side.Blocks[0].Style.FontSize, 3);
        Assert.Equal(plan.Blocks[0].Latex, side.Blocks[0].Latex);
        Assert.Equal(plan.Originals, side.Originals);
        Assert.Equal(plan.Warnings, side.Warnings);

        Assert.Equal("main", Apply(side, "移到主展板", out _).TargetBoard);
    }

    [Fact]
    public void LineHeight_GrowsAndShrinksWithClamp()
    {
        AssistantBoardPlan grow = Apply(Plan(), "行距大一点", out _);
        Assert.All(grow.Blocks, block => Assert.Equal(1.61, block.Style.LineHeight, 3));
        Assert.All(grow.Blocks, block => Assert.Equal(24, block.Style.FontSize, 3));

        AssistantBoardPlan shrink = Apply(Plan(), "行距小一点", out _);
        Assert.All(shrink.Blocks, block => Assert.Equal(1.26, block.Style.LineHeight, 3));

        AssistantBoardPlan atMax = Plan();
        foreach (AssistantBoardBlock block in atMax.Blocks) block.Style.LineHeight = 2.2;
        Assert.All(Apply(atMax, "行距再大一点", out _).Blocks,
            block => Assert.Equal(2.2, block.Style.LineHeight, 3));

        AssistantBoardPlan atMin = Plan();
        foreach (AssistantBoardBlock block in atMin.Blocks) block.Style.LineHeight = 1.05;
        Assert.All(Apply(atMin, "行距小一点", out _).Blocks,
            block => Assert.Equal(1.0, block.Style.LineHeight, 3));
    }

    [Fact]
    public void CombinedInstructions_AreAppliedTogether()
    {
        AssistantBoardPlan updated = Apply(Plan(), "字号大一点，改成两列，只保留公式", out string message);

        AssistantBoardBlock only = Assert.Single(updated.Blocks);
        Assert.Equal(28.8, only.Style.FontSize, 3);
        Assert.Equal(2, only.Style.Columns);
        Assert.Contains("未重新生成", message);
    }

    [Fact]
    public void OriginalPlanIsNotMutated()
    {
        AssistantBoardPlan plan = Plan();
        AssistantBoardBlock original = plan.Blocks[0];

        AssistantBoardPlan updated = Apply(plan, "字号大一点，改成两列", out _);

        Assert.Equal(24, original.Style.FontSize, 3);
        Assert.Equal(1, original.Style.Columns);
        Assert.NotSame(original, updated.Blocks[0]);
        Assert.NotSame(original.Style, updated.Blocks[0].Style);
        Assert.Equal(original.Latex, updated.Blocks[0].Latex);
    }

    [Fact]
    public void UnknownInstruction_ReturnsFalse()
    {
        Assert.False(BoardPlanEditPolicy.TryApply(
            Plan(), "把颜色改成红色", out AssistantBoardPlan updated, out string message));
        Assert.Null(updated);
        Assert.Contains("未识别", message);
    }

    [Fact]
    public void EmptyPlanOrInstruction_ReturnsFalse()
    {
        var empty = new AssistantBoardPlan { Blocks = Array.Empty<AssistantBoardBlock>() };
        Assert.False(BoardPlanEditPolicy.TryApply(empty, "字号大一点", out _, out string emptyMessage));
        Assert.Contains("没有可调整", emptyMessage);

        Assert.False(BoardPlanEditPolicy.TryApply(Plan(), "   ", out _, out string blankMessage));
        Assert.Contains("为空", blankMessage);

        Assert.False(BoardPlanEditPolicy.TryApply(null, "字号大一点", out _, out _));
    }

    [Fact]
    public void InstructionsAreCaseInsensitiveForEnglishKeywords()
    {
        AssistantBoardPlan updated = Apply(Plan(), "FONT SIZE 30", out _);
        Assert.All(updated.Blocks, block => Assert.Equal(30, block.Style.FontSize, 3));
    }

    [Fact]
    public void PlanLevelFields_ArePreserved()
    {
        AssistantBoardPlan plan = Plan();
        AssistantBoardPlan updated = Apply(plan, "移到板中板", out _);

        Assert.Equal(plan.Schema, updated.Schema);
        Assert.Equal(plan.Intent, updated.Intent);
        Assert.Equal(plan.TargetPageIndex, updated.TargetPageIndex);
        Assert.Equal(plan.Confidence, updated.Confidence, 3);
        Assert.Equal(plan.Notes, updated.Notes);
        Assert.Equal(plan.Originals, updated.Originals);
        Assert.Equal(plan.Warnings, updated.Warnings);
    }

    [Fact]
    public void ObjectEditParser_ReadsStyleFilterAndAlign()
    {
        Assert.True(BoardPlanEditPolicy.TryParseObjectEdit(
            "字号大一点，改成两列，居中，只保留公式", out BoardPlanEditPolicy.BoardObjectEdit edit,
            out string message), message);

        Assert.Equal(1.2, edit.FontScale, 3);
        Assert.Equal(2, edit.Columns);
        Assert.Equal("center", edit.Align);
        Assert.Contains("formula", edit.KeepKinds);
        Assert.Empty(edit.RemoveKinds);
        Assert.False(edit.IsEmpty);
    }

    [Fact]
    public void ObjectEditParser_RejectsUnknownInstruction()
    {
        Assert.False(BoardPlanEditPolicy.TryParseObjectEdit(
            "把颜色改成红色", out BoardPlanEditPolicy.BoardObjectEdit edit, out string message));
        Assert.Null(edit);
        Assert.Contains("未识别", message);
    }
}