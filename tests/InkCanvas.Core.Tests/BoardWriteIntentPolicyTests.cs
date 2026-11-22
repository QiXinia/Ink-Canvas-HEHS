using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>「整理板书 / AI 书写」意图判定：空页只允许 AI 书写。</summary>
public sealed class BoardWriteIntentPolicyTests
{
    [Theory]
    [InlineData("organize", "organize")]
    [InlineData("ORGANIZE", "organize")]
    [InlineData(" organize ", "organize")]
    [InlineData("write", "write")]
    [InlineData("WRITE", "write")]
    [InlineData("", "write")]
    [InlineData(null, "write")]
    [InlineData("unknown", "write")]
    public void Normalize_DefaultsToWrite(string intent, string expected)
    {
        Assert.Equal(expected, BoardWriteIntentPolicy.Normalize(intent));
    }

    [Fact]
    public void RequiresExistingContent_OnlyForOrganize()
    {
        Assert.True(BoardWriteIntentPolicy.RequiresExistingContent("organize"));
        Assert.False(BoardWriteIntentPolicy.RequiresExistingContent("write"));
        Assert.False(BoardWriteIntentPolicy.RequiresExistingContent(null));
        Assert.False(BoardWriteIntentPolicy.RequiresExistingContent("unknown"));
    }

    [Fact]
    public void CanRequest_EmptyPage_AllowsWriteButBlocksOrganize()
    {
        Assert.True(BoardWriteIntentPolicy.CanRequest("write", pageHasContent: false));
        Assert.True(BoardWriteIntentPolicy.CanRequest(null, pageHasContent: false));
        Assert.False(BoardWriteIntentPolicy.CanRequest("organize", pageHasContent: false));
        Assert.True(BoardWriteIntentPolicy.CanRequest("organize", pageHasContent: true));
        Assert.True(BoardWriteIntentPolicy.CanRequest("write", pageHasContent: true));
    }

    [Fact]
    public void EmptyPageNotice_OnlyOrganizeHasPrompt()
    {
        string organize = BoardWriteIntentPolicy.EmptyPageNotice("organize");
        Assert.Contains("请先书写或框选内容", organize);
        Assert.Equal("", BoardWriteIntentPolicy.EmptyPageNotice("write"));
        Assert.Equal("", BoardWriteIntentPolicy.EmptyPageNotice(null));
    }

    [Fact]
    public void DefaultInstruction_MatchesIntent()
    {
        Assert.Equal("整理当前页板书", BoardWriteIntentPolicy.DefaultInstruction("organize"));
        Assert.Equal("写一句本课总结", BoardWriteIntentPolicy.DefaultInstruction("write"));
        Assert.Equal("写一句本课总结", BoardWriteIntentPolicy.DefaultInstruction(null));
    }

    [Fact]
    public void Screenshot_OnlyAttachedForOrganizeWithThumbnail()
    {
        Assert.True(BoardWriteIntentPolicy.ShouldAttachScreenshot("organize", "thumb"));
        Assert.False(BoardWriteIntentPolicy.ShouldAttachScreenshot("organize", ""));
        Assert.False(BoardWriteIntentPolicy.ShouldAttachScreenshot("organize", null));
        Assert.False(BoardWriteIntentPolicy.ShouldAttachScreenshot("write", "thumb"));
        Assert.False(BoardWriteIntentPolicy.ShouldAttachScreenshot(null, "thumb"));
    }
}