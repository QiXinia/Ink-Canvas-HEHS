using InkCanvas.Ink;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using System.Text;
using Windows.UI.Text;

namespace InkCanvas.Ink.Rendering;

/// <summary>Snapshot of the text fields needed for vector layout.</summary>
public sealed class AiTextRenderSpec
{
    public string Type { get; init; } = "text";
    public string Content { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Dynasty { get; init; } = "";
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public float FontSize { get; init; } = 32;
    public float LineHeight { get; init; } = 1.4f;
    public string Align { get; init; } = "left";
    public int Columns { get; init; } = 1;
    public string Color { get; init; } = "#FF202024";

    public static AiTextRenderSpec From(IBoardTextElement text) => new()
    {
        Type = text.Type ?? "text",
        Content = text.Content ?? "",
        Title = text.Title ?? "",
        Author = text.Author ?? "",
        Dynasty = text.Dynasty ?? "",
        FontFamily = string.IsNullOrWhiteSpace(text.FontFamily) ? "Microsoft YaHei UI" : text.FontFamily,
        FontSize = text.FontSize > 0 ? (float)text.FontSize : 32f,
        LineHeight = text.LineHeight > 0 ? (float)text.LineHeight : 1.4f,
        Align = text.Align ?? "left",
        Columns = text.Columns <= 0 ? 1 : Math.Clamp(text.Columns, 1, 3),
        Color = text.Color ?? "#FF202024",
    };
}

/// <summary>Measured text layouts for one text object, drawn directly into a canvas session.</summary>
public sealed class AiTextLayoutPlan : IDisposable
{
    public CanvasTextLayout Title { get; internal set; }
    public CanvasTextLayout Header { get; internal set; }
    public IReadOnlyList<CanvasTextLayout> Bodies { get; internal set; } = Array.Empty<CanvasTextLayout>();
    public CanvasTextLayout Body => Bodies.Count > 0 ? Bodies[0] : null;
    public CanvasHorizontalAlignment Alignment { get; internal set; }
    public float TitleGap { get; internal set; }
    public float HeaderGap { get; internal set; }
    public int Columns { get; internal set; } = 1;
    public float ColumnWidth { get; internal set; }
    public float ColumnGap { get; internal set; }
    public int BodyLineCount
    {
        get
        {
            int count = 0;
            foreach (CanvasTextLayout body in Bodies) count += body?.LineMetrics.Length ?? 0;
            return count;
        }
    }

    public float TotalHeight
    {
        get
        {
            float height = 0;
            if (Title is not null) height += (float)Title.LayoutBounds.Height + TitleGap;
            if (Header is not null) height += (float)Header.LayoutBounds.Height + HeaderGap;

            float bodyHeight = 0;
            foreach (CanvasTextLayout body in Bodies)
                if (body is not null) bodyHeight = Math.Max(bodyHeight, (float)body.LayoutBounds.Height);
            return height + bodyHeight;
        }
    }

    public void Dispose()
    {
        Title?.Dispose();
        Title = null;
        Header?.Dispose();
        Header = null;
        foreach (CanvasTextLayout body in Bodies) body?.Dispose();
        Bodies = Array.Empty<CanvasTextLayout>();
    }
}

/// <summary>
/// Builds <see cref="CanvasTextLayout"/> instances for native board text. Wrapping, alignment,
/// line spacing and CJK metrics are all measured by Win2D, but the output is drawn as text,
/// never rasterised into an image asset.
/// </summary>
public static class AiTextLayoutBuilder
{
    private const float TitleScale = 1.3f;
    private const float HeaderScale = 0.78f;

    public static AiTextLayoutPlan Build(CanvasDevice device, AiTextRenderSpec spec, float width)
    {
        if (spec is null || device is null) return null;
        float availableWidth = MathF.Max(1f, width);
        float fontSize = float.IsFinite(spec.FontSize) && spec.FontSize > 0 ? Math.Clamp(spec.FontSize, 6f, 400f) : 32f;
        float lineHeight = float.IsFinite(spec.LineHeight) && spec.LineHeight > 0 ? Math.Clamp(spec.LineHeight, 0.8f, 3f) : 1.4f;
        string family = string.IsNullOrWhiteSpace(spec.FontFamily) ? "Microsoft YaHei UI" : spec.FontFamily;
        CanvasHorizontalAlignment alignment = ParseAlignment(spec.Align);

        var plan = new AiTextLayoutPlan { Alignment = alignment };
        try
        {
            if (!string.IsNullOrWhiteSpace(spec.Title))
            {
                using var titleFormat = CreateFormat(family, fontSize * TitleScale, alignment, lineHeight);
                titleFormat.FontWeight = new FontWeight { Weight = 600 };
                plan.Title = new CanvasTextLayout(device, spec.Title, titleFormat, availableWidth, float.MaxValue);
                plan.TitleGap = fontSize * 0.25f;
            }

            string header = BuildHeader(spec);
            if (header.Length > 0)
            {
                using var headerFormat = CreateFormat(family, fontSize * HeaderScale, alignment, lineHeight);
                plan.Header = new CanvasTextLayout(device, header, headerFormat, availableWidth, float.MaxValue);
                plan.HeaderGap = fontSize * 0.2f;
            }

            if (!string.IsNullOrEmpty(spec.Content))
            {
                int columns = Math.Clamp(spec.Columns <= 0 ? 1 : spec.Columns, 1, 3);
                IReadOnlyList<string> columnTexts = SplitColumns(spec.Content, columns);
                float columnGap = fontSize * 0.5f;
                int count = Math.Max(1, columnTexts.Count);
                float columnWidth = count <= 1
                    ? availableWidth
                    : MathF.Max(20f, (availableWidth - columnGap * (count - 1)) / count);

                using var bodyFormat = CreateFormat(family, fontSize, alignment, lineHeight);
                var bodies = new List<CanvasTextLayout>();
                foreach (string columnText in columnTexts)
                {
                    if (string.IsNullOrEmpty(columnText)) continue;
                    bodies.Add(new CanvasTextLayout(device, columnText, bodyFormat, columnWidth, float.MaxValue));
                }

                plan.Bodies = bodies;
                plan.Columns = Math.Max(1, bodies.Count);
                plan.ColumnWidth = columnWidth;
                plan.ColumnGap = bodies.Count > 1 ? columnGap : 0;
            }

            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    public static CanvasHorizontalAlignment ParseAlignment(string align) => (align ?? "").Trim().ToLowerInvariant() switch
    {
        "center" => CanvasHorizontalAlignment.Center,
        "right" => CanvasHorizontalAlignment.Right,
        "justify" => CanvasHorizontalAlignment.Justified,
        _ => CanvasHorizontalAlignment.Left,
    };

    /// <summary>
    /// Split multi-line content into 1-3 columns, filling each column top-to-bottom.
    /// Pure logic (no Win2D), reused by the vector renderer and tests.
    /// </summary>
    public static IReadOnlyList<string> SplitColumns(string content, int columns)
    {
        string text = content ?? "";
        columns = Math.Clamp(columns <= 0 ? 1 : columns, 1, 3);
        if (columns == 1 || text.Length == 0) return new[] { text };

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length <= 1) return new[] { text };

        int rows = (lines.Length + columns - 1) / columns;
        var result = new List<string>(columns);
        for (int column = 0; column < columns; column++)
        {
            int start = column * rows;
            if (start >= lines.Length) break;
            int count = Math.Min(rows, lines.Length - start);
            var builder = new StringBuilder();
            for (int line = 0; line < count; line++)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(lines[start + line]);
            }
            if (builder.Length > 0) result.Add(builder.ToString());
        }

        return result.Count == 0 ? new[] { text } : result;
    }

    private static CanvasTextFormat CreateFormat(string family, float fontSize, CanvasHorizontalAlignment alignment, float lineHeight)
        => new()
        {
            FontFamily = family,
            FontSize = fontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
            HorizontalAlignment = alignment,
            LineSpacing = fontSize * lineHeight,
            LineSpacingMode = CanvasLineSpacingMode.Uniform,
        };

    private static string BuildHeader(AiTextRenderSpec spec)
    {
        string dynasty = (spec.Dynasty ?? "").Trim();
        string author = (spec.Author ?? "").Trim();
        if (dynasty.Length == 0) return author;
        if (author.Length == 0) return dynasty;
        return $"{dynasty} · {author}";
    }
}