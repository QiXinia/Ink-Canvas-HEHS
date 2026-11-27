using System.Xml;
using System.Xml.Linq;
using InkCanvas.Core.Latex;

namespace InkCanvas.Ink.Rendering;

/// <summary>Measured size of one formula atom. <see cref="Baseline"/> is measured from the top.</summary>
public readonly record struct FormulaTextMetrics(float Width, float Height, float Baseline);

/// <summary>Text metric provider. Layout itself is pure; the renderer passes a Win2D-backed measurer.</summary>
public delegate FormulaTextMetrics FormulaTextMeasurer(string text, float fontSize);

/// <summary>Base node of the vector formula layout tree. Coordinates are local to the parent box.</summary>
public abstract class FormulaBox
{
    public float Width { get; internal set; }
    public float Height { get; internal set; }
    public float Baseline { get; internal set; }
    public float FontSize { get; internal set; }
}

public sealed class FormulaRowBox : FormulaBox
{
    public IReadOnlyList<FormulaBox> Children { get; internal set; } = Array.Empty<FormulaBox>();
    public float Gap { get; internal set; }
}

public sealed class FormulaAtomBox : FormulaBox
{
    public string Text { get; internal set; } = "";
    public bool IsTextFont { get; internal set; }
}

public sealed class FormulaFractionBox : FormulaBox
{
    public FormulaBox Numerator { get; internal set; }
    public FormulaBox Denominator { get; internal set; }
    public float NumeratorX { get; internal set; }
    public float NumeratorY { get; internal set; }
    public float DenominatorX { get; internal set; }
    public float DenominatorY { get; internal set; }
    public float BarY { get; internal set; }
    public float BarThickness { get; internal set; }

    /// <summary>Math axis: the fraction-bar centre. This is also the box baseline.</summary>
    public float AxisY { get; internal set; }
}

public sealed class FormulaScriptBox : FormulaBox
{
    public FormulaBox Basis { get; internal set; }
    public FormulaBox Superscript { get; internal set; }
    public FormulaBox Subscript { get; internal set; }
    public bool Limits { get; internal set; }
    public float BasisX { get; internal set; }
    public float BasisY { get; internal set; }
    public float SuperscriptX { get; internal set; }
    public float SuperscriptY { get; internal set; }
    public float SubscriptX { get; internal set; }
    public float SubscriptY { get; internal set; }
}

public sealed class FormulaRadicalBox : FormulaBox
{
    public FormulaBox Radicand { get; internal set; }
    public FormulaBox Index { get; internal set; }
    public float RadicandX { get; internal set; }
    public float RadicandY { get; internal set; }
    public float IndexX { get; internal set; }
    public float IndexY { get; internal set; }
    public float BarLeft { get; internal set; }
    public float BarRight { get; internal set; }
    public float BarY { get; internal set; }
    public float BarThickness { get; internal set; }
}

public sealed class FormulaTableBox : FormulaBox
{
    public IReadOnlyList<IReadOnlyList<FormulaBox>> Rows { get; internal set; } = Array.Empty<IReadOnlyList<FormulaBox>>();
    public IReadOnlyList<FormulaTableCell> Cells { get; internal set; } = Array.Empty<FormulaTableCell>();
    public float[] ColumnWidths { get; internal set; } = Array.Empty<float>();
    public float[] ColumnX { get; internal set; } = Array.Empty<float>();
    public float[] RowHeights { get; internal set; } = Array.Empty<float>();
    public float[] RowBaselines { get; internal set; } = Array.Empty<float>();
    public float[] RowY { get; internal set; } = Array.Empty<float>();
    public float CellGapX { get; internal set; }
    public float CellGapY { get; internal set; }
}

public readonly record struct FormulaTableCell(FormulaBox Box, float X, float Y);

/// <summary>
/// Pure LaTeX → MathML → layout-box pipeline used by the native formula renderer.
/// It has no Win2D dependency: callers may supply a metric delegate (the renderer uses
/// <c>CanvasTextLayout</c>), while tests use the built-in deterministic approximation.
/// Fractions, scripts, radicals, fences and tables are measured with a baseline so the
/// vector renderer can place strokes without ever rasterising the formula.
/// </summary>
public sealed class FormulaLayout
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 1024;
    private const float MinFontSize = 6f;
    private const float MaxFontSize = 400f;

    private FormulaLayout(FormulaBox root, float fontSize)
    {
        Root = root;
        FontSize = fontSize;
    }

    public FormulaBox Root { get; }

    public float FontSize { get; }

    public float Width => Root.Width;

    public float Height => Root.Height;

    public float Baseline => Root.Baseline;

    public static FormulaLayout Create(string latex, float fontSize, out string error, FormulaTextMeasurer measurer = null)
    {
        if (!LatexToMathML.TryConvert(latex, out string mathMl, out string latexError))
        {
            error = string.IsNullOrWhiteSpace(latex) ? "LaTeX 内容为空" : latexError;
            return null;
        }
        return FromMathMl(mathMl, fontSize, out error, measurer);
    }

    public static FormulaLayout FromMathMl(string mathMl, float fontSize, out string error, FormulaTextMeasurer measurer = null)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(mathMl))
        {
            error = "MathML 为空";
            return null;
        }

        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(mathMl), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 32768,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });
            root = XDocument.Load(reader).Root;
        }
        catch (Exception ex)
        {
            error = "MathML 解析失败：" + (ex.Message ?? ex.GetType().Name);
            return null;
        }

        if (root is null || root.Name.LocalName != "math")
        {
            error = "MathML 根节点必须是 math";
            return null;
        }

        float size = float.IsFinite(fontSize) ? Math.Clamp(fontSize, MinFontSize, MaxFontSize) : 30f;
        var context = new LayoutContext(size, measurer ?? MeasureApproximate);
        try
        {
            return new FormulaLayout(context.Build(root, 1f), size);
        }
        catch (FormulaLayoutException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            error = "公式排版失败：" + (ex.Message ?? ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Deterministic fallback metric used by pure-logic tests and headless layout.</summary>
    public static FormulaTextMetrics MeasureApproximate(string text, float fontSize)
    {
        float width = 0;
        foreach (char character in text ?? "")
        {
            if (char.IsWhiteSpace(character)) width += fontSize * 0.3f;
            else if (character < 0x80) width += fontSize * 0.56f;
            else if (character is >= '\u0370' and <= '\u03FF') width += fontSize * 0.62f;
            else width += fontSize;
        }

        return new FormulaTextMetrics(width, fontSize * 1.25f, fontSize * 0.98f);
    }

    private sealed class FormulaLayoutException : Exception
    {
        public FormulaLayoutException(string message) : base(message) { }
    }

    private sealed class LayoutContext
    {
        private readonly float _baseFontSize;
        private readonly FormulaTextMeasurer _measure;
        private int _nodes;

        public LayoutContext(float baseFontSize, FormulaTextMeasurer measure)
        {
            _baseFontSize = baseFontSize;
            _measure = measure;
        }

        public FormulaBox Build(XElement element, float scale)
        {
            if (element is null) return Empty();
            if (++_nodes > MaxNodes) throw new FormulaLayoutException("公式结构过于复杂");
            scale = Math.Clamp(scale, 0.2f, 2f);

            string name = element.Name.LocalName;
            XElement[] children = element.Elements().ToArray();
            FormulaBox Child(int index, float childScale = 1f)
                => index < children.Length ? Build(children[index], scale * childScale) : Empty();

            switch (name)
            {
                case "math":
                case "mrow":
                case "mstyle":
                case "mpadded":
                case "mtd":
                case "semantics":
                    return Row(children.Where(child => child.Name.LocalName is not ("annotation" or "annotation-xml")).ToArray(), scale);
                case "mi":
                case "mn":
                case "mo":
                case "mtext":
                    return Atom(element.Value, scale, name == "mtext");
                case "mfrac":
                    return Fraction(Child(0), Child(1), scale);
                case "msup":
                    return Script(Child(0), Child(1, 0.62f), null, limits: false, scale);
                case "msub":
                    return Script(Child(0), null, Child(1, 0.62f), limits: false, scale);
                case "msubsup":
                    return Script(Child(0), Child(1, 0.62f), Child(2, 0.62f), limits: false, scale);
                case "mover":
                    return Script(Child(0), Child(1, 0.72f), null, limits: true, scale);
                case "munder":
                    return Script(Child(0), null, Child(1, 0.72f), limits: true, scale);
                case "munderover":
                    return Script(Child(0), Child(1, 0.72f), Child(2, 0.72f), limits: true, scale);
                case "msqrt":
                    return Radical(Child(0), null, scale);
                case "mroot":
                    return Radical(Child(0), Child(1, 0.55f), scale);
                case "mtable":
                    return Table(children, scale);
                case "mfenced":
                    return Fenced(element, children, scale);
                case "mspace":
                case "none":
                case "annotation":
                case "annotation-xml":
                    return Empty();
                default:
                    return children.Length == 0 ? Atom(element.Value, scale, isText: false) : Row(children, scale);
            }
        }

        private FormulaBox Row(XElement[] children, float scale)
        {
            if (children.Length == 0) return Empty();
            var boxes = children.Select(child => Build(child, scale)).ToArray();
            return Row(boxes, scale);
        }

        private FormulaRowBox Row(IReadOnlyList<FormulaBox> boxes, float scale)
        {
            float gap = _baseFontSize * scale * 0.04f;
            float width = 0;
            foreach (FormulaBox box in boxes) width += box.Width;
            width += gap * Math.Max(0, boxes.Count - 1);

            float height = 0, baseline = 0;
            float ascent = 0, descent = 0;
            foreach (FormulaBox box in boxes)
            {
                height = MathF.Max(height, box.Height);
                baseline = MathF.Max(baseline, box.Baseline);
                ascent = MathF.Max(ascent, box.Baseline);
                descent = MathF.Max(descent, box.Height - box.Baseline);
            }
            // Align every child on the shared math axis (a fraction contributes its bar axis).
            // Extent is ascent + descent so descenders below the axis are not clipped when the
            // row is fitted into the formula frame.
            height = MathF.Max(height, ascent + descent);
            baseline = ascent;

            return new FormulaRowBox { Children = boxes, Gap = gap, Width = width, Height = height, Baseline = baseline, FontSize = _baseFontSize * scale };
        }

        private FormulaAtomBox Atom(string text, float scale, bool isText)
        {
            float size = _baseFontSize * scale;
            FormulaTextMetrics metrics = _measure(text ?? "", size);
            return new FormulaAtomBox
            {
                Text = text ?? "",
                FontSize = size,
                IsTextFont = isText,
                Width = MathF.Max(0, metrics.Width),
                Height = MathF.Max(0, metrics.Height),
                Baseline = Math.Clamp(metrics.Baseline, 0, MathF.Max(0, metrics.Height)),
            };
        }

        private static FormulaAtomBox Empty() => new() { Text = "", FontSize = 0, Width = 0, Height = 0, Baseline = 0 };

        private FormulaFractionBox Fraction(FormulaBox numerator, FormulaBox denominator, float scale)
        {
            float size = _baseFontSize * scale;
            float padding = size * 0.12f;
            float gap = size * 0.10f;
            float thickness = MathF.Max(1f, size * 0.05f);
            float barY = MathF.Max(numerator.Height, size * 0.55f * scale) + gap * 0.5f;
            float denominatorY = barY + thickness + gap * 0.5f;
            float width = MathF.Max(numerator.Width, denominator.Width) + padding * 2;
            float axisY = barY + thickness * 0.5f;

            return new FormulaFractionBox
            {
                Numerator = numerator,
                Denominator = denominator,
                NumeratorX = (width - numerator.Width) * 0.5f,
                NumeratorY = 0,
                DenominatorX = (width - denominator.Width) * 0.5f,
                DenominatorY = denominatorY,
                BarY = barY,
                BarThickness = thickness,
                AxisY = axisY,
                Width = width,
                Height = denominatorY + denominator.Height,
                Baseline = axisY,
                FontSize = size,
            };
        }

        private FormulaScriptBox Script(FormulaBox basis, FormulaBox superscript, FormulaBox subscript, bool limits, float scale)
        {
            float size = _baseFontSize * scale;
            var box = new FormulaScriptBox
            {
                Basis = basis,
                Superscript = superscript,
                Subscript = subscript,
                Limits = limits,
                FontSize = size,
            };

            if (limits)
            {
                float superscriptY = superscript is null ? 0 : -superscript.Height - size * 0.08f;
                float subscriptY = basis.Height + size * 0.08f;
                float top = MathF.Min(0, superscriptY);
                float bottom = MathF.Max(basis.Height, subscript is null ? 0 : subscriptY + subscript.Height);
                float width = MathF.Max(basis.Width, MathF.Max(superscript?.Width ?? 0, subscript?.Width ?? 0));

                box.BasisX = (width - basis.Width) * 0.5f;
                box.BasisY = -top;
                if (superscript is not null)
                {
                    box.SuperscriptX = (width - superscript.Width) * 0.5f;
                    box.SuperscriptY = superscriptY - top;
                }
                if (subscript is not null)
                {
                    box.SubscriptX = (width - subscript.Width) * 0.5f;
                    box.SubscriptY = subscriptY - top;
                }
                box.Width = width;
                box.Height = bottom - top;
                box.Baseline = basis.Baseline - top;
                return box;
            }

            float scriptGap = size * 0.06f;
            float superscriptTop = superscript is null ? 0 : basis.Baseline - superscript.Baseline - size * 0.30f;
            float subscriptTop = subscript is null ? 0 : basis.Baseline + size * 0.12f;
            float rowTop = superscript is null ? 0 : MathF.Min(0, superscriptTop);
            float rowBottom = MathF.Max(basis.Height, subscript is null ? 0 : subscriptTop + subscript.Height);
            float rowWidth = basis.Width + scriptGap + MathF.Max(superscript?.Width ?? 0, subscript?.Width ?? 0);

            box.BasisX = 0;
            box.BasisY = -rowTop;
            if (superscript is not null)
            {
                box.SuperscriptX = basis.Width + scriptGap;
                box.SuperscriptY = superscriptTop - rowTop;
            }
            if (subscript is not null)
            {
                box.SubscriptX = basis.Width + scriptGap;
                box.SubscriptY = subscriptTop - rowTop;
            }
            box.Width = rowWidth;
            box.Height = rowBottom - rowTop;
            box.Baseline = basis.Baseline - rowTop;
            return box;
        }

        private FormulaRadicalBox Radical(FormulaBox radicand, FormulaBox index, float scale)
        {
            float size = _baseFontSize * scale;
            FormulaTextMetrics sign = _measure("√", size);
            float signWidth = MathF.Max(size * 0.55f, sign.Width * 0.7f);
            float gap = size * 0.10f;
            float thickness = MathF.Max(1f, size * 0.05f);
            float barY = gap;
            float barLeft = signWidth * 0.70f;
            float indexWidth = index?.Width ?? 0;
            float indexHeight = index?.Height ?? 0;
            if (index is not null && indexHeight > barY) barY = indexHeight * 0.55f;

            float radicandX = MathF.Max(signWidth, indexWidth) + size * 0.06f;
            float radicandY = barY + thickness;
            float width = radicandX + radicand.Width;
            float height = radicandY + radicand.Height;

            return new FormulaRadicalBox
            {
                Radicand = radicand,
                Index = index,
                RadicandX = radicandX,
                RadicandY = radicandY,
                IndexX = 0,
                IndexY = 0,
                BarLeft = barLeft,
                BarRight = width,
                BarY = barY,
                BarThickness = thickness,
                Width = width,
                Height = height,
                Baseline = radicandY + radicand.Baseline,
                FontSize = size,
            };
        }

        private FormulaTableBox Table(XElement[] rowElements, float scale)
        {
            float size = _baseFontSize * scale;
            var rows = new List<FormulaBox[]>();
            int columns = 0;
            foreach (XElement rowElement in rowElements)
            {
                FormulaBox[] cells = rowElement.Elements()
                    .Where(child => child.Name.LocalName is "mtd" or "mtr")
                    .Select(child => child.Name.LocalName == "mtr" ? Row(child.Elements().ToArray(), scale) : Build(child, scale))
                    .ToArray();
                rows.Add(cells);
                columns = Math.Max(columns, cells.Length);
            }

            if (rows.Count == 0 || columns == 0) return new FormulaTableBox
            {
                Rows = Array.Empty<IReadOnlyList<FormulaBox>>(),
                Cells = Array.Empty<FormulaTableCell>(),
                Width = 0,
                Height = 0,
                Baseline = 0,
                FontSize = size,
            };

            var columnWidths = new float[columns];
            var rowHeights = new float[rows.Count];
            var rowBaselines = new float[rows.Count];
            foreach (var row in rows)
            {
                for (int column = 0; column < row.Length; column++)
                    columnWidths[column] = MathF.Max(columnWidths[column], row[column].Width);
            }
            for (int r = 0; r < rows.Count; r++)
            {
                foreach (FormulaBox cell in rows[r])
                {
                    rowHeights[r] = MathF.Max(rowHeights[r], cell.Height);
                    rowBaselines[r] = MathF.Max(rowBaselines[r], cell.Baseline);
                }
            }

            float gapX = size * 0.9f;
            float gapY = size * 0.35f;
            var columnX = new float[columns];
            float width = 0;
            for (int c = 0; c < columns; c++)
            {
                columnX[c] = width;
                width += columnWidths[c] + (c < columns - 1 ? gapX : 0);
            }

            var rowY = new float[rows.Count];
            float height = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                rowY[r] = height;
                height += rowHeights[r] + (r < rows.Count - 1 ? gapY : 0);
            }

            var cellPlacements = new List<FormulaTableCell>();
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < rows[r].Length; c++)
                {
                    FormulaBox cell = rows[r][c];
                    float x = columnX[c] + (columnWidths[c] - cell.Width) * 0.5f;
                    float y = rowY[r] + (rowHeights[r] - cell.Height) * 0.5f;
                    cellPlacements.Add(new FormulaTableCell(cell, x, y));
                }
            }

            return new FormulaTableBox
            {
                Rows = rows.Select(row => (IReadOnlyList<FormulaBox>)row).ToArray(),
                Cells = cellPlacements,
                ColumnWidths = columnWidths,
                ColumnX = columnX,
                RowHeights = rowHeights,
                RowBaselines = rowBaselines,
                RowY = rowY,
                CellGapX = gapX,
                CellGapY = gapY,
                Width = width,
                Height = height,
                Baseline = rows.Count > 0 ? rowBaselines[0] : height * 0.5f,
                FontSize = size,
            };
        }

        private FormulaBox Fenced(XElement element, XElement[] children, float scale)
        {
            float size = _baseFontSize * scale;
            string open = (string)element.Attribute("open") ?? "(";
            string close = (string)element.Attribute("close") ?? ")";
            string separators = (string)element.Attribute("separators") ?? ",";
            string separator = separators.Length == 0 ? "" : separators[..1];

            var inner = new List<FormulaBox>();
            for (int index = 0; index < children.Length; index++)
            {
                if (index > 0 && separator.Length > 0) inner.Add(Atom(separator, scale, isText: false));
                inner.Add(Build(children[index], scale));
            }

            FormulaRowBox content = Row(inner, scale);
            float delimiterSize = MathF.Max(size, content.Height * 0.95f);
            var parts = new List<FormulaBox>();
            if (open.Length > 0) parts.Add(Atom(open, delimiterSize / MathF.Max(1f, _baseFontSize), isText: false));
            parts.Add(content);
            if (close.Length > 0) parts.Add(Atom(close, delimiterSize / MathF.Max(1f, _baseFontSize), isText: false));
            return Row(parts, scale);
        }
    }
}