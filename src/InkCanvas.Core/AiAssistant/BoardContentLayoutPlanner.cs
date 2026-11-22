namespace InkCanvas.Core.AiAssistant;

/// <summary>待写入展板的卡片原始尺寸（DIP，未缩放）。</summary>
public readonly record struct BoardCardSize(double Width, double Height);

/// <summary>规划器输出的单卡落点（世界坐标，DIP）。</summary>
public readonly record struct BoardCardPlacement(int Index, double Left, double Top, double Width, double Height);

/// <summary>一次板书自动落点的结果；<see cref="NeedsNewPage"/> 为 true 时不应写入当前页。</summary>
public sealed class BoardLayoutPlan
{
    public IReadOnlyList<BoardCardPlacement> Placements { get; init; } = Array.Empty<BoardCardPlacement>();

    /// <summary>实际采用的缩放比例；1.0 表示未缩小。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>当前页空间不足，调用方应提示新建页而不是覆盖原内容。</summary>
    public bool NeedsNewPage { get; init; }

    public string Message { get; init; } = "";

    public bool IsComplete => !NeedsNewPage && Placements.Count > 0;
}

/// <summary>
/// 纯逻辑的板书自动落点：在视口内按网格扫描，避开当前页已有内容（笔画/图片/媒体的包围盒），
/// 卡片之间保持 gap；空间不足时依次缩小 10%–30%，仍不足则建议新建页。
/// 不依赖 Win2D/UI，便于单元测试。
/// </summary>
public static class BoardContentLayoutPlanner
{
    public const double DefaultMargin = 32;

    public const double DefaultGap = 24;

    public const double GridStep = 24;

    private const int MaxProbes = 60000;

    private static readonly double[] ScaleSteps = { 1.0, 0.9, 0.8, 0.7 };

    /// <param name="contentBounds">当前页已有内容包围盒（世界坐标）；空页传 <see cref="AssistantRect"/> 默认值。</param>
    /// <param name="cards">待写入卡片尺寸（DIP）。</param>
    /// <param name="viewport">可视区域（世界坐标）。</param>
    public static BoardLayoutPlan Plan(
        AssistantRect contentBounds,
        IReadOnlyList<BoardCardSize> cards,
        AssistantRect viewport,
        double gap = DefaultGap,
        double margin = DefaultMargin)
    {
        if (cards is null || cards.Count == 0)
            return new BoardLayoutPlan { Message = "没有可写入的板书内容。" };

        gap = Math.Max(0, gap);
        margin = Math.Max(0, margin);

        var usable = new AssistantRect(
            viewport.Left + margin,
            viewport.Top + margin,
            viewport.Right - margin,
            viewport.Bottom - margin);
        if (usable.IsEmpty)
            return NeedsNewPage("可写入区域太小，请新建一页后再写入。");

        var obstacles = new List<AssistantRect>(1);
        if (!contentBounds.IsEmpty) obstacles.Add(contentBounds);

        foreach (double scale in ScaleSteps)
        {
            List<BoardCardPlacement> placements = TryPlace(usable, obstacles, cards, scale, gap);
            if (placements is null) continue;

            return new BoardLayoutPlan
            {
                Placements = placements,
                Scale = scale,
                Message = scale < 1.0
                    ? $"当前页空间不足，已缩小到 {Math.Round(scale * 100)}%。"
                    : "",
            };
        }

        return NeedsNewPage("当前页空间不足，请新建一页后再写入。");
    }

    private static BoardLayoutPlan NeedsNewPage(string message)
        => new() { NeedsNewPage = true, Scale = ScaleSteps[^1], Message = message };

    private static List<BoardCardPlacement> TryPlace(
        AssistantRect usable,
        List<AssistantRect> obstacles,
        IReadOnlyList<BoardCardSize> cards,
        double scale,
        double gap)
    {
        var placements = new List<BoardCardPlacement>(cards.Count);
        var occupied = new List<AssistantRect>(cards.Count + obstacles.Count);
        occupied.AddRange(obstacles);
        int probes = 0;

        for (int index = 0; index < cards.Count; index++)
        {
            double width = Math.Max(1, cards[index].Width * scale);
            double height = Math.Max(1, cards[index].Height * scale);
            if (width > usable.Width || height > usable.Height) return null;

            bool found = false;
            for (double top = usable.Top; top + height <= usable.Bottom + 0.001; top += GridStep)
            {
                for (double left = usable.Left; left + width <= usable.Right + 0.001; left += GridStep)
                {
                    if (++probes > MaxProbes) return null;

                    var candidate = new AssistantRect(left, top, left + width, top + height);
                    if (Intersects(candidate, occupied, gap)) continue;

                    placements.Add(new BoardCardPlacement(index, left, top, width, height));
                    occupied.Add(candidate);
                    found = true;
                    break;
                }

                if (found) break;
            }

            if (!found) return null;
        }

        return placements;
    }

    private static bool Intersects(AssistantRect candidate, List<AssistantRect> occupied, double gap)
    {
        double left = candidate.Left - gap;
        double top = candidate.Top - gap;
        double right = candidate.Right + gap;
        double bottom = candidate.Bottom + gap;

        foreach (AssistantRect rect in occupied)
        {
            if (left < rect.Right && right > rect.Left && top < rect.Bottom && bottom > rect.Top)
                return true;
        }

        return false;
    }
}