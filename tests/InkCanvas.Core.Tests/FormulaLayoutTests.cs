using InkCanvas.Ink.Rendering;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>
/// Pure LaTeX → MathML → layout-box checks: fractions, scripts, radicals and simple matrices keep
/// positive dimensions and a baseline that stays inside the box, without any Win2D dependency.
/// </summary>
public sealed class FormulaLayoutTests
{
    private static T Find<T>(FormulaBox box) where T : FormulaBox
    {
        if (box is T match) return match;
        if (box is FormulaRowBox row)
        {
            foreach (FormulaBox child in row.Children)
            {
                T found = Find<T>(child);
                if (found is not null) return found;
            }
        }
        else if (box is FormulaRadicalBox radical)
        {
            T found = Find<T>(radical.Radicand);
            if (found is not null) return found;
            return Find<T>(radical.Index);
        }
        else if (box is FormulaScriptBox script)
        {
            T found = Find<T>(script.Basis);
            if (found is not null) return found;
            found = Find<T>(script.Superscript);
            if (found is not null) return found;
            return Find<T>(script.Subscript);
        }
        else if (box is FormulaFractionBox fraction)
        {
            T found = Find<T>(fraction.Numerator);
            if (found is not null) return found;
            return Find<T>(fraction.Denominator);
        }
        return null;
    }

    private static FormulaRowBox FindFractionRow(FormulaBox box)
    {
        if (box is not FormulaRowBox row) return null;
        if (row.Children.Any(child => child is FormulaFractionBox)
            && row.Children.Any(child => child is FormulaScriptBox))
            return row;
        foreach (FormulaBox child in row.Children)
        {
            FormulaRowBox found = FindFractionRow(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static FormulaLayout Create(string latex, float fontSize = 30)
    {
        FormulaLayout layout = FormulaLayout.Create(latex, fontSize, out string error);
        Assert.NotNull(layout);
        Assert.True(string.IsNullOrEmpty(error), error);
        Assert.True(layout.Width > 0);
        Assert.True(layout.Height > 0);
        Assert.InRange(layout.Baseline, 0, layout.Height);
        return layout;
    }

    [Fact]
    public void FractionHasStackedPartsAndABarBaseline()
    {
        FormulaLayout layout = Create("\\frac{1}{2}");
        FormulaFractionBox fraction = Find<FormulaFractionBox>(layout.Root);

        Assert.NotNull(fraction);
        Assert.True(fraction.Numerator.Width > 0);
        Assert.True(fraction.Denominator.Height > 0);
        Assert.True(fraction.Height > fraction.Numerator.Height + fraction.Denominator.Height);
        Assert.True(fraction.Baseline >= fraction.Numerator.Height);
        Assert.True(fraction.BarThickness > 0);
        Assert.True(fraction.Width >= fraction.Numerator.Width);
    }

    [Fact]
    public void FractionAndFollowingTermShareTheFractionBarAxis()
    {
        FormulaLayout layout = Create("\\frac{1}{2}x^2");
        FormulaFractionBox fraction = Find<FormulaFractionBox>(layout.Root);
        FormulaScriptBox script = Find<FormulaScriptBox>(layout.Root);
        FormulaRowBox row = FindFractionRow(layout.Root);

        Assert.NotNull(fraction);
        Assert.NotNull(script);
        Assert.NotNull(row);
        Assert.Equal(fraction.AxisY, fraction.Baseline, 2);
        Assert.Equal(row.Baseline, fraction.AxisY, 2);

        float fractionTop = row.Baseline - fraction.Baseline;
        float scriptTop = row.Baseline - script.Baseline;
        Assert.Equal(0f, fractionTop, 2);
        Assert.True(scriptTop > 0f);
        // The following x^2 must sit on the same math axis as the fraction bar, never on the
        // denominator bottom line.
        Assert.Equal(fractionTop + fraction.AxisY, scriptTop + script.Baseline, 2);
        Assert.Equal(layout.Baseline, row.Baseline, 2);
        Assert.True(row.Height >= row.Baseline);
    }

    [Fact]
    public void FractionWithCompoundNumeratorCentersPartsAroundTheBar()
    {
        FormulaLayout layout = Create("\\frac{a+b}{c}");
        FormulaFractionBox fraction = Find<FormulaFractionBox>(layout.Root);

        Assert.NotNull(fraction);
        Assert.IsType<FormulaRowBox>(fraction.Numerator);
        Assert.Equal((fraction.Width - fraction.Numerator.Width) * 0.5f, fraction.NumeratorX, 2);
        Assert.Equal((fraction.Width - fraction.Denominator.Width) * 0.5f, fraction.DenominatorX, 2);
        Assert.True(fraction.NumeratorY + fraction.Numerator.Height <= fraction.BarY + .01f);
        Assert.True(fraction.DenominatorY >= fraction.BarY + fraction.BarThickness - .01f);
        Assert.Equal(fraction.BarY + fraction.BarThickness * 0.5f, fraction.AxisY, 2);
        Assert.InRange(fraction.AxisY, 0, fraction.Height);
        Assert.Equal(fraction.Height, fraction.DenominatorY + fraction.Denominator.Height, 2);
    }

    [Fact]
    public void RadicalAroundAFractionKeepsTheBarAxis()
    {
        FormulaLayout layout = Create("\\sqrt{\\frac{1}{2}}");
        FormulaRadicalBox radical = Find<FormulaRadicalBox>(layout.Root);
        FormulaFractionBox fraction = Find<FormulaFractionBox>(layout.Root);

        Assert.NotNull(radical);
        Assert.NotNull(fraction);
        Assert.Same(fraction, radical.Radicand);
        Assert.Equal(radical.RadicandY + fraction.AxisY, radical.Baseline, 2);
        Assert.Equal(layout.Baseline, radical.Baseline, 2);
        Assert.True(radical.Height >= radical.Radicand.Height);
    }

    [Fact]
    public void SuperscriptAndSubscriptRaiseAndLowerTheirBoxes()
    {
        FormulaLayout layout = Create("x_1^2");
        FormulaScriptBox script = Find<FormulaScriptBox>(layout.Root);

        Assert.NotNull(script);
        Assert.NotNull(script.Superscript);
        Assert.NotNull(script.Subscript);
        Assert.True(script.Height > script.Basis.Height);
        Assert.True(script.SuperscriptY + script.Superscript.Baseline < script.BasisY + script.Basis.Baseline);
        Assert.True(script.SubscriptY + script.Subscript.Baseline > script.BasisY + script.Basis.Baseline);
        Assert.True(script.Width > script.Basis.Width);
        Assert.True(script.Superscript.FontSize < script.Basis.FontSize);
    }

    [Fact]
    public void RadicalAddsASignAndOverbarWithItsOwnBaseline()
    {
        FormulaLayout layout = Create("\\sqrt{x^2+1}");
        FormulaRadicalBox radical = Find<FormulaRadicalBox>(layout.Root);

        Assert.NotNull(radical);
        Assert.True(radical.RadicandX > 0);
        Assert.True(radical.RadicandY > 0);
        Assert.True(radical.Width > radical.Radicand.Width);
        Assert.True(radical.BarRight >= radical.BarLeft);
        Assert.True(radical.Baseline > radical.RadicandY);
        Assert.True(layout.Baseline >= radical.RadicandY);
    }

    [Fact]
    public void MatrixMeasuresColumnsRowsCellsAndGridExtent()
    {
        FormulaLayout layout = Create("\\begin{pmatrix}1&2\\\\3&4\\end{pmatrix}");
        FormulaTableBox table = Find<FormulaTableBox>(layout.Root);

        Assert.NotNull(table);
        Assert.Equal(2, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(2, row.Count));
        Assert.Equal(2, table.ColumnWidths.Length);
        Assert.Equal(2, table.RowHeights.Length);
        Assert.Equal(4, table.Cells.Count);
        Assert.All(table.ColumnWidths, width => Assert.True(width > 0));
        Assert.All(table.RowHeights, height => Assert.True(height > 0));
        Assert.True(table.Width > table.ColumnWidths[0]);
        Assert.True(table.Height >= table.RowHeights[0] + table.RowHeights[1]);
        Assert.InRange(table.Baseline, 0, table.Height);
        Assert.Equal(TableWidth(table), table.Width, 3);
    }

    private static float TableWidth(FormulaTableBox table)
    {
        float width = 0;
        for (int index = 0; index < table.ColumnWidths.Length; index++)
            width += table.ColumnWidths[index] + (index < table.ColumnWidths.Length - 1 ? table.CellGapX : 0);
        return width;
    }

    [Fact]
    public void InvalidLatexReportsAnErrorWithoutThrowing()
    {
        FormulaLayout layout = FormulaLayout.Create("\\frac{1}", 30, out string error);

        Assert.Null(layout);
        Assert.False(string.IsNullOrWhiteSpace(error));

        Assert.Null(FormulaLayout.Create("", 30, out string emptyError));
        Assert.False(string.IsNullOrWhiteSpace(emptyError));
    }

    [Fact]
    public void LayoutIsDeterministicForTheSameInput()
    {
        FormulaLayout first = Create("\\frac{\\alpha}{\\beta}+x_1^2");
        FormulaLayout second = Create("\\frac{\\alpha}{\\beta}+x_1^2");

        Assert.Equal(first.Width, second.Width, 3);
        Assert.Equal(first.Height, second.Height, 3);
        Assert.Equal(first.Baseline, second.Baseline, 3);
    }
}