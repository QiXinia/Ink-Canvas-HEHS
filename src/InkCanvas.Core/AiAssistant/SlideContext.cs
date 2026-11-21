using System.Text;

namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 课件上下文：标题、当前页文本与必要的相邻页文本。只作为辅助信息随请求发送，
/// 不得覆盖用户实际框选内容；上下文缺失时应允许用户补充框选。
/// </summary>
public sealed class SlideContext
{
    public const int CurrentVersion = 1;

    public const int MaxCurrentChars = 1200;

    public const int MaxNeighbourChars = 400;

    public int Version { get; set; } = CurrentVersion;

    public int SlideId { get; set; }

    public int SlideIndex { get; set; }

    public int AnimationStep { get; set; }

    public string Title { get; set; } = "";

    public string CurrentText { get; set; } = "";

    public string PreviousText { get; set; } = "";

    public string NextText { get; set; } = "";

    public string SelectionText { get; set; } = "";

    public bool HasText => !string.IsNullOrWhiteSpace(CurrentText)
        || !string.IsNullOrWhiteSpace(SelectionText);

    /// <summary>服务端提示词片段；明确标注「辅助信息」并保留用户选区优先级。</summary>
    public string ToPrompt()
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(Title)) builder.AppendLine("课件标题：" + Title.Trim());
        if (SlideIndex > 0)
        {
            builder.Append("当前页：第 ").Append(SlideIndex).Append(" 页");
            if (AnimationStep > 0) builder.Append("（动画阶段 ").Append(AnimationStep).Append('）');
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(CurrentText))
            builder.AppendLine("当前页文本：" + Trim(CurrentText, MaxCurrentChars));
        if (!string.IsNullOrWhiteSpace(PreviousText))
            builder.AppendLine("上一页文本（仅供参考）：" + Trim(PreviousText, MaxNeighbourChars));
        if (!string.IsNullOrWhiteSpace(NextText))
            builder.AppendLine("下一页文本（仅供参考）：" + Trim(NextText, MaxNeighbourChars));

        builder.AppendLine("约束：以上课件文本仅为辅助上下文，题目内容以用户框选的图片与墨迹为准；"
            + "框选内容与课件文本冲突时以框选内容为准。");
        return builder.ToString().TrimEnd();
    }

    private static string Trim(string value, int max)
    {
        string text = value?.Trim() ?? "";
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }
}

public static class SlideContextPolicy
{
    public static SlideContext Build(
        string title,
        int slideId,
        int slideIndex,
        string currentText,
        string previousText = "",
        string nextText = "",
        int animationStep = 0)
    {
        return new SlideContext
        {
            Title = title ?? "",
            SlideId = slideId,
            SlideIndex = slideIndex,
            AnimationStep = animationStep,
            CurrentText = currentText ?? "",
            PreviousText = previousText ?? "",
            NextText = nextText ?? "",
        };
    }

    /// <summary>用户补充框选后，把识别文字附在上下文里，但不替换框选图片。</summary>
    public static SlideContext WithSelection(SlideContext context, string selectionText)
    {
        if (context is null) return null;
        context.SelectionText = selectionText ?? "";
        return context;
    }

    public static bool IsUsable(SlideContext context)
        => context is not null && context.HasText;
}