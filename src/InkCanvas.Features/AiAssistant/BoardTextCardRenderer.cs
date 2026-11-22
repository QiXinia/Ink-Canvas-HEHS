using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace InkCanvas.Features.AiAssistant;

/// <summary>文本/词语/古诗卡片的内容与排版参数；尺寸单位为 DIP。</summary>
public sealed class BoardTextCardSpec
{
    /// <summary>排版类型：text（段落/总结）、wordlist（词语列表）、poem（古诗）。</summary>
    public string Kind { get; set; } = "text";

    public string Title { get; set; } = "";

    public string Subtitle { get; set; } = "";

    public string Text { get; set; } = "";

    public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();

    public double FontSize { get; set; } = 24;

    public double LineHeight { get; set; } = 1.4;

    public string Align { get; set; } = "left";

    public int Columns { get; set; } = 1;

    /// <summary>卡片内容高度上限（DIP）；超出时按 1.0→0.7 缩放，仍超则截断并标注。</summary>
    public double MaxHeight { get; set; } = 900;

    /// <summary>true 时透明底（适合深色展板），默认白底。</summary>
    public bool TransparentBackground { get; set; }
}

public sealed class BoardTextCardRenderResult
{
    public byte[] Png { get; set; } = Array.Empty<byte>();

    /// <summary>卡片在展板上的 DIP 宽度。</summary>
    public double Width { get; set; }

    /// <summary>卡片在展板上的 DIP 高度。</summary>
    public double Height { get; set; }

    public string Error { get; set; } = "";

    /// <summary>true 表示因内容超高而截断，PNG 末尾已标注。</summary>
    public bool Truncated { get; set; }

    public bool Success => Png is { Length: > 0 } && Width > 0 && Height > 0;
}

/// <summary>
/// AI 板书文本卡渲染器：Win2D <see cref="CanvasTextLayout"/> 真实测量，支持
/// 古诗（标题/作者/逐行居中）、词语列表（词条/拼音/释义，1-3 列）与段落（h1/h2、项目符号）。
/// 内容超高时按 1.0-0.7 缩放字号；仍溢出则截断并标注。按 2x DPI 渲染，长边不超过 8192 像素。
/// </summary>
public static class BoardTextCardRenderer
{
    private const double Padding = 16;
    private const double ColumnGap = 18;
    private const double HeaderGap = 10;
    private const double MinCardWidth = 180;
    private const double MaxCardWidth = 560;
    private const double DesiredRenderScale = 2.0;
    private const int MaxPixels = 8192;
    private const float LayoutMaxHeight = 100000f;
    private const int MaxPoemLines = 8;
    private const int MaxTruncateAttempts = 24;
    private const string TruncationNote = "（内容过长，已截断）";

    private static readonly double[] FontScales = { 1.0, 0.9, 0.8, 0.7 };

    private static readonly Color LightBackground = Color.FromArgb(255, 250, 250, 250);
    private static readonly Color LightBorder = Color.FromArgb(255, 220, 220, 224);
    private static readonly Color DarkTextColor = Color.FromArgb(255, 32, 32, 36);
    private static readonly Color LightTextColor = Color.FromArgb(255, 245, 245, 248);
    private static readonly string[] ColumnSeparators = { "｜", "|" };

    public static async Task<BoardTextCardRenderResult> RenderAsync(BoardTextCardSpec spec)
    {
        try
        {
            return await RenderCoreAsync(spec);
        }
        catch (Exception ex)
        {
            return new BoardTextCardRenderResult { Error = ex.Message ?? "文本卡片渲染失败" };
        }
    }

    private static async Task<BoardTextCardRenderResult> RenderCoreAsync(BoardTextCardSpec spec)
    {
        if (spec is null) return new BoardTextCardRenderResult { Error = "卡片参数为空" };

        string kind = NormalizeKind(spec.Kind);
        double baseFontSize = Clamp(spec.FontSize > 0 ? spec.FontSize : 24, 10, 96);
        double lineHeight = Clamp(spec.LineHeight > 0 ? spec.LineHeight : 1.4, 1.0, 3.0);
        int requestedColumns = Math.Clamp(spec.Columns <= 0 ? 1 : spec.Columns, 1, 3);
        double maxHeight = spec.MaxHeight > 0 ? spec.MaxHeight : 900;

        List<LineModel> models = BuildLineModels(spec, kind);
        bool hasHeader = !string.IsNullOrWhiteSpace(spec.Title) || !string.IsNullOrWhiteSpace(spec.Subtitle);
        if (models.Count == 0 && !hasHeader)
            return new BoardTextCardRenderResult { Error = "卡片没有可显示的文字" };

        var device = CanvasDevice.GetSharedDevice();
        Attempt attempt = null;
        try
        {
            foreach (double fontScale in FontScales)
            {
                attempt = TryLayout(
                    device, spec, kind, models, baseFontSize * fontScale, lineHeight, requestedColumns, maxHeight);
                if (attempt.CardHeight <= maxHeight || fontScale <= 0.7) break;

                attempt.Dispose();
                attempt = null;
            }

            if (attempt is null)
                return new BoardTextCardRenderResult { Error = "卡片排版失败" };

            bool truncated = false;
            if (attempt.CardHeight > maxHeight)
            {
                truncated = TryTruncate(
                    device, spec, kind, models, baseFontSize * 0.7, lineHeight, requestedColumns, maxHeight, out attempt);
                if (attempt is null)
                    return new BoardTextCardRenderResult { Error = "卡片内容过长且无法截断" };
            }

            return await DrawAsync(spec, attempt, truncated);
        }
        finally
        {
            attempt?.Dispose();
        }
    }

    // ---- layout ---------------------------------------------------------------

    private sealed class LineModel
    {
        public string Text = "";
        public double FontScale = 1.0;
        public bool Bold;
        public int BoldLength;
        public int SmallStart = -1;
        public int SmallLength;
        public double SmallScale = 0.8;
    }

    private sealed class Attempt : IDisposable
    {
        public CanvasTextLayout TitleLayout;
        public CanvasTextLayout SubtitleLayout;
        public double TitleFontSize;
        public double SubtitleFontSize;
        public double FontSize;
        public CanvasHorizontalAlignment BodyAlignment;
        public int Columns;
        public int RowsPerColumn;
        public double CardWidth;
        public double ColumnWidth;
        public double BodyTop;
        public double CardHeight;
        public List<CanvasTextLayout> BodyLayouts = new();
        public List<double> BodySteps = new();
        public double[] ColumnHeights = Array.Empty<double>();

        public void Dispose()
        {
            TitleLayout?.Dispose();
            TitleLayout = null;
            SubtitleLayout?.Dispose();
            SubtitleLayout = null;
            foreach (CanvasTextLayout layout in BodyLayouts) layout.Dispose();
            BodyLayouts.Clear();
        }
    }

    private static List<LineModel> BuildLineModels(BoardTextCardSpec spec, string kind)
    {
        var models = new List<LineModel>();
        if (kind == "wordlist") return BuildWordListModels(spec);
        if (kind == "poem") return BuildPoemModels(spec);

        var rawLines = new List<string>();
        if (spec.Lines is { Count: > 0 })
            rawLines.AddRange(spec.Lines.Select(line => line ?? ""));
        else if (!string.IsNullOrWhiteSpace(spec.Text))
            rawLines.AddRange(spec.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

        foreach (string raw in rawLines)
        {
            string line = (raw ?? "").TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                models.Add(new LineModel { Text = line[2..].Trim(), FontScale = 1.45, Bold = true, BoldLength = int.MaxValue });
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                models.Add(new LineModel { Text = line[3..].Trim(), FontScale = 1.25, Bold = true, BoldLength = int.MaxValue });
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
                || line.StartsWith("• ", StringComparison.Ordinal))
            {
                models.Add(new LineModel { Text = "• " + line[2..].Trim(), FontScale = 1.0 });
                continue;
            }

            models.Add(new LineModel { Text = line });
        }

        return models;
    }

    private static List<LineModel> BuildPoemModels(BoardTextCardSpec spec)
    {
        var models = new List<LineModel>();
        var lines = new List<string>();
        if (spec.Lines is { Count: > 0 })
            lines.AddRange(spec.Lines.Select(line => line ?? ""));
        else if (!string.IsNullOrWhiteSpace(spec.Text))
            lines.AddRange(spec.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

        foreach (string line in lines)
        {
            string trimmed = (line ?? "").Trim();
            if (trimmed.Length == 0) continue;
            if (models.Count >= MaxPoemLines) break;
            models.Add(new LineModel { Text = trimmed });
        }

        if (lines.Count(line => !string.IsNullOrWhiteSpace(line)) > MaxPoemLines)
            models.Add(new LineModel { Text = TruncationNote, FontScale = 0.72 });

        return models;
    }

    private static List<LineModel> BuildWordListModels(BoardTextCardSpec spec)
    {
        var models = new List<LineModel>();
        var lines = new List<string>();
        if (spec.Lines is { Count: > 0 })
            lines.AddRange(spec.Lines.Select(line => line ?? ""));
        else if (!string.IsNullOrWhiteSpace(spec.Text))
            lines.AddRange(spec.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

        foreach (string raw in lines)
        {
            string line = (raw ?? "").Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(ColumnSeparators, StringSplitOptions.None)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();

            if (parts.Length == 0) continue;
            if (parts.Length == 1)
            {
                models.Add(new LineModel { Text = parts[0], Bold = true, BoldLength = parts[0].Length });
                continue;
            }

            string term = parts[0];
            string pinyin = parts.Length > 1 ? parts[1] : "";
            string meaning = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : "";

            string text = meaning.Length == 0
                ? term + "  " + pinyin
                : term + "　" + pinyin + "　" + meaning;

            var model = new LineModel { Text = text, Bold = true, BoldLength = term.Length };
            if (pinyin.Length > 0)
            {
                model.SmallStart = term.Length + 1;
                model.SmallLength = pinyin.Length;
                model.SmallScale = 0.78;
            }

            models.Add(model);
        }

        return models;
    }

    private static Attempt TryLayout(
        CanvasDevice device,
        BoardTextCardSpec spec,
        string kind,
        List<LineModel> models,
        double fontSize,
        double lineHeight,
        int requestedColumns,
        double maxHeight)
    {
        CanvasHorizontalAlignment headerAlignment = kind == "poem"
            ? CanvasHorizontalAlignment.Center
            : ParseAlignment(spec.Align, CanvasHorizontalAlignment.Center);
        CanvasHorizontalAlignment bodyAlignment = kind == "poem"
            ? CanvasHorizontalAlignment.Center
            : ParseAlignment(spec.Align, CanvasHorizontalAlignment.Left);

        double widest = 0;
        foreach (LineModel model in models)
            widest = System.Math.Max(widest, MeasureApproxWidth(model.Text, fontSize * model.FontScale));

        if (!string.IsNullOrWhiteSpace(spec.Title))
            widest = System.Math.Max(widest, MeasureApproxWidth(spec.Title, fontSize * 1.35));
        if (!string.IsNullOrWhiteSpace(spec.Subtitle))
            widest = System.Math.Max(widest, MeasureApproxWidth(spec.Subtitle, fontSize * 0.82));

        int columns = requestedColumns;
        double cardWidth = widest / System.Math.Max(1, columns) + ColumnGap * (columns - 1) + Padding * 2;
        cardWidth = Clamp(cardWidth, MinCardWidth, MaxCardWidth);

        double columnWidth = (cardWidth - Padding * 2 - ColumnGap * (columns - 1)) / columns;
        if (columns > 1 && columnWidth < fontSize * 3.2)
        {
            columns = 1;
            columnWidth = cardWidth - Padding * 2;
        }

        int rowsPerColumn = columns <= 1
            ? System.Math.Max(1, models.Count)
            : (int)System.Math.Ceiling(models.Count / (double)columns);

        var attempt = new Attempt
        {
            FontSize = fontSize,
            Columns = columns,
            RowsPerColumn = rowsPerColumn,
            CardWidth = cardWidth,
            ColumnWidth = columnWidth,
            BodyAlignment = bodyAlignment,
            ColumnHeights = new double[columns],
        };

        using var titleFormat = new CanvasTextFormat
        {
            FontFamily = "Microsoft YaHei UI",
            FontSize = (float)(fontSize * 1.35),
            FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.Wrap,
            HorizontalAlignment = headerAlignment,
        };
        using var subtitleFormat = new CanvasTextFormat
        {
            FontFamily = "Microsoft YaHei UI",
            FontSize = (float)(fontSize * 0.82),
            WordWrapping = CanvasWordWrapping.Wrap,
            HorizontalAlignment = headerAlignment,
        };
        using var bodyFormat = new CanvasTextFormat
        {
            FontFamily = "Microsoft YaHei UI",
            FontSize = (float)fontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
            HorizontalAlignment = bodyAlignment,
        };

        attempt.TitleFontSize = fontSize * 1.35;
        attempt.SubtitleFontSize = fontSize * 0.82;

        if (!string.IsNullOrWhiteSpace(spec.Title))
            attempt.TitleLayout = new CanvasTextLayout(
                device, spec.Title, titleFormat, (float)(cardWidth - Padding * 2), LayoutMaxHeight);

        if (!string.IsNullOrWhiteSpace(spec.Subtitle))
            attempt.SubtitleLayout = new CanvasTextLayout(
                device, spec.Subtitle, subtitleFormat, (float)(cardWidth - Padding * 2), LayoutMaxHeight);

        double lineStep = fontSize * (kind == "poem" ? System.Math.Max(lineHeight, 1.7) : lineHeight);

        for (int index = 0; index < models.Count; index++)
        {
            LineModel model = models[index];
            float modelFontSize = (float)(fontSize * model.FontScale);
            string text = string.IsNullOrEmpty(model.Text) ? " " : model.Text;
            var layout = new CanvasTextLayout(
                device, text, bodyFormat, (float)columnWidth, LayoutMaxHeight);

            if (System.Math.Abs(model.FontScale - 1.0) > 0.001)
                layout.SetFontSize(0, text.Length, modelFontSize);

            if (model.Bold)
            {
                int boldLength = model.BoldLength == int.MaxValue
                    ? text.Length
                    : System.Math.Min(model.BoldLength, text.Length);
                if (boldLength > 0)
                    layout.SetFontWeight(0, boldLength, FontWeights.SemiBold);
            }

            if (model.SmallStart >= 0 && model.SmallLength > 0 && model.SmallStart < text.Length)
            {
                int length = System.Math.Min(model.SmallLength, text.Length - model.SmallStart);
                layout.SetFontSize(model.SmallStart, length, (float)(modelFontSize * model.SmallScale));
            }

            attempt.BodyLayouts.Add(layout);
            double step = System.Math.Max(lineStep * model.FontScale, lineStep);
            attempt.BodySteps.Add(System.Math.Max(step, layout.LayoutBounds.Height));

            int column = System.Math.Min(columns - 1, index / System.Math.Max(1, rowsPerColumn));
            attempt.ColumnHeights[column] += attempt.BodySteps[^1];
        }

        double titleHeight = attempt.TitleLayout?.LayoutBounds.Height ?? 0;
        double subtitleHeight = attempt.SubtitleLayout?.LayoutBounds.Height ?? 0;
        double headerHeight = titleHeight + subtitleHeight + (titleHeight + subtitleHeight > 0 ? HeaderGap : 0);
        double bodyHeight = 0;
        foreach (double height in attempt.ColumnHeights) bodyHeight = System.Math.Max(bodyHeight, height);

        attempt.BodyTop = Padding + headerHeight;
        attempt.CardHeight = System.Math.Max(80, Padding * 2 + headerHeight + bodyHeight);
        return attempt;
    }

    private static bool TryTruncate(
        CanvasDevice device,
        BoardTextCardSpec spec,
        string kind,
        List<LineModel> models,
        double fontSize,
        double lineHeight,
        int requestedColumns,
        double maxHeight,
        out Attempt attempt)
    {
        attempt = null;
        int keep = models.Count;
        for (int index = 0; index < MaxTruncateAttempts && keep > 0; index++)
        {
            var trimmed = models.Take(keep).ToList();
            trimmed.Add(new LineModel { Text = TruncationNote, FontScale = 0.72 });
            attempt = TryLayout(device, spec, kind, trimmed, fontSize, lineHeight, requestedColumns, maxHeight);
            if (attempt.CardHeight <= maxHeight) return true;

            attempt.Dispose();
            attempt = null;
            keep--;
        }

        return attempt is not null;
    }

    // ---- drawing --------------------------------------------------------------

    private static async Task<BoardTextCardRenderResult> DrawAsync(
        BoardTextCardSpec spec, Attempt attempt, bool truncated)
    {
        var device = CanvasDevice.GetSharedDevice();
        double renderScale = System.Math.Min(DesiredRenderScale, MaxPixels / System.Math.Max(1, attempt.CardWidth));
        renderScale = System.Math.Min(renderScale, MaxPixels / System.Math.Max(1, attempt.CardHeight));
        if (!(renderScale > 0)) renderScale = 1.0;

        using var target = new CanvasRenderTarget(
            device, (float)attempt.CardWidth, (float)attempt.CardHeight, (float)(96 * renderScale));

        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            Color textColor = spec.TransparentBackground ? LightTextColor : DarkTextColor;
            session.Clear(spec.TransparentBackground ? Color.FromArgb(0, 0, 0, 0) : LightBackground);
            if (!spec.TransparentBackground)
            {
                session.FillRoundedRectangle(
                    new Rect(0, 0, attempt.CardWidth, attempt.CardHeight), 12, 12, LightBackground);
                session.DrawRoundedRectangle(
                    new Rect(0.5, 0.5, attempt.CardWidth - 1, attempt.CardHeight - 1), 12, 12, LightBorder, 1);
            }

            double top = Padding;
            if (attempt.TitleLayout is not null)
            {
                session.DrawTextLayout(attempt.TitleLayout, (float)Padding, (float)top, textColor);
                top += attempt.TitleLayout.LayoutBounds.Height;
            }

            if (attempt.SubtitleLayout is not null)
            {
                session.DrawTextLayout(attempt.SubtitleLayout, (float)Padding, (float)top, textColor);
                top += attempt.SubtitleLayout.LayoutBounds.Height;
            }

            if (attempt.TitleLayout is not null || attempt.SubtitleLayout is not null) top += HeaderGap;

            var columnTops = new double[attempt.Columns];
            for (int column = 0; column < attempt.Columns; column++) columnTops[column] = top;

            for (int index = 0; index < attempt.BodyLayouts.Count; index++)
            {
                int column = System.Math.Min(attempt.Columns - 1, index / System.Math.Max(1, attempt.RowsPerColumn));
                double x = Padding + column * (attempt.ColumnWidth + ColumnGap);
                session.DrawTextLayout(attempt.BodyLayouts[index], (float)x, (float)columnTops[column], textColor);
                columnTops[column] += attempt.BodySteps[index];
            }
        }

        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);

        int length = (int)stream.Size;
        var bytes = new byte[length];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)length);
        reader.ReadBytes(bytes);

        return new BoardTextCardRenderResult
        {
            Png = bytes,
            Width = attempt.CardWidth,
            Height = attempt.CardHeight,
            Truncated = truncated,
        };
    }

    // ---- helpers --------------------------------------------------------------

    private static string NormalizeKind(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "poem" => "poem",
        "wordlist" => "wordlist",
        _ => "text",
    };

    private static CanvasHorizontalAlignment ParseAlignment(
        string align, CanvasHorizontalAlignment fallback)
        => (align ?? "").Trim().ToLowerInvariant() switch
        {
            "center" => CanvasHorizontalAlignment.Center,
            "right" => CanvasHorizontalAlignment.Right,
            "justify" => CanvasHorizontalAlignment.Justified,
            "left" => CanvasHorizontalAlignment.Left,
            _ => fallback,
        };

    private static double MeasureApproxWidth(string text, double fontSize)
    {
        double width = 0;
        foreach (char character in text ?? "")
        {
            if (char.IsWhiteSpace(character)) width += fontSize * 0.3;
            else if (character < 0x80) width += fontSize * 0.58;
            else width += fontSize;
        }

        return width;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}