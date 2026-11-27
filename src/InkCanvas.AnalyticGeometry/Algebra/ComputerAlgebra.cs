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
        return ParseSpecialFunctions(text);
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
                result = E(0);
                bool implicitVariable = args.Length == 1 || args.Length == 2 && !MathSyntax.Identifier(args[1]);
                var derivativeVariable = implicitVariable ? MainVariable(result) : V(1);
                double order = args.Length > 2 ? Real(args[2]) : args.Length == 2 && implicitVariable ? Real(args[1]) : 1;
                if (order is < 0 or > 16 || order != Math.Truncate(order)) throw new FormatException("导数阶数须为 0–16 的整数。");
                for (int i = 0; i < order; i++) { token.ThrowIfCancellationRequested(); result = result.Differentiate(derivativeVariable); }
                result = result.Simplify(); break;
            case "integral": case "integrate": case "积分":
                var integrand = E(0);
                bool boundsWithoutVariable = args.Length == 3 || args.Length == 4 && args[3].Trim().ToLowerInvariant() is "true" or "false";
                var integrationVariable = args.Length == 1 || boundsWithoutVariable ? MainVariable(integrand) : V(1);
                var primitive = integrand.Integrate(integrationVariable);
                result = boundsWithoutVariable
                    ? (primitive.Substitute(integrationVariable, E(2)) - primitive.Substitute(integrationVariable, E(1))).Simplify()
                    : args.Length >= 4 ? (primitive.Substitute(integrationVariable, E(3)) - primitive.Substitute(integrationVariable, E(2))).Simplify()
                    : primitive.Simplify();
                break;
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
    private static Entity.Variable MainVariable(Entity expression)
        => expression.Vars.Count == 1 ? expression.Vars.Single() : MathS.Var("x");

    private static Entity ParseSpecialFunctions(string text)
    {
        var rewritten = new System.Text.StringBuilder(text.Length);
        var substitutions = new List<(string Name, Entity Value)>();
        for (int index = 0; index < text.Length;)
        {
            if (!char.IsLetter(text[index]) && text[index] != '_')
            {
                rewritten.Append(text[index++]);
                continue;
            }

            int start = index++;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
            string name = text[start..index];
            int opening = index;
            while (opening < text.Length && char.IsWhiteSpace(text[opening])) opening++;
            if (opening >= text.Length || text[opening] != '(' || !TryFindCallEnd(text, opening, out int end)
                || !TryParseSpecialFunction(name, MathSyntax.Split(text[(opening + 1)..(end - 1)]), out var value))
            {
                rewritten.Append(text[start..index]);
                continue;
            }

            string placeholder = "icSpecial" + substitutions.Count;
            while (text.Contains(placeholder, StringComparison.Ordinal)) placeholder += "x";
            substitutions.Add((placeholder, value));
            rewritten.Append(placeholder);
            index = end;
        }

        Entity result = MathS.FromString(rewritten.ToString());
        foreach (var substitution in substitutions)
            result = result.Substitute(substitution.Name, substitution.Value);
        return result;
    }

    private static bool TryFindCallEnd(string text, int opening, out int end)
    {
        int depth = 1;
        for (end = opening + 1; end < text.Length; end++)
        {
            if (text[end] == '(') depth++;
            else if (text[end] == ')' && --depth == 0) { end++; return true; }
        }
        return false;
    }

    private static bool TryParseSpecialFunction(string name, string[] arguments, out Entity value)
    {
        Entity Argument(int index) => Parse(arguments[index]);
        bool Count(params int[] allowed) => allowed.Contains(arguments.Length);
        string command = name.ToLowerInvariant();
        value = null;
        switch (command)
        {
            case "factorial" when Count(1): value = MathS.Factorial(Argument(0)); break;
            case "gamma" when Count(1): value = MathS.Gamma(Argument(0)); break;
            case "signum" or "sign" or "sgn" when Count(1): value = MathS.Signum(Argument(0)); break;
            case "abs" when Count(1): value = MathS.Abs(Argument(0)); break;
            case "sigmoid" when Count(1): value = MathS.Sigmoid(Argument(0)); break;
            case "floor" when Count(1): value = MathS.Floor(Argument(0)); break;
            case "ceil" or "ceiling" when Count(1): value = MathS.Ceil(Argument(0)); break;
            case "round" when Count(1): value = MathS.Round(Argument(0)); break;
            case "round" when Count(2):
                var precision = ((Entity)10).Pow(Argument(1));
                value = MathS.Round(Argument(0) * precision) / precision;
                break;
            case "cbrt" when Count(1): value = Root(Argument(0), 3); break;
            case "nroot" when Count(2): value = Root(Argument(0), Argument(1)); break;
            case "ld" or "log2" when Count(1): value = MathS.Log(2, Argument(0)); break;
            case "lg" or "log10" when Count(1): value = MathS.Log(Argument(0)); break;
            case "csc" or "cosec" when Count(1): value = 1 / MathS.Sin(Argument(0)); break;
            case "cot" or "cotan" when Count(1): value = 1 / MathS.Tan(Argument(0)); break;
            case "coth" or "cotanh" when Count(1): value = MathS.Hyperbolic.Cotanh(Argument(0)); break;
            case "sech" when Count(1): value = MathS.Hyperbolic.Sech(Argument(0)); break;
            case "csch" or "cosech" when Count(1): value = MathS.Hyperbolic.Cosech(Argument(0)); break;
            case "acosh" or "arccosh" when Count(1): value = MathS.Hyperbolic.Arcosh(Argument(0)); break;
            case "asinh" or "arcsinh" when Count(1): value = MathS.Hyperbolic.Arsinh(Argument(0)); break;
            case "atanh" or "arctanh" when Count(1): value = MathS.Hyperbolic.Artanh(Argument(0)); break;
            case "asind" when Count(1): value = MathS.Arcsin(Argument(0)) * (180 / MathS.pi); break;
            case "acosd" when Count(1): value = MathS.Arccos(Argument(0)) * (180 / MathS.pi); break;
            case "atand" when Count(1): value = MathS.Arctan(Argument(0)) * (180 / MathS.pi); break;
            case "atan2" or "atan2d" when Count(2):
                var y = Argument(0);
                var x = Argument(1);
                var tangent = MathS.Arctan(y / x);
                value = MathS.Piecewise(new (Entity, Entity)[]
                {
                    (tangent, MathS.GreaterThan(x, 0)),
                    (tangent - MathS.pi, MathS.Conjunction(MathS.LessThan(x, 0), MathS.LessThan(y, 0))),
                    (tangent + MathS.pi, MathS.LessThan(x, 0)),
                    (MathS.pi / 2, MathS.GreaterThan(y, 0)),
                    (-MathS.pi / 2, MathS.LessThan(y, 0)),
                    (0, true),
                });
                if (command == "atan2d") value *= 180 / MathS.pi;
                break;
            case "min" when Count(2): value = MathS.Min(Argument(0), Argument(1)); break;
            case "max" when Count(2): value = MathS.Max(Argument(0), Argument(1)); break;
            case "gcd" when Count(2): value = MathS.Gcd(Argument(0), Argument(1)); break;
            case "lcm" when Count(2): value = MathS.Lcm(Argument(0), Argument(1)); break;
            case "binomial" when Count(2): value = MathS.Binomial(Argument(0), Argument(1)); break;
            case "npr" when Count(2): value = MathS.FallingFactorial(Argument(0), Argument(1)); break;
            case "harmonicnumber" when Count(1): value = MathS.HarmonicNumber(Argument(0)); break;
            case "harmonicnumber" when Count(2): value = MathS.HarmonicNumber(Argument(0), Argument(1)); break;
            case "catalannumber" when Count(1): value = MathS.CatalanNumber(Argument(0)); break;
            case "fallingfactorial" when Count(2): value = MathS.FallingFactorial(Argument(0), Argument(1)); break;
            case "risingfactorial" when Count(2): value = MathS.RisingFactorial(Argument(0), Argument(1)); break;
            case "iverson" when Count(1): value = MathS.Iverson(Argument(0)); break;
            case "conjugate" when Count(1): value = MathS.Conjugate(Argument(0)); break;
            case "arg" when Count(1): value = MathS.Arg(Argument(0)); break;
            case "re" or "real" when Count(1): value = MathS.Re(Argument(0)); break;
            case "im" or "imaginary" when Count(1): value = MathS.Im(Argument(0)); break;
            case "doublefactorial" when Count(1): value = MathS.DoubleFactorial(Argument(0)); break;
            case "erf" when Count(1): value = MathS.Erf(Argument(0)); break;
            case "erfc" when Count(1): value = MathS.Erfc(Argument(0)); break;
            case "erfi" when Count(1): value = MathS.Erfi(Argument(0)); break;
            case "ei" or "expintegral" when Count(1): value = MathS.Ei(Argument(0)); break;
            case "li" when Count(1): value = MathS.Li(Argument(0)); break;
            case "si" or "sinintegral" when Count(1): value = MathS.Si(Argument(0)); break;
            case "ci" or "cosintegral" when Count(1): value = MathS.Ci(Argument(0)); break;
            case "shi" when Count(1): value = MathS.Shi(Argument(0)); break;
            case "chi" when Count(1): value = MathS.Chi(Argument(0)); break;
            case "polygamma" when Count(2): value = MathS.Polygamma(Argument(0), Argument(1)); break;
            case "digamma" or "psi" when Count(1): value = MathS.Digamma(Argument(0)); break;
            case "lambertw" when Count(1): value = MathS.LambertW(Argument(0)); break;
            // GeoGebra writes LambertW(x, branch); AngouriMath's native form is lambertw(branch, x).
            case "lambertw" when Count(2):
                value = name == "lambertw" ? MathS.LambertW(Argument(0), Argument(1)) : MathS.LambertW(Argument(1), Argument(0));
                break;
            case "zeta" when Count(1): value = MathS.Zeta(Argument(0)); break;
            case "zeta" when Count(2): value = MathS.Zeta(Argument(0), Argument(1)); break;
            case "beta" when Count(2): value = MathS.Beta(Argument(0), Argument(1)); break;
            case "beta" when Count(3):
            case "betaregularized" when Count(3):
                var a = Argument(0);
                var b = Argument(1);
                var limit = Argument(2);
                if (a.Simplify() is Entity.Number.Real { IsPositive: true } && b.Simplify() is Entity.Number.Real { IsPositive: true })
                {
                    var endpoint = limit.Simplify();
                    if (endpoint.Equals((Entity)0)) { value = 0; break; }
                    if (endpoint.Equals((Entity)1)) { value = command == "beta" ? MathS.Beta(a, b) : 1; break; }
                }
                string variableName = "icBetaVariable";
                var free = a.Vars.Concat(b.Vars).Concat(limit.Vars).Select(variable => variable.ToString()).ToHashSet();
                while (free.Contains(variableName)) variableName += "_";
                var variable = MathS.Var(variableName);
                var incompleteBeta = MathS.Integral(variable.Pow(a - 1) * (1 - variable).Pow(b - 1), variable, 0, limit);
                value = command == "beta" ? incompleteBeta : incompleteBeta / MathS.Beta(a, b);
                break;
            case "gamma" when Count(2):
            case "gammaregularized" when Count(2):
                var parameter = Argument(0);
                var argument = Argument(1);
                if (parameter.Simplify() is Entity.Number.Real { IsPositive: true } && argument.Simplify().Equals((Entity)0))
                {
                    value = 0;
                    break;
                }
                var gamma = MathS.Gamma(parameter);
                var upper = MathS.IncompleteGamma(parameter, argument);
                value = command == "gamma" ? gamma - upper : 1 - upper / gamma;
                break;
            case "norm" when Count(2): value = MathS.Norm(Argument(0), Argument(1)); break;
            case "incompletegamma" when Count(2): value = MathS.IncompleteGamma(Argument(0), Argument(1)); break;
            default: return false;
        }
        return true;
    }

    private static Entity Root(Entity radicand, Entity degree)
    {
        var power = 1 / degree;
        if (degree.Simplify() is not Entity.Number.Integer integer || integer.EInteger.IsEven)
            return radicand.Pow(power);
        if (radicand.Vars.Count == 0 && radicand.EvalNumerical() is Entity.Number.Complex and not Entity.Number.Real)
            return radicand.Pow(power);
        return MathS.Signum(radicand) * MathS.Abs(radicand).Pow(power);
    }

    public static double Real(string input) => Real(Parse(input));
    public static double Real(Entity value)
    {
        var complex = value.EvalNumerical().ToNumerics();
        if (Math.Abs(complex.Imaginary) > 1e-9 || !double.IsFinite(complex.Real)) throw new FormatException("此处需要有限实数。");
        return complex.Real;
    }

    public static Func<double, double, double, double, double, double, double> Compile(Entity value)
    {
        if (!value.Nodes.Any(node => node is Entity.IncompleteGammaf or Entity.Integralf))
            return CompileDirect(value);

        var simplified = value.Simplify();
        if (!simplified.Nodes.Any(node => node is Entity.IncompleteGammaf or Entity.Integralf))
            return CompileDirect(simplified);

        var variables = simplified.Vars.ToDictionary(variable => variable.ToString(), StringComparer.Ordinal);
        if (variables.Keys.Any(variable => variable is not ("x" or "y" or "z" or "t" or "u" or "v")))
            throw new FormatException("函数含有未绑定的变量。");
        return (x, y, z, t, u, v) =>
        {
            if (!double.IsFinite(x + y + z + t + u + v)) return double.NaN;
            var evaluated = simplified;
            if (variables.TryGetValue("x", out var vx)) evaluated = evaluated.Substitute(vx, x);
            if (variables.TryGetValue("y", out var vy)) evaluated = evaluated.Substitute(vy, y);
            if (variables.TryGetValue("z", out var vz)) evaluated = evaluated.Substitute(vz, z);
            if (variables.TryGetValue("t", out var vt)) evaluated = evaluated.Substitute(vt, t);
            if (variables.TryGetValue("u", out var vu)) evaluated = evaluated.Substitute(vu, u);
            if (variables.TryGetValue("v", out var vv)) evaluated = evaluated.Substitute(vv, v);
            try
            {
                var number = evaluated.EvalNumerical().ToNumerics();
                return Math.Abs(number.Imaginary) <= 1e-9 ? number.Real : double.NaN;
            }
            catch (AngouriMath.Core.Exceptions.CannotEvalException) { return double.NaN; }
        };
    }

    private static Func<double, double, double, double, double, double, double> CompileDirect(Entity value)
        => value.Compile<double, double, double, double, double, double, double>("x", "y", "z", "t", "u", "v");
    public static string Number(double value) => value.ToString("G15", CultureInfo.InvariantCulture);
}
