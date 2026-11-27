using System.Text;
using System.Xml.Linq;

namespace InkCanvas.Core.Latex;

/// <summary>
/// 把常用 LaTeX 子集转换为 <c>MathFormulaPreview</c> 可接受的 MathML 3.0。
/// 纯逻辑、无 UI 依赖；任何非法输入都通过返回值报告，不抛异常。
/// 未识别的命令按文本节点保留并记录警告，绝不静默丢弃。
/// </summary>
public static class LatexToMathML
{
    public const int MaxLatexLength = 4096;

    internal const int MaxDepth = 48;

    internal const int MaxNodes = 512;

    private const int MaxWarnings = 32;

    private const string LatexNamespace = "http://www.w3.org/1998/Math/MathML";

    private static readonly Dictionary<string, string> GreekLetters = new(StringComparer.Ordinal)
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
        ["epsilon"] = "ϵ", ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
        ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ",
        ["omicron"] = "ο", ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ",
        ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ",
        ["upsilon"] = "υ", ["phi"] = "ϕ", ["varphi"] = "φ", ["chi"] = "χ",
        ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ",
        ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Upsilon"] = "Υ",
        ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
    };

    private static readonly Dictionary<string, string> OperatorCommands = new(StringComparer.Ordinal)
    {
        ["cdot"] = "⋅", ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓",
        ["ast"] = "∗", ["star"] = "⋆", ["circ"] = "∘", ["bullet"] = "∙",
        ["leq"] = "≤", ["le"] = "≤", ["geq"] = "≥", ["ge"] = "≥",
        ["neq"] = "≠", ["ne"] = "≠", ["approx"] = "≈", ["equiv"] = "≡",
        ["sim"] = "∼", ["propto"] = "∝", ["to"] = "→", ["rightarrow"] = "→",
        ["leftarrow"] = "←", ["leftrightarrow"] = "↔", ["Rightarrow"] = "⇒",
        ["mapsto"] = "↦", ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇",
        ["forall"] = "∀", ["exists"] = "∃", ["in"] = "∈", ["notin"] = "∉",
        ["subset"] = "⊂", ["supset"] = "⊃", ["cup"] = "∪", ["cap"] = "∩",
        ["emptyset"] = "∅", ["angle"] = "∠", ["perp"] = "⊥", ["parallel"] = "∥",
        ["degree"] = "°", ["prime"] = "′", ["ldots"] = "…", ["cdots"] = "⋯",
        ["therefore"] = "∴", ["because"] = "∵",
    };

    private static readonly HashSet<string> FunctionCommands = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan",
        "sinh", "cosh", "tanh", "log", "ln", "lg", "exp", "lim", "max", "min",
        "sup", "inf", "det", "gcd", "mod", "dim", "ker",
    };

    private static readonly HashSet<string> BigOperatorCommands = new(StringComparer.Ordinal)
    {
        "sum", "prod", "int", "iint", "iiint", "oint",
    };

    private static readonly Dictionary<string, string> BigOperatorSymbols = new(StringComparer.Ordinal)
    {
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["iint"] = "∬",
        ["iiint"] = "∭", ["oint"] = "∮",
    };

    /// <summary>转换 LaTeX；成功时 <paramref name="error"/> 为空，失败时 <paramref name="mathMl"/> 为空。</summary>
    public static bool TryConvert(string latex, out string mathMl, out string error)
        => TryConvert(latex, out mathMl, out error, out _);

    /// <summary>
    /// 转换 LaTeX，并通过 <paramref name="warnings"/> 返回「已按文本保留」的未知命令等警告。
    /// 成功但存在警告时，<paramref name="error"/> 仍为空，警告独立返回。
    /// </summary>
    public static bool TryConvert(
        string latex, out string mathMl, out string error, out IReadOnlyList<string> warnings)
    {
        mathMl = "";
        error = "";
        var collected = new List<string>();
        warnings = collected;

        if (string.IsNullOrWhiteSpace(latex))
        {
            error = "LaTeX 内容为空";
            return false;
        }

        if (latex.Length > MaxLatexLength)
        {
            error = $"LaTeX 超过 {MaxLatexLength} 个字符";
            return false;
        }

        try
        {
            var parser = new LatexParser(latex, collected);
            XElement root = parser.ParseDocument();
            mathMl = new XDocument(root).ToString(SaveOptions.DisableFormatting);
            return true;
        }
        catch (LatexFormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = "LaTeX 转换失败：" + (ex.Message ?? ex.GetType().Name);
            return false;
        }
    }

    private sealed class LatexFormatException : Exception
    {
        public LatexFormatException(string message) : base(message) { }
    }

    [Flags]
    private enum StopFlags
    {
        None = 0,
        AtBrace = 1,
        AtBracket = 2,
        AtRight = 4,
        AtAmpersand = 8,
        AtRowBreak = 16,
        AtEndEnvironment = 32,
    }

    private readonly record struct ParsedNode(XElement Element, bool IsBigOperator);

    private sealed class LatexParser
    {
        private static readonly XNamespace MathNs = LatexNamespace;

        private readonly string _text;
        private readonly List<string> _warnings;
        private int _position;
        private int _depth;
        private int _nodes;

        public LatexParser(string text, List<string> warnings)
        {
            _text = text;
            _warnings = warnings;
        }

        // ---- document ---------------------------------------------------------

        public XElement ParseDocument()
        {
            var rows = new List<XElement>();
            while (true)
            {
                List<XElement> items = ParseExpression(StopFlags.AtRowBreak);
                rows.Add(Mrow(items));
                if (!PeekRowBreak()) break;
                SkipRowBreak();
            }

            if (!AtEnd) throw Error("公式包含无法解析的内容");

            XElement body = rows.Count == 1
                ? rows[0]
                : new XElement(MathNs + "mtable",
                    rows.Select(row => new XElement(MathNs + "mtr", new XElement(MathNs + "mtd", row))));

            return new XElement(MathNs + "math",
                new XAttribute("display", "block"),
                body);
        }

        // ---- expressions ------------------------------------------------------

        private List<XElement> ParseExpression(StopFlags flags)
        {
            var items = new List<XElement>();
            Enter();
            try
            {
                while (true)
                {
                    SkipSpaces();
                    if (AtEnd) break;

                    char current = _text[_position];
                    if (current == '}' && Has(flags, StopFlags.AtBrace)) break;
                    if (current == ']' && Has(flags, StopFlags.AtBracket)) break;
                    if (current == '&' && Has(flags, StopFlags.AtAmpersand)) break;
                    if (PeekRowBreak() && Has(flags, StopFlags.AtRowBreak)) break;
                    if (PeekCommand("right") && Has(flags, StopFlags.AtRight)) break;
                    if (PeekCommand("end") && Has(flags, StopFlags.AtEndEnvironment)) break;

                    if (current == '^' || current == '_')
                        throw Error("上下标缺少底数");

                    ParseItem(items);
                }
            }
            finally
            {
                Exit();
            }

            return items;
        }

        private void ParseItem(List<XElement> items)
        {
            Count();
            ParsedNode node = ParseAtom();
            XElement subscript = null;
            XElement superscript = null;
            bool hasSubscript = false;
            bool hasSuperscript = false;

            while (true)
            {
                SkipSpaces();
                if (!AtEnd && _text[_position] == '^')
                {
                    if (hasSuperscript) throw Error("重复的上标");
                    _position++;
                    hasSuperscript = true;
                    superscript = ParseScriptArgument();
                    continue;
                }

                if (!AtEnd && _text[_position] == '_')
                {
                    if (hasSubscript) throw Error("重复的下标");
                    _position++;
                    hasSubscript = true;
                    subscript = ParseScriptArgument();
                    continue;
                }

                break;
            }

            XElement result = node.Element;
            if (hasSubscript || hasSuperscript)
            {
                if (node.IsBigOperator)
                {
                    result = hasSubscript && hasSuperscript
                        ? new XElement(MathNs + "munderover", result, subscript, superscript)
                        : hasSubscript
                            ? new XElement(MathNs + "munder", result, subscript)
                            : new XElement(MathNs + "mover", result, superscript);
                }
                else
                {
                    result = hasSubscript && hasSuperscript
                        ? new XElement(MathNs + "msubsup", result, subscript, superscript)
                        : hasSubscript
                            ? new XElement(MathNs + "msub", result, subscript)
                            : new XElement(MathNs + "msup", result, superscript);
                }
            }

            items.Add(result);
        }

        private XElement ParseScriptArgument()
        {
            SkipSpaces();
            if (AtEnd) throw Error("上下标缺少内容");
            return ParseArgument();
        }

        private XElement ParseArgument()
        {
            SkipSpaces();
            if (AtEnd) throw Error("命令缺少参数");

            if (_text[_position] == '{') return ParseGroup();
            return ParseAtom().Element;
        }

        private ParsedNode ParseAtom()
        {
            SkipSpaces();
            if (AtEnd) throw Error("此处需要内容");

            char current = _text[_position];
            switch (current)
            {
                case '{':
                    return new ParsedNode(ParseGroup(), false);
                case '\\':
                    return ParseCommand();
                case '}':
                case ']':
                    throw Error("括号不匹配");
                case '^':
                case '_':
                    throw Error("上下标缺少底数");
                case '(':
                case ')':
                case '[':
                case '|':
                case '/':
                case '=':
                case '+':
                case '-':
                case ',':
                case ';':
                case ':':
                case '!':
                case '<':
                case '>':
                case '?':
                    _position++;
                    if ((current == '<' || current == '>') && !AtEnd && _text[_position] == '=')
                    {
                        _position++;
                        return Operator(current == '<' ? "≤" : "≥");
                    }
                    if (current == '!' && !AtEnd && _text[_position] == '=')
                    {
                        _position++;
                        return Operator("≠");
                    }
                    return Operator(current.ToString());
                case '\'':
                    _position++;
                    return Operator("′");
                default:
                    if (char.IsAsciiDigit(current) || (current == '.' && StartsNumber()))
                        return new ParsedNode(ParseNumber(), false);
                    if (IsGreekLetter(current)) return Atom(current.ToString(), "mi");
                    if (IsCjk(current) || IsFullWidthSymbol(current)) return new ParsedNode(ParseTextRun(), false);
                    if (char.IsLetter(current)) return Atom(_text[_position++].ToString(), "mi");
                    throw Error($"不支持的符号 {current}");
            }
        }

        private XElement ParseGroup()
        {
            Expect('{');
            List<XElement> items = ParseExpression(StopFlags.AtBrace);
            if (AtEnd || _text[_position] != '}') throw Error("花括号未闭合");
            _position++;
            return Mrow(items);
        }

        private XElement ParseNumber()
        {
            int start = _position;
            bool seenDot = false;
            while (!AtEnd)
            {
                char current = _text[_position];
                if (char.IsAsciiDigit(current))
                {
                    _position++;
                    continue;
                }

                if (current == '.' && !seenDot)
                {
                    seenDot = true;
                    _position++;
                    continue;
                }

                break;
            }

            return new XElement(MathNs + "mn", _text[start.._position]);
        }

        private XElement ParseTextRun()
        {
            var builder = new StringBuilder();
            while (!AtEnd)
            {
                char current = _text[_position];
                if (!(IsCjk(current) || IsFullWidthSymbol(current))) break;
                builder.Append(current);
                _position++;
            }

            if (builder.Length == 0) throw Error("此处需要文本");
            return new XElement(MathNs + "mtext", builder.ToString());
        }

        // ---- commands ---------------------------------------------------------

        private ParsedNode ParseCommand()
        {
            Expect('\\');
            if (AtEnd) throw Error("反斜杠后缺少命令");

            string name = ReadCommandName();
            if (name.Length == 0) throw Error("无法解析的命令");

            switch (name)
            {
                case "frac":
                case "dfrac":
                case "tfrac":
                    return new ParsedNode(new XElement(MathNs + "mfrac", ParseArgument(), ParseArgument()), false);
                case "sqrt":
                    return new ParsedNode(ParseRoot(), false);
                case "left":
                    return new ParsedNode(ParseFenced(), false);
                case "right":
                    throw Error(@"\right 缺少对应的 \left");
                case "begin":
                    return new ParsedNode(ParseEnvironment(), false);
                case "end":
                    throw Error(@"\end 缺少对应的 \begin");
                case "text":
                case "textrm":
                case "mbox":
                case "operatorname":
                    return new ParsedNode(ParseTextArgument(), false);
                case "mathrm":
                case "mathbf":
                case "mathit":
                case "boldsymbol":
                case "mathcal":
                case "mathbb":
                    return new ParsedNode(ParseGroup(), false);
                case " ":
                    return Operator("\u00A0");
                case ",":
                    return Operator("\u2009");
                case ";":
                case ":":
                    return Operator("\u2005");
                case "quad":
                    return Operator("\u2003");
                case "qquad":
                    return Operator("\u2003\u2003");
                case "!":
                    return Operator("");
                case "{":
                case "}":
                case "%":
                case "_":
                case "&":
                case "#":
                case "$":
                    return Operator(name);
                case "|":
                    return Operator("‖");
            }

            if (BigOperatorCommands.Contains(name))
                return new ParsedNode(Operator(BigOperatorSymbols[name]).Element, true);

            if (GreekLetters.TryGetValue(name, out string greek))
                return Atom(greek, "mi");

            if (OperatorCommands.TryGetValue(name, out string symbol))
                return Operator(symbol);

            if (FunctionCommands.Contains(name))
                return Atom(name, "mtext");

            Warn($@"未识别命令 \{name}，已按文本保留");
            return Atom("\\" + name, "mtext");
        }

        private XElement ParseRoot()
        {
            XElement index = null;
            SkipSpaces();
            if (!AtEnd && _text[_position] == '[')
            {
                _position++;
                List<XElement> items = ParseExpression(StopFlags.AtBracket);
                if (AtEnd || _text[_position] != ']') throw Error("根指数缺少右方括号");
                _position++;
                index = Mrow(items);
            }

            XElement radicand = ParseArgument();
            return index is null
                ? new XElement(MathNs + "msqrt", radicand)
                : new XElement(MathNs + "mroot", radicand, index);
        }

        private XElement ParseFenced()
        {
            SkipSpaces();
            string open = ReadDelimiter();
            List<XElement> items = ParseExpression(StopFlags.AtRight);
            if (!PeekCommand("right")) throw Error(@"\left 缺少对应的 \right");
            ConsumeCommand("right");
            SkipSpaces();
            string close = ReadDelimiter();

            return new XElement(MathNs + "mfenced",
                new XAttribute("open", open),
                new XAttribute("close", close),
                items);
        }

        private XElement ParseEnvironment()
        {
            string environment = ReadEnvironmentName();
            if (string.IsNullOrEmpty(environment)) throw Error(@"\begin 缺少环境名");

            if (environment == "array") SkipOptionalColumnSpec();

            var rows = new List<XElement>();
            while (true)
            {
                var cells = new List<XElement>();
                while (true)
                {
                    List<XElement> items = ParseExpression(
                        StopFlags.AtAmpersand | StopFlags.AtRowBreak | StopFlags.AtEndEnvironment);
                    cells.Add(new XElement(MathNs + "mtd", Mrow(items)));

                    if (!AtEnd && _text[_position] == '&')
                    {
                        _position++;
                        continue;
                    }

                    break;
                }

                rows.Add(new XElement(MathNs + "mtr", cells));

                if (PeekRowBreak())
                {
                    SkipRowBreak();
                    continue;
                }

                if (!PeekCommand("end"))
                    throw Error($"环境 {environment} 缺少 \\end");

                ConsumeCommand("end");
                string endName = ReadEnvironmentName();
                if (!string.Equals(endName, environment, StringComparison.Ordinal))
                    throw Error($"环境 {environment} 被 {endName} 结束");
                break;
            }

            XElement table = new XElement(MathNs + "mtable", rows);
            return environment switch
            {
                "pmatrix" => FencedTable("(", ")", table),
                "bmatrix" => FencedTable("[", "]", table),
                "vmatrix" => FencedTable("|", "|", table),
                "cases" => FencedTable("{", "", table),
                "matrix" or "array" or "smallmatrix" or "aligned" or "align" or "gathered" => table,
                _ => throw Error($"暂不支持环境 {environment}"),
            };
        }

        private static XElement FencedTable(string open, string close, XElement table)
            => new(MathNs + "mfenced", new XAttribute("open", open), new XAttribute("close", close), table);

        private XElement ParseTextArgument()
        {
            SkipSpaces();
            if (AtEnd || _text[_position] != '{') throw Error(@"\text 缺少花括号参数");
            _position++;

            var builder = new StringBuilder();
            int depth = 0;
            while (!AtEnd)
            {
                char current = _text[_position];
                if (current == '{')
                {
                    depth++;
                    _position++;
                    builder.Append('{');
                    continue;
                }

                if (current == '}')
                {
                    if (depth == 0)
                    {
                        _position++;
                        return new XElement(MathNs + "mtext", builder.ToString());
                    }

                    depth--;
                    _position++;
                    builder.Append('}');
                    continue;
                }

                if (current == '\\' && _position + 1 < _text.Length)
                {
                    char escaped = _text[_position + 1];
                    if (escaped is '{' or '}' or '%' or '_' or '&' or '#' or '$')
                    {
                        builder.Append(escaped);
                        _position += 2;
                        continue;
                    }
                }

                builder.Append(current);
                _position++;
            }

            throw Error(@"\text 花括号未闭合");
        }

        // ---- environment helpers ---------------------------------------------

        private string ReadEnvironmentName()
        {
            SkipSpaces();
            if (AtEnd || _text[_position] != '{') throw Error(@"环境名缺少花括号");
            _position++;

            int start = _position;
            while (!AtEnd && _text[_position] != '}') _position++;
            if (AtEnd) throw Error("环境名花括号未闭合");

            string name = _text[start.._position].Trim();
            _position++;
            return name;
        }

        private void SkipOptionalColumnSpec()
        {
            SkipSpaces();
            if (AtEnd || _text[_position] != '{') return;

            int depth = 0;
            while (!AtEnd)
            {
                char current = _text[_position];
                _position++;
                if (current == '{') depth++;
                else if (current == '}')
                {
                    depth--;
                    if (depth == 0) return;
                }
            }

            throw Error("array 列格式花括号未闭合");
        }

        // ---- token helpers ----------------------------------------------------

        private string ReadCommandName()
        {
            if (AtEnd) return "";

            int start = _position;
            if (char.IsLetter(_text[_position]))
            {
                while (!AtEnd && char.IsLetter(_text[_position])) _position++;
                return _text[start.._position];
            }

            _position++;
            return _text[start].ToString();
        }

        private string ReadDelimiter()
        {
            if (AtEnd) throw Error(@"\left/\right 后缺少定界符");

            if (_text[_position] == '\\')
            {
                _position++;
                if (AtEnd) throw Error(@"\left/\right 后缺少定界符");
                char escaped = _text[_position++];
                return escaped switch
                {
                    '{' => "{",
                    '}' => "}",
                    '|' => "‖",
                    _ => escaped.ToString(),
                };
            }

            char delimiter = _text[_position++];
            return delimiter switch
            {
                '.' => "",
                _ => delimiter.ToString(),
            };
        }

        private bool PeekCommand(string name)
        {
            if (_position >= _text.Length || _text[_position] != '\\') return false;
            int index = _position + 1;
            int start = index;
            while (index < _text.Length && char.IsLetter(_text[index])) index++;
            return index > start && string.CompareOrdinal(_text, start, name, 0, index - start) == 0
                && index - start == name.Length;
        }

        private void ConsumeCommand(string name)
        {
            if (!PeekCommand(name)) throw Error("缺少命令 \\" + name);
            _position += name.Length + 1;
        }

        private bool PeekRowBreak()
            => _position + 1 < _text.Length && _text[_position] == '\\' && _text[_position + 1] == '\\';

        private void SkipRowBreak() => _position += 2;

        private void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(_text[_position])) _position++;
        }

        private void Expect(char expected)
        {
            if (AtEnd || _text[_position] != expected) throw Error($"期望 {expected}");
            _position++;
        }

        private bool AtEnd => _position >= _text.Length;

        private bool StartsNumber()
        {
            if (_text[_position] != '.') return false;
            if (_position + 1 >= _text.Length) return false;
            return char.IsAsciiDigit(_text[_position + 1]);
        }

        private static bool Has(StopFlags flags, StopFlags expected) => (flags & expected) != 0;

        private static bool IsGreekLetter(char value) => value is >= '\u0370' and <= '\u03FF';

        private static bool IsCjk(char value)
            => value is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF'
                or >= '\u3040' and <= '\u30FF' or >= '\uAC00' and <= '\uD7AF';

        private static bool IsFullWidthSymbol(char value)
            => value is >= '\uFF00' and <= '\uFFEF' or >= '\u3000' and <= '\u303F';

        // ---- node helpers -----------------------------------------------------

        private ParsedNode Atom(string text, string kind)
            => new(new XElement(MathNs + kind, text), false);

        private ParsedNode Operator(string text)
            => new(new XElement(MathNs + "mo", text), false);

        private static XElement Mrow(List<XElement> items)
        {
            if (items.Count == 1) return items[0];
            return new XElement(MathNs + "mrow", items);
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Error("公式嵌套过深");
        }

        private void Exit() => _depth--;

        private void Count()
        {
            if (++_nodes > MaxNodes) throw Error("公式过于复杂");
        }

        private void Warn(string message)
        {
            if (_warnings.Count < MaxWarnings) _warnings.Add(message);
        }

        private LatexFormatException Error(string message)
            => new($"位置 {System.Math.Max(1, _position + 1)}：{message}");
    }
}