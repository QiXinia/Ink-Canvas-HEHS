namespace InkCanvas.AnalyticGeometry.Algebra;

public readonly record struct FormulaEdit(string Text, int SelectionStart, int SelectionLength);

/// <summary>Shared caret and template behavior for the plot editor and construction/CAS workbench.</summary>
public static class FormulaEditing
{
    public static FormulaEdit Apply(string source, int start, int length, string key, int maximumLength = 4096)
    {
        source ??= "";
        start = Math.Clamp(start, 0, source.Length);
        length = Math.Clamp(length, 0, source.Length - start);
        if (key is "←" or "→")
        {
            int caret = length > 0 ? key == "←" ? start : start + length
                : Math.Clamp(start + (key == "←" ? -1 : 1), 0, source.Length);
            return new(source, caret, 0);
        }
        if (key == "⌫")
        {
            if (length == 0 && start > 0)
            {
                start--;
                length = char.IsLowSurrogate(source[start]) && start > 0 && char.IsHighSurrogate(source[start - 1]) ? 2 : 1;
                if (length == 2) start--;
            }
            return new(source.Remove(start, length), start, 0);
        }

        string selected = source.Substring(start, length);
        string argument = selected.Length > 0 ? selected : "□";
        string template = key switch
        {
            "分数" => "(" + argument + ")/(□)",
            "平方根" or "根号" or "√" => "sqrt(" + argument + ")",
            "上标" or "x^n" or "^" => (selected.Length > 0 ? "(" + selected + ")" : "") + "^(□)",
            "下标" or "_" => (selected.Length > 0 ? "(" + selected + ")" : "") + "_(□)",
            "平方" or "x²" => selected.Length > 0 ? "(" + selected + ")^2" : "^2",
            "立方" or "x³" => selected.Length > 0 ? "(" + selected + ")^3" : "^3",
            "倒数" or "1/x" => "1/(" + argument + ")",
            "绝对值" or "|x|" => "abs(" + argument + ")",
            "括号" or "()" => "(" + argument + ")",
            "[]" => "[" + argument + "]",
            "{}" => "{" + argument + "}",
            "矩阵" => "[[□,□],[□,□]]",
            "分段" => "If(□,□,□)",
            "π" => "pi",
            "×" => "*",
            "÷" => "/",
            "−" => "-",
            "Integral()" => "Integral(" + argument + ",x)",
            "Derivative()" => "Derivative(" + argument + ",x)",
            "Solve()" => "Solve(" + argument + ",x)",
            "Limit()" => "Limit(" + argument + ",x,□)",
            "Substitute()" => "Substitute(" + argument + ",x,□)",
            "pow" or "min" or "max" or "atan2" => key + "(" + argument + ",□)",
            "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "sinh" or "cosh" or "tanh"
                or "sqrt" or "abs" or "exp" or "ln" or "log" or "log10" or "floor" or "ceil" or "round"
                => key + "(" + argument + ")",
            _ => key.EndsWith("()", StringComparison.Ordinal) ? key[..^1] + argument + ")" : key,
        };
        if (source.Length - length + template.Length > maximumLength) throw new FormatException("公式超过允许长度。");
        int placeholder = template.IndexOf('□');
        string text = source.Remove(start, length).Insert(start, template);
        return placeholder >= 0 ? new(text, start + placeholder, 1) : new(text, start + template.Length, 0);
    }
}
