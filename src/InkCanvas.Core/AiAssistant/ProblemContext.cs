using System.Text.Json;

namespace InkCanvas.Core.AiAssistant;

/// <summary>拼题中的一个选区：题干、图形、条件可以分别框选后合并为一题。</summary>
public sealed class ProblemRegion
{
    public string Id { get; set; } = "";

    public string ImageBase64 { get; set; } = "";

    public AssistantRect Anchor { get; set; }

    /// <summary>该选区单独识别出的文字，缓存后切模式不重新识别。</summary>
    public string Text { get; set; } = "";

    public int Order { get; set; }

    /// <summary>selection / canvas / slide，仅用于诊断与提示。</summary>
    public string Source { get; set; } = "";

    public bool IsEmpty => Anchor.IsEmpty && string.IsNullOrEmpty(ImageBase64);
}

/// <summary>
/// 一次讲题请求的题目上下文。多个选区按 <see cref="ProblemRegion.Order"/> 排序，
/// 合并后外接矩形用于定位浮窗，第一张图保持与旧请求体的 imageBase64 兼容。
/// </summary>
public sealed class ProblemContext
{
    public int Version { get; set; } = CurrentVersion;

    public const int CurrentVersion = 1;

    public string SessionId { get; set; } = "";

    public int SlideId { get; set; }

    public int SlideIndex { get; set; }

    /// <summary>课件动画/点击阶段；0 表示未使用动画或不可读。</summary>
    public int AnimationStep { get; set; }

    public List<ProblemRegion> Regions { get; set; } = new();

    public long UpdatedAtMs { get; set; }

    public bool IsEmpty => Regions.Count == 0;

    public int Count => Regions.Count;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static ProblemContext Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            ProblemContext context = JsonSerializer.Deserialize<ProblemContext>(json, JsonOptions);
            if (context?.Regions is null) return null;
            context.Version = Math.Max(1, context.Version);
            return context;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>拼题上下文的增删排序规则；纯逻辑，便于单测与两端复用。</summary>
public static class ProblemContextPolicy
{
    public static ProblemRegion Add(
        ProblemContext context,
        ProblemRegion region,
        string id = null,
        long nowMs = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(region);

        if (region.IsEmpty) return null;

        string resolved = string.IsNullOrWhiteSpace(id)
            ? (context.Regions.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : id;

        if (context.Regions.Exists(r => string.Equals(r.Id, resolved, StringComparison.Ordinal)))
            resolved = NextFreeId(context);

        region.Id = resolved;
        region.Order = NextOrder(context);
        context.Regions.Add(region);
        context.SessionId = string.IsNullOrEmpty(context.SessionId)
            ? Guid.NewGuid().ToString("N")
            : context.SessionId;
        context.UpdatedAtMs = nowMs;
        return region;
    }

    public static bool Remove(ProblemContext context, string id, long nowMs = 0)
    {
        if (context is null || string.IsNullOrWhiteSpace(id)) return false;

        int index = context.Regions.FindIndex(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;

        context.Regions.RemoveAt(index);
        Reindex(context);
        context.UpdatedAtMs = nowMs;
        return true;
    }

    /// <summary>按 --- 顺序移动；delta 为 -1 上移、1 下移，越界时不动。</summary>
    public static bool Move(ProblemContext context, string id, int delta, long nowMs = 0)
    {
        if (context is null || string.IsNullOrWhiteSpace(id) || delta == 0) return false;

        int index = context.Regions.FindIndex(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;

        int target = index + delta;
        if (target < 0 || target >= context.Regions.Count) return false;

        ProblemRegion item = context.Regions[index];
        context.Regions.RemoveAt(index);
        context.Regions.Insert(target, item);
        Reindex(context);
        context.UpdatedAtMs = nowMs;
        return true;
    }

    public static IReadOnlyList<ProblemRegion> Ordered(ProblemContext context)
    {
        if (context?.Regions is null || context.Regions.Count == 0)
            return Array.Empty<ProblemRegion>();

        return context.Regions
            .OrderBy(r => r.Order)
            .ToList();
    }

    /// <summary>所有选区的外接矩形；空上下文返回空矩形。</summary>
    public static AssistantRect CombinedAnchor(ProblemContext context)
    {
        if (context?.Regions is null || context.Regions.Count == 0) return default;

        double left = double.MaxValue;
        double top = double.MaxValue;
        double right = double.MinValue;
        double bottom = double.MinValue;
        bool any = false;

        foreach (ProblemRegion region in context.Regions)
        {
            if (region.Anchor.IsEmpty) continue;
            any = true;
            left = Math.Min(left, region.Anchor.Left);
            top = Math.Min(top, region.Anchor.Top);
            right = Math.Max(right, region.Anchor.Right);
            bottom = Math.Max(bottom, region.Anchor.Bottom);
        }

        return any ? new AssistantRect(left, top, right, bottom) : default;
    }

    /// <summary>当前主要图片（排序后的第一张），保持旧请求体兼容。</summary>
    public static string PrimaryImage(ProblemContext context)
    {
        IReadOnlyList<ProblemRegion> ordered = Ordered(context);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!string.IsNullOrEmpty(ordered[i].ImageBase64)) return ordered[i].ImageBase64;
        }

        return "";
    }

    /// <summary>
    /// 与旧上下文比较，判断是否换了题目（例如翻页后误用旧选区）。
    /// 返回提示文字，null 表示无需提示。
    /// </summary>
    public static string DescribeSlideChange(ProblemContext context, int slideId, int slideIndex)
        => DescribeSlideChange(context, slideId, slideIndex, context?.AnimationStep ?? 0);

    /// <summary>
    /// 带动画阶段的变更提示：同页但动画阶段变化时也提醒，避免不同阶段的讲解状态混用。
    /// </summary>
    public static string DescribeSlideChange(
        ProblemContext context, int slideId, int slideIndex, int animationStep)
    {
        if (context is null || context.IsEmpty) return null;
        if (slideId == 0 && slideIndex == 0) return null;

        bool sameSlide = context.SlideId == slideId && context.SlideIndex == slideIndex;
        if (sameSlide && context.AnimationStep == animationStep) return null;

        if (sameSlide)
        {
            return "课件已进入新的动画阶段（" + animationStep + "），当前题目仍来自上一阶段；"
                + "可继续讲题，或重新框选当前画面并替换选区。";
        }

        return "课件已翻到第 " + slideIndex + " 页，当前题目仍来自旧页；"
            + "可继续讲题，或重新框选当前页并替换选区。";
    }

    private static string NextFreeId(ProblemContext context)
    {
        int candidate = 1;
        while (context.Regions.Exists(r => string.Equals(r.Id,
                   candidate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)))
        {
            candidate++;
        }

        return candidate.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int NextOrder(ProblemContext context)
    {
        int max = 0;
        foreach (ProblemRegion region in context.Regions) max = Math.Max(max, region.Order);
        return max + 1;
    }

    private static void Reindex(ProblemContext context)
    {
        for (int i = 0; i < context.Regions.Count; i++) context.Regions[i].Order = i + 1;
    }
}