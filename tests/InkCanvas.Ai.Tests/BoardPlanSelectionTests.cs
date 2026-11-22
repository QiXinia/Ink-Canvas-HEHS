using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>阶段 3：originals/confidence/warnings 解析与“部分采用”块过滤。</summary>
public sealed class BoardPlanSelectionTests
{
    private const string PlanJson =
        """
        {
          "type": "board_plan",
          "schema": 2,
          "intent": "organize",
          "originals": ["x^2 - 5x + 6 = 0", "x1 = 2", "x2 = 3"],
          "confidence": 0.78,
          "warnings": ["第 1 块置信度偏低，请复核", "原文疑似笔误：x1 应为 x_1"],
          "blocks": [
            {
              "id": "b1",
              "kind": "formula",
              "latex": "x^2 - 5x + 6 = 0",
              "confidence": 0.72,
              "note": "符号疑似笔误"
            },
            { "id": "b2", "kind": "text", "text": "因式分解", "confidence": 0.93 }
          ]
        }
        """;

    private static AssistantBoardPlan Parse(string json)
    {
        Assert.True(AssistantBoardPlan.TryParse(json, out AssistantBoardPlan plan, out string error), error);
        return plan;
    }

    [Fact]
    public void TryParse_ReadsOriginalsConfidenceWarningsAndBlockConfidence()
    {
        AssistantBoardPlan plan = Parse(PlanJson);

        Assert.Equal(2, plan.Schema);
        Assert.Equal(new[] { "x^2 - 5x + 6 = 0", "x1 = 2", "x2 = 3" }, plan.Originals);
        Assert.Empty(plan.OriginalBlocks);
        Assert.Equal(0.78, plan.Confidence, 3);
        Assert.True(plan.NeedsReview);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.Contains("笔误", plan.Warnings[1]);

        Assert.Equal(0.72, plan.Blocks[0].Confidence, 3);
        Assert.Equal("符号疑似笔误", plan.Blocks[0].Note);
        Assert.Equal(0.93, plan.Blocks[1].Confidence, 3);
    }

    [Fact]
    public void TryParse_ObjectOriginals_KeepStructureAndFlattenToLines()
    {
        const string json =
            """
            {
              "originals": [
                { "text": "第一行原文", "rect": { "x": 0.1, "y": 0.2, "w": 0.3, "h": 0.1 } },
                { "latex": "x^2" }
              ],
              "blocks": [{ "id": "b1", "kind": "text", "text": "整理" }]
            }
            """;

        AssistantBoardPlan plan = Parse(json);

        Assert.Equal(new[] { "第一行原文", "x^2" }, plan.Originals);
        Assert.Equal(2, plan.OriginalBlocks.Count);
        Assert.Equal("第一行原文", plan.OriginalBlocks[0].Text);
        Assert.Equal("x^2", plan.OriginalBlocks[1].Latex);
        Assert.False(plan.OriginalBlocks[0].Rect.IsEmpty);
    }

    [Fact]
    public void TryParse_ClampsConfidenceAndDefaultsWhenMissing()
    {
        const string high =
            """
            { "confidence": 1.7, "blocks": [{ "id": "b1", "confidence": -3 }] }
            """;
        AssistantBoardPlan clamped = Parse(high);
        Assert.Equal(1.0, clamped.Confidence);
        Assert.Equal(0.0, clamped.Blocks[0].Confidence);

        const string stringValue =
            """
            { "originals": "第一行\n第二行", "confidence": "0.55", "blocks": [{ "id": "b1" }] }
            """;
        AssistantBoardPlan fromString = Parse(stringValue);
        Assert.Equal(0.55, fromString.Confidence, 3);
        Assert.Equal(new[] { "第一行", "第二行" }, fromString.Originals);

        const string missing = """{ "blocks": [{ "id": "b1" }] }""";
        AssistantBoardPlan defaults = Parse(missing);
        Assert.Equal(1.0, defaults.Confidence);
        Assert.Empty(defaults.Originals);
        Assert.Empty(defaults.Warnings);
        Assert.Equal(1.0, defaults.Blocks[0].Confidence);
        Assert.False(defaults.NeedsReview);
    }

    [Fact]
    public void MockBoardWriteScript_ExposesOriginalsConfidenceAndWarnings()
    {
        IReadOnlyList<InkCanvas.Core.AiAssistant.AssistantEvent> events =
            InkCanvas.Platform.Ai.AiMockScript.Build(
                InkCanvas.Core.AiAssistant.AssistantMode.Chat, purpose: "board_write");

        string json = events.Single(item =>
            item.Kind == InkCanvas.Core.AiAssistant.AssistantEventKind.BoardPlan).Text;
        AssistantBoardPlan plan = Parse(json);

        Assert.Equal(3, plan.Originals.Count);
        Assert.Contains("判别式", plan.Originals[2]);
        Assert.Equal(0.78, plan.Confidence, 3);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.Equal(0.72, plan.Blocks[0].Confidence, 3);
        Assert.Contains("笔误", plan.Blocks[0].Note);
        Assert.Single(plan.Notes);
    }

    [Fact]
    public void SelectBlocks_KeepsOrderAndPlanLevelFields()
    {
        AssistantBoardPlan plan = Parse(PlanJson);

        AssistantBoardPlan selected = plan.SelectBlocks(new[] { "b2", "b1" });
        Assert.Equal(new[] { "b1", "b2" }, selected.Blocks.Select(block => block.Id));
        Assert.Equal(plan.Originals, selected.Originals);
        Assert.Equal(plan.Warnings, selected.Warnings);
        Assert.Equal(plan.Confidence, selected.Confidence, 3);
        Assert.Equal(plan.Schema, selected.Schema);

        AssistantBoardPlan unknown = plan.SelectBlocks(new[] { "nope" });
        Assert.True(unknown.IsEmpty);
        Assert.Empty(unknown.Blocks);

        AssistantBoardPlan none = plan.SelectBlocks(Array.Empty<string>());
        Assert.True(none.IsEmpty);

        AssistantBoardPlan all = plan.SelectBlocks(plan.Blocks.Select(block => block.Id).ToArray());
        Assert.Equal(2, all.Blocks.Count);
    }
}