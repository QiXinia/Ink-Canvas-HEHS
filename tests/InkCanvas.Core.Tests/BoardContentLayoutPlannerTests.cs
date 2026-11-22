using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>板书自动落点：不重叠、避让既有内容、空间不足时缩小或建议新建页。</summary>
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
        BoardCardPlacement placement = Assert.Single(plan.Placements);
        Assert.True(placement.Top >= content.Bottom);
        Assert.True(placement.Top - content.Bottom >= BoardContentLayoutPlanner.DefaultGap - Epsilon);
    }

    [Fact]
    public void Plan_ShrinksCardsWhenPageIsTight()
    {
        var viewport = new AssistantRect(0, 0, 900, 300);
        var cards = new[]
        {
            new BoardCardSize(420, 200),
            new BoardCardSize(420, 200),
        };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport);

        Assert.True(plan.IsComplete);
        Assert.Equal(0.9, plan.Scale, 6);
        Assert.Equal(2, plan.Placements.Count);
        Assert.False(Overlaps(plan.Placements[0], plan.Placements[1]));
        Assert.Contains("90%", plan.Message);
    }

    [Fact]
    public void Plan_NeedsNewPageWhenCardsCannotFit()
    {
        var viewport = new AssistantRect(0, 0, 200, 100);
        var cards = new[] { new BoardCardSize(300, 300) };

        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0), cards, viewport);

        Assert.True(plan.NeedsNewPage);
        Assert.False(plan.IsComplete);
        Assert.Empty(plan.Placements);
        Assert.Contains("新建一页", plan.Message);
    }

    [Fact]
    public void Plan_NoCards_ReturnsEmptyPlanWithoutNewPage()
    {
        BoardLayoutPlan plan = BoardContentLayoutPlanner.Plan(
            new AssistantRect(0, 0, 0, 0),
            Array.Empty<BoardCardSize>(),
            new AssistantRect(0, 0, 100, 100));

        Assert.False(plan.IsComplete);
        Assert.False(plan.NeedsNewPage);
        Assert.Empty(plan.Placements);
    }
}