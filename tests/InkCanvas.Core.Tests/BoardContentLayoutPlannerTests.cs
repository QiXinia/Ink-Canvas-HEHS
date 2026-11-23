using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>板书自动落点：不重叠、避让既有内容、缩小/新页/移动决策与建议文案。</summary>
public sealed class BoardContentLayoutPlannerTests
{
    private const double Epsilon = 0.001;

    private static bool Overlaps(BoardCardPlacement a, BoardCardPlacement b)
        => a.Left < b.Left + b.Width - Epsilon && a.Left + a.Width > b.Left + Epsilon
           && a.Top < b.Top + b.Height - Epsilon && a.Top + a.Height > b.Top + Epsilon;

    [Fact]
    public void Plan_EmptyPage_PlacesAllCardsInsideViewportWithoutOverlap()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var cards = new[]
        {
            new BoardCardSize(300, 120),
            new BoardCardSize(300, 120),
            new BoardCardSize(300, 120),
        };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport);

        Assert.Equal(BoardLayoutStatus.Placed, plan.Status);
        Assert.True(plan.IsComplete);
        Assert.Equal(1.0, plan.Scale);
        Assert.Equal(3, plan.Placements.Count);
        for (int index = 0; index < plan.Placements.Count; index++)
        {
            BoardCardPlacement placement = plan.Placements[index];
            Assert.Equal(index, placement.Index);
            Assert.InRange(placement.Left, 32, 1000 - 32 - placement.Width + Epsilon);
            Assert.InRange(placement.Top, 32, 800 - 32 - placement.Height + Epsilon);
            for (int other = index + 1; other < plan.Placements.Count; other++)
                Assert.False(Overlaps(placement, plan.Placements[other]));
        }
    }

    [Fact]
    public void Plan_AvoidsExistingContentBounds()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var content = new AssistantRect(0, 0, 1000, 200);
        var cards = new[] { new BoardCardSize(300, 120) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(content, cards, viewport);

        Assert.True(plan.IsComplete);
        Assert.Equal(BoardLayoutStatus.Placed, plan.Status);
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.True(placement.Top >= content.Bottom);
        Assert.True(placement.Top - content.Bottom >= BoardContentLayoutPlanner.DefaultGap - Epsilon);
    }

    [Fact]
    public void Plan_TightPage_NeedsShrinkAtSeventyPercent()
    {
        var viewport = new AssistantRect(0, 0, 900, 300);
        var cards = new[]
        {
            new BoardCardSize(420, 200),
            new BoardCardSize(420, 200),
        };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport);

        Assert.Equal(BoardLayoutStatus.NeedsShrink, plan.Status);
        Assert.True(plan.NeedsShrink);
        Assert.True(plan.IsComplete);
        Assert.Equal(0.7, plan.Scale, 6);
        Assert.Equal(2, plan.Placements.Count);
        Assert.False(Overlaps(plan.Placements[0], plan.Placements[1]));
        Assert.Contains("70%", plan.Message);
        Assert.Contains("缩小排版写入", plan.Options);
        Assert.Contains("写入新的一页", plan.Options);
    }

    [Fact]
    public void Plan_TightPage_FallsBackToHalf()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var content = new AssistantRect(0, 0, 500, 800);
        var cards = new[] { new BoardCardSize(850, 200) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(content, cards, viewport);

        Assert.Equal(BoardLayoutStatus.NeedsShrink, plan.Status);
        Assert.Equal(0.5, plan.Scale, 6);
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.True(placement.Left >= content.Right);
        Assert.Contains("50%", plan.Message);
    }

    [Fact]
    public void Plan_FullPage_NeedsNewPage()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var cards = new[] { new BoardCardSize(300, 120) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(viewport, cards, viewport);

        Assert.Equal(BoardLayoutStatus.NeedsNewPage, plan.Status);
        Assert.True(plan.NeedsNewPage);
        Assert.False(plan.IsComplete);
        Assert.Empty(plan.Placements);
        Assert.Contains("新的一页", plan.Message);
        Assert.Contains("写入新的一页", plan.Options);
        Assert.Contains("取消", plan.Options);
    }

    [Fact]
    public void Plan_OversizedCardOnEmptyPage_NeedsMove()
    {
        var viewport = new AssistantRect(0, 0, 200, 100);
        var cards = new[] { new BoardCardSize(300, 300) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport);

        Assert.Equal(BoardLayoutStatus.NeedsMove, plan.Status);
        Assert.True(plan.NeedsMove);
        Assert.False(plan.IsComplete);
        Assert.Empty(plan.Placements);
        Assert.Contains("调整", plan.Message);
    }

    [Fact]
    public void Plan_ViewportTooSmall_NeedsMove()
    {
        var viewport = new AssistantRect(0, 0, 50, 50);
        var cards = new[] { new BoardCardSize(300, 120) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport);

        Assert.Equal(BoardLayoutStatus.NeedsMove, plan.Status);
        Assert.Contains("视口太小", plan.Message);
    }

    [Fact]
    public void Plan_PartiallyFullPage_ShrinkKeepsPlacementsInsideViewport()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var content = new AssistantRect(0, 0, 700, 800);
        var cards = new[] { new BoardCardSize(400, 200) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(content, cards, viewport);

        Assert.Equal(BoardLayoutStatus.NeedsShrink, plan.Status);
        Assert.True(plan.IsComplete);
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.InRange(placement.Left, 0, viewport.Right);
        Assert.InRange(placement.Top, 0, viewport.Bottom);
        Assert.True(placement.Left >= content.Right);
    }

    [Fact]
    public void Plan_NoCards_ReturnsEmptyPlanWithoutRecommendation()
    {
        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0),
            Array.Empty<BoardCardSize>(),
            new AssistantRect(0, 0, 100, 100));

        Assert.False(plan.IsComplete);
        Assert.False(plan.NeedsNewPage);
        Assert.Empty(plan.Placements);
    }

    [Fact]
    public void Plan_SafeInsets_KeepsCardsClearOfTopAndBottomBars()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var cards = new[] { new BoardCardSize(300, 120) };
        var safe = new BoardSafeInsets(96, 120, 24, 24);

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport, safeInsets: safe);

        Assert.Equal(BoardLayoutStatus.Placed, plan.Status);
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.True(placement.Left >= 24 + 32 - Epsilon);
        Assert.True(placement.Top >= 96 + 32 - Epsilon);
        Assert.True(placement.Top + placement.Height <= 800 - 120 - 32 + Epsilon);
    }

    [Fact]
    public void Plan_ZeroSafeInsets_KeepsLegacyTopMargin()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var cards = new[] { new BoardCardSize(300, 120) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport);

        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.Equal(32, placement.Top, 3);
        Assert.Equal(32, placement.Left, 3);
    }

    [Fact]
    public void Plan_SafeInsets_ShrinkPlacementStaysInsideSafeArea()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var content = new AssistantRect(0, 0, 480, 800);
        var cards = new[] { new BoardCardSize(850, 200) };
        var safe = new BoardSafeInsets(96, 120, 24, 24);

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            content, cards, viewport, safeInsets: safe);

        Assert.Equal(BoardLayoutStatus.NeedsShrink, plan.Status);
        Assert.Equal(0.5, plan.Scale, 6);
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.True(placement.Left >= content.Right + BoardContentLayoutPlanner.DefaultGap - Epsilon);
        Assert.True(placement.Top >= 96 + 32 - Epsilon);
        Assert.True(placement.Top + placement.Height <= 800 - 120 - 32 + Epsilon);
    }

    [Fact]
    public void Plan_SafeAreaTooSmall_NeedsMove()
    {
        var viewport = new AssistantRect(0, 0, 900, 400);
        var cards = new[] { new BoardCardSize(400, 200) };
        var safe = new BoardSafeInsets(100, 150, 20, 20);

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport, safeInsets: safe);

        Assert.Equal(BoardLayoutStatus.NeedsMove, plan.Status);
        Assert.Contains("调整", plan.Message);
    }

    [Fact]
    public void Plan_FullPageWithSafeInsets_NeedsNewPage()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var cards = new[] { new BoardCardSize(300, 120) };
        var safe = new BoardSafeInsets(96, 120, 24, 24);

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            viewport, cards, viewport, safeInsets: safe);

        Assert.Equal(BoardLayoutStatus.NeedsNewPage, plan.Status);
        Assert.Contains("新的一页", plan.Message);
    }

    [Fact]
    public void Plan_MainBoardSafeInsets_ReserveSelectionToolbarAtTopAndBottom()
    {
        var viewport = new AssistantRect(0, 0, 1000, 800);
        var cards = new[] { new BoardCardSize(300, 120) };
        BoardSafeInsets safe = BoardContentLayoutPlanner.DefaultMainBoardInsets;

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport, safeInsets: safe);

        Assert.Equal(BoardLayoutStatus.Placed, plan.Status);
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.True(placement.Top >= safe.Top + BoardContentLayoutPlanner.DefaultMargin - Epsilon);
        Assert.True(placement.Top + placement.Height
            <= viewport.Bottom - safe.Bottom - BoardContentLayoutPlanner.DefaultMargin + Epsilon);
        Assert.True(viewport.Bottom - (placement.Top + placement.Height)
            >= BoardContentLayoutPlanner.SelectionToolbarReserve + 120 + 32 - Epsilon);
    }

    [Fact]
    public void Plan_TightPageWithSelectionReserve_ShrinksAndKeepsBottomClearance()
    {
        var viewport = new AssistantRect(0, 0, 1000, 500);
        var cards = new[] { new BoardCardSize(300, 120) };
        BoardSafeInsets safe = BoardContentLayoutPlanner.DefaultMainBoardInsets;

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport, safeInsets: safe);

        Assert.Equal(BoardLayoutStatus.NeedsShrink, plan.Status);
        Assert.Equal(0.5, plan.Scale, 6);
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.True(placement.Top >= safe.Top + BoardContentLayoutPlanner.DefaultMargin - Epsilon);
        Assert.True(placement.Top + placement.Height
            <= viewport.Bottom - safe.Bottom - BoardContentLayoutPlanner.DefaultMargin + Epsilon);
    }

    [Fact]
    public void Plan_SideBoardSafeInsets_KeepSelectionReserve()
    {
        BoardSafeInsets safe = BoardContentLayoutPlanner.DefaultSideBoardInsets;
        Assert.Equal(48 + BoardContentLayoutPlanner.SelectionToolbarReserve, safe.Top);
        Assert.Equal(96 + BoardContentLayoutPlanner.SelectionToolbarReserve, safe.Bottom);

        var viewport = new AssistantRect(0, 0, 800, 600);
        var cards = new[] { new BoardCardSize(200, 100) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport, safeInsets: safe);

        Assert.Equal(BoardLayoutStatus.Placed, plan.Status);
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.True(placement.Top >= safe.Top + BoardContentLayoutPlanner.DefaultMargin - Epsilon);
        Assert.True(placement.Top + placement.Height
            <= viewport.Bottom - safe.Bottom - BoardContentLayoutPlanner.DefaultMargin + Epsilon);
    }
}