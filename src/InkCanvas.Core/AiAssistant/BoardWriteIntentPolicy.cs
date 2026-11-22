namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 「整理板书 / AI 书写」的意图判定（纯逻辑）：
/// 整理要求目标页已有内容；AI 书写允许空页，不会被“无内容”拦截。
/// </summary>
public static class BoardWriteIntentPolicy
{
    public const string Organize = "organize";

    public const string Write = "write";

    /// <summary>已知意图归一化；缺失/未知一律按 write（允许空页）。</summary>
    public static string Normalize(string intent)
        => string.Equals((intent ?? "").Trim(), Organize, StringComparison.OrdinalIgnoreCase)
            ? Organize
            : Write;

    /// <summary>只有整理要求当前页有笔画/图片/媒体。</summary>
    public static bool RequiresExistingContent(string intent)
        => Normalize(intent) == Organize;

    /// <summary>是否允许发起请求：write 恒允许；organize 需目标页有内容。</summary>
    public static bool CanRequest(string intent, bool pageHasContent)
        => !RequiresExistingContent(intent) || pageHasContent;

    /// <summary>无内容时的提示（write 返回空字符串，调用方不应提示）。</summary>
    public static string EmptyPageNotice(string intent)
        => RequiresExistingContent(intent)
            ? "当前页还没有可整理的板书，请先书写或框选内容后再试。"
            : "";

    /// <summary>浮窗未填写指令时的默认指令。</summary>
    public static string DefaultInstruction(string intent)
        => RequiresExistingContent(intent) ? "整理当前页板书" : "写一句本课总结";

    /// <summary>仅整理需要附带当前页/选区截图；AI 书写允许空截图。</summary>
    public static bool ShouldAttachScreenshot(string intent, string thumbnailBase64)
        => RequiresExistingContent(intent) && !string.IsNullOrWhiteSpace(thumbnailBase64);
}