using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace InkCanvas.AnalyticGeometry;

/// <summary>Bounded arithmetic grammar compiled once, with a fixed set of numeric functions.</summary>
public sealed class MathExpression
{
    public delegate double Evaluator(double x, double y, double t, double a, double b, double c);
    public Evaluator Evaluate { get; }
    public IReadOnlySet<string> Variables { get; }
    private MathExpression(Evaluator evaluate, IReadOnlySet<string> variables) { Evaluate = evaluate; Variables = variables; }

    public static string Normalize(string value)
    {
        value = (value ?? "").Replace("²", "^2").Replace("³", "^3").Replace("⁴", "^4");
        return value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Replace('−', '-').Replace('–', '-').Replace('×', '*').Replace('·', '*').Replace('÷', '/')
            .Replace("π", "pi").Replace("θ", "t").Replace('：', ':');
    }
    public static MathExpression Compile(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) throw new FormatException("公式为空或过长。");
        var parser = new Parser(Normalize(text));
        var body = parser.Parse();
        return new MathExpression(Expression.Lambda<Evaluator>(body, parser.Parameters).Compile(), parser.Variables);
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _position, _nodes, _depth;
        private Token _token;
        public ParameterExpression[] Parameters { get; } = new[] { "x", "y", "t", "a", "b", "c" }.Select(name => Expression.Parameter(typeof(double), name)).ToArray();
        public HashSet<string> Variables { get; } = new(StringComparer.Ordinal);
        private enum TokenKind { End, Number, Name, Plus, Minus, Multiply, Divide, Power, Open, Close, Comma }
        private readonly record struct Token(TokenKind Kind, string Text, double Number = 0);
        private static readonly IReadOnlyDictionary<string, (string Method, int Arity)> Functions = new Dictionary<string, (string, int)>
        {
            ["sin"] = ("Sin", 1), ["cos"] = ("Cos", 1), ["tan"] = ("Tan", 1),
            ["asin"] = ("Asin", 1), ["acos"] = ("Acos", 1), ["atan"] = ("Atan", 1),
            ["sinh"] = ("Sinh", 1), ["cosh"] = ("Cosh", 1), ["tanh"] = ("Tanh", 1),
            ["sqrt"] = ("Sqrt", 1), ["abs"] = ("Abs", 1), ["exp"] = ("Exp", 1),
            ["ln"] = ("Log", 1), ["log"] = ("Log10", 1), ["log10"] = ("Log10", 1),
            ["floor"] = ("Floor", 1), ["ceil"] = ("Ceiling", 1), ["round"] = ("Round", 1),
            ["min"] = ("Min", 2), ["max"] = ("Max", 2), ["pow"] = ("Pow", 2), ["atan2"] = ("Atan2", 2),
        };
        public Parser(string text) { _text = text; Next(); }
        public Expression Parse()
        {
            var result = Sum();
            if (_token.Kind != TokenKind.End) throw Error("此处有多余的字符");
            return result;
        }
        private FormatException Error(string message) => new(message + "，位置 " + Math.Max(1, _position) + "。");
        private Expression Sum()
        {
            Enter();
            try
            {
                var result = Product();
                while (_token.Kind is TokenKind.Plus or TokenKind.Minus)
                {
                    var operation = _token.Kind; Next(); var right = Product(); Count();
                    result = operation == TokenKind.Plus ? Expression.Add(result, right) : Expression.Subtract(result, right);
                }
                return result;
            }
            finally { _depth--; }
        }
        private Expression Product()
        {
            var result = Unary();
            while (_token.Kind is TokenKind.Multiply or TokenKind.Divide or TokenKind.Open or TokenKind.Name or TokenKind.Number)
            {
                var operation = _token.Kind;
                if (operation is TokenKind.Multiply or TokenKind.Divide) Next();
                var right = Unary(); Count();
                result = operation == TokenKind.Divide ? Expression.Divide(result, right) : Expression.Multiply(result, right);
            }
            return result;
        }
        private Expression Unary()
        {
            Enter();
            try
            {
                if (_token.Kind is TokenKind.Plus or TokenKind.Minus)
                {
                    bool negative = _token.Kind == TokenKind.Minus; Next(); var operand = Unary(); Count();
                    return negative ? Expression.Negate(operand) : operand;
                }
                var value = Primary();
                if (_token.Kind == TokenKind.Power) { Next(); value = Expression.Call(typeof(Math), nameof(Math.Pow), null, value, Unary()); Count(); }
                return value;
            }
            finally { _depth--; }
        }
        private Expression Primary()
        {
            Count();
            if (_token.Kind == TokenKind.Number) { double number = _token.Number; Next(); return Expression.Constant(number); }
            if (_token.Kind == TokenKind.Open)
            { Next(); var value = Sum(); Expect(TokenKind.Close, "缺少右括号"); return value; }
            if (_token.Kind != TokenKind.Name) throw Error("需要数值、变量或左括号");
            string name = _token.Text; Next();
            if (name == "pi") return Expression.Constant(Math.PI);
            if (name == "e") return Expression.Constant(Math.E);
            var parameter = Parameters.FirstOrDefault(item => item.Name == name);
            if (parameter is not null) { Variables.Add(name); return parameter; }
            if (name.Length > 1 && name.All(letter => "xytabc".Contains(letter)))
            {
                Expression value = Expression.Constant(1d);
                foreach (char letter in name) { Variables.Add(letter.ToString()); value = Expression.Multiply(value, Parameters.Single(item => item.Name == letter.ToString())); Count(); }
                return value;
            }
            if (!Functions.TryGetValue(name, out var function)) throw Error("未知名称 " + name);
            Expect(TokenKind.Open, "函数 " + name + " 后需要左括号");
            var arguments = new Expression[function.Arity];
            for (int i = 0; i < arguments.Length; i++)
            { if (i > 0) Expect(TokenKind.Comma, "函数参数之间需要逗号"); arguments[i] = Sum(); }
            Expect(TokenKind.Close, "函数参数后需要右括号");
            var method = typeof(Math).GetMethod(function.Method, Enumerable.Repeat(typeof(double), function.Arity).ToArray())!;
            return Expression.Call(method, arguments);
        }
        private void Expect(TokenKind kind, string message) { if (_token.Kind != kind) throw Error(message); Next(); }
        private void Enter() { if (++_depth > 48) throw Error("公式嵌套过深"); }
        private void Count() { if (++_nodes > 512) throw Error("公式过于复杂"); }
        private void Next()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
            if (_position == _text.Length) { _token = new(TokenKind.End, ""); return; }
            int start = _position; char first = _text[_position++];
            if (char.IsAsciiDigit(first) || first == '.')
            {
                while (_position < _text.Length && (char.IsAsciiDigit(_text[_position]) || _text[_position] == '.')) _position++;
                if (_position < _text.Length && _text[_position] == 'e')
                {
                    int exponent = _position + 1;
                    if (exponent < _text.Length && _text[exponent] is '+' or '-') exponent++;
                    if (exponent < _text.Length && char.IsAsciiDigit(_text[exponent]))
                    { _position = exponent + 1; while (_position < _text.Length && char.IsAsciiDigit(_text[_position])) _position++; }
                }
                string number = _text[start.._position];
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) throw Error("数值无效");
                _token = new(TokenKind.Number, number, value); return;
            }
            if (char.IsAsciiLetter(first))
            {
                while (_position < _text.Length && char.IsAsciiLetterOrDigit(_text[_position])) _position++;
                _token = new(TokenKind.Name, _text[start.._position]); return;
            }
            _token = new(first switch
            {
                '+' => TokenKind.Plus, '-' => TokenKind.Minus, '*' => TokenKind.Multiply, '/' => TokenKind.Divide,
                '^' => TokenKind.Power, '(' => TokenKind.Open, ')' => TokenKind.Close, ',' => TokenKind.Comma,
                _ => throw Error("不支持的字符 " + first),
            }, first.ToString());
        }
    }
}
