using System.Numerics;
using InkCanvas.Core.Selection;
using InkCanvas.Ink;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace InkCanvas.Ink.Rendering;

/// <summary>
/// Draws native formula objects from the pure <see cref="FormulaLayout"/> box tree directly into a
/// Win2D drawing session: atoms, fraction bars, radicals, fences and matrix grids are stroked as
/// vectors. The LaTeX source is never converted to a bitmap on this path.
/// </summary>
public sealed class FormulaSceneRenderer : IDisposable
{
    private const float LayoutMaxWidth = 100000f;

    private sealed class Entry
    {
        public FormulaLayout Layout;
        public string Error;
    }

    private readonly Dictionary<string, Entry> _cache = new();
    private bool _disposed;

    public void Draw(CanvasDrawingSession session, IBoardFormulaElement formula, Matrix3x2 view)
    {
        if (_disposed || session is null || formula is null) return;
        var frame = formula.Frame;
        if (frame.Bounds.Width <= 0.5f || frame.Bounds.Height <= 0.5f) return;

        var color = AiRenderColor.Parse(formula.Color);
        Entry entry = GetLayout(session.Device, formula);
        var previous = session.Transform;
        try
        {
            if (entry.Layout is null)
            {
                DrawLatexFallback(session, formula, frame, view, color);
                return;
            }

            FormulaLayout layout = entry.Layout;
            float scale = MathF.Min(frame.Bounds.Width / MathF.Max(1f, layout.Width),
                frame.Bounds.Height / MathF.Max(1f, layout.Height));
            if (!float.IsFinite(scale) || scale <= 0) scale = 1f;
            float offsetX = frame.Bounds.Left + (frame.Bounds.Width - layout.Width * scale) * 0.5f;
            float offsetY = frame.Bounds.Top + (frame.Bounds.Height - layout.Height * scale) * 0.5f;

            session.Transform = Matrix3x2.CreateScale(scale)
                * Matrix3x2.CreateTranslation(offsetX, offsetY)
                * frame.Transform * view;
            DrawBox(session, layout.Root, 0, 0, formula, color);
        }
        finally
        {
            session.Transform = previous;
        }
    }

    private void DrawLatexFallback(CanvasDrawingSession session, IBoardFormulaElement formula,
        SelectionFrame frame, Matrix3x2 view, Windows.UI.Color color)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Consolas",
            FontSize = (float)Math.Clamp(formula.FontSize, 8, 400),
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        string text = (formula.Latex ?? "").Trim();
        if (text.Length == 0) return;
        using var layout = new CanvasTextLayout(session.Device, text, format,
            (float)MathF.Max(1f, frame.Bounds.Width), (float)MathF.Max(1f, frame.Bounds.Height));

        float scale = MathF.Min(frame.Bounds.Width / MathF.Max(1f, (float)layout.LayoutBounds.Width),
            frame.Bounds.Height / MathF.Max(1f, (float)layout.LayoutBounds.Height));
        scale = MathF.Max(0.1f, scale);
        session.Transform = Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateTranslation(frame.Bounds.Left, frame.Bounds.Top)
            * frame.Transform * view;
        session.DrawTextLayout(layout, 0, 0, color);
    }

    private void DrawBox(CanvasDrawingSession session, FormulaBox box, float x, float y,
        IBoardFormulaElement formula, Windows.UI.Color color)
    {
        switch (box)
        {
            case FormulaAtomBox atom:
                DrawAtom(session, atom, x, y, formula, color);
                break;
            case FormulaRowBox row:
            {
                float cursor = x;
                foreach (FormulaBox child in row.Children)
                {
                    DrawBox(session, child, cursor, y + row.Baseline - child.Baseline, formula, color);
                    cursor += child.Width + row.Gap;
                }
                break;
            }
            case FormulaFractionBox fraction:
                DrawBox(session, fraction.Numerator, x + fraction.NumeratorX, y + fraction.NumeratorY, formula, color);
                session.DrawLine(x, y + fraction.BarY, x + fraction.Width, y + fraction.BarY,
                    color, MathF.Max(1f, fraction.BarThickness));
                DrawBox(session, fraction.Denominator, x + fraction.DenominatorX, y + fraction.DenominatorY, formula, color);
                break;
            case FormulaScriptBox script:
                DrawBox(session, script.Basis, x + script.BasisX, y + script.BasisY, formula, color);
                if (script.Superscript is not null)
                    DrawBox(session, script.Superscript, x + script.SuperscriptX, y + script.SuperscriptY, formula, color);
                if (script.Subscript is not null)
                    DrawBox(session, script.Subscript, x + script.SubscriptX, y + script.SubscriptY, formula, color);
                break;
            case FormulaRadicalBox radical:
                DrawRadicalSign(session, radical, x, y, formula, color);
                if (radical.Index is not null)
                    DrawBox(session, radical.Index, x + radical.IndexX, y + radical.IndexY, formula, color);
                DrawBox(session, radical.Radicand, x + radical.RadicandX, y + radical.RadicandY, formula, color);
                break;
            case FormulaTableBox table:
                DrawTableGrid(session, table, x, y, color);
                foreach (FormulaTableCell cell in table.Cells)
                    DrawBox(session, cell.Box, x + cell.X, y + cell.Y, formula, color);
                break;
        }
    }

    private void DrawAtom(CanvasDrawingSession session, FormulaAtomBox atom, float x, float y,
        IBoardFormulaElement formula, Windows.UI.Color color)
    {
        if (string.IsNullOrEmpty(atom.Text) || atom.FontSize <= 0) return;
        string family = atom.IsTextFont || ContainsCjk(atom.Text)
            ? "Microsoft YaHei UI"
            : string.IsNullOrWhiteSpace(formula.FontFamily) ? "Cambria Math" : formula.FontFamily;
        using var format = new CanvasTextFormat
        {
            FontFamily = family,
            FontSize = atom.FontSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var layout = new CanvasTextLayout(session.Device, atom.Text, format,
            LayoutMaxWidth, LayoutMaxWidth);
        float width = (float)layout.LayoutBounds.Width;
        session.DrawTextLayout(layout, x + (atom.Width - width) * 0.5f, y, color);
    }

    private static void DrawRadicalSign(CanvasDrawingSession session, FormulaRadicalBox radical,
        float x, float y, IBoardFormulaElement formula, Windows.UI.Color color)
    {
        float size = radical.FontSize > 0 ? radical.FontSize : (float)Math.Clamp(formula.FontSize, 8, 400);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Cambria Math",
            FontSize = size * 1.05f,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var layout = new CanvasTextLayout(session.Device, "√", format, LayoutMaxWidth, LayoutMaxWidth);
        float signHeight = (float)layout.LayoutBounds.Height;
        session.DrawTextLayout(layout, x, y + radical.BarY + radical.BarThickness - signHeight,
            color);
        session.DrawLine(x + radical.BarLeft, y + radical.BarY, x + radical.BarRight, y + radical.BarY,
            color, MathF.Max(1f, radical.BarThickness));
    }

    private static void DrawTableGrid(CanvasDrawingSession session, FormulaTableBox table,
        float x, float y, Windows.UI.Color color)
    {
        var gridColor = Windows.UI.Color.FromArgb(70, color.R, color.G, color.B);
        float thickness = MathF.Max(0.75f, table.FontSize * 0.025f);
        for (int column = 1; column < table.ColumnX.Length; column++)
        {
            float lineX = x + table.ColumnX[column] - table.CellGapX * 0.5f;
            session.DrawLine(lineX, y, lineX, y + table.Height, gridColor, thickness);
        }
        for (int row = 1; row < table.RowY.Length; row++)
        {
            float lineY = y + table.RowY[row] - table.CellGapY * 0.5f;
            session.DrawLine(x, lineY, x + table.Width, lineY, gridColor, thickness);
        }
    }

    private Entry GetLayout(CanvasDevice device, IBoardFormulaElement formula)
    {
        string key = CacheKey(formula);
        if (_cache.TryGetValue(key, out Entry cached)) return cached;

        float fontSize = (float)Math.Clamp(
            double.IsFinite(formula.FontSize) && formula.FontSize > 0 ? formula.FontSize : 30, 6, 400);
        string family = string.IsNullOrWhiteSpace(formula.FontFamily) ? "Cambria Math" : formula.FontFamily;
        var entry = new Entry();
        entry.Layout = FormulaLayout.Create(formula.Latex ?? "", fontSize, out entry.Error,
            (text, size) => MeasureText(device, text, size, family));
        _cache[key] = entry;
        return entry;
    }

    private static string CacheKey(IBoardFormulaElement formula)
    {
        double requested = double.IsFinite(formula.FontSize) && formula.FontSize > 0 ? formula.FontSize : 30;
        float fontSize = (float)Math.Clamp(requested, 6, 400);
        string family = string.IsNullOrWhiteSpace(formula.FontFamily) ? "Cambria Math" : formula.FontFamily;
        return $"{family}|{fontSize:R}|{formula.Latex ?? ""}";
    }

    /// <summary>Drop layouts for formulas that are no longer present on the page.</summary>
    public void Retain(IEnumerable<IBoardFormulaElement> formulas)
    {
        if (formulas is null)
        {
            Clear();
            return;
        }
        var present = formulas.Select(CacheKey).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _cache.Keys.Where(key => !present.Contains(key)).ToArray())
            _cache.Remove(key);
    }

    private static FormulaTextMetrics MeasureText(CanvasDevice device, string text, float size, string family)
    {
        string value = string.IsNullOrEmpty(text) ? " " : text;
        string chosen = ContainsCjk(value) ? "Microsoft YaHei UI" : family;
        using var format = new CanvasTextFormat
        {
            FontFamily = chosen,
            FontSize = size,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var layout = new CanvasTextLayout(device, value, format, LayoutMaxWidth, LayoutMaxWidth);
        var bounds = layout.LayoutBounds;
        CanvasLineMetrics[] lines = layout.LineMetrics;
        float height = MathF.Max((float)bounds.Height, size * 1.2f);
        float baseline = lines.Length > 0 ? lines[0].Baseline : height * 0.8f;
        return new FormulaTextMetrics((float)bounds.Width, height, Math.Clamp(baseline, 0, height));
    }

    private static bool ContainsCjk(string value)
    {
        foreach (char character in value ?? "")
            if (character is >= '\u2E80' and <= '\u9FFF' or >= '\uFF00' and <= '\uFFEF') return true;
        return false;
    }

    public void Clear() => _cache.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}