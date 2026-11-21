using System.Text;

namespace InkCanvas.Core.AiAssistant;

public sealed class FormulaSegment
{
    public bool IsFormula { get; set; }

    public bool Complete { get; set; }

    public string Text { get; set; } = "";

    public string Raw { get; set; } = "";
}

public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Code,
    Table,
}

public sealed class MarkdownBlock
{
    public MarkdownBlockKind Kind { get; set; }

    public int Level { get; set; }

    public string Text { get; set; } = "";

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; set; } = Array.Empty<IReadOnlyList<string>>();

    public bool Complete { get; set; } = true;
}

public static class AssistantText
{
    private static readonly (string Command, string Symbol)[] Symbols =
    {
        ("\\times", "×"),
        ("\\cdot", "·"),
        ("\\div", "÷"),
        ("\\leq", "≤"),
        ("\\le", "≤"),
        ("\\geq", "≥"),
        ("\\ge", "≥"),
        ("\\neq", "≠"),
        ("\\ne", "≠"),
        ("\\approx", "≈"),
        ("\\infty", "∞"),
        ("\\int", "∫"),
        ("\\sum", "∑"),
        ("\\prod", "∏"),
        ("\\partial", "∂"),
        ("\\pm", "±"),
        ("\\rightarrow", "→"),
        ("\\to", "→"),
        ("\\leftarrow", "←"),
        ("\\angle", "∠"),
        ("\\perp", "⊥"),
        ("\\parallel", "∥"),
        ("\\degree", "°"),
        ("\\alpha", "α"),
        ("\\beta", "β"),
        ("\\gamma", "γ"),
        ("\\delta", "δ"),
        ("\\epsilon", "ε"),
        ("\\theta", "θ"),
        ("\\lambda", "λ"),
        ("\\mu", "μ"),
        ("\\pi", "π"),
        ("\\rho", "ρ"),
        ("\\sigma", "σ"),
        ("\\tau", "τ"),
        ("\\phi", "φ"),
        ("\\omega", "ω"),
        ("\\Delta", "Δ"),
        ("\\Sigma", "Σ"),
        ("\\Omega", "Ω"),
        ("\\left", ""),
        ("\\right", ""),
        ("\\quad", " "),
        ("\\qquad", "  "),
        ("\\,", " "),
        ("\\;", " "),
        ("\\!", ""),
    };

    public static string LatexToPlain(string latex)
    {
        if (string.IsNullOrEmpty(latex)) return "";

        string text = latex;
        text = ExpandFractions(text);
        text = ExpandSqrt(text);
        text = ReplaceSymbols(text);
        text = ExpandScripts(text);
        text = text.Replace("{", "").Replace("}", "");
        text = text.Replace("\\\\", "\n");
        text = text.Replace("\\ ", " ");
        return text.Trim();
    }

    public static IReadOnlyList<FormulaSegment> SplitFormulas(string text)
    {
        var segments = new List<FormulaSegment>();
        text ??= "";
        if (text.Length == 0) return segments;

        int index = 0;
        int segmentStart = 0;

        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] == '$')
            {
                index += 2;
                continue;
            }

            if (text[index] != '$')
            {
                index++;
                continue;
            }

            bool display = index + 1 < text.Length && text[index + 1] == '$';
            int delimiterLength = display ? 2 : 1;

            if (index > segmentStart)
            {
                string plain = text.Substring(segmentStart, index - segmentStart);
                segments.Add(new FormulaSegment
                {
                    IsFormula = false,
                    Complete = true,
                    Text = plain,
                    Raw = plain,
                });
            }

            int contentStart = index + delimiterLength;
            int close = FindClosingDollar(text, contentStart, delimiterLength);

            if (close < 0)
            {
                string inner = text.Substring(contentStart);
                segments.Add(new FormulaSegment
                {
                    IsFormula = true,
                    Complete = false,
                    Text = LatexToPlain(inner),
                    Raw = text.Substring(index),
                });
                return segments;
            }

            string content = text.Substring(contentStart, close - contentStart);
            segments.Add(new FormulaSegment
            {
                IsFormula = true,
                Complete = true,
                Text = LatexToPlain(content),
                Raw = text.Substring(index, close + delimiterLength - index),
            });

            index = close + delimiterLength;
            segmentStart = index;
        }

        if (segmentStart < text.Length)
        {
            string plain = text.Substring(segmentStart);
            segments.Add(new FormulaSegment
            {
                IsFormula = false,
                Complete = true,
                Text = plain,
                Raw = plain,
            });
        }

        return segments;
    }

    public static IReadOnlyList<MarkdownBlock> ParseMarkdown(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MarkdownBlock
            {
                Kind = MarkdownBlockKind.Paragraph,
                Text = string.Join(" ", paragraph),
                Complete = true,
            });
            paragraph.Clear();
        }

        text ??= "";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();
                i++;
                var code = new StringBuilder();
                bool closed = false;
                while (i < lines.Length)
                {
                    if (lines[i].TrimStart().StartsWith("```"))
                    {
                        closed = true;
                        i++;
                        break;
                    }

                    code.AppendLine(lines[i]);
                    i++;
                }

                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Code,
                    Text = code.ToString().TrimEnd('\r', '\n'),
                    Complete = closed,
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                i++;
                continue;
            }

            int hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes >= 1 && hashes <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Heading,
                    Level = hashes,
                    Text = trimmed.Substring(hashes + 1).Trim(),
                    Complete = true,
                });
                i++;
                continue;
            }

            if (trimmed.StartsWith("|"))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<string>>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    var cells = SplitTableRow(lines[i]);
                    i++;
                    if (IsSeparatorRow(cells)) continue;
                    rows.Add(cells);
                }

                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Table,
                    Rows = rows,
                    Complete = true,
                });
                continue;
            }

            if (TryReadListLine(trimmed, out string item))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.ListItem,
                    Level = 0,
                    Text = item,
                    Complete = true,
                });
                i++;
                continue;
            }

            paragraph.Add(line.Trim());
            i++;
        }

        FlushParagraph();
        return blocks;
    }

    private static string ExpandFractions(string input)
    {
        if (input.IndexOf("\\frac", StringComparison.Ordinal) < 0) return input;

        var output = new StringBuilder();
        int position = 0;

        while (true)
        {
            int index = input.IndexOf("\\frac", position, StringComparison.Ordinal);
            if (index < 0)
            {
                output.Append(input, position, input.Length - position);
                break;
            }

            output.Append(input, position, index - position);
            int cursor = index + 5;
            string numerator = ReadGroup(input, ref cursor);
            string denominator = ReadGroup(input, ref cursor);

            if (numerator == null || denominator == null)
            {
                output.Append("\\frac");
                position = index + 5;
                continue;
            }

            output.Append('(')
                .Append(ExpandFractions(numerator))
                .Append(")/(")
                .Append(ExpandFractions(denominator))
                .Append(')');
            position = cursor;
        }

        return output.ToString();
    }

    private static string ExpandSqrt(string input)
    {
        if (input.IndexOf("\\sqrt", StringComparison.Ordinal) < 0) return input;

        var output = new StringBuilder();
        int position = 0;

        while (true)
        {
            int index = input.IndexOf("\\sqrt", position, StringComparison.Ordinal);
            if (index < 0)
            {
                output.Append(input, position, input.Length - position);
                break;
            }

            output.Append(input, position, index - position);
            int cursor = index + 5;
            string root = null;

            if (cursor < input.Length && input[cursor] == '[')
            {
                int close = input.IndexOf(']', cursor + 1);
                if (close > 0)
                {
                    root = input.Substring(cursor + 1, close - cursor - 1).Trim();
                    cursor = close + 1;
                }
            }

            string argument = ReadGroup(input, ref cursor);
            if (argument == null)
            {
                output.Append("\\sqrt");
                position = index + 5;
                continue;
            }

            string prefix = root switch
            {
                "3" => "∛",
                "4" => "∜",
                null => "√",
                _ => "√[" + root + "]",
            };

            output.Append(prefix).Append('(').Append(ExpandSqrt(argument)).Append(')');
            position = cursor;
        }

        return output.ToString();
    }

    private static string ReplaceSymbols(string input)
    {
        string output = input;
        foreach (var (command, symbol) in Symbols)
        {
            output = output.Replace(command, symbol);
        }

        return output;
    }

    private static string ExpandScripts(string input)
    {
        var output = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            char current = input[i];
            if ((current == '^' || current == '_') && i + 1 < input.Length)
            {
                bool superscript = current == '^';
                string inner;
                int next;

                if (input[i + 1] == '{')
                {
                    int close = FindClosingBrace(input, i + 1);
                    if (close < 0)
                    {
                        output.Append(current);
                        continue;
                    }

                    inner = input.Substring(i + 2, close - i - 2);
                    next = close + 1;
                }
                else
                {
                    inner = input[i + 1].ToString();
                    next = i + 2;
                }

                string converted = ConvertScript(inner, superscript);
                output.Append(converted ?? (superscript ? "^" : "_") + inner);
                i = next - 1;
                continue;
            }

            output.Append(current);
        }

        return output.ToString();
    }

    private static string ConvertScript(string value, bool superscript)
    {
        string map = superscript
            ? "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱ"
            : "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₙₓ";
        string keys = superscript
            ? "0123456789+-=()ni"
            : "0123456789+-=()nx";

        var output = new StringBuilder();
        foreach (char c in value)
        {
            int index = keys.IndexOf(c);
            if (index < 0) return null;
            output.Append(map[index]);
        }

        return output.Length == 0 ? null : output.ToString();
    }

    private static int FindClosingBrace(string text, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private static int FindClosingDollar(string text, int start, int delimiterLength)
    {
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] != '$') continue;

            if (delimiterLength == 1)
            {
                if (i + 1 < text.Length && text[i + 1] == '$') continue;
                return i;
            }

            if (i + 1 < text.Length && text[i + 1] == '$') return i;
        }

        return -1;
    }

    private static string ReadGroup(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length) return null;

        if (text[index] == '{')
        {
            int close = FindClosingBrace(text, index);
            if (close < 0) return null;

            string group = text.Substring(index + 1, close - index - 1);
            index = close + 1;
            return group;
        }

        if (text[index] == '\\')
        {
            int end = index + 1;
            while (end < text.Length && char.IsLetter(text[end])) end++;
            if (end == index + 1) end++;
            string token = text.Substring(index, end - index);
            index = end;
            return token;
        }

        char single = text[index];
        index++;
        return single.ToString();
    }

    private static bool TryReadListLine(string trimmed, out string item)
    {
        item = null;
        if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') &&
            trimmed[1] == ' ')
        {
            item = trimmed.Substring(2).Trim();
            return true;
        }

        int digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits])) digits++;
        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ')
        {
            item = trimmed.Substring(digits + 2).Trim();
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> SplitTableRow(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

        var cells = new List<string>();
        foreach (string cell in trimmed.Split('|'))
        {
            cells.Add(cell.Trim());
        }

        return cells;
    }

    private static bool IsSeparatorRow(IReadOnlyList<string> cells)
    {
        if (cells.Count == 0) return false;

        bool hasDash = false;
        foreach (string cell in cells)
        {
            string value = cell.Trim();
            if (value.Length == 0) return false;

            string core = value;
            if (core.StartsWith(":")) core = core.Substring(1);
            if (core.EndsWith(":")) core = core.Substring(0, core.Length - 1);
            if (core.Length == 0) return false;

            foreach (char c in core)
            {
                if (c != '-') return false;
            }

            hasDash = true;
        }

        return hasDash;
    }
}