using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>空间不足弹窗的纯逻辑选项映射。</summary>
public sealed class BoardLayoutDecisionPolicyTests
{
    [Fact]
    public void Describe_NeedsShrink_OffersShrinkAndNewPage()
    {
        var plan = new BoardLayoutPlan
        {
            Status = BoardLayoutStatus.NeedsShrink,
            Scale = 0.7,
            Message = "当前页空间不足，建议缩小到 70% 后写入。",
        };

        BoardLayoutDialogSpec spec = BoardLayoutDecisionPolicy.Describe(plan);

        Assert.Equal("当前页空间不足", spec.Title);
        Assert.Equal("缩小排版写入", spec.PrimaryText);
        Assert.Equal(BoardLayoutChoice.Shrink, spec.PrimaryChoice);
        Assert.Equal("写入新的一页", spec.SecondaryText);
        Assert.Equal(BoardLayoutChoice.NewPage, spec.SecondaryChoice);
        Assert.Equal("取消", spec.CloseText);
        Assert.True(spec.CanShrink);
        Assert.True(spec.CanNewPage);
        Assert.Contains("70%", spec.Message);
    }

    [Fact]
    public void Describe_NeedsMove_OnlyOffersNewPageAndKeepsAdjustmentHint()
    {
        var plan = new BoardLayoutPlan
        {
            Status = BoardLayoutStatus.NeedsMove,
            Message = "当前页空间不足，建议先移动/清理原迹或调整字号；也可以写入新的一页。",
        };

        BoardLayoutDialogSpec spec = BoardLayoutDecisionPolicy.Describe(plan);

        Assert.Equal("写入新的一页", spec.PrimaryText);
        Assert.Equal(BoardLayoutChoice.NewPage, spec.PrimaryChoice);
        Assert.Equal("", spec.SecondaryText);
        Assert.Equal(BoardLayoutChoice.Cancel, spec.SecondaryChoice);
        Assert.False(spec.CanShrink);
        Assert.True(spec.CanNewPage);
        Assert.Contains("调整", spec.Message);
    }

    [Fact]
    public void Describe_NeedsNewPage_OffersOnlyNewPage()
    {
        var plan = new BoardLayoutPlan
        {
            Status = BoardLayoutStatus.NeedsNewPage,
            Message = "当前页空间不足，建议写入新的一页。",
        };

        BoardLayoutDialogSpec spec = BoardLayoutDecisionPolicy.Describe(plan);

        Assert.Equal("写入新的一页", spec.PrimaryText);
        Assert.Equal(BoardLayoutChoice.NewPage, spec.PrimaryChoice);
        Assert.False(spec.CanShrink);
        Assert.True(spec.CanNewPage);
        Assert.Equal("取消", spec.CloseText);
    }

    [Fact]
    public void Describe_PlacedOrNull_HasNoWritableChoice()
    {
        BoardLayoutDialogSpec placed = BoardLayoutDecisionPolicy.Describe(new BoardLayoutPlan
        {
            Status = BoardLayoutStatus.Placed,
        });
        Assert.False(placed.CanShrink);
        Assert.False(placed.CanNewPage);
        Assert.Equal(BoardLayoutChoice.Cancel, placed.PrimaryChoice);

        BoardLayoutDialogSpec missing = BoardLayoutDecisionPolicy.Describe(null);
        Assert.False(missing.CanNewPage);
        Assert.Equal(BoardLayoutChoice.Cancel, missing.PrimaryChoice);
    }
}