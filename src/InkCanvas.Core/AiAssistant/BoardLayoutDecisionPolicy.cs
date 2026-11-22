namespace InkCanvas.Core.AiAssistant;

/// <summary>空间不足弹窗的用户选择。</summary>
public enum BoardLayoutChoice
{
    Shrink,
    NewPage,
    Cancel,
}

/// <summary>弹窗的纯逻辑描述，Shell 只负责把它映射到 ContentDialog。</summary>
public sealed class BoardLayoutDialogSpec
{
    public string Title { get; init; } = "当前页空间不足";

    public string Message { get; init; } = "";

    public string PrimaryText { get; init; } = "";

    public string SecondaryText { get; init; } = "";

    public string CloseText { get; init; } = "取消";

    public BoardLayoutChoice PrimaryChoice { get; init; } = BoardLayoutChoice.Cancel;

    public BoardLayoutChoice SecondaryChoice { get; init; } = BoardLayoutChoice.Cancel;

    public bool CanShrink
        => PrimaryChoice == BoardLayoutChoice.Shrink || SecondaryChoice == BoardLayoutChoice.Shrink;

    public bool CanNewPage
        => PrimaryChoice == BoardLayoutChoice.NewPage || SecondaryChoice == BoardLayoutChoice.NewPage;
}

/// <summary>
/// 把规划结论映射为“缩小排版 / 写入新页 / 取消”的 UI 选项。
/// 缩小不可行时（NeedsMove/NeedsNewPage）只提供新页与取消，并保留调整位置建议。
/// </summary>
public static class BoardLayoutDecisionPolicy
{
    public static BoardLayoutDialogSpec Describe(BoardLayoutPlan plan)
    {
        if (plan is null)
            return new BoardLayoutDialogSpec { Message = "当前页空间不足，请重试。" };

        return plan.Status switch
        {
            BoardLayoutStatus.NeedsShrink => new BoardLayoutDialogSpec
            {
                Title = "当前页空间不足",
                Message = plan.Message,
                PrimaryText = "缩小排版写入",
                SecondaryText = "写入新的一页",
                CloseText = "取消",
                PrimaryChoice = BoardLayoutChoice.Shrink,
                SecondaryChoice = BoardLayoutChoice.NewPage,
            },
            BoardLayoutStatus.NeedsNewPage => new BoardLayoutDialogSpec
            {
                Title = "当前页空间不足",
                Message = plan.Message,
                PrimaryText = "写入新的一页",
                SecondaryText = "",
                CloseText = "取消",
                PrimaryChoice = BoardLayoutChoice.NewPage,
            },
            BoardLayoutStatus.NeedsMove => new BoardLayoutDialogSpec
            {
                Title = "当前页空间不足",
                Message = plan.Message,
                PrimaryText = "写入新的一页",
                SecondaryText = "",
                CloseText = "取消",
                PrimaryChoice = BoardLayoutChoice.NewPage,
            },
            _ => new BoardLayoutDialogSpec
            {
                Message = "",
                PrimaryText = "",
                SecondaryText = "",
                CloseText = "取消",
                PrimaryChoice = BoardLayoutChoice.Cancel,
            },
        };
    }
}