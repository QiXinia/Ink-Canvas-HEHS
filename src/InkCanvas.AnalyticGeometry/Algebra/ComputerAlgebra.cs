using System.Globalization;
using System.Numerics;
using AngouriMath;
using AngouriMath.Core;

namespace InkCanvas.AnalyticGeometry.Algebra;

public sealed record AlgebraResult(string Input, string Exact, string Latex, string Numeric, bool Evaluated);

/// <summary>Local symbolic computation. Call on a worker with the caller's cancellation budget.</summary>
public static class ComputerAlgebra
{
    public static Entity Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) throw new FormatException("表达式须为 1–4096 个字符。");
        int depth = 0;
        foreach (char c in text)
        {
            if (c is '(' or '[' or '{' && ++depth > 48) throw new FormatException("表达式嵌套过深。");
            if (c is ')' or ']' or '}') depth--;
        }
        text = MathSyntax.Normalize(text);
        if (MathSyntax.Call(text, out var command, out var args) && command.Equals("If", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length is < 2 or > 3) throw new FormatException("If 需要条件、成立分支与可选的不成立分支。");
            return MathS.Piecewise(new[] { (Parse(args[1]), Parse(args[0])), (args.Length > 2 ? Parse(args[2]) : (Entity)"NaN", (Entity)true) });
        }
        return MathS.FromString(text);
    }

    public static AlgebraResult Calculate(string input, CancellationToken token = default)
    {
        MathS.Multithreading.SetLocalCancellationToken(token);
        token.ThrowIfCancellationRequested();
        var expression = Evaluate(input, token);
        string numeric = "";
        if (expression.Vars.Count == 0)
        {
            try { numeric = expression.EvalNumerical().ToString(); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return new(input, expression.ToString(), expression.Latexize(), numeric,
            !expression.ToString().Contains("integral(", StringComparison.Ordinal));
    }

    public static Entity Evaluate(string input, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!MathSyntax.Call(input, out string command, out string[] args)) return Parse(input).Simplify();
        if (args.Length == 0) throw new FormatException("指令缺少参数。");
        Entity E(int index) => index < args.Length ? Evaluate(args[index], token) : throw new FormatException("指令缺少参数。");
        Entity.Variable V(int index, string fallback = "x") => args.Length > index ? MathS.Var(args[index].Trim()) : MathS.Var(fallback);
        Entity result;
        switch (command.ToLowerInvariant())
        {
            case "simplify": case "化简": result = E(0).Simplify(); break;
            case "expand": case "展开": result = E(0).Expand(); break;
            case "factor": case "factorize": case "因式分解": result = E(0).Factorize(); break;
            case "numeric": case "n": case "数值": result = E(0).EvalNumerical(); break;
            case "derivative": case "diff": case "导数":
                result = E(0); int order = args.Length > 2 ? (int)Real(args[2]) : 1;
                if (order is < 0 or > 16) throw new FormatException("导数阶数范围为 0–16。");
                for (int i = 0; i < order; i++) { token.ThrowIfCancellationRequested(); result = result.Differentiate(V(1)); }
                result = result.Simplify(); break;
            case "integral": case "integrate": case "积分":
                var primitive = E(0).Integrate(V(1));
                result = args.Length >= 4 ? (primitive.Substitute(V(1), E(3)) - primitive.Substitute(V(1), E(2))).Simplify() : primitive.Simplify(); break;
            case "limit": case "极限": result = E(0).Limit(V(1), E(2)).Simplify(); break;
            case "solve": case "solutions": case "解方程":
                if (args[0].TrimStart().StartsWith('{'))
                {
                    var equations = MathSyntax.Split(args[0].Trim()[1..^1]).Select(text => Parse(MathSyntax.EquationDifference(text))).ToArray();
                    string variables = args.Length > 1 ? args[1].Trim().Trim('{', '}') : "x,y";
                    result = MathS.Equations(equations).Solve(MathSyntax.Split(variables).Select(MathS.Var).ToArray())
                        ?? throw new InvalidOperationException("方程组未求得显式解。");
                }
                else result = args[0].Contains('<') || args[0].Contains('>') || args[0].Contains('≤') || args[0].Contains('≥')
                    ? Parse(args[0]).Solve(V(1)) : Parse(MathSyntax.EquationDifference(args[0])).SolveEquation(V(1));
                break;
            case "substitute": case "代入": result = E(0).Substitute(Parse(args[1]), E(2)).Simplify(); break;
            case "determinant": case "det": result = Matrix(E(0)).Determinant ?? throw new FormatException("需为方阵。"); break;
            case "inverse": result = Matrix(E(0)).Inverse ?? throw new FormatException("矩阵不可逆。"); break;
            case "transpose": result = Matrix(E(0)).T; break;
            case "rank": result = Matrix(E(0)).Rank; break;
            case "real": result = E(0).EvalNumerical().RealPart; break;
            case "imaginary": result = E(0).EvalNumerical().ImaginaryPart; break;
            case "abs": case "arg": case "sin": case "cos": case "tan": case "sqrt": case "ln": case "exp":
                result = Parse(input).Simplify(); break;
            default: result = Parse(input).Simplify(); break;
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static Entity.Matrix Matrix(Entity value) => value as Entity.Matrix ?? throw new FormatException("请输入矩阵，例如 [[1,2],[3,4]]。");
    public static double Real(string input) => Real(Parse(input));
    public static double Real(Entity value)
    {
        var complex = value.EvalNumerical().ToNumerics();
        if (Math.Abs(complex.Imaginary) > 1e-9 || !double.IsFinite(complex.Real)) throw new FormatException("此处需要有限实数。");
        return complex.Real;
    }

    public static Func<double, double, double, double, double, double, double> Compile(Entity value) =>
        value.Compile<double, double, double, double, double, double, double>("x", "y", "z", "t", "u", "v");
    public static string Number(double value) => value.ToString("G15", CultureInfo.InvariantCulture);
}
