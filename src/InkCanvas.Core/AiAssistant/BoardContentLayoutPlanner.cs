namespace InkCanvas.Core.AiAssistant;

/// <summary>待写入展板的卡片原始尺寸（DIP，未缩放）。</summary>
public readonly record struct BoardCardSize(double Width, double Height);

/// <summary>规划器输出的单卡落点（世界坐标，DIP）。</summary>
public readonly record struct BoardCardPlacement(int Index, double Left, double Top, double Width, double Height);

/// <summary>
/// 自动布局安全区（world DIP）：避开展板顶部/底部悬浮工具栏与边缘。
/// 由调用方按窗口实际工具栏高度换算后传入。
/// </summary>
public readonly record struct BoardSafeInsets(double Top, double Bottom, double Left, double Right)
{
    public static BoardSafeInsets None => default;

    public bool IsZero => Top <= 0 && Bottom <= 0 && Left <= 0 && Right <= 0;
}

/// <summary>一次板书落点规划的结论。</summary>
public enum BoardLayoutStatus
{
    /// <summary>原尺寸可直接排下。</summary>
    Placed,

    /// <summary>原尺寸放不下，但缩小到 70%/50% 后可排下。</summary>
    NeedsShrink,

    /// <summary>缩小仍放不下，但当前页并非被内容占满（建议调整/清理原迹后再试）。</summary>
    NeedsMove,

    /// <summary>当前页内容已接近占满，建议写入新页。</summary>
    NeedsNewPage,
}

/// <summary>
/// 一次板书自动落点的结果。缩小可排下时 <see cref="Placements"/> 已按建议比例计算；
/// NeedsMove/NeedsNewPage 时为空，调用方应弹窗让用户选择新页或取消。
/// </summary>
public sealed class BoardLayoutPlan
{
    public BoardLayoutStatus Status { get; init; }

    public IReadOnlyList<BoardCardPlacement> Placements { get; init; } = Array.Empty<BoardCardPlacement>();

    /// <summary>实际采用的缩放比例；1.0 表示未缩小。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>当前页空间不足且被内容占满，建议新建页。</summary>
    public bool NeedsNewPage => Status == BoardLayoutStatus.NeedsNewPage;

    /// <summary>原尺寸放不下，缩小后可写入当前页。</summary>
    public bool NeedsShrink => Status == BoardLayoutStatus.NeedsShrink;

    /// <summary>缩小仍放不下，但当前页还有调整空间。</summary>
    public bool NeedsMove => Status == BoardLayoutStatus.NeedsMove;

    /// <summary>建议原因与下一步提示（始终面向用户）。</summary>
    public string Message { get; init; } = "";

    /// <summary>可选项文案，供 UI 直接展示（收起/新页/取消）。</summary>
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    /// <summary>true 表示可直接按当前 <see cref="Placements"/> 写入（含缩小方案）。</summary>
    public bool IsComplete => Status is BoardLayoutStatus.Placed or BoardLayoutStatus.NeedsShrink
        && Placements.Count > 0;
}

/// <summary>
/// 纯逻辑的板书自动落点：在视口内按网格扫描，避开当前页已有内容（笔画/图片/媒体的包围盒），
/// 卡片之间保持 gap。原尺寸失败时先尝试 70%，再尝试 50%；仍失败则根据当前页内容占比
/// 判定“建议调整位置”或“建议新页”。不依赖 Win2D/UI，便于单元测试。
/// </summary>
public static class BoardContentLayoutPlanner
{
    public const double DefaultMargin = 32;

    public const double DefaultGap = 24;

    public const double GridStep = 24;

    /// <summary>第一次缩小目标。</summary>
    public const double ShrinkFirst = 0.7;

    /// <summary>最小缩小目标。</summary>
    public const double ShrinkMinimum = 0.5;

    /// <summary>内容包围盒占可用区面积达到该比例时判定“需要新页”。</summary>
    public const double FullPageRatio = 0.6;

    /// <summary>
    /// 选中对象浮动工具栏（收起/克隆/样式/变换/层级/更多/删除）的预留高度。
    /// 工具栏位于对象底部边缘，空间不足时会翻转到上方，故上下安全区都叠加该余量。
    /// </summary>
    public const double SelectionToolbarReserve = 72;

    /// <summary>主展板默认安全区（顶部标题/悬浮栏 96 + 选中工具栏 72，底部工具栏 120 + 选中工具栏 72，左右 24）。</summary>
    public static readonly BoardSafeInsets DefaultMainBoardInsets =
        new(96 + SelectionToolbarReserve, 120 + SelectionToolbarReserve, 24, 24);

    /// <summary>板中板默认安全区（顶部 48 + 选中工具栏 72，底部工具栏 96 + 选中工具栏 72，左右 12）。</summary>
    public static readonly BoardSafeInsets DefaultSideBoardInsets =
        new(48 + SelectionToolbarReserve, 96 + SelectionToolbarReserve, 12, 12);

    private const int MaxProbes = 60000;

    /// <param name="contentBounds">当前页已有内容包围盒（世界坐标）；空页传 <see cref="AssistantRect"/> 默认值。</param>
    /// <param name="cards">待写入卡片尺寸（DIP）。</param>
    /// <param name="viewport">可视区域（世界坐标）。</param>
    /// <param name="safeInsets">顶部/底部/左右安全区（world DIP），避开展板工具栏。</param>
    public static BoardLayoutPlan Plan(
        AssistantRect contentBounds,
        IReadOnlyList<BoardCardSize> cards,
        AssistantRect viewport,
        double gap = DefaultGap,
        double margin = DefaultMargin,
        BoardSafeInsets safeInsets = default)
    {
        if (cards is null || cards.Count == 0)
            return new BoardLayoutPlan { Status = BoardLayoutStatus.Placed, Message = "没有可写入的板书内容。" };

        gap = Math.Max(0, gap);
        margin = Math.Max(0, margin);

        var usable = new AssistantRect(
            viewport.Left + margin + safeInsets.Left,
            viewport.Top + margin + safeInsets.Top,
            viewport.Right - margin - safeInsets.Right,
            viewport.Bottom - margin - safeInsets.Bottom);
        if (usable.IsEmpty)
            return NeedsMove("当前视口太小，无法排下板书；请先调整窗口或清理原迹，或写入新的一页。");

        var obstacles = new List<AssistantRect>(1);
        if (!contentBounds.IsEmpty) obstacles.Add(contentBounds);

        List<BoardCardPlacement> placements = TryPlace(usable, obstacles, cards, 1.0, gap);
        if (placements is not null) return Placed(placements, 1.0);

        placements = TryPlace(usable, obstacles, cards, ShrinkFirst, gap);
        if (placements is not null)
            return NeedsShrinkPlan(placements, ShrinkFirst,
                $"当前页空间不足，建议缩小到 {Percent(ShrinkFirst)} 后写入。");

        placements = TryPlace(usable, obstacles, cards, ShrinkMinimum, gap);
        if (placements is not null)
            return NeedsShrinkPlan(placements, ShrinkMinimum,
                $"当前页空间不足，建议缩小到 {Percent(ShrinkMinimum)} 后写入。");

        double ratio = Coverage(contentBounds, usable);
        if (!contentBounds.IsEmpty && ratio >= FullPageRatio)
            return NeedsNewPage(
                $"当前页空间不足，缩小到 {Percent(ShrinkMinimum)} 仍无法排下；建议写入新的一页。");

        return NeedsMove(
            $"当前页空间不足，缩小到 {Percent(ShrinkMinimum)} 仍无法排下；"
            + "建议先移动/清理原迹或调整字号后重试，也可以写入新的一页。");
    }

    private static BoardLayoutPlan Placed(List<BoardCardPlacement> placements, double scale) => new()
    {
        Status = BoardLayoutStatus.Placed,
        Placements = placements,
        Scale = scale,
        Options = Array.Empty<string>(),
    };

    private static BoardLayoutPlan NeedsShrinkPlan(
        List<BoardCardPlacement> placements, double scale, string message) => new()
    {
        Status = BoardLayoutStatus.NeedsShrink,
        Placements = placements,
        Scale = scale,
        Message = message,
        Options = new[] { "缩小排版写入", "写入新的一页", "取消" },
    };

    private static BoardLayoutPlan NeedsMove(string message) => new()
    {
        Status = BoardLayoutStatus.NeedsMove,
        Scale = ShrinkMinimum,
        Message = message,
        Options = new[] { "写入新的一页", "取消" },
    };

    private static BoardLayoutPlan NeedsNewPage(string message) => new()
    {
        Status = BoardLayoutStatus.NeedsNewPage,
        Scale = ShrinkMinimum,
        Message = message,
        Options = new[] { "写入新的一页", "取消" },
    };

    private static double Coverage(AssistantRect content, AssistantRect viewport)
    {
        if (content.IsEmpty || viewport.IsEmpty) return 0;

        double left = Math.Max(content.Left, viewport.Left);
        double top = Math.Max(content.Top, viewport.Top);
        double right = Math.Min(content.Right, viewport.Right);
        double bottom = Math.Min(content.Bottom, viewport.Bottom);
        double area = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        double total = viewport.Width * viewport.Height;
        if (total <= 0) return 0;
        return Math.Clamp(area / total, 0, 1);
    }

    private static string Percent(double scale) => Math.Round(scale * 100) + "%";

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