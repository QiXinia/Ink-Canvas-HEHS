using InkCanvas.Core.AiAssistant;
using InkCanvas.Platform.Ai;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AssistantBoardPlanTests
{
    private const string FullPlanJson =
        """
        {
          "type": "board_plan",
          "schema": 2,
          "intent": "organize",
          "target": { "board": "side", "pageIndex": 3 },
          "blocks": [
            {
              "id": "b1",
              "kind": "formula",
              "latex": "x^2 - 1",
              "rect": { "x": 0.1, "y": 0.2, "w": 0.3, "h": 0.4 },
              "style": { "fontSize": 30, "align": "CENTER", "columns": 2, "lineHeight": 1.6 },
              "note": "公式块"
            },
            {
              "id": "b2",
              "kind": "poem",
              "title": "静夜思",
              "author": "李白",
              "lines": ["床前明月光", "疑是地上霜"],
              "rect": [0.5, 0.1, 0.4, 0.2]
            }
          ],
          "notes": ["第 2 行识别置信度较低"]
        }
        """;

    private static AssistantBoardPlan Parse(string json)
    {
        Assert.True(AssistantBoardPlan.TryParse(json, out AssistantBoardPlan plan, out string error), error);
        Assert.Equal("", error);
        return plan;
    }

    private static string SseData(string json)
    {
        string normalized = json.Replace("\r\n", "\n").Replace('\r', '\n');
        return string.Join("\n", normalized.Split('\n').Select(line => "data: " + line));
    }

    [Fact]
    public void TryParse_FullPlan_ReadsTargetBlocksAndNotes()
    {
        AssistantBoardPlan plan = Parse(FullPlanJson);

        Assert.Equal(2, plan.Schema);
        Assert.Equal("organize", plan.Intent);
        Assert.Equal("side", plan.TargetBoard);
        Assert.Equal(3, plan.TargetPageIndex);
        Assert.Equal(2, plan.Blocks.Count);
        Assert.Single(plan.Notes);
        Assert.Equal("第 2 行识别置信度较低", plan.Notes[0]);
        Assert.False(plan.IsEmpty);

        AssistantBoardBlock formula = plan.Blocks[0];
        Assert.Equal("b1", formula.Id);
        Assert.Equal("formula", formula.Kind);
        Assert.Equal("x^2 - 1", formula.Latex);
        Assert.Equal("公式块", formula.Note);
        Assert.Equal(0.1, formula.Rect.Left, 6);
        Assert.Equal(0.2, formula.Rect.Top, 6);
        Assert.Equal(0.4, formula.Rect.Right, 6);
        Assert.Equal(0.6, formula.Rect.Bottom, 6);
        Assert.Equal(30, formula.Style.FontSize);
        Assert.Equal("center", formula.Style.Align);
        Assert.Equal(2, formula.Style.Columns);
        Assert.Equal(1.6, formula.Style.LineHeight);

        AssistantBoardBlock poem = plan.Blocks[1];
        Assert.Equal("poem", poem.Kind);
        Assert.Equal("静夜思", poem.Title);
        Assert.Equal("李白", poem.Author);
        Assert.Equal(2, poem.Lines.Count);
        Assert.Equal("床前明月光", poem.Lines[0]);
        Assert.Equal(0.5, poem.Rect.Left, 6);
        Assert.Equal(0.9, poem.Rect.Right, 6);
        Assert.Equal(24, poem.Style.FontSize);
        Assert.Equal("left", poem.Style.Align);
        Assert.Equal(1, poem.Style.Columns);
        Assert.Equal(1.4, poem.Style.LineHeight);
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsFalseWithoutThrowing()
    {
        Assert.False(AssistantBoardPlan.TryParse("{ not json", out AssistantBoardPlan plan, out string error));
        Assert.Null(plan);
        Assert.False(string.IsNullOrEmpty(error));

        Assert.False(AssistantBoardPlan.TryParse("[]", out _, out error));
        Assert.False(string.IsNullOrEmpty(error));

        Assert.False(AssistantBoardPlan.TryParse("", out _, out error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryParse_MissingOrEmptyBlocks_IsInvalid()
    {
        Assert.False(AssistantBoardPlan.TryParse("{}", out _, out string error));
        Assert.Contains("blocks", error);

        Assert.False(AssistantBoardPlan.TryParse("{\"blocks\":[]}", out _, out error));
        Assert.Contains("blocks", error);

        Assert.False(AssistantBoardPlan.TryParse("{\"blocks\":[1,2,3]}", out _, out error));
        Assert.Contains("blocks", error);
    }

    [Fact]
    public void TryParse_Rect_IsClampedToUnitSquare()
    {
        const string json =
            """
            {
              "blocks": [
                { "kind": "text", "text": "a", "rect": { "x": -0.2, "y": 0.9, "w": 0.5, "h": 0.5 } },
                { "kind": "text", "text": "b", "rect": [-1, 0.5, 2, 2] }
              ]
            }
            """;

        AssistantBoardPlan plan = Parse(json);

        AssistantRect first = plan.Blocks[0].Rect;
        Assert.Equal(0, first.Left);
        Assert.Equal(0.9, first.Top, 6);
        Assert.Equal(0.3, first.Right, 6);
        Assert.Equal(1, first.Bottom);

        AssistantRect second = plan.Blocks[1].Rect;
        Assert.Equal(0, second.Left);
        Assert.Equal(0.5, second.Top, 6);
        Assert.Equal(1, second.Right);
        Assert.Equal(1, second.Bottom);
    }

    [Fact]
    public void TryParse_UnknownKind_FallsBackToTextAndDefaults()
    {
        const string json =
            """
            {
              "blocks": [
                { "kind": "diagram", "text": "结构图", "rect": [0.1, 0.1, 0.2, 0.2] }
              ]
            }
            """;

        AssistantBoardPlan plan = Parse(json);
        AssistantBoardBlock block = Assert.Single(plan.Blocks);

        Assert.Equal("text", block.Kind);
        Assert.Equal("b1", block.Id);
        Assert.Equal("结构图", block.Text);
        Assert.Equal(24, block.Style.FontSize);
        Assert.Equal("left", block.Style.Align);
        Assert.Equal(1, block.Style.Columns);
        Assert.Equal(1.4, block.Style.LineHeight);
    }

    [Fact]
    public void TryParse_EnforcesBlockTextAndLineLimits()
    {
        var builder = new StringBuilder();
        builder.Append("{\"blocks\":[");
        for (int i = 0; i < AssistantBoardPlan.MaxBlockCount + 6; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append("{\"kind\":\"text\",\"text\":\"x\",\"rect\":[0,0,1,1]}");
        }

        builder.Append("],\"notes\":[");
        builder.Append("\"长文本块");
        builder.Append('a', AssistantBoardPlan.MaxTextLength + 500);
        builder.Append("\"]}");

        AssistantBoardPlan plan = Parse(builder.ToString());
        Assert.Equal(AssistantBoardPlan.MaxBlockCount, plan.Blocks.Count);

        var textBuilder = new StringBuilder("{\"blocks\":[{\"kind\":\"text\",\"text\":\"");
        textBuilder.Append('a', AssistantBoardPlan.MaxTextLength + 500);
        textBuilder.Append("\"}]}");
        AssistantBoardPlan textPlan = Parse(textBuilder.ToString());
        Assert.Equal(AssistantBoardPlan.MaxTextLength, textPlan.Blocks[0].Text.Length);

        var linesBuilder = new StringBuilder("{\"blocks\":[{\"kind\":\"wordlist\",\"lines\":[");
        for (int i = 0; i < AssistantBoardPlan.MaxLineCount + 50; i++)
        {
            if (i > 0) linesBuilder.Append(',');
            linesBuilder.Append("\"行").Append(i).Append('"');
        }

        linesBuilder.Append("]}]}");
        AssistantBoardPlan linesPlan = Parse(linesBuilder.ToString());
        Assert.Equal(AssistantBoardPlan.MaxLineCount, linesPlan.Blocks[0].Lines.Count);
    }

    [Fact]
    public void Sse_BoardPlanEvent_KeepsRawJsonForTryParse()
    {
        var parser = new SseStreamParser();
        var events = parser.Push("event: board_plan\n" + SseData(FullPlanJson) + "\n\n");

        AssistantEvent boardPlan = Assert.Single(events);
        Assert.Equal(AssistantEventKind.BoardPlan, boardPlan.Kind);
        Assert.True(AssistantBoardPlan.TryParse(boardPlan.Text, out AssistantBoardPlan plan, out _));
        Assert.Equal("side", plan.TargetBoard);
        Assert.Equal(3, plan.TargetPageIndex);
    }

    [Fact]
    public void Sse_BoardPlan_IsRecognizedFromTypeOrBlocks()
    {
        var parser = new SseStreamParser();
        var byType = parser.Push(
            "data: {\"type\":\"board_plan\",\"blocks\":[{\"kind\":\"text\",\"text\":\"a\"}]}\n\n");
        Assert.Equal(AssistantEventKind.BoardPlan, Assert.Single(byType).Kind);

        var byBlocks = parser.Push(
            "data: {\"blocks\":[{\"kind\":\"text\",\"text\":\"a\"}]}\n\n");
        Assert.Equal(AssistantEventKind.BoardPlan, Assert.Single(byBlocks).Kind);
    }

    [Fact]
    public void Machine_BoardPlanEvent_StoresPlanAndStreams()
    {
        var session = AssistantMachine.CreateSession(AssistantMode.Chat, "session-board");
        AssistantMachine.BeginCommand(session, "把这段整理成板书", 1000);
        AssistantMachine.ApplyEvent(
            session,
            new AssistantEvent { Kind = AssistantEventKind.BoardPlan, Text = FullPlanJson },
            1200);

        Assert.Equal(AssistantState.Streaming, session.State);
        Assert.NotNull(session.BoardPlan);
        Assert.Equal("side", session.BoardPlan.TargetBoard);
        Assert.Equal(2, session.BoardPlan.Blocks.Count);
        Assert.Equal(1200, session.FirstTokenAtMs);
        Assert.Equal("", session.Text);
    }

    [Fact]
    public void BeginCommand_DoesNotAppendInstructionToBody()
    {
        var session = AssistantMachine.CreateSession(AssistantMode.Chat, "session-command");
        session.Text = "已有正文";
        session.Error = "旧错误";

        AssistantMachine.BeginCommand(session, "请把左侧内容整理成三列", 2000);

        Assert.Equal("已有正文", session.Text);
        Assert.Equal("", session.Error);
        Assert.Equal(AssistantState.Streaming, session.State);
        Assert.Equal(2000, session.StartedAtMs);
        Assert.Equal(0, session.FirstTokenAtMs);
    }

    [Fact]
    public void BuildPayload_WritesPurposeInstructionAndTargetPage()
    {
        var request = new AssistantRequest
        {
            Mode = AssistantMode.Chat,
            Purpose = "board_write",
            Instruction = "把这段整理成板书",
            TargetPageIndex = 2,
        };

        using JsonDocument document = JsonDocument.Parse(AiClient.BuildPayload(request, "m1"));
        JsonElement root = document.RootElement;

        Assert.Equal("board_write", root.GetProperty("purpose").GetString());
        Assert.Equal("把这段整理成板书", root.GetProperty("instruction").GetString());
        Assert.Equal(2, root.GetProperty("targetPageIndex").GetInt32());
    }

    [Fact]
    public void BuildPayload_OmitsTargetPageIndex_WhenUnspecified()
    {
        var request = new AssistantRequest { Mode = AssistantMode.Chat };

        using JsonDocument document = JsonDocument.Parse(AiClient.BuildPayload(request, "m1"));
        JsonElement root = document.RootElement;

        Assert.False(root.TryGetProperty("targetPageIndex", out _));
        Assert.False(root.TryGetProperty("instruction", out _));
    }

    [Fact]
    public void MockScript_BoardWrite_ProducesFourKindsAndNotes()
    {
        IReadOnlyList<AssistantEvent> script = AiMockScript.Build(
            AssistantMode.Chat, AssistantTeachingMode.Full, "board_write");

        Assert.Equal(2, script.Count);
        Assert.Equal(AssistantEventKind.BoardPlan, script[0].Kind);
        Assert.Equal(AssistantEventKind.Done, script[1].Kind);

        Assert.True(AssistantBoardPlan.TryParse(script[0].Text, out AssistantBoardPlan plan, out string error), error);
        Assert.Equal(4, plan.Blocks.Count);
        Assert.Contains(plan.Blocks, block => block.Kind == "formula");
        Assert.Contains(plan.Blocks, block => block.Kind == "text");
        Assert.Contains(plan.Blocks, block => block.Kind == "wordlist");
        Assert.Contains(plan.Blocks, block => block.Kind == "poem");
        Assert.Single(plan.Notes);
    }
}