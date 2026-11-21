namespace InkCanvas.AnalyticGeometry;

public enum CompiledPlotKind { FunctionY, FunctionX, Implicit, Parametric, Polar }

public sealed class CompiledPlot
{
    public CompiledPlotKind Kind { get; }
    public MathExpression First { get; }
    public MathExpression Second { get; }
    private CompiledPlot(CompiledPlotKind kind, MathExpression first, MathExpression second = null) { Kind = kind; First = first; Second = second; }
    public static CompiledPlot Compile(PlotDefinition definition)
    {
        definition.Validate();
        string formula = MathExpression.Normalize(definition.Formula).Trim();
        if (definition.Kind == PlotKind.Parametric)
        {
            var first = MathExpression.Compile(RemovePrefix(formula, "x"));
            var second = MathExpression.Compile(RemovePrefix(MathExpression.Normalize(definition.SecondFormula), "y"));
            if (first.Variables.Concat(second.Variables).Any(name => name is "x" or "y")) throw new FormatException("参数方程请使用变量 t 和参数 a、b、c。");
            return new(CompiledPlotKind.Parametric, first, second);
        }
        if (definition.Kind == PlotKind.Polar)
        {
            var radius = MathExpression.Compile(RemovePrefix(formula, "r"));
            if (radius.Variables.Any(name => name is "x" or "y")) throw new FormatException("极坐标请使用角度 t 或 θ，以及参数 a、b、c。");
            return new(CompiledPlotKind.Polar, radius);
        }
        string[] sides = formula.Split('=');
        if (sides.Length > 2 || sides.Any(string.IsNullOrWhiteSpace)) throw new FormatException("方程需要完整的左右两侧，等号最多一个。");
        if (sides.Length == 1)
        {
            var expression = MathExpression.Compile(formula); CheckCartesian(expression);
            return new(expression.Variables.Contains("y") ? CompiledPlotKind.Implicit : CompiledPlotKind.FunctionY, expression);
        }
        var right = MathExpression.Compile(sides[1]); CheckCartesian(right);
        if (sides[0].Trim() == "y" && !right.Variables.Contains("y")) return new(CompiledPlotKind.FunctionY, right);
        if (sides[0].Trim() == "x" && !right.Variables.Contains("x")) return new(CompiledPlotKind.FunctionX, right);
        var difference = MathExpression.Compile("(" + sides[0] + ")-(" + sides[1] + ")"); CheckCartesian(difference);
        return new(CompiledPlotKind.Implicit, difference);
    }
    private static void CheckCartesian(MathExpression expression)
    { if (expression.Variables.Contains("t")) throw new FormatException("直角坐标请使用 x、y 和参数 a、b、c。"); }
    private static string RemovePrefix(string text, string variable)
    {
        var sides = text.Split('=');
        if (sides.Length == 1) return text;
        if (sides.Length == 2 && (sides[0].Replace(" ", "") == variable || sides[0].Replace(" ", "") == variable + "(t)")) return sides[1];
        throw new FormatException("此处请输入 " + variable + " 的表达式。");
    }
}
