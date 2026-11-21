using System.Text;
using System.Text.RegularExpressions;

namespace InkCanvas.AnalyticGeometry.Algebra;

public static class MathSyntax
{
    public static string Normalize(string text) => (text ?? "").Replace("²", "^2").Replace("³", "^3")
        .Normalize(NormalizationForm.FormKC).Replace('−', '-').Replace('×', '*').Replace('÷', '/')
        .Replace("π", "pi").Replace("θ", "t").Replace("∞", "+oo").Replace("≤", "<=").Replace("≥", ">=");

    public static string[] Split(string text, char separator = ',')
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"' && (i == 0 || text[i - 1] != '\\')) quoted = !quoted;
            if (quoted) continue;
            if (text[i] is '(' or '[' or '{') depth++;
            if (text[i] is ')' or ']' or '}') depth--;
            if (depth < 0) throw new FormatException("括号不匹配。");
            if (depth == 0 && text[i] == separator) { result.Add(text[start..i].Trim()); start = i + 1; }
        }
        if (depth != 0) throw new FormatException("括号不匹配。");
        result.Add(text[start..].Trim());
        return result.ToArray();
    }

    public static bool Call(string text, out string name, out string[] arguments)
    {
        text = text.Trim();
        var match = Regex.Match(text, @"^([\p{L}_][\p{L}\d_]*)\s*[\(\[]");
        name = match.Success ? match.Groups[1].Value : "";
        arguments = Array.Empty<string>();
        if (!match.Success || text[^1] is not (')' or ']')) return false;
        string body = text[match.Length..^1];
        // Reject f(x)+g(x) as a single call.
        int depth = 0;
        foreach (char c in body)
        {
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}') depth--;
            if (depth < 0) return false;
        }
        if (depth != 0) return false;
        arguments = string.IsNullOrWhiteSpace(body) ? Array.Empty<string>() : Split(body);
        return true;
    }

    public static string EquationDifference(string text)
    {
        var halves = Split(text, '=');
        return halves.Length == 2 ? $"({halves[0]})-({halves[1]})" : text;
    }

    public static bool Identifier(string text) => Regex.IsMatch(text, @"^[\p{L}_][\p{L}\d_]*$");
}
