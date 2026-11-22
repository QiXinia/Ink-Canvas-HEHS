using System.Text.Json;
using InkCanvas.Core.AiAssistant;
using Microsoft.UI.Xaml;

namespace InkCanvas.Features.AiAssistant;

/// <summary>单张待写入展板的 AI 板书卡片（PNG + DIP 尺寸 + 可编辑元数据）。</summary>
public sealed class BoardPlanCardDraft
{
    public AssistantBoardBlock Block { get; init; }

    public string Name { get; init; } = "AI 板书";

    public byte[] Png { get; init; } = Array.Empty<byte>();

    public double Width { get; init; }

    public double Height { get; init; }

    public string Metadata { get; init; } = "";

    public bool IsValid => Png is { Length: > 0 } && Width > 0 && Height > 0;
}

public sealed class BoardPlanRenderResult
{
    public IReadOnlyList<BoardPlanCardDraft> Cards { get; init; } = Array.Empty<BoardPlanCardDraft>();

    public string Error { get; init; } = "";

    public bool Success => Cards.Count > 0 && string.IsNullOrEmpty(Error);
}

/// <summary>
/// 把结构化 <see cref="AssistantBoardPlan"/> 渲染为多张 <see cref="BoardImageAsset"/>：
/// 公式优先 <see cref="BoardFormulaRenderer"/>（LaTeX → MathML → 位图），转换或渲染失败时
/// 回退 <see cref="BoardTextCardRenderer"/> 并保留 LaTeX 源；古诗/词语/段落按块类型排版。
/// </summary>
public static class BoardPlanWriter
{
    public const string FormulaStageNote = "阶段 2：LaTeX 排版，失败时回退文本";

    private const int FormulaMaxWidthDip = 560;

    private static readonly JsonSerializerOptions StyleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Task<BoardPlanRenderResult> RenderAsync(
        AssistantBoardPlan plan, string instruction, long? nowMs = null)
        => RenderAsync(plan, instruction, nowMs, null);

    /// <summary>
    /// <paramref name="formulaHost"/> 必须是前台窗口已挂载的根元素；
    /// 为空或公式渲染失败时公式块回退为文本卡片。
    /// </summary>
    public static async Task<BoardPlanRenderResult> RenderAsync(
        AssistantBoardPlan plan, string instruction, long? nowMs, FrameworkElement formulaHost)
    {
        if (plan is null || plan.IsEmpty)
            return new BoardPlanRenderResult { Error = "板书计划为空" };

        long createdAt = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cards = new List<BoardPlanCardDraft>(plan.Blocks.Count);

        for (int index = 0; index < plan.Blocks.Count; index++)
        {
            AssistantBoardBlock block = plan.Blocks[index];
            BoardPlanCardDraft card = await RenderBlockAsync(block, plan, instruction, createdAt, formulaHost);
            if (!card.IsValid)
            {
                return new BoardPlanRenderResult
                {
                    Error = $"第 {index + 1} 块（{KindLabel(block.Kind)}）渲染失败",
                };
            }

            cards.Add(card);
        }

        return new BoardPlanRenderResult { Cards = cards };
    }

    private static async Task<BoardPlanCardDraft> RenderBlockAsync(
        AssistantBoardBlock block,
        AssistantBoardPlan plan,
        string instruction,
        long createdAt,
        FrameworkElement formulaHost)
    {
        string kind = string.IsNullOrWhiteSpace(block.Kind) ? "text" : block.Kind.Trim().ToLowerInvariant();
        string name = "AI 板书·" + KindLabel(kind);

        if (kind == "formula")
            return await RenderFormulaAsync(block, plan, instruction, createdAt, formulaHost, name);

        var spec = new BoardTextCardSpec
        {
            Kind = kind,
            Title = block.Title ?? "",
            Subtitle = kind == "poem" ? block.Author ?? "" : "",
            Text = block.Text ?? "",
            Lines = block.Lines ?? Array.Empty<string>(),
            FontSize = block.Style?.FontSize ?? 24,
            LineHeight = block.Style?.LineHeight ?? 1.4,
            Align = block.Style?.Align ?? (kind == "poem" ? "center" : "left"),
            Columns = block.Style?.Columns ?? 1,
        };

        BoardTextCardRenderResult rendered = await BoardTextCardRenderer.RenderAsync(spec);
        return new BoardPlanCardDraft
        {
            Block = block,
            Name = name,
            Png = rendered.Png,
            Width = rendered.Width,
            Height = rendered.Height,
            Metadata = BuildMetadata(block, plan, instruction, createdAt, rendered.Truncated ? "内容过长已截断" : ""),
        };
    }

    private static async Task<BoardPlanCardDraft> RenderFormulaAsync(
        AssistantBoardBlock block,
        AssistantBoardPlan plan,
        string instruction,
        long createdAt,
        FrameworkElement formulaHost,
        string name)
    {
        string latex = block.Latex ?? "";
        BoardFormulaRenderResult formula = await BoardFormulaRenderer.RenderPngAsync(
            latex, FormulaMaxWidthDip, 2.0, formulaHost);

        if (formula.Success)
        {
            return new BoardPlanCardDraft
            {
                Block = block,
                Name = name,
                Png = formula.Png,
                Width = formula.Width,
                Height = formula.Height,
                Metadata = BuildMetadata(block, plan, instruction, createdAt, ""),
            };
        }

        // 公式渲染不可用（非 UI 线程/无前台宿主/转换失败）：回退文本并保留 LaTeX 源。
        string reason = string.IsNullOrWhiteSpace(formula.Error) ? "公式渲染不可用" : formula.Error;
        InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 板书公式回退文本：{reason}，latex={ShortenLatex(latex)}",
            InkCanvas.Core.Diagnostics.LogType.Trace);
        var fallbackSpec = new BoardTextCardSpec
        {
            Kind = "text",
            Title = "公式（LaTeX）",
            Text = string.IsNullOrWhiteSpace(latex) ? "（空公式）" : latex,
            FontSize = block.Style?.FontSize ?? 22,
            LineHeight = block.Style?.LineHeight ?? 1.5,
            Align = "left",
        };

        BoardTextCardRenderResult fallback = await BoardTextCardRenderer.RenderAsync(fallbackSpec);
        return new BoardPlanCardDraft
        {
            Block = block,
            Name = name,
            Png = fallback.Png,
            Width = fallback.Width,
            Height = fallback.Height,
            Metadata = BuildMetadata(
                block, plan, instruction, createdAt,
                $"LaTeX 排版回退为文本：{reason}"),
        };
    }

    private static string BuildMetadata(
        AssistantBoardBlock block,
        AssistantBoardPlan plan,
        string instruction,
        long createdAt,
        string extraNote)
    {
        var lines = new List<string>();
        if (block.Lines is { Count: > 0 }) lines.AddRange(block.Lines);
        else if (!string.IsNullOrWhiteSpace(block.Text))
            lines.AddRange(block.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

        AssistantBoardStyle style = block.Style ?? new AssistantBoardStyle();
        string styleJson;
        try
        {
            styleJson = JsonSerializer.Serialize(style, StyleJsonOptions);
        }
        catch (Exception)
        {
            styleJson = "";
        }

        List<string> notes = BuildNotes(block, plan);
        if (!string.IsNullOrWhiteSpace(extraNote) && !notes.Contains(extraNote))
            notes.Add(extraNote);

        return new AiPasteMetadata
        {
            Kind = AiPasteKind.BoardPlan,
            BlockKind = string.IsNullOrWhiteSpace(block.Kind) ? "text" : block.Kind.Trim().ToLowerInvariant(),
            Title = block.Title ?? "",
            Author = block.Author ?? "",
            Schema = plan.Schema,
            Latex = block.Latex ?? "",
            Text = block.Text ?? "",
            Lines = lines,
            StyleJson = styleJson,
            Instruction = instruction ?? "",
            CreatedAt = createdAt,
            Notes = notes,
        }.Serialize();
    }

    private static List<string> BuildNotes(AssistantBoardBlock block, AssistantBoardPlan plan)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(block.Note)) notes.Add(block.Note.Trim());

        foreach (string note in plan.Notes ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(note)) continue;
            string trimmed = note.Trim();
            if (!notes.Contains(trimmed)) notes.Add(trimmed);
        }

        return notes;
    }

    private static string ShortenLatex(string latex)
    {
        if (string.IsNullOrEmpty(latex)) return "(空)";
        return latex.Length <= 120 ? latex : latex[..120] + "…";
    }

    private static string KindLabel(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "formula" => "公式",
        "wordlist" => "词语",
        "poem" => "古诗",
        _ => "文本",
    };
}