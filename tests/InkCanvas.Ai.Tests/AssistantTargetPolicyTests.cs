using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AssistantTargetPolicyTests
{
    private static AssistantTarget Board(object canvas, object page, int index, string session)
        => new AssistantTarget
        {
            Canvas = canvas,
            Page = page,
            Kind = AssistantCanvasKind.MainBoard,
            PageIndex = index,
            SessionId = session,
        };

    [Fact]
    public void SameTarget_AllowsWriteBack()
    {
        var board = new object();
        var page = new object();
        var requested = Board(board, page, 2, "session-1");
        var current = Board(board, page, 2, "session-1");

        Assert.True(AssistantTargetPolicy.SameCanvas(requested, current));
        Assert.True(AssistantTargetPolicy.SameSession(requested, current));
        Assert.True(AssistantTargetPolicy.SamePage(requested, current));
        Assert.True(AssistantTargetPolicy.IsCurrent(requested, current));
        Assert.True(AssistantTargetPolicy.CanWriteBack(requested, current));
        Assert.Contains("kind=MainBoard", AssistantTargetPolicy.Describe(requested));
    }

    [Fact]
    public void NewSession_InvalidatesWriteBack()
    {
        var board = new object();
        var page = new object();
        var requested = Board(board, page, 1, "session-1");
        var current = Board(board, page, 1, "session-2");

        Assert.True(AssistantTargetPolicy.SameCanvas(requested, current));
        Assert.False(AssistantTargetPolicy.SameSession(requested, current));
        Assert.False(AssistantTargetPolicy.CanWriteBack(requested, current));
        Assert.Contains("新的请求替代", AssistantTargetPolicy.StaleNotice(requested, current));
    }

    [Fact]
    public void ReplacedPage_WithSameIndex_IsStale()
    {
        var board = new object();
        var requestedPage = new object();
        var currentPage = new object();
        var requested = Board(board, requestedPage, 3, "session-1");
        var current = Board(board, currentPage, 3, "session-1");

        Assert.True(AssistantTargetPolicy.SameCanvas(requested, current));
        Assert.True(AssistantTargetPolicy.SameSession(requested, current));
        Assert.False(AssistantTargetPolicy.SamePage(requested, current));
        Assert.False(AssistantTargetPolicy.CanWriteBack(requested, current));
        Assert.Contains("页面已切换或被删除", AssistantTargetPolicy.StaleNotice(requested, current));
    }

    [Fact]
    public void DifferentBoard_IsStale()
    {
        var mainBoard = new object();
        var sideBoard = new object();
        var page = new object();
        var requested = Board(mainBoard, page, 0, "session-1");
        var current = Board(sideBoard, page, 0, "session-1");

        Assert.False(AssistantTargetPolicy.SameCanvas(requested, current));
        Assert.False(AssistantTargetPolicy.CanWriteBack(requested, current));
        Assert.Contains("画板已关闭或已切换", AssistantTargetPolicy.StaleNotice(requested, current));
    }

    [Fact]
    public void Overlay_TargetsAreStable()
    {
        var requested = new AssistantTarget
        {
            Canvas = null,
            Page = null,
            Kind = AssistantCanvasKind.Overlay,
            PageIndex = 0,
            SessionId = "overlay-1",
        };
        var current = new AssistantTarget
        {
            Canvas = null,
            Page = null,
            Kind = AssistantCanvasKind.Overlay,
            PageIndex = 0,
            SessionId = "overlay-1",
        };

        Assert.True(AssistantTargetPolicy.SameCanvas(requested, current));
        Assert.True(AssistantTargetPolicy.CanWriteBack(requested, current));

        var board = Board(new object(), new object(), 0, "overlay-1");
        Assert.False(AssistantTargetPolicy.SameCanvas(requested, board));
        Assert.False(AssistantTargetPolicy.CanWriteBack(requested, board));
    }

    [Fact]
    public void NullTargets_AreSafe()
    {
        var target = Board(new object(), new object(), 0, "session-1");

        Assert.False(AssistantTargetPolicy.SameCanvas(null, target));
        Assert.False(AssistantTargetPolicy.SameCanvas(target, null));
        Assert.False(AssistantTargetPolicy.SameSession(null, null));
        Assert.False(AssistantTargetPolicy.SamePage(target, null));
        Assert.False(AssistantTargetPolicy.IsCurrent(null, target));
        Assert.False(AssistantTargetPolicy.CanWriteBack(target, null));
        Assert.Contains("已过期", AssistantTargetPolicy.StaleNotice(null, target));
        Assert.Contains("已过期", AssistantTargetPolicy.StaleNotice(target, null));
        Assert.False(string.IsNullOrEmpty(AssistantTargetPolicy.Describe(null)));
    }
}