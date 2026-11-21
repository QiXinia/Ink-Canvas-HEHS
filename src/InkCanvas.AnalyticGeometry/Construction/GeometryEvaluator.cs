using System.Text.RegularExpressions;
using AngouriMath;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator(ConstructionSnapshot view, IReadOnlyDictionary<string, GeometryValue> values, CancellationToken token)
{
    private int _samples;
    private readonly Dictionary<string, Func<double, double, double, double, double, double, double>> _compiled = new();

    public GeometryValue Evaluate(string text)
    {
        token.ThrowIfCancellationRequested();
        text = MathSyntax.Normalize(text.Trim());
        if (values.TryGetValue(text, out var reference)) return reference.Defined ? reference : throw new FormatException(text + " 未定义 · " + reference.Error);
        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            var coordinates = MathSyntax.Split(text[1..^1]);
            if (coordinates.Length is 2 or 3) return GeometryValue.Point(new(N(coordinates[0]), N(coordinates[1]), coordinates.Length == 3 ? N(coordinates[2]) : 0));
        }
        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            var items = MathSyntax.Split(text[1..^1]).Select(Evaluate).ToArray();
            return new()
            {
                Kind = GeometryKind.List,
                Items = items,
                Display = "{" + string.Join(", ", items.Select(item => item.Display)) + "}",
                Paths = items.SelectMany(item => item.Paths).ToArray(),
                Points = items.SelectMany(item => item.Points).ToArray()
            };
        }
        if (MathSyntax.Call(text, out var command, out var args))
        {
            if (args.Length == 0) throw new FormatException("指令缺少参数。");
            var geometry = Command(command.ToLowerInvariant(), args);
            if (geometry is not null) return geometry;
            if (ComputerAlgebraCommands.Contains(command.ToLowerInvariant()))
            {
                var result = ComputerAlgebra.Evaluate(ExpandAlgebra(text), token);
                return result is not (Entity.Set or Entity.Matrix) && result.Vars.Count == 1
                    ? Function(result.ToString(),result.Vars.Single().ToString()) : AlgebraValue(result.ToString());
            }
        }
        var arithmetic = Regex.Match(text, @"^([\p{L}_][\p{L}\d_]*)\s*([+-])\s*([\p{L}_][\p{L}\d_]*)$");
        if (arithmetic.Success && values.TryGetValue(arithmetic.Groups[1].Value, out var a) && values.TryGetValue(arithmetic.Groups[3].Value, out var b)
            && a.Kind is GeometryKind.Point or GeometryKind.Vector && b.Kind is GeometryKind.Point or GeometryKind.Vector)
        {
            var result = arithmetic.Groups[2].Value == "+" ? a.Origin + b.Origin : a.Origin - b.Origin;
            return a.Kind == GeometryKind.Vector || b.Kind == GeometryKind.Vector ? Vector(result) : GeometryValue.Point(result);
        }
        string expanded = Expand(text);
        if (Regex.IsMatch(expanded, @"[<>]")) return Inequality(expanded);
        if (expanded.Contains('=') || Regex.IsMatch(expanded, @"(?<![\p{L}\d_])[xy](?![\p{L}\d_])"))
        {
            var equation = MathSyntax.Split(expanded, '=');
            if (equation.Length == 2 && equation[0].Trim() == "y") return Function(equation[1]);
            if (equation.Length == 2 && equation[0].Trim() == "z") return Surface(new[] { equation[1], "-6", "6", "-6", "6" });
            if (equation.Length == 1 && !Regex.IsMatch(expanded, @"\by\b")) return Function(expanded);
            return Implicit(expanded);
        }
        return AlgebraValue(expanded);
    }

    private static readonly HashSet<string> ComputerAlgebraCommands = new(StringComparer.OrdinalIgnoreCase)
    { "simplify", "expand", "factor", "factorize", "numeric", "n", "derivative", "diff", "integral", "integrate", "limit", "solve", "solutions", "substitute", "determinant", "det", "inverse", "transpose", "rank", "real", "imaginary" };

    private GeometryValue AlgebraValue(string expanded)
    {
        var entity = ComputerAlgebra.Evaluate(expanded, token);
        if (entity.Vars.Count > 0) return new() { Kind = GeometryKind.Text, Display = entity.ToString(), Expression = entity.ToString() };
        try
        {
            var value = entity.EvalNumerical().ToNumerics();
            if (Math.Abs(value.Imaginary) > 1e-9) return new() { Kind = GeometryKind.Complex, Origin = new(value.Real, value.Imaginary),
                Expression = entity.ToString(), Display = entity.ToString() };
            return GeometryValue.Scalar(ComputerAlgebra.Real(entity)) with { Expression = entity.ToString(), Display = entity.ToString() };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new() { Kind = GeometryKind.Text, Display = entity.ToString(), Expression = entity.ToString() }; }
    }

    private GeometryValue Command(string command, string[] args)
    {
        GeometryValue G(int i) => Evaluate(args[i]);
        GeoVector P(int i) => Point(G(i));
        double D(int i) => N(args[i]);
        switch (command)
        {
            case "complexpoint":
                var complex = ComputerAlgebra.Parse(Expand(args[0])).EvalNumerical().ToNumerics();
                return GeometryValue.Point(new(complex.Real, complex.Imaginary));
            case "complex":
                var coordinate = P(0);
                return AlgebraValue(ComputerAlgebra.Number(coordinate.X) + "+(" + ComputerAlgebra.Number(coordinate.Y) + ")*i");
            case "point":
            case "pointonobject":
                if (args.Length is 2 or 3)
                {
                    var coordinates = args.Select(Evaluate).ToArray();
                    if (coordinates.All(value => value.Kind == GeometryKind.Scalar))
                        return GeometryValue.Point(new(coordinates[0].Number, coordinates[1].Number, args.Length > 2 ? coordinates[2].Number : 0));
                }
                var source = G(0); double t = args.Length > 1 ? D(1) : .5;
                if (source.Kind == GeometryKind.Circle) return GeometryValue.Point(source.Origin + CircleBasis(source.Normal).U * (source.Radius * Math.Cos(t))
                    + CircleBasis(source.Normal).V * (source.Radius * Math.Sin(t)));
                if (source.Kind is GeometryKind.Line or GeometryKind.Segment or GeometryKind.Ray)
                    return GeometryValue.Point(source.Origin + source.Direction * (source.Kind == GeometryKind.Segment ? Math.Clamp(t,0,1) : source.Kind == GeometryKind.Ray ? Math.Max(0,t) : t));
                throw new FormatException("点工具需要坐标、直线或圆。");
            case "midpoint":
            case "center":
                if (args.Length == 2) return GeometryValue.Point((P(0) + P(1)) / 2);
                var mid = G(0); return GeometryValue.Point(mid.Kind == GeometryKind.Segment ? mid.Origin + mid.Direction / 2 : mid.Origin);
            case "line":
            case "segment":
            case "ray":
                var start = P(0); var second = G(1);
                GeoVector direction = second.Kind == GeometryKind.Scalar ? new(second.Number, 0) : second.Kind == GeometryKind.Vector ? second.Origin : Point(second) - start;
                return Line(start, direction, command == "segment" ? GeometryKind.Segment : command == "ray" ? GeometryKind.Ray : GeometryKind.Line);
            case "vector": return Vector(args.Length == 1 ? P(0) : P(1) - P(0));
            case "unitvector":
                var unitSource = G(0);
                return Vector((unitSource.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment ? unitSource.Direction : Point(unitSource)).Unit());
            case "dot": return GeometryValue.Scalar(P(0).Dot(P(1)));
            case "cross": return Vector(P(0).Cross(P(1)));
            case "perpendicularvector": var vector = P(0); return Vector(new(-vector.Y, vector.X));
            case "parallel": case "parallelline": return Line(P(0), G(1).Direction);
            case "perpendicular": case "perpendicularline": var line = G(1); return Line(P(0), new(-line.Direction.Y, line.Direction.X));
            case "perpendicularbisector":
                var first = P(0); var last = P(1); var delta = last - first;
                return Line((first + last) / 2, new(-delta.Y, delta.X));
            case "anglebisector":
                var vertex = P(1); return Line(vertex, (P(0) - vertex).Unit() + (P(2) - vertex).Unit());
            case "polygon": return Polygon(Points(args));
            case "polyline": return new() { Kind = GeometryKind.Curve, Paths = new[] { Points(args) }, Display = "折线" };
            case "rigidpolygon": return RigidPolygon(args);
            case "vectorpolygon": return VectorPolygon(args);
            case "regularpolygon": return RegularPolygon(P(0), P(1), (int)D(2));
            case "circle":
            case "compass":
                if (args.Length == 3) return Circumcircle(P(0), P(1), P(2));
                var center = P(0); var radius = G(1);
                return Circle(center, radius.Kind == GeometryKind.Scalar ? radius.Number : radius.Kind == GeometryKind.Segment ? radius.Direction.Length : (Point(radius) - center).Length);
            case "circumcircle": return Circumcircle(P(0), P(1), P(2));
            case "semicircle":
                var c = (P(0) + P(1)) / 2; double angle = Math.Atan2(P(0).Y - c.Y, P(0).X - c.X);
                return Arc(c, (P(0) - c).Length, angle, angle + Math.PI, false);
            case "arc":
            case "sector":
                var origin = P(0); double begin = Math.Atan2(P(1).Y - origin.Y, P(1).X - origin.X);
                double end = Math.Atan2(P(2).Y - origin.Y, P(2).X - origin.X);
                if (end < begin) end += Math.Tau;
                return Arc(origin, (P(1) - origin).Length, begin, end, command == "sector");
            case "circumarc":
            case "circumsector":
                var circum = Circumcircle(P(0), P(1), P(2));
                return CircumArc(circum, P(0), P(1), P(2), command == "circumsector");
            case "intersect": case "intersectpath": return Intersect(G(0), G(1));
            case "element":
                var list = G(0); int index = (int)D(1) - 1;
                if (index < 0 || index >= list.Items.Count) throw new FormatException("元素序号超出范围。"); return list.Items[index];
            case "tangent": return Tangent(P(0), G(1));
            case "polar":
                var circle = G(1); var diff = P(0) - circle.Origin;
                return Line(circle.Origin + diff * (circle.Radius * circle.Radius / diff.Dot(diff)), new(-diff.Y, diff.X));
            case "translate": return Transform(G(0), point => point + P(1));
            case "rotate":
                var pivot = args.Length > 2 ? P(2) : default; double theta = D(1);
                var axis = args.Length > 3 ? P(3) : new GeoVector(0, 0, 1);
                return Transform(G(0), point => Rotate(point - pivot, axis, theta) + pivot);
            case "dilate":
            case "homothety":
                var anchor = args.Length > 2 ? P(2) : default; double factor = D(1);
                return Transform(G(0), point => anchor + (point - anchor) * factor);
            case "reflect":
                var mirror = G(1);
                if (mirror.Kind == GeometryKind.Circle) return Invert(G(0), mirror);
                var reflected = Transform(G(0), point => mirror.Kind switch
                {
                    GeometryKind.Point => mirror.Origin * 2 - point,
                    GeometryKind.Line => mirror.Origin + mirror.Direction.Unit() * (2 * (point - mirror.Origin).Dot(mirror.Direction.Unit())) - (point - mirror.Origin),
                    GeometryKind.Plane => point - mirror.Normal * (2 * (point - mirror.Origin).Dot(mirror.Normal)),
                    _ => throw new FormatException("对称对象需为点、直线、平面或圆。"),
                });
                return reflected;
            case "distance":
            case "length":
                if (command == "length" && args.Length == 3) return ArcLength(G(0), D(1), D(2));
                if (args.Length == 2) return GeometryValue.Scalar(Distance(G(0), G(1)));
                var length = G(0); return GeometryValue.Scalar(length.Kind == GeometryKind.Circle ? Math.Tau * length.Radius
                    : length.Paths.Sum(path => path.Zip(path.Skip(1)).Sum(pair => (pair.Second - pair.First).Length)));
            case "area": return GeometryValue.Scalar(Area(G(0)));
            case "slope": var slope = G(0); return GeometryValue.Scalar(slope.Direction.Y / slope.Direction.X);
            case "angle":
                return GeometryValue.Scalar(Angle(args.Length == 3 ? P(0) - P(1) : G(0).Direction,
                args.Length == 3 ? P(2) - P(1) : G(1).Direction));
            case "relation": return Relation(G(0), G(1), args.Length > 2 ? args[2].Trim('"') : "parallel");
            case "function": return Function(ExpandBound(args[0], args.Length > 1 ? args[1] : "x"), args.Length > 1 ? args[1] : "x",
                args.Length > 2 ? D(2) : null, args.Length > 3 ? D(3) : null);
            case "curve": return Curve(args);
            case "polarcurve": return PolarCurve(args);
            case "root": case "extremum": case "inflectionpoint": return SpecialPoints(G(0), command);
            case "curvature": case "osculatingcircle": case "normal": return Differential(G(0), D(1), command);
            case "integralbetween": return IntegralRegion(G(0), G(1), D(2), D(3));
            case "integralarea": return IntegralRegion(G(0), null, D(1), D(2));
            case "slopefield": return SlopeField(args[0]);
            case "solveode": return SolveOde(args);
            case "sequence": return Sequence(args);
            case "zip": return Zip(args);
            case "locus": case "envelope": return Locus(args, command == "envelope");
            case "plane": case "parallelplane": case "perpendicularplane": return Plane(args, command);
            case "sphere":
            case "cylinder":
            case "cone":
            case "prism":
            case "pyramid":
            case "cube":
            case "tetrahedron":
            case "octahedron":
            case "icosahedron":
            case "dodecahedron": return Solid(command, args);
            case "surface": return Surface(args);
            case "revolve": return Revolve(args);
            case "net": return Net(Evaluate(args[0]), args.Length > 1 ? D(1) : 1);
            case "prove": case "provedetails": return GeometryProof.Evaluate(view, args[0], command == "provedetails", token);
            case "volume": return GeometryValue.Scalar(G(0).Number);
            case "surfacearea": return GeometryValue.Scalar(G(0).Faces.Sum(face => (face.B - face.A).Cross(face.C - face.A).Length / 2));
            case "conic": return FivePointConic(Points(args));
            case "ellipse": case "hyperbola": case "parabola": return Conic(command, args);
            case "focus": case "directrix": case "eccentricity": case "asymptote": case "vertex": return ConicProperty(G(0), command);
            case "checkbox":
                bool enabled = Condition(args[0]);
                return new() { Kind = GeometryKind.Boolean, Number = enabled ? 1 : 0, Display = enabled ? "true" : "false", ControlKind = "checkbox" };
            case "button": return new() { Kind = GeometryKind.Text, Display = args[0].Trim('"'), ControlKind = "button" };
            case "inputbox": return new() { Kind = GeometryKind.Text, Display = G(0).Display, ControlKind = "input", ControlTarget = args[0].Trim() };
            case "dropdown":
                var choices = G(0);
                int selected = args.Length > 1 ? (int)D(1) - 1 : 0;
                if (choices.Kind != GeometryKind.List || selected < 0 || selected >= choices.Items.Count) throw new FormatException("Dropdown 需要非空列表与有效序号。");
                return choices.Items[selected] with { ControlKind = "dropdown", ControlTarget = args[0], Options = choices.Items.Select(value => value.Display).ToArray() };
            case "if":
                if (Regex.IsMatch(args[0], @"\b[xy]\b")) return Function(Expand("If(" + string.Join(',', args) + ")"));
                return Condition(args[0]) ? G(1) : args.Length > 2 ? G(2) : new() { Kind = GeometryKind.Text, Display = "" };
            default: return null;
        }
    }

    private GeoVector[] Points(string[] args) => args.Select(arg => Point(Evaluate(arg))).ToArray();
    private static GeoVector Point(GeometryValue value) => value.Kind is GeometryKind.Point or GeometryKind.Vector ? value.Origin
        : value.Kind == GeometryKind.List && value.Items.Count == 1 ? Point(value.Items[0]) : throw new FormatException("此参数需要点或向量。");
    private double N(string text) => ComputerAlgebra.Real(Expression(text));
    private Entity Expression(string text) => ComputerAlgebra.Parse(Expand(text));
    internal string Expand(string text) => ExpandCore(text, null);
    internal string ExpandAlgebra(string text)
    {
        if (!MathSyntax.Call(text, out var command, out var args)) return Expand(text);
        if (command.ToLowerInvariant() is "derivative" or "diff" or "integral" or "integrate" or "limit" or "solve" or "solutions" or "substitute")
        {
            string variables = args.Length > 1 ? args[1].Trim().Trim('{','}') : "x";
            return ExpandBound(text,MathSyntax.Split(variables).Where(MathSyntax.Identifier).ToArray());
        }
        return Expand(text);
    }
    private string ExpandCore(string text, IReadOnlySet<string> bound)
    {
        if (bound?.Contains(text.Trim()) != true && values.TryGetValue(text.Trim(), out var named) && named.Kind == GeometryKind.Function) return named.Expression;
        text = text.Replace("°", "*pi/180");
        var scalarCalls = Regex.Matches(text, @"\b(Distance|Length|Area|Slope|Angle|Dot|Volume|SurfaceArea|Curvature|Eccentricity)\s*\(", RegexOptions.IgnoreCase);
        foreach (Match match in scalarCalls.Reverse())
        {
            int depth = 1, end = match.Index + match.Length;
            for (; end < text.Length && depth > 0; end++)
            { if (text[end] == '(') depth++; else if (text[end] == ')') depth--; }
            if (depth != 0) continue;
            string call = text[match.Index..end];
            var result = Evaluate(call);
            if (result.Kind == GeometryKind.Scalar) text = text[..match.Index] + "(" + ComputerAlgebra.Number(result.Number) + ")" + text[end..];
        }
        text = Regex.Replace(text, @"\b([xyz])\(([\p{L}_][\p{L}\d_]*)\)", match =>
        {
            if (!values.TryGetValue(match.Groups[2].Value, out var value)) return match.Value;
            var p = Point(value);
            return ComputerAlgebra.Number(match.Groups[1].Value == "x" ? p.X : match.Groups[1].Value == "y" ? p.Y : p.Z);
        });
        foreach (var pair in values.Where(pair => pair.Value.Kind == GeometryKind.Function && bound?.Contains(pair.Key) != true))
        {
            var calls = Regex.Matches(text, @"\b" + Regex.Escape(pair.Key) + @"\s*\(");
            foreach (Match match in calls.Reverse())
            {
                int start = match.Index + match.Length, end = start, depth = 1;
                for (; end < text.Length && depth > 0; end++)
                { if (text[end] == '(') depth++; else if (text[end] == ')') depth--; }
                if (depth != 0) continue;
                string argument = text[start..(end - 1)];
                string substituted = Regex.Replace(pair.Value.Expression, @"\b" + Regex.Escape(pair.Value.Variable) + @"\b", _ => "(" + argument + ")");
                text = text[..match.Index] + "(" + substituted + ")" + text[end..];
            }
        }
        foreach (var pair in values.Where(pair => pair.Value.Kind == GeometryKind.Function && bound?.Contains(pair.Key) != true))
            text = Regex.Replace(text, @"\b" + Regex.Escape(pair.Key) + @"\b(?!\s*\()", "(" + pair.Value.Expression + ")");
        return Regex.Replace(text, @"\b[\p{L}_][\p{L}\d_]*\b", match => bound?.Contains(match.Value) != true && values.TryGetValue(match.Value, out var value)
            && value.Kind is GeometryKind.Scalar or GeometryKind.Boolean or GeometryKind.Complex
            ? "(" + (value.Expression.Length > 0 ? value.Expression : ComputerAlgebra.Number(value.Number)) + ")" : match.Value);
    }
    private Func<double, double, double, double, double, double, double> Compile(string text)
    {
        string expanded = Expand(text);
        return CompileExpanded(expanded);
    }
    private Func<double, double, double, double, double, double, double> CompileExpanded(string expanded)
    {
        if (!_compiled.TryGetValue(expanded, out var result)) _compiled[expanded] = result = ComputerAlgebra.Compile(ComputerAlgebra.Parse(expanded));
        return result;
    }
    private string ExpandBound(string text, params string[] variables)
    {
        return ExpandCore(text,variables.Select(variable => variable.Trim()).ToHashSet(StringComparer.Ordinal));
    }
    private Func<double, double, double, double, double, double, double> CompileBound(string text, params (string Variable, string Target)[] variables)
    {
        text = ExpandBound(text, variables.Select(variable => variable.Variable).ToArray());
        var replacements = variables.ToDictionary(variable => variable.Variable, variable => variable.Target);
        text = Regex.Replace(text, @"\b[\p{L}_][\p{L}\d_]*\b", match => replacements.GetValueOrDefault(match.Value, match.Value));
        return CompileExpanded(text);
    }
    private void Sample()
    {
        if (++_samples > 200_000) throw new InvalidOperationException("作图采样达到上限，请缩小范围。");
        if ((_samples & 127) == 0) token.ThrowIfCancellationRequested();
    }
    private bool Condition(string text)
    {
        var expression = ComputerAlgebra.Parse(Expand(text));
        if (expression.Vars.Count > 0) throw new FormatException("此处需要不含自由变量的逻辑条件。");
        try { return (bool)expression.EvalBoolean(); }
        catch (Exception) { return ComputerAlgebra.Real(expression) != 0; }
    }
    private GeometryValue Function(string expression, string variable = "x", double? minimum = null, double? maximum = null)
    {
        var function = CompileBound(expression, (variable,"x"));
        var paths = SampleCurve(t => new(t, function(t, 0, 0, 0, 0, 0)), minimum ?? view.XMin, maximum ?? view.XMax);
        return new() { Kind = GeometryKind.Function, Expression = expression, Variable = variable, Display = expression, Paths = paths };
    }
    private GeometryValue Implicit(string expression)
    {
        string expanded = Expand(expression);
        if (Regex.IsMatch(expanded, @"\bz\b")) return ImplicitSurface(expanded);
        // The established contour sampler handles discontinuities and cancellation.
        var definition = new PlotDefinition { Formula = expanded, XMin = view.XMin, XMax = view.XMax, YMin = view.YMin, YMax = view.YMax, IncludeAxes = false };
        var mesh = PlotSampler.Sample(CompiledPlot.Compile(definition), definition, token);
        var paths = mesh.Paths.Select(path => path.Select(point => new GeoVector(view.XMin + point.X / PlotSampler.Width * (view.XMax - view.XMin),
            view.YMax - point.Y / PlotSampler.Height * (view.YMax - view.YMin))).ToArray()).ToArray();
        return new() { Kind = GeometryKind.Curve, Display = expression, Expression = expression, Paths = paths };
    }
    private GeometryValue Curve(string[] args)
    {
        bool spatial = args.Length >= 6;
        string variable = args[spatial ? 3 : 2].Trim();
        var x = CompileBound(args[0], (variable,"t")); var y = CompileBound(args[1], (variable,"t"));
        var z = spatial ? CompileBound(args[2], (variable,"t")) : null;
        double min = N(args[spatial ? 4 : 3]), max = N(args[spatial ? 5 : 4]);
        return new() { Kind = GeometryKind.Curve, Display = "参数曲线", Paths = SampleCurve(t => new(x(0, 0, 0, t, 0, 0), y(0, 0, 0, t, 0, 0), z?.Invoke(0, 0, 0, t, 0, 0) ?? 0), min, max) };
    }
    private GeometryValue PolarCurve(string[] args)
    {
        var r = CompileBound(args[0], ("t","t")); double min = args.Length > 1 ? N(args[1]) : 0, max = args.Length > 2 ? N(args[2]) : Math.Tau;
        return new() { Kind = GeometryKind.Curve, Display = "极坐标曲线", Paths = SampleCurve(t => { double radius = r(0, 0, 0, t, 0, 0); return new(radius * Math.Cos(t), radius * Math.Sin(t)); }, min, max) };
    }
    private List<GeoVector[]> SampleCurve(Func<double, GeoVector> evaluate, double min, double max)
    {
        if (!double.IsFinite(min + max) || max <= min) throw new FormatException("参数区间无效。");
        var paths = new List<GeoVector[]>(); var current = new List<GeoVector>();
        double span = Math.Max(view.XMax - view.XMin, view.YMax - view.YMin);
        for (int index = 0; index <= 512; index++)
        {
            Sample(); var point = evaluate(min + (max - min) * index / 512);
            bool valid = point.Finite && Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))) < 1e5;
            if (!valid || current.Count > 0 && (point - current[^1]).Length > span * .4)
            { if (current.Count > 1) paths.Add(current.ToArray()); current.Clear(); }
            if (valid) current.Add(point);
        }
        if (current.Count > 1) paths.Add(current.ToArray());
        return paths;
    }
    private static GeometryValue Vector(GeoVector vector) => new()
    {
        Kind = GeometryKind.Vector,
        Origin = vector,
        Direction = vector,
        Paths = new[] { new[] { new GeoVector(), vector } },
        Display = vector.ToString()
    };
    private static GeometryValue Line(GeoVector origin, GeoVector direction, GeometryKind kind = GeometryKind.Line)
    {
        if (direction.Length < 1e-10) throw new FormatException("两个点重合，无法确定直线。");
        var unit = direction.Unit();
        return new()
        {
            Kind = kind,
            Origin = origin,
            Direction = direction,
            Display = $"{origin} + t {direction}",
            Paths = new[] { kind == GeometryKind.Segment ? new[] { origin, origin + direction } : kind == GeometryKind.Ray ? new[] { origin, origin + unit * 1000 } : new[] { origin - unit * 1000, origin + unit * 1000 } }
        };
    }
    private static (GeoVector U, GeoVector V) CircleBasis(GeoVector normal)
    {
        var n = normal.Unit(); var u = Math.Abs(n.Z) > .99 ? new GeoVector(1, 0, 0) : new GeoVector(0, 0, 1).Cross(n).Unit();
        return (u, n.Cross(u));
    }
    private static GeometryValue Circle(GeoVector center, double radius, GeoVector? normal = null)
    {
        if (!double.IsFinite(radius) || radius <= 1e-10) throw new FormatException("半径必须大于零。");
        var basis = CircleBasis(normal ?? new(0, 0, 1));
        var path = Enumerable.Range(0, 129).Select(i => center + basis.U * (radius * Math.Cos(i * Math.Tau / 128)) + basis.V * (radius * Math.Sin(i * Math.Tau / 128))).ToArray();
        return new() { Kind = GeometryKind.Circle, Origin = center, Radius = radius, Normal = normal ?? new(0, 0, 1), Paths = new[] { path }, Display = $"圆心 {center}，r = {radius:G6}" };
    }
    private static GeometryValue Polygon(GeoVector[] points)
    {
        if (points.Length < 3) throw new FormatException("多边形至少需要三个顶点。");
        var faces = Triangulate(points);
        return new()
        {
            Kind = GeometryKind.Polygon,
            Points = points,
            Paths = new[] { points.Append(points[0]).ToArray() },
            Faces = faces,
            FaceLoops = new[] { points },
            Display = points.Length + " 边形"
        };
    }
    private static GeometryValue Arc(GeoVector center, double radius, double start, double end, bool sector)
    {
        var path = Enumerable.Range(0, 97).Select(i => center + new GeoVector(Math.Cos(start + (end - start) * i / 96), Math.Sin(start + (end - start) * i / 96)) * radius).ToList();
        if (sector) { path.Add(center); path.Add(path[0]); }
        return new() { Kind = sector ? GeometryKind.Region : GeometryKind.Curve, Origin = center, Radius = radius,
            Number = sector ? Math.Abs(end - start) * radius * radius / 2 : 0,
            Paths = new[] { path.ToArray() }, Display = sector ? "扇形" : "圆弧" };
    }
    private static GeometryValue CircumArc(GeometryValue circle, GeoVector first, GeoVector middle, GeoVector last, bool sector)
    {
        var basis = CircleBasis(circle.Normal);
        double Parameter(GeoVector point) => Math.Atan2((point - circle.Origin).Dot(basis.V), (point - circle.Origin).Dot(basis.U));
        double start = Parameter(first), pass = Parameter(middle), end = Parameter(last);
        double Positive(double angle) => (angle % Math.Tau + Math.Tau) % Math.Tau;
        double sweep = Positive(end - start);
        if (Positive(pass - start) > sweep) sweep -= Math.Tau;
        var path = Enumerable.Range(0, 97).Select(i => circle.Origin
            + (basis.U * Math.Cos(start + sweep * i / 96) + basis.V * Math.Sin(start + sweep * i / 96)) * circle.Radius).ToList();
        if (sector) { path.Add(circle.Origin); path.Add(path[0]); }
        return new() { Kind = sector ? GeometryKind.Region : GeometryKind.Curve, Origin = circle.Origin, Normal = circle.Normal,
            Radius = circle.Radius, Number = sector ? Math.Abs(sweep) * circle.Radius * circle.Radius / 2 : 0,
            Paths = new[] { path.ToArray() }, Display = sector ? "外接扇形" : "外接圆弧" };
    }
    private static GeometryValue Circumcircle(GeoVector a, GeoVector b, GeoVector c)
    {
        var u = b - a; var v = c - a; var n = u.Cross(v);
        if (n.Dot(n) < 1e-15) throw new FormatException("三点共线，外接圆不存在。");
        var center = a + (v.Cross(n) * u.Dot(u) + n.Cross(u) * v.Dot(v)) / (2 * n.Dot(n));
        return Circle(center, (a - center).Length, n.Unit());
    }
    private static GeometryValue RegularPolygon(GeoVector a, GeoVector b, int count)
    {
        if (count is < 3 or > 256) throw new FormatException("正多边形边数范围为 3–256。");
        var mid = (a + b) / 2; var d = b - a; var center = mid + new GeoVector(-d.Y, d.X) * (.5 / Math.Tan(Math.PI / count));
        return Polygon(Enumerable.Range(0, count).Select(i => center + Rotate(a - center, new(0, 0, 1), Math.Tau * i / count)).ToArray());
    }
    private static GeoVector Rotate(GeoVector vector, GeoVector axis, double angle)
    { var n = axis.Unit(); return vector * Math.Cos(angle) + n.Cross(vector) * Math.Sin(angle) + n * (n.Dot(vector) * (1 - Math.Cos(angle))); }
    private static GeometryValue Transform(GeometryValue value, Func<GeoVector, GeoVector> transform)
    {
        var origin = transform(value.Origin);
        double scale = (transform(value.Origin + new GeoVector(1, 0, 0)) - origin).Length;
        return value with
        {
            Kind = value.Kind == GeometryKind.Function ? GeometryKind.Curve : value.Kind,
            Origin = origin,
            Direction = transform(value.Origin + value.Direction) - origin,
            Normal = (transform(value.Origin + value.Normal) - origin).Unit(),
            Number = value.Kind == GeometryKind.Solid ? value.Number * scale * scale * scale
                : value.Kind == GeometryKind.Conic ? value.Number * scale : value.Kind == GeometryKind.Region ? value.Number * scale * scale : value.Number,
            Radius = value.Radius > 0 ? (transform(value.Origin + new GeoVector(value.Radius, 0)) - origin).Length : 0,
            Points = value.Points.Select(transform).ToArray(),
            Paths = value.Paths.Select(path => path.Select(transform).ToArray()).ToArray(),
            FaceLoops = value.FaceLoops.Select(path => path.Select(transform).ToArray()).ToArray(),
            Faces = value.Faces.Select(face => new GeoTriangle(transform(face.A), transform(face.B), transform(face.C))).ToArray(),
            Display = "变换 · " + value.Display
        };
    }
    private static double Angle(GeoVector a, GeoVector b) => Math.Acos(Math.Clamp(a.Unit().Dot(b.Unit()), -1, 1));
    private static double Area(GeometryValue value) => value.Kind == GeometryKind.Circle ? Math.PI * value.Radius * value.Radius
        : value.Kind == GeometryKind.Region && value.Faces.Count == 0 ? Math.Abs(value.Number)
        : value.Faces.Sum(face => (face.B - face.A).Cross(face.C - face.A).Length / 2);
    private static double Distance(GeometryValue a, GeometryValue b)
    {
        if (b.Kind == GeometryKind.Point && a.Kind != GeometryKind.Point) return Distance(b, a);
        if (a.Kind == GeometryKind.Point && b.Kind == GeometryKind.Point) return (a.Origin - b.Origin).Length;
        if (a.Kind == GeometryKind.Point && b.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment)
        {
            double t = (a.Origin - b.Origin).Dot(b.Direction) / b.Direction.Dot(b.Direction);
            if (b.Kind == GeometryKind.Segment) t = Math.Clamp(t, 0, 1);
            if (b.Kind == GeometryKind.Ray) t = Math.Max(0, t);
            return (a.Origin - b.Origin - b.Direction * t).Length;
        }
        if (a.Kind == GeometryKind.Point && b.Kind == GeometryKind.Plane) return Math.Abs((a.Origin - b.Origin).Dot(b.Normal));
        throw new FormatException("距离支持点–点、点–直线和点–平面。");
    }
}
