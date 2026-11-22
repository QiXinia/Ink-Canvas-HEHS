using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.AiAssistant;
using System;
using System.Collections.Generic;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AiAssistantTests
{
    private static AssistantSession NewSession()
    {
        var session = AssistantMachine.CreateSession(AssistantMode.Solve, "session-1");
        AssistantMachine.BeginCapture(session, 1000);
        AssistantMachine.BeginRecognize(session);
        return session;
    }

    [Fact]
    public void Sse_OpenAiDelta_ProducesToken()
    {
        var parser = new SseStreamParser();
        var events = parser.Push("data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n");

        var token = Assert.Single(events);
        Assert.Equal(AssistantEventKind.Token, token.Kind);
        Assert.Equal("Hello", token.Text);
    }

    [Fact]
    public void Sse_CustomFormulaEvent_ProducesFormula()
    {
        var parser = new SseStreamParser();
        var events = parser.Push("event: formula\ndata: {\"latex\":\"x^2\"}\n\n");

        var formula = Assert.Single(events);
        Assert.Equal(AssistantEventKind.Formula, formula.Kind);
        Assert.Equal("x^2", formula.Latex);
    }

    [Fact]
    public void Sse_CustomStepEvent_ProducesStep()
    {
        var parser = new SseStreamParser();
        var events = parser.Push("event: step\ndata: {\"index\":2,\"text\":\"Subtract\",\"latex\":\"x=1\"}\n\n");

        var step = Assert.Single(events);
        Assert.Equal(AssistantEventKind.Step, step.Kind);
        Assert.Equal(2, step.Index);
        Assert.Equal("Subtract", step.Text);
        Assert.Equal("x=1", step.Latex);
    }

    [Fact]
    public void Sse_DoneMarker_ProducesDone()
    {
        var parser = new SseStreamParser();
        var events = parser.Push("data: [DONE]\n\n");

        var done = Assert.Single(events);
        Assert.Equal(AssistantEventKind.Done, done.Kind);
    }

    [Fact]
    public void Sse_ErrorEvent_ProducesError()
    {
        var parser = new SseStreamParser();
        var events = parser.Push("event: error\ndata: {\"message\":\"boom\"}\n\n");

        var error = Assert.Single(events);
        Assert.Equal(AssistantEventKind.Error, error.Kind);
        Assert.Equal("boom", error.Text);
    }

    [Fact]
    public void Sse_CarriedPartialLine_IsJoined()
    {
        var parser = new SseStreamParser();
        Assert.Empty(parser.Push("event: token\ndata: {\"text\":\"he"));

        var events = parser.Push("llo\"}\n\n");
        var token = Assert.Single(events);
        Assert.Equal("hello", token.Text);
    }

    [Fact]
    public void Sse_Flush_DispatchesTrailingBlock()
    {
        var parser = new SseStreamParser();
        Assert.Empty(parser.Push("event: formula\ndata: x+y"));

        var events = parser.Flush();
        var formula = Assert.Single(events);
        Assert.Equal(AssistantEventKind.Formula, formula.Kind);
        Assert.Equal("x+y", formula.Latex);
    }

    [Fact]
    public void Machine_Token_MovesToStreaming()
    {
        var session = NewSession();
        AssistantMachine.ApplyEvent(session, new AssistantEvent { Kind = AssistantEventKind.Token, Text = "a" }, 1200);

        Assert.Equal(AssistantState.Streaming, session.State);
        Assert.Equal("a", session.Text);
        Assert.Equal(1, session.TokenCount);
        Assert.Equal(1200, session.FirstTokenAtMs);
    }

    [Fact]
    public void Machine_Done_MovesToDone()
    {
        var session = NewSession();
        AssistantMachine.ApplyEvent(session, new AssistantEvent { Kind = AssistantEventKind.Done }, 1500);

        Assert.Equal(AssistantState.Done, session.State);
    }

    [Fact]
    public void Machine_Error_IgnoresSubsequentTokens()
    {
        var session = NewSession();
        AssistantMachine.ApplyEvent(session, new AssistantEvent { Kind = AssistantEventKind.Error, Text = "fail" }, 1300);
        AssistantMachine.ApplyEvent(session, new AssistantEvent { Kind = AssistantEventKind.Token, Text = "later" }, 1400);

        Assert.Equal(AssistantState.Error, session.State);
        Assert.Equal("fail", session.Error);
        Assert.Equal("", session.Text);
        Assert.Equal(0, session.TokenCount);
    }

    [Fact]
    public void Machine_FirstTokenTimeout_IsDetected()
    {
        var session = NewSession();

        Assert.False(AssistantMachine.FirstTokenTimedOut(session, 1200, 500));
        Assert.True(AssistantMachine.FirstTokenTimedOut(session, 1500, 500));

        AssistantMachine.ApplyEvent(session, new AssistantEvent { Kind = AssistantEventKind.Token, Text = "a" }, 1600);
        Assert.False(AssistantMachine.FirstTokenTimedOut(session, 5000, 500));
    }

    [Fact]
    public void Machine_FollowUp_ResetsStreamingState()
    {
        var session = NewSession();
        AssistantMachine.ApplyEvent(session, new AssistantEvent { Kind = AssistantEventKind.Token, Text = "answer" }, 1200);
        AssistantMachine.ApplyEvent(session, new AssistantEvent { Kind = AssistantEventKind.Done }, 1300);

        AssistantMachine.BeginFollowUp(session, "why?", 2000);

        Assert.Equal(AssistantState.Streaming, session.State);
        Assert.Equal(0, session.FirstTokenAtMs);
        Assert.Equal("", session.Error);
        Assert.Contains("why?", session.Text);
        Assert.Contains("answer", session.Text);
    }

    [Fact]
    public void Text_Fraction_IsExpanded()
    {
        Assert.Equal("(1)/(2)", AssistantText.LatexToPlain("\\frac{1}{2}"));
    }

    [Fact]
    public void Text_Sqrt_IsExpanded()
    {
        Assert.Equal("√(x²+1)", AssistantText.LatexToPlain("\\sqrt{x^2+1}"));
    }

    [Fact]
    public void Text_Scripts_LoseBraces()
    {
        Assert.Equal("x²+y₁", AssistantText.LatexToPlain("x^{2}+y_{1}"));
    }

    [Fact]
    public void Text_UnclosedFormula_IsIncomplete()
    {
        var segments = AssistantText.SplitFormulas("面积 $x^2$ 未闭合 $y");

        Assert.Equal(4, segments.Count);
        Assert.False(segments[0].IsFormula);
        Assert.True(segments[1].IsFormula);
        Assert.True(segments[1].Complete);
        Assert.Equal("x²", segments[1].Text);
        Assert.False(segments[2].IsFormula);
        Assert.True(segments[3].IsFormula);
        Assert.False(segments[3].Complete);
    }

    [Fact]
    public void Markdown_Heading_IsParsed()
    {
        var blocks = AssistantText.ParseMarkdown("## 解题步骤");

        var heading = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Heading, heading.Kind);
        Assert.Equal(2, heading.Level);
        Assert.Equal("解题步骤", heading.Text);
    }

    [Fact]
    public void Markdown_ListItems_AreParsed()
    {
        var blocks = AssistantText.ParseMarkdown("- 第一\n- 第二\n1. 第三");

        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(MarkdownBlockKind.ListItem, block.Kind));
        Assert.Equal("第一", blocks[0].Text);
        Assert.Equal("第三", blocks[2].Text);
    }

    [Fact]
    public void Markdown_UnclosedCodeFence_IsIncomplete()
    {
        var blocks = AssistantText.ParseMarkdown("```cs\nvar x = 1;");

        var code = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Code, code.Kind);
        Assert.False(code.Complete);
        Assert.Equal("var x = 1;", code.Text);
    }

    [Fact]
    public void Markdown_Table_IsParsed()
    {
        var blocks = AssistantText.ParseMarkdown("| 名称 | 值 |\n| --- | --- |\n| a | 1 |\n| b | 2 |");

        var table = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Table, table.Kind);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("名称", table.Rows[0][0]);
        Assert.Equal("b", table.Rows[2][0]);
    }

    [Fact]
    public void Layout_Below_IsPreferred()
    {
        var placement = AssistantLayout.Place(
            new AssistantRect(100, 100, 200, 200), 150, 100, 800, 600);

        Assert.Equal(100, placement.X);
        Assert.Equal(212, placement.Y);
        Assert.Equal(0, placement.Side);
        Assert.False(placement.FlippedX);
        Assert.False(placement.FlippedY);
    }

    [Fact]
    public void Layout_FlipsAbove_WhenNoRoomBelow()
    {
        var placement = AssistantLayout.Place(
            new AssistantRect(100, 450, 200, 550), 150, 100, 800, 600);

        Assert.Equal(338, placement.Y);
        Assert.Equal(1, placement.Side);
        Assert.True(placement.FlippedY);
    }

    [Fact]
    public void Layout_FlipsLeft_WhenRightEdgeOverflows()
    {
        var placement = AssistantLayout.Place(
            new AssistantRect(700, 100, 760, 200), 200, 100, 800, 600);

        Assert.Equal(488, placement.X);
        Assert.Equal(3, placement.Side);
        Assert.True(placement.FlippedX);
    }

    [Fact]
    public void Layout_Clamps_OversizedCard()
    {
        var placement = AssistantLayout.Place(
            new AssistantRect(100, 100, 200, 200), 900, 700, 800, 600);

        Assert.Equal(12, placement.X);
        Assert.Equal(12, placement.Y);
    }

    [Fact]
    public void PasteCard_PlacesRightWithGap()
    {
        var placement = PasteCardLayout.Place(
            new AssistantRect(100, 100, 200, 200), 120, 60, 800, 600);

        Assert.Equal(216, placement.Left);
        Assert.Equal(100, placement.Top);
    }

    [Fact]
    public void PasteCard_FallsBackLeft_WhenRightOverflows()
    {
        var placement = PasteCardLayout.Place(
            new AssistantRect(600, 100, 750, 200), 200, 60, 800, 600);

        Assert.Equal(384, placement.Left);
        Assert.Equal(100, placement.Top);
    }

    [Fact]
    public void PasteCard_Estimate_UsesMinWidthForEmptyLines()
    {
        var (width, height) = PasteCardLayout.Estimate(Array.Empty<string>());

        Assert.Equal(180, width);
        Assert.Equal(38, height);
    }

    [Fact]
    public void Layout_OffsetAndPlaceInWorkArea_HandleNonZeroClientOrigin()
    {
        // 板窗口客户区原点在副屏 (1920, 0)，ViewTransform 得到的客户区 DIP 锚点 (100,100)-(300,200)。
        var work = new AssistantRect(1920, 0, 3840, 1080);
        var anchor = AssistantLayout.Offset(new AssistantRect(100, 100, 300, 200), 1920, 0);

        var placement = AssistantLayout.PlaceInWorkArea(anchor, 380, 400, work);

        Assert.Equal(2020, placement.X);
        Assert.Equal(212, placement.Y);
    }

    [Fact]
    public void Layout_PlaceInWorkArea_ClampsInsideNegativeSecondaryWorkArea()
    {
        // 副屏位于主屏左侧时工作区原点为负，clamp 必须留在副屏内而不是回到主屏。
        var work = new AssistantRect(-1920, 40, 0, 1040);
        var anchor = AssistantLayout.Offset(new AssistantRect(10, 10, 60, 40), -1920, 40);

        var placement = AssistantLayout.PlaceInWorkArea(anchor, 380, 400, work);

        Assert.InRange(placement.X, work.Left + 12, work.Right - 380 - 12);
        Assert.InRange(placement.Y, work.Top + 12, work.Bottom - 400 - 12);
    }

    [Fact]
    public void Layout_DockBottomRight_UsesSecondaryWorkArea()
    {
        var work = new AssistantRect(1920, 0, 3840, 1040);

        var placement = AssistantLayout.DockBottomRight(work, 380, 460);

        Assert.Equal(3444, placement.X);
        Assert.Equal(564, placement.Y);
        Assert.False(placement.FlippedX);
        Assert.False(placement.FlippedY);
    }

    [Fact]
    public void Layout_ToDips_ConvertsPhysicalClientOrigin()
    {
        var dips = AssistantLayout.ToDips(new AssistantRect(2880, 0, 4800, 1620), 1.5);

        Assert.Equal(1920, dips.Left);
        Assert.Equal(3200, dips.Right);
        Assert.Equal(1080, dips.Bottom);
    }
}