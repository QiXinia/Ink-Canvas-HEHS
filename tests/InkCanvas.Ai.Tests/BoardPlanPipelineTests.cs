using InkCanvas.Core.AiAssistant;
using InkCanvas.Platform.Ai;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>
/// board_write mock 端到端的最小闭环：剧本 → SSE 原始 JSON → 结构化计划 → 目标路由。
/// 真实 PNG 渲染/实机写入不在本测试范围（需 Win2D 设备与浮窗）。
/// </summary>
public sealed class BoardPlanPipelineTests
{
    [Fact]
    public void MockBoardWriteScript_ProducesParsableBoardPlan()
    {
        IReadOnlyList<AssistantEvent> events = AiMockScript.Build(
            AssistantMode.Chat, purpose: "board_write");

        AssistantEvent planEvent = Assert.Single(events, item => item.Kind == AssistantEventKind.BoardPlan);
        Assert.Equal(AssistantEventKind.Done, events[^1].Kind);
        Assert.False(string.IsNullOrWhiteSpace(planEvent.Text));

        Assert.True(AssistantBoardPlan.TryParse(planEvent.Text, out AssistantBoardPlan plan, out string error), error);
        Assert.Equal(4, plan.Blocks.Count);
        Assert.False(plan.IsEmpty);
        Assert.Equal(
            new[] { "formula", "text", "wordlist", "poem" },
            plan.Blocks.Select(block => block.Kind));
        Assert.Contains("\\frac{1}{2}", plan.Blocks[0].Latex);
        Assert.Equal(4, plan.Blocks[3].Lines.Count);
        Assert.Equal("静夜思", plan.Blocks[3].Title);
        Assert.Single(plan.Notes);
    }

    [Fact]
    public void SseStream_EmitsBoardPlanEventWithRawJson()
    {
        IReadOnlyList<AssistantEvent> mock = AiMockScript.Build(
            AssistantMode.Chat, purpose: "board_write");
        string json = mock.Single(item => item.Kind == AssistantEventKind.BoardPlan).Text;

        string payload = "event: board_plan\n"
            + string.Join("\n", json.Split('\n').Select(line => "data: " + line))
            + "\n\n";

        var parser = new SseStreamParser();
        IReadOnlyList<AssistantEvent> events = parser.Push(payload);

        AssistantEvent boardPlan = Assert.Single(events);
        Assert.Equal(AssistantEventKind.BoardPlan, boardPlan.Kind);
        Assert.Equal(json, boardPlan.Text);

        Assert.True(AssistantBoardPlan.TryParse(boardPlan.Text, out AssistantBoardPlan plan, out string error), error);
        Assert.Equal(4, plan.Blocks.Count);
    }

    [Fact]
    public void BoardPlanTarget_DoesNotOverrideRequestSnapshot()
    {
        AssistantBoardPlan plan = ParseFullPlan();
        object canvas = new();
        object page = new();

        var requested = new AssistantTarget
        {
            Canvas = canvas,
            Page = page,
            Kind = AssistantCanvasKind.SideBoard,
            PageIndex = 2,
            SessionId = "board-session",
        };
        var current = new AssistantTarget
        {
            Canvas = canvas,
            Page = page,
            Kind = AssistantCanvasKind.SideBoard,
            PageIndex = 2,
            SessionId = "board-session",
        };
        var otherPage = new AssistantTarget
        {
            Canvas = canvas,
            Page = new object(),
            Kind = AssistantCanvasKind.SideBoard,
            PageIndex = 2,
            SessionId = "board-session",
        };
        var otherBoard = new AssistantTarget
        {
            Canvas = new object(),
            Page = page,
            Kind = AssistantCanvasKind.MainBoard,
            PageIndex = 2,
            SessionId = "board-session",
        };

        // 计划里的 target 可能落后于发起请求时的快照；写入必须跟随快照。
        Assert.Equal("side", plan.TargetBoard);
        Assert.Equal(3, plan.TargetPageIndex);
        Assert.NotEqual(plan.TargetPageIndex, requested.PageIndex);

        Assert.True(AssistantTargetPolicy.CanWriteBack(requested, current));
        Assert.False(AssistantTargetPolicy.CanWriteBack(requested, otherPage));
        Assert.False(AssistantTargetPolicy.CanWriteBack(requested, otherBoard));
    }

    private static AssistantBoardPlan ParseFullPlan()
    {
        const string json =
            """
            {
              "type": "board_plan",
              "schema": 1,
              "intent": "organize",
              "target": { "board": "side", "pageIndex": 3 },
              "blocks": [
                { "id": "b1", "kind": "text", "text": "整理结果", "rect": { "x": 0.1, "y": 0.2, "w": 0.3, "h": 0.4 } }
              ]
            }
            """;

        Assert.True(AssistantBoardPlan.TryParse(json, out AssistantBoardPlan plan, out string error), error);
        return plan;
    }
}