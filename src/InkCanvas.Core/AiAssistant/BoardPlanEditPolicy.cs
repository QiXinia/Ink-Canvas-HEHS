using System.Text;
using System.Text.RegularExpressions;

namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// “连续修改指令”的本地编辑策略：只调整字号/列数/行距/过滤/目标画板，
/// 不重新生成内容、不调用 AI。指令未识别或过滤后为空时返回 <c>false</c>。
/// </summary>
public static class BoardPlanEditPolicy
{
    public const double MinFontSize = 12;

    public const double MaxFontSize = 96;

    public const double FontGrowFactor = 1.2;

    public const double FontShrinkFactor = 0.85;

    public const double MinLineHeight = 1.0;

    public const double MaxLineHeight = 2.2;

    public const double LineGrowFactor = 1.15;

    public const double LineShrinkFactor = 0.9;

    public const int MinColumns = 1;

    public const int MaxColumns = 3;

    /// <summary>重排矩形时卡片之间保留的相对间距。</summary>
    public const double RectGap = 0.02;

    private static readonly Regex ExplicitFontPattern = new(
        @"(?:字号|字体|font\s*size)\s*[:：]?\s*(\d{1,3}(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] ClauseSeparators =
        { "\n", "\r", "，", ",", "；", ";", "。", "！", "!", "？", "?" };

    private static readonly string[] GrowWords =
        { "放大", "增大", "调大", "加大", "变大", "大一点", "大一些", "大点", "大些", "更大" };

    private static readonly string[] ShrinkWords =
        { "缩小", "减小", "调小", "变小", "小一点", "小一些", "小点", "小些", "更小" };

    private static readonly string[] KeepWords =
        { "只保留", "只留", "仅保留", "仅留", "只要", "仅要", "只显示", "仅显示", "保留" };

    private static readonly string[] RemoveWords =
        { "去掉", "删除", "移除", "不要", "去除", "删掉", "排除" };

    private static readonly (string[] Words, string Kind)[] KindWords =
    {
        (new[] { "公式", "方程" }, "formula"),
        (new[] { "文字", "文本", "段落" }, "text"),
        (new[] { "古诗", "诗句", "诗" }, "poem"),
        (new[] { "词语", "词汇", "词组", "词" }, "wordlist"),
    };

    /// <summary>
    /// 尝试把一条自然语言调整指令应用到板书计划。
    /// 成功返回新的计划（原计划不变），失败返回 <c>false</c> 并在 <paramref name="message"/> 说明原因。
    /// </summary>
    public static bool TryApply(
        AssistantBoardPlan plan, string instruction, out AssistantBoardPlan updated, out string message)
    {
        updated = null;
        message = "";

        if (plan is null || plan.IsEmpty)
        {
            message = "没有可调整的板书内容";
            return false;
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            message = "调整指令为空";
            return false;
        }

        string text = Normalize(instruction);
        List<AssistantBoardBlock> blocks = plan.Blocks.Select(CloneBlock).ToList();
        var changes = new List<string>();
        string targetBoard = string.IsNullOrWhiteSpace(plan.TargetBoard) ? "main" : plan.TargetBoard.Trim();
        bool columnsChanged = false;

        bool hasExplicitFont = TryReadExplicitFontSize(text, out double explicitFontSize);
        if (hasExplicitFont)
        {
            double font = ClampFont(explicitFontSize);
            ApplyFontSize(blocks, font);
            changes.Add($"字号={Format(font)}");
        }

        foreach (string clause in SplitClauses(text))
        {
            bool mentionsLine = ContainsAny(clause, "行距", "行高", "间距");
            bool mentionsFont = ContainsAny(clause, "字号", "字体");
            bool grow = ContainsAny(clause, GrowWords);
            bool shrink = ContainsAny(clause, ShrinkWords);

            if (!hasExplicitFont && (mentionsFont || (!mentionsLine && !HasOtherDirective(clause))))
            {
                if (grow)
                {
                    ApplyFontScale(blocks, FontGrowFactor);
                    changes.Add($"字号×{Format(FontGrowFactor)}");
                }
                else if (shrink)
                {
                    ApplyFontScale(blocks, FontShrinkFactor);
                    changes.Add($"字号×{Format(FontShrinkFactor)}");
                }
            }

            if (mentionsLine)
            {
                if (grow)
                {
                    ApplyLineHeightScale(blocks, LineGrowFactor);
                    changes.Add($"行距×{Format(LineGrowFactor)}");
                }
                else if (shrink)
                {
                    ApplyLineHeightScale(blocks, LineShrinkFactor);
                    changes.Add($"行距×{Format(LineShrinkFactor)}");
                }
            }

            int? columns = ReadColumns(clause);
            if (columns.HasValue)
            {
                ApplyColumns(blocks, columns.Value);
                columnsChanged = true;
                changes.Add($"列数={columns.Value}");
            }

            string align = ReadAlign(clause);
            if (!string.IsNullOrEmpty(align))
            {
                ApplyAlign(blocks, align);
                changes.Add($"对齐={align}");
            }

            if (ApplyFilter(clause, ref blocks, changes) && blocks.Count == 0)
            {
                message = "过滤后没有可保留的板书块";
                return false;
            }

            if (ContainsAny(clause, "板中板", "侧板", "副板"))
            {
                targetBoard = "side";
                changes.Add("目标=板中板");
            }
            else if (ContainsAny(clause, "主展板", "主画板", "主白板", "主板"))
            {
                targetBoard = "main";
                changes.Add("目标=主展板");
            }
        }

        if (changes.Count == 0)
        {
            message = "未识别到可执行的调整指令（支持字号/列/过滤/移动/行距）";
            return false;
        }

        if (columnsChanged)
        {
            int columns = blocks.Count > 0 ? Math.Clamp(blocks[0].Style?.Columns ?? 1, MinColumns, MaxColumns) : 1;
            ReassignRects(blocks, columns);
        }

        updated = new AssistantBoardPlan
        {
            Schema = plan.Schema,
            Intent = plan.Intent,
            TargetBoard = targetBoard,
            TargetPageIndex = plan.TargetPageIndex,
            Blocks = blocks,
            Notes = plan.Notes ?? Array.Empty<string>(),
            Originals = plan.Originals ?? Array.Empty<string>(),
            OriginalBlocks = plan.OriginalBlocks ?? Array.Empty<AssistantOriginalBlock>(),
            Confidence = plan.Confidence,
            Warnings = plan.Warnings ?? Array.Empty<string>(),
        };

        message = "已调整：" + string.Join("；", changes.Distinct())
            + $"（共 {blocks.Count} 块，未重新生成）";
        return true;
    }

    // ---- instruction parsing --------------------------------------------------

    private static string[] SplitClauses(string text)
    {
        string[] clauses = text.Split(ClauseSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(clause => clause.Trim())
            .Where(clause => clause.Length > 0)
            .ToArray();
        return clauses.Length > 0 ? clauses : new[] { text };
    }

    private static bool TryReadExplicitFontSize(string text, out double size)
    {
        size = 0;
        Match match = ExplicitFontPattern.Match(text);
        if (!match.Success) return false;
        return double.TryParse(
            match.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out size);
    }

    private static int? ReadColumns(string clause)
    {
        if (ContainsAny(clause, "三列", "3列", "三栏")) return 3;
        if (ContainsAny(clause, "两列", "双列", "二列", "2列", "两栏", "双栏")) return 2;
        if (ContainsAny(clause, "一列", "单列", "1列", "一栏")) return 1;
        return null;
    }

    private static bool HasOtherDirective(string clause)
        => ContainsAny(clause, "公式", "方程", "文字", "文本", "古诗", "诗句", "词语", "词汇", "词组")
            || ContainsAny(clause, "板中板", "主展板", "主画板")
            || ReadColumns(clause).HasValue;

    private static bool ApplyFilter(string clause, ref List<AssistantBoardBlock> blocks, List<string> changes)
    {
        bool keep = ContainsAny(clause, KeepWords);
        bool remove = ContainsAny(clause, RemoveWords);
        if (!keep && !remove) return false;

        var kinds = new List<string>();
        foreach ((string[] words, string kind) in KindWords)
        {
            if (!ContainsAny(clause, words)) continue;
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }

        if (kinds.Count == 0) return false;

        if (keep)
        {
            blocks = blocks.Where(block => kinds.Contains(NormalizeKind(block.Kind))).ToList();
            changes.Add("仅保留" + string.Join("、", kinds.Select(KindLabel)));
        }
        else
        {
            blocks = blocks.Where(block => !kinds.Contains(NormalizeKind(block.Kind))).ToList();
            changes.Add("去掉" + string.Join("、", kinds.Select(KindLabel)));
        }

        return true;
    }

    // ---- mutations ------------------------------------------------------------

    private static void ApplyFontSize(List<AssistantBoardBlock> blocks, double size)
    {
        double clamped = ClampFont(size);
        foreach (AssistantBoardBlock block in blocks)
            EnsureStyle(block).FontSize = clamped;
    }

    private static void ApplyFontScale(List<AssistantBoardBlock> blocks, double factor)
    {
        foreach (AssistantBoardBlock block in blocks)
        {
            AssistantBoardStyle style = EnsureStyle(block);
            style.FontSize = ClampFont(style.FontSize * factor);
        }
    }

    private static void ApplyLineHeightScale(List<AssistantBoardBlock> blocks, double factor)
    {
        foreach (AssistantBoardBlock block in blocks)
        {
            AssistantBoardStyle style = EnsureStyle(block);
            style.LineHeight = ClampLineHeight(style.LineHeight * factor);
        }
    }

    private static void ApplyColumns(List<AssistantBoardBlock> blocks, int columns)
    {
        int clamped = Math.Clamp(columns, MinColumns, MaxColumns);
        foreach (AssistantBoardBlock block in blocks)
            EnsureStyle(block).Columns = clamped;
    }

    private static void ApplyAlign(List<AssistantBoardBlock> blocks, string align)
    {
        foreach (AssistantBoardBlock block in blocks)
            EnsureStyle(block).Align = align;
    }

    private static string ReadAlign(string clause)
    {
        if (ContainsAny(clause, "两端对齐", "分散对齐", "justify")) return "justify";
        if (ContainsAny(clause, "右对齐", "靠右", "右端")) return "right";
        if (ContainsAny(clause, "左对齐", "靠左", "左端")) return "left";
        if (ContainsAny(clause, "居中", "中间对齐", "center")) return "center";
        return null;
    }

    private static void ReassignRects(List<AssistantBoardBlock> blocks, int columns)
    {
        if (blocks.Count == 0) return;

        int cols = Math.Clamp(columns, MinColumns, MaxColumns);
        int rows = Math.Max(1, (int)Math.Ceiling(blocks.Count / (double)cols));
        double cellWidth = 1.0 / cols;
        double cellHeight = 1.0 / rows;
        double halfGap = RectGap / 2;

        for (int index = 0; index < blocks.Count; index++)
        {
            int column = index % cols;
            int row = index / cols;
            double left = column * cellWidth + halfGap;
            double top = row * cellHeight + halfGap;
            double right = (column + 1) * cellWidth - halfGap;
            double bottom = (row + 1) * cellHeight - halfGap;
            blocks[index].Rect = new AssistantRect(left, top, right, bottom);
        }
    }

    private static AssistantBoardStyle EnsureStyle(AssistantBoardBlock block)
    {
        block.Style ??= new AssistantBoardStyle();
        return block.Style;
    }

    private static AssistantBoardBlock CloneBlock(AssistantBoardBlock source)
    {
        AssistantBoardStyle style = source.Style ?? new AssistantBoardStyle();
        return new AssistantBoardBlock
        {
            Id = source.Id ?? "",
            Kind = source.Kind ?? "text",
            Latex = source.Latex ?? "",
            Text = source.Text ?? "",
            Lines = source.Lines is { Count: > 0 } ? source.Lines.ToArray() : Array.Empty<string>(),
            Title = source.Title ?? "",
            Author = source.Author ?? "",
            Rect = source.Rect,
            Style = new AssistantBoardStyle
            {
                FontSize = style.FontSize,
                Align = style.Align ?? "left",
                Columns = style.Columns,
                LineHeight = style.LineHeight,
            },
            Note = source.Note ?? "",
            Confidence = source.Confidence,
        };
    }

    /// <summary>
    /// 解析后的连续修改意图，可同时用于预览 plan 与当前选中的原生对象。
    /// 只描述样式/过滤，不包含任何 AI 生成内容。
    /// </summary>
    public sealed class BoardObjectEdit
    {
        public double? ExplicitFontSize { get; set; }

        public double FontScale { get; set; } = 1;

        public int? Columns { get; set; }

        public double? ExplicitLineHeight { get; set; }

        public double LineHeightScale { get; set; } = 1;

        public string Align { get; set; } = "";

        public string TargetBoard { get; set; } = "";

        public HashSet<string> KeepKinds { get; } = new();

        public HashSet<string> RemoveKinds { get; } = new();

        public bool HasStyleChange => ExplicitFontSize.HasValue
            || Math.Abs(FontScale - 1) > 0.001
            || Columns.HasValue
            || ExplicitLineHeight.HasValue
            || Math.Abs(LineHeightScale - 1) > 0.001
            || !string.IsNullOrEmpty(Align);

        public bool HasFilter => KeepKinds.Count > 0 || RemoveKinds.Count > 0;

        public bool IsEmpty => !HasStyleChange && !HasFilter && string.IsNullOrWhiteSpace(TargetBoard);
    }

    /// <summary>
    /// 解析“字号/列/行距/对齐/过滤/目标画板”指令，供选中 AI 对象批量修改使用。
    /// 未识别到任何可执行项时返回 false。
    /// </summary>
    public static bool TryParseObjectEdit(
        string instruction, out BoardObjectEdit edit, out string message)
    {
        edit = null;
        message = "";
        if (string.IsNullOrWhiteSpace(instruction))
        {
            message = "调整指令为空";
            return false;
        }

        string text = Normalize(instruction);
        var result = new BoardObjectEdit();
        var changes = new List<string>();
        bool hasExplicitFont = TryReadExplicitFontSize(text, out double explicitFontSize);
        if (hasExplicitFont)
        {
            result.ExplicitFontSize = ClampFont(explicitFontSize);
            changes.Add($"字号={Format(result.ExplicitFontSize.Value)}");
        }

        foreach (string clause in SplitClauses(text))
        {
            bool mentionsLine = ContainsAny(clause, "行距", "行高", "间距");
            bool mentionsFont = ContainsAny(clause, "字号", "字体");
            bool grow = ContainsAny(clause, GrowWords);
            bool shrink = ContainsAny(clause, ShrinkWords);

            if (!hasExplicitFont && (mentionsFont || (!mentionsLine && !HasOtherDirective(clause))))
            {
                if (grow)
                {
                    result.FontScale *= FontGrowFactor;
                    changes.Add($"字号×{Format(FontGrowFactor)}");
                }
                else if (shrink)
                {
                    result.FontScale *= FontShrinkFactor;
                    changes.Add($"字号×{Format(FontShrinkFactor)}");
                }
            }

            if (mentionsLine)
            {
                if (grow)
                {
                    result.LineHeightScale *= LineGrowFactor;
                    changes.Add($"行距×{Format(LineGrowFactor)}");
                }
                else if (shrink)
                {
                    result.LineHeightScale *= LineShrinkFactor;
                    changes.Add($"行距×{Format(LineShrinkFactor)}");
                }
            }

            int? columns = ReadColumns(clause);
            if (columns.HasValue)
            {
                result.Columns = Math.Clamp(columns.Value, MinColumns, MaxColumns);
                changes.Add($"列数={result.Columns.Value}");
            }

            string align = ReadAlign(clause);
            if (!string.IsNullOrEmpty(align))
            {
                result.Align = align;
                changes.Add($"对齐={align}");
            }

            bool keep = ContainsAny(clause, KeepWords);
            bool remove = ContainsAny(clause, RemoveWords);
            if (keep || remove)
            {
                var kinds = new List<string>();
                foreach ((string[] words, string kind) in KindWords)
                {
                    if (!ContainsAny(clause, words)) continue;
                    if (!kinds.Contains(kind)) kinds.Add(kind);
                }

                if (kinds.Count > 0)
                {
                    foreach (string kind in kinds)
                    {
                        if (keep) result.KeepKinds.Add(kind);
                        if (remove) result.RemoveKinds.Add(kind);
                    }
                    changes.Add((keep ? "仅保留" : "去掉") + string.Join("、", kinds.Select(KindLabel)));
                }
            }

            if (ContainsAny(clause, "板中板", "侧板", "副板"))
            {
                result.TargetBoard = "side";
                changes.Add("目标=板中板");
            }
            else if (ContainsAny(clause, "主展板", "主画板", "主白板", "主板"))
            {
                result.TargetBoard = "main";
                changes.Add("目标=主展板");
            }
        }

        if (result.IsEmpty)
        {
            message = "未识别到可执行的调整指令（支持字号/列/过滤/行距/对齐）";
            return false;
        }

        edit = result;
        message = "已识别本地调整：" + string.Join("；", changes.Distinct());
        return true;
    }

    // ---- helpers --------------------------------------------------------------

    private static string Normalize(string text)
        => (text ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();

    private static bool ContainsAny(string clause, params string[] words)
    {
        foreach (string word in words)
        {
            if (clause.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string NormalizeKind(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "formula" => "formula",
        "wordlist" => "wordlist",
        "poem" => "poem",
        _ => "text",
    };

    private static string KindLabel(string kind) => kind switch
    {
        "formula" => "公式",
        "wordlist" => "词语",
        "poem" => "古诗",
        _ => "文字",
    };

    private static double ClampFont(double value)
    {
        if (double.IsNaN(value)) return MinFontSize;
        return Math.Clamp(value, MinFontSize, MaxFontSize);
    }

    private static double ClampLineHeight(double value)
    {
        if (double.IsNaN(value)) return MinLineHeight;
        return Math.Clamp(value, MinLineHeight, MaxLineHeight);
    }

    private static string Format(double value)
        => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}