using System.Text.RegularExpressions;
using AngouriMath;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

/// <summary>Exact polynomial proofs for supported geometric constructions.</summary>
internal sealed class GeometryProof
{
    private readonly ConstructionSnapshot _state;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, ConstructionObject> _objects;
    private readonly Dictionary<string, (Entity X, Entity Y)> _points = new();
    private readonly Dictionary<string, SymbolicPosition> _spatialPoints = new();
    private readonly HashSet<string> _visiting = new();
    private readonly List<Entity> _conditions = new();
    private readonly List<(Entity.Variable Variable, Entity Equation)> _equations = new();
    private int _operations;
    private int _intersectionIndex;
    private HashSet<string> _names;
    private string _predicate;
    private GeometryProof(ConstructionSnapshot state, CancellationToken token)
    {
        _state = state;
        _token = token;
        _objects = state.Objects.ToDictionary(item => item.Label, StringComparer.Ordinal);
    }

    public static GeometryValue Evaluate(ConstructionSnapshot state, string predicate, bool details, CancellationToken token)
    {
        var proof = new GeometryProof(state, token);
        var referenced = proof._objects.GetValueOrDefault(predicate);
        if (referenced is not null) predicate = referenced.Definition;
        try { return proof.Prove(predicate, details); }
        catch (FormatException ex) { return new() { Kind = GeometryKind.Text, Display = "未证明 · " + ex.Message }; }
    }

    private GeometryValue Prove(string predicate, bool details)
    {
        _predicate = predicate;
        if (!MathSyntax.Call(predicate, out string command, out var args))
        {
            var terms = MathSyntax.Split(predicate, '=');
            if (terms.Length != 3 || terms[1].Length != 0) throw new FormatException("证明需要几何关系或等式命题。");
            command = "AreEqual";
            args = [terms[0], terms[2]];
        }
        Entity polynomial;
        switch (command.ToLowerInvariant())
        {
            case "areparallel":
                polynomial = LineDirection(args[0]).Cross(LineDirection(args[1])).SquaredLength; break;
            case "areperpendicular":
                polynomial = LineDirection(args[0]).Dot(LineDirection(args[1])); break;
            case "arecollinear":
                var start = SpatialPoint(args[0]);
                var direction = SpatialPoint(args[1]) - start;
                _conditions.Add(direction.SquaredLength.Simplify());
                polynomial = direction.Cross(SpatialPoint(args[2]) - start).SquaredLength; break;
            case "arecoplanar":
                if (args.Length != 4) throw new FormatException("共面证明需要四个点。");
                var origin = SpatialPoint(args[0]);
                polynomial = (SpatialPoint(args[1]) - origin).Cross(SpatialPoint(args[2]) - origin)
                    .Dot(SpatialPoint(args[3]) - origin); break;
            case "areequal":
                polynomial = EqualityResidual(args[0], args[1]); break;
            case "istangent":
                polynomial = TangencyResidual(args[0], args[1]); break;
            case "areconcurrent":
                var first = Line(args[0]); var second = Line(args[1]); var third = Line(args[2]);
                var determinant = first.A * second.B - second.A * first.B;
                _conditions.Add(determinant.Simplify());
                polynomial = determinant * third.C + (first.B * second.C - second.B * first.C) * third.A
                    + (first.C * second.A - second.C * first.A) * third.B;
                break;
            case "arecongruent":
                polynomial = LengthSquared(args[0]) - LengthSquared(args[1]); break;
            case "areconcyclic":
                var points = args.Select(Point).ToArray();
                if (points.Length != 4) throw new FormatException("共圆证明需要四个点。");
                var firstSide = Through(points[0], points[1]);
                _conditions.Add((firstSide.A * points[2].X + firstSide.B * points[2].Y + firstSide.C).Simplify());
                var rows = points.Select(point => new Entity[] { Square(point.X) + Square(point.Y), point.X, point.Y, 1 }).ToArray();
                var matrix = MathS.Matrix(4, 4, (row, column) => rows[row][column]);
                polynomial = matrix.Determinant; break;
            default: return new() { Kind = GeometryKind.Text, Display = "未证明 · 此命题缺少符号模型。" };
        }
        _token.ThrowIfCancellationRequested();
        var residual = polynomial.Expand().Simplify();
        bool usedEquations = residual.ToString() != "0" && _equations.Count != 0;
        if (usedEquations)
            for (int index = _equations.Count - 1; index >= 0 && residual.ToString() != "0"; index--)
                residual = Reduce(residual, _equations[index].Variable, _equations[index].Equation);
        string remainder = residual.ToString();
        bool proven = remainder == "0" && !_conditions.Any(condition => condition.ToString() == "0");
        bool disproven = remainder != "0" && _equations.Count == 0 && HasRationalCounterexample(polynomial);
        string display = proven ? usedEquations ? "true · 构造约束证明" : "true · 符号恒等式证明"
            : disproven ? "false · 存在精确反例" : "未证明 · 化简余式为 " + remainder;
        if (details && _conditions.Count > 0)
            display += "\n非退化条件 " + string.Join("，", _conditions.Distinct().Select(condition => $"{condition} ≠ 0"));
        if (details && _equations.Count > 0)
            display += "\n交点存在条件 " + string.Join("，", _equations.Select(item => $"{item.Equation} = 0"));
        return new() { Kind = proven || disproven ? GeometryKind.Boolean : GeometryKind.Text,
            Number = proven ? 1 : 0, Display = display, Expression = remainder };
    }

    private Entity Reduce(Entity residual, Entity.Variable variable, Entity equation)
    {
        if (!residual.Vars.Contains(variable)
            || !MathS.Utils.TryGetPolynomial(equation, variable, out var divisor) || divisor.Count == 0)
            return residual;
        var degree = divisor.Keys.Max();
        if (degree.Sign <= 0) return residual;

        var leading = divisor[degree].Simplify();
        if (leading.ToString() == "0" || !MathS.Utils.TryGetPolynomial(residual, variable, out var terms)
            || terms.Count == 0 || terms.Keys.Max().CompareTo(degree) < 0
            || terms.Keys.Max().CompareTo(PeterO.Numbers.EInteger.FromInt32(32)) > 0)
            return residual;

        // Pseudo-division uses exact coefficients and never divides by a symbolic leading term.
        for (int step = 0; step < 16 && terms.Count > 0; step++)
        {
            Budget();
            var highest = terms.Keys.Max();
            if (highest.CompareTo(degree) < 0) break;
            var coefficient = terms[highest];
            terms.Remove(highest);
            var next = terms.ToDictionary(term => term.Key, term => leading * term.Value);
            foreach (var term in divisor)
            {
                if (term.Key.Equals(degree)) continue;
                var power = term.Key.Add(highest.Subtract(degree));
                var contribution = coefficient * term.Value;
                next[power] = next.TryGetValue(power, out var previous) ? previous - contribution : -contribution;
            }
            terms = next.Select(term => (term.Key, Value: term.Value.Expand().Simplify()))
                .Where(term => term.Value.ToString() != "0").ToDictionary(term => term.Key, term => term.Value);
        }
        if (leading.ToString() != "1") _conditions.Add(leading);
        return terms.Aggregate((Entity)0, (sum, term) => term.Key.IsZero ? sum + term.Value
            : sum + term.Value * MathS.Pow(variable, term.Key.ToInt32Checked())).Simplify();
    }

    private bool HasRationalCounterexample(Entity polynomial)
    {
        var variables = polynomial.Vars.Concat(_conditions.SelectMany(condition => condition.Vars)).Distinct().ToArray();
        if (variables.Length > 32) return false;
        for (int sample = 0; sample < 4; sample++)
        {
            _token.ThrowIfCancellationRequested();
            Entity Substitute(Entity expression)
            {
                for (int index = 0; index < variables.Length; index++)
                {
                    Entity value = MathS.Numbers.CreateRational(((index + 1) * (index + sample + 2) + sample * 3) % 37 + 1, 39);
                    expression = expression.Substitute(variables[index], value);
                }
                return expression.Simplify();
            }
            if (_conditions.All(condition => IsNonZeroRational(Substitute(condition))) && IsNonZeroRational(Substitute(polynomial)))
                return true;
        }
        return false;
    }

    private static bool IsNonZeroRational(Entity value) => value is Entity.Number.Rational number && number.ToString() != "0";

    private Entity EqualityResidual(string first, string second)
    {
        int previousConditionCount = _conditions.Count;
        int previousEquationCount = _equations.Count;
        try
        {
            return (SpatialPoint(first) - SpatialPoint(second)).SquaredLength;
        }
        catch (FormatException)
        {
            _conditions.RemoveRange(previousConditionCount, _conditions.Count - previousConditionCount);
            _equations.RemoveRange(previousEquationCount, _equations.Count - previousEquationCount);
            _points.Clear();
            _spatialPoints.Clear();
            var left = Scalar(first);
            var right = Scalar(second);
            var half = MathS.Numbers.CreateRational(1, 2);
            // Principal square roots are injective, so compare their radicands directly.
            var leftRadicand = left is Entity.Powf leftRoot && leftRoot.NodeSecondChild.Equals(half) ? leftRoot.NodeFirstChild : null;
            var rightRadicand = right is Entity.Powf rightRoot && rightRoot.NodeSecondChild.Equals(half) ? rightRoot.NodeFirstChild : null;
            if (leftRadicand is not null && rightRadicand is not null) return leftRadicand - rightRadicand;
            if (leftRadicand is not null && right is Entity.Number.Rational rightNumber && rightNumber.ToNumerics().Real >= 0)
                return leftRadicand - Square(right);
            if (rightRadicand is not null && left is Entity.Number.Rational leftNumber && leftNumber.ToNumerics().Real >= 0)
                return Square(left) - rightRadicand;
            return left - right;
        }
    }

    private static Entity Square(Entity value) => value * value;
    private Entity LengthSquared(string label)
    {
        if (!MathSyntax.Call(Definition(label), out string command, out var args) || args.Length != 2
            || command.ToLowerInvariant() is not ("segment" or "vector"))
            throw new FormatException("全等证明需要线段或两点定义的向量。");
        return (SpatialPoint(args[1]) - SpatialPoint(args[0])).SquaredLength;
    }
    private void Budget()
    {
        _token.ThrowIfCancellationRequested();
        if (++_operations > 256) throw new FormatException("证明构造过于复杂。");
    }
    private string Definition(string text) => ConstructionDocument.ExpandTool(
        _objects.GetValueOrDefault(text)?.Definition ?? text, _state);
    private readonly record struct SymbolicPosition(Entity X, Entity Y, Entity Z)
    {
        public static SymbolicPosition operator +(SymbolicPosition a, SymbolicPosition b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static SymbolicPosition operator -(SymbolicPosition a, SymbolicPosition b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static SymbolicPosition operator *(SymbolicPosition value, Entity scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
        public Entity Dot(SymbolicPosition other) => X * other.X + Y * other.Y + Z * other.Z;
        public SymbolicPosition Cross(SymbolicPosition other)
            => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
        public Entity SquaredLength => Square(X) + Square(Y) + Square(Z);
    }

    private SymbolicPosition SpatialPoint(string label)
    {
        Budget();
        if (_spatialPoints.TryGetValue(label, out var cached)) return cached;
        string definition = Definition(label);
        if (definition.StartsWith('(') && definition.EndsWith(')'))
        {
            var coordinates = MathSyntax.Split(definition[1..^1]);
            if (coordinates.Length == 3)
            {
                var item = _objects.GetValueOrDefault(label);
                return _spatialPoints[label] = item is not null && !item.Fixed && ConstructionDocument.IsFreePoint(item)
                    ? new(MathS.Var(label + "_x"), MathS.Var(label + "_y"), MathS.Var(label + "_z"))
                    : new(Scalar(coordinates[0]), Scalar(coordinates[1]), Scalar(coordinates[2]));
            }
        }
        else if (MathSyntax.Call(definition, out string command, out var args))
        {
            switch (command.ToLowerInvariant())
            {
                case "midpoint" when args.Length == 2:
                    return _spatialPoints[label] = (SpatialPoint(args[0]) + SpatialPoint(args[1])) * MathS.Numbers.CreateRational(1, 2);
                case "midpoint" when args.Length == 1 && MathSyntax.Call(Definition(args[0]), out string segment, out var endpoints)
                    && segment.Equals("Segment", StringComparison.OrdinalIgnoreCase) && endpoints.Length == 2:
                    return _spatialPoints[label] = (SpatialPoint(endpoints[0]) + SpatialPoint(endpoints[1])) * MathS.Numbers.CreateRational(1, 2);
                case "translate" when args.Length == 2:
                    return _spatialPoints[label] = SpatialPoint(args[0]) + SpatialPoint(args[1]);
                case "centroid" when args.Length == 1 && MathSyntax.Call(Definition(args[0]), out string polygon, out var vertices)
                    && polygon.Equals("Polygon", StringComparison.OrdinalIgnoreCase) && vertices.Length == 3:
                    var first = SpatialPoint(vertices[0]);
                    var second = SpatialPoint(vertices[1]);
                    var third = SpatialPoint(vertices[2]);
                    _conditions.Add((second - first).Cross(third - first).SquaredLength.Simplify());
                    return _spatialPoints[label] = (first + second + third) * MathS.Numbers.CreateRational(1, 3);
                case "vector" when args.Length == 1:
                    return _spatialPoints[label] = SpatialPoint(args[0]);
                case "vector" when args.Length == 2:
                    return _spatialPoints[label] = SpatialPoint(args[1]) - SpatialPoint(args[0]);
            }
        }

        var planar = Point(label);
        return _spatialPoints[label] = new(planar.X, planar.Y, 0);
    }

    private SymbolicPosition LineDirection(string label)
    {
        Budget();
        if (MathSyntax.Call(Definition(label), out string command, out var args) && args.Length == 2
            && command.ToLowerInvariant() is "line" or "segment" or "ray")
        {
            var direction = SpatialPoint(args[1]) - SpatialPoint(args[0]);
            _conditions.Add(direction.SquaredLength.Simplify());
            return direction;
        }
        var line = Line(label);
        return new(line.B, -line.A, 0);
    }

    private (Entity X, Entity Y) Point(string label)
    {
        Budget();
        if (_points.TryGetValue(label, out var cached)) return cached;
        if (!_visiting.Add(label)) throw new FormatException("证明依赖存在循环。");
        try
        {
            string definition = Definition(label);
            (Entity X, Entity Y) point;
            if (definition.StartsWith('(') && definition.EndsWith(')'))
            {
                var coordinates = MathSyntax.Split(definition[1..^1]);
                if (coordinates.Length != 2) throw new FormatException("严格证明仅支持平面构造。");
                var item = _objects.GetValueOrDefault(label);
                point = item is not null && !item.Fixed && ConstructionDocument.IsFreePoint(item)
                    ? (MathS.Var(label + "_x"), MathS.Var(label + "_y"))
                    : (Scalar(coordinates[0]), Scalar(coordinates[1]));
            }
            else if (MathSyntax.Call(definition, out string command, out var args))
            {
                switch (command.ToLowerInvariant())
                {
                    case "midpoint" when args.Length == 1 && MathSyntax.Call(Definition(args[0]), out var segment, out var endpoints)
                        && segment.Equals("Segment", StringComparison.OrdinalIgnoreCase) && endpoints.Length == 2:
                        point = Midpoint(Point(endpoints[0]), Point(endpoints[1])); break;
                    case "midpoint":
                        point = Midpoint(Point(args[0]), Point(args[1])); break;
                    case "intersect" when args.Length is 2 or 3:
                        point = Intersection(args[0], args[1]); break;
                    case "constructionoutput" when args.Length == 2 && MathSyntax.Call(args[0], out string sourceCommand, out var sourceArgs)
                        && sourceCommand.Equals("Intersect", StringComparison.OrdinalIgnoreCase) && sourceArgs.Length == 2:
                        point = Intersection(sourceArgs[0], sourceArgs[1]); break;
                    case "translate":
                        var original = Point(args[0]); var vector = Point(args[1]); point = (original.X + vector.X, original.Y + vector.Y); break;
                    case "rotate" when args.Length is 2 or 3:
                        var source = Point(args[0]);
                        (Entity X, Entity Y) pivot = args.Length == 3 ? Point(args[2]) : (0, 0);
                        var angle = Scalar(args[1]);
                        var cosine = MathS.Cos(angle);
                        var sine = MathS.Sin(angle);
                        var x = source.X - pivot.X;
                        var y = source.Y - pivot.Y;
                        point = (pivot.X + x * cosine - y * sine, pivot.Y + x * sine + y * cosine);
                        break;
                    case "reflect" when args.Length == 2:
                        var reflected = Point(args[0]);
                        if (MathSyntax.Call(Definition(args[1]), out var mirror, out _) && mirror.ToLowerInvariant() is
                            "line" or "parallelline" or "parallel" or "perpendicular" or "perpendicularline" or "perpendicularbisector" or "lineequation")
                        {
                            var axis = Line(args[1]);
                            var projection = (axis.A * reflected.X + axis.B * reflected.Y + axis.C) / (Square(axis.A) + Square(axis.B));
                            point = (reflected.X - 2 * projection * axis.A, reflected.Y - 2 * projection * axis.B);
                        }
                        else
                        {
                            var reflectionCenter = Point(args[1]);
                            point = (2 * reflectionCenter.X - reflected.X, 2 * reflectionCenter.Y - reflected.Y);
                        }
                        break;
                    case "dilate": case "homothety":
                        var dilated = Point(args[0]);
                        var center = args.Length > 2 ? Point(args[2]) : ((Entity)0, (Entity)0);
                        var factor = Scalar(args[1]);
                        point = (center.Item1 + (dilated.X - center.Item1) * factor, center.Item2 + (dilated.Y - center.Item2) * factor);
                        break;
                    case "point": case "pointonobject" when args.Length == 2:
                        if (args.Length != 2) throw new FormatException("证明需要平面点或直线路径参数。");
                        if (command.Equals("Point", StringComparison.OrdinalIgnoreCase)
                            && !_state.Objects.Any(item => item.Label == args[0] && !double.TryParse(item.Definition,
                                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
                        {
                            point = (Scalar(args[0]), Scalar(args[1]));
                            break;
                        }
                        if (!MathSyntax.Call(Definition(args[0]), out var path, out var ends) || ends.Length != 2)
                            throw new FormatException("此路径尚无符号参数化。");
                        Entity parameter = MathS.Var(label + "_t");
                        var start = Point(ends[0]);
                        if (path.Equals("Circle", StringComparison.OrdinalIgnoreCase))
                        {
                            Entity radius = Scalar(ends[1]);
                            if (_state.Objects.Any(item => item.Label == ends[1] && item.Definition.StartsWith('(')))
                            {
                                var radiusPoint = Point(ends[1]);
                                radius = MathS.Sqrt(Square(radiusPoint.X - start.X) + Square(radiusPoint.Y - start.Y));
                            }
                            Entity denominator = 1 + Square(parameter);
                            point = (start.X + radius * (1 - Square(parameter)) / denominator, start.Y + radius * (2 * parameter) / denominator);
                        }
                        else
                        {
                            if (path.ToLowerInvariant() is not ("line" or "segment" or "ray"))
                                throw new FormatException("此路径尚无符号参数化。");
                            var end = Point(ends[1]);
                            point = (start.X + (end.X - start.X) * parameter, start.Y + (end.Y - start.Y) * parameter);
                        }
                        break;
                    case "vector":
                        if (args.Length == 1) point = Point(args[0]);
                        else { var from = Point(args[0]); var to = Point(args[1]); point = (to.X - from.X, to.Y - from.Y); }
                        break;
                    default: throw new FormatException("此点的构造尚不支持严格证明 · " + command);
                }
            }
            else if (definition != label) point = Point(definition);
            else throw new FormatException("未找到符号点 " + label);
            return _points[label] = point;
        }
        finally { _visiting.Remove(label); }
    }

    private Entity TangencyResidual(string lineName, string conicName)
    {
        var line = Line(lineName);
        var normalSquared = (Square(line.A) + Square(line.B)).Simplify();
        _conditions.Add(normalSquared);
        if (CircleDefinition(conicName) is { } circle)
            return Square(line.A * circle.X + line.B * circle.Y + line.C) - normalSquared * circle.RadiusSquared;

        var equation = ConicPolynomial(conicName) ?? throw new FormatException("切线证明需要圆或二次方程。");
        var t = IntersectionVariable("t");
        var offset = -line.C / normalSquared;
        var trace = equation(offset * line.A - t * line.B, offset * line.B + t * line.A).Expand().Simplify();
        if (!MathS.Utils.TryGetPolynomial(trace, t, out var coefficients)
            || coefficients.Keys.Any(degree => degree.CompareTo(PeterO.Numbers.EInteger.FromInt32(2)) > 0))
            throw new FormatException("切线证明需要二次方程。");
        Entity Coefficient(int degree) => coefficients.GetValueOrDefault(PeterO.Numbers.EInteger.FromInt32(degree), (Entity)0);
        var quadratic = Coefficient(2).Simplify();
        if (quadratic.ToString() == "0") throw new FormatException("该方向没有唯一有限切点。");
        _conditions.Add(quadratic);
        return Square(Coefficient(1)) - 4 * quadratic * Coefficient(0);
    }

    private (Entity X, Entity Y) Intersection(string first, string second)
    {
        var left = CircleDefinition(first);
        var right = CircleDefinition(second);
        var leftPolynomial = ConicPolynomial(first);
        var rightPolynomial = ConicPolynomial(second);
        if (leftPolynomial is not null || rightPolynomial is not null)
        {
            var u = IntersectionVariable("x");
            var v = IntersectionVariable("y");
            var primary = leftPolynomial ?? rightPolynomial;
            var secondary = leftPolynomial is not null ? rightPolynomial : leftPolynomial;
            var circleArgument = leftPolynomial is not null ? right : left;
            Entity other;
            if (secondary is not null) other = secondary(u, v);
            else if (circleArgument is { } circleData)
                other = Square(u - circleData.X) + Square(v - circleData.Y) - circleData.RadiusSquared;
            else
            {
                var lineData = Line(leftPolynomial is not null ? second : first);
                other = lineData.A * u + lineData.B * v + lineData.C;
            }

            var constraint = primary(u, v).Expand().Simplify();
            other = other.Expand().Simplify();
            var eliminationVariable = constraint.Vars.Contains(u) && other.Vars.Contains(u) ? u : v;
            var alternative = eliminationVariable == u ? v : u;
            // Both defining equations may be reduced directly. Their remainder adds a lower-degree consequence.
            if (constraint.Vars.Contains(eliminationVariable) && other.Vars.Contains(eliminationVariable))
            {
                var reduced = Reduce(constraint, eliminationVariable, other);
                if (reduced.ToString() != "0" && !reduced.Equals(constraint))
                    _equations.Add((reduced.Vars.Contains(eliminationVariable) ? eliminationVariable : alternative, reduced));
            }
            _equations.Add((other.Vars.Contains(eliminationVariable) ? eliminationVariable : alternative, other));
            _equations.Add((constraint.Vars.Contains(eliminationVariable) ? eliminationVariable : alternative, constraint));
            return (u, v);
        }

        if (left is null && right is null)
        {
            var l = Line(first);
            var m = Line(second);
            var determinant = (l.A * m.B - m.A * l.B).Simplify();
            _conditions.Add(determinant);
            return ((l.B * m.C - m.B * l.C) / determinant, (l.C * m.A - m.C * l.A) / determinant);
        }

        var circle = left ?? right.Value;
        var line = left is { } a && right is { } b
            ? (A: 2 * (b.X - a.X), B: 2 * (b.Y - a.Y),
                C: Square(a.X) + Square(a.Y) - a.RadiusSquared - Square(b.X) - Square(b.Y) + b.RadiusSquared)
            : Line(left is null ? first : second);
        var aCoefficient = line.A.Simplify();
        var bCoefficient = line.B.Simplify();
        _conditions.Add((Square(aCoefficient) + Square(bCoefficient)).Simplify());

        var x = IntersectionVariable("x");
        var y = IntersectionVariable("y");
        bool preferY = aCoefficient.ToString() == "0"
            || bCoefficient is Entity.Number.Rational && aCoefficient is not Entity.Number.Rational;
        var pivot = preferY ? y : x;
        _equations.Add((pivot, (aCoefficient * x + bCoefficient * y + line.C).Expand().Simplify()));
        _equations.Add((pivot, (Square(x - circle.X) + Square(y - circle.Y) - circle.RadiusSquared).Expand().Simplify()));
        return (x, y);
    }

    private Entity.Variable IntersectionVariable(string axis)
    {
        _names ??= _state.Objects.SelectMany(item => SymbolNames(item.Definition))
            .Concat(_state.Tools.SelectMany(tool => SymbolNames(tool.Definition)))
            .Concat(SymbolNames(_predicate)).Concat(_objects.Keys).ToHashSet(StringComparer.Ordinal);
        string name;
        do { name = $"intersection_{_intersectionIndex++}_{axis}"; } while (!_names.Add(name));
        return MathS.Var(name);

        static IEnumerable<string> SymbolNames(string definition)
            => Regex.Matches(definition, @"[\p{L}_][\p{L}\d_]*").Select(match => match.Value);
    }

    private Func<Entity, Entity, Entity> ConicPolynomial(string label)
    {
        if (!MathSyntax.Call(Definition(label), out string command, out var arguments)) return null;
        if (command.Equals("ConicEquation", StringComparison.OrdinalIgnoreCase) && arguments.Length == 1)
        {
            Entity.Variable x = MathS.Var("x"), y = MathS.Var("y");
            var equation = Scalar(MathSyntax.EquationDifference(arguments[0]), new HashSet<string>(StringComparer.Ordinal) { "x", "y" });
            return (u, v) => equation.Substitute(new Dictionary<Entity.Variable, Entity> { [x] = u, [y] = v });
        }
        if (!command.Equals("Conic", StringComparison.OrdinalIgnoreCase)) return null;
        if (arguments.Length == 1 && Definition(arguments[0]) is { } list && list.StartsWith('{') && list.EndsWith('}'))
            arguments = MathSyntax.Split(list[1..^1]);
        if (arguments.Length != 6) return null;
        var terms = arguments.Select(term => Scalar(term)).ToArray();
        return (x, y) => terms[0] * Square(x) + terms[1] * Square(y) + terms[2]
            + terms[3] * x * y + terms[4] * x + terms[5] * y;
    }

    private (Entity X, Entity Y, Entity RadiusSquared)? CircleDefinition(string label)
    {
        if (!MathSyntax.Call(Definition(label), out string command, out var args) || args.Length != 2
            || !command.Equals("Circle", StringComparison.OrdinalIgnoreCase)) return null;
        var center = Point(args[0]);
        string radiusDefinition = Definition(args[1]);
        if (radiusDefinition.StartsWith('(') && radiusDefinition.EndsWith(')')
            && MathSyntax.Split(radiusDefinition[1..^1]).Length == 2)
        {
            var endpoint = Point(args[1]);
            var radiusSquared = (Square(endpoint.X - center.X) + Square(endpoint.Y - center.Y)).Simplify();
            _conditions.Add(radiusSquared);
            return (center.X, center.Y, radiusSquared);
        }
        var radius = Scalar(args[1]);
        _conditions.Add(radius.Simplify());
        return (center.X, center.Y, Square(radius));
    }

    private static (Entity X, Entity Y) Midpoint((Entity X, Entity Y) a, (Entity X, Entity Y) b)
        => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private Entity Scalar(string text, IReadOnlySet<string> bound = null)
    {
        Budget();
        var terms = new Dictionary<Entity.Variable, Entity>();
        foreach (Match match in Regex.Matches(text, @"\b(x|y|z|Distance|Length)\s*\(", RegexOptions.IgnoreCase).Reverse())
        {
            int end = match.Index + match.Length;
            int depth = 1;
            for (; end < text.Length && depth > 0; end++)
            {
                if (text[end] == '(') depth++;
                else if (text[end] == ')') depth--;
            }
            if (depth > 0 || !MathSyntax.Call(text[match.Index..end], out string command, out var arguments)) continue;
            Entity value;
            switch (command.ToLowerInvariant())
            {
                case "x" when arguments.Length == 1: value = SpatialPoint(arguments[0]).X; break;
                case "y" when arguments.Length == 1: value = SpatialPoint(arguments[0]).Y; break;
                case "z" when arguments.Length == 1: value = SpatialPoint(arguments[0]).Z; break;
                case "length" when arguments.Length == 1: value = MathS.Sqrt(LengthSquared(arguments[0])); break;
                case "distance" when arguments.Length == 2:
                    value = MathS.Sqrt((SpatialPoint(arguments[0]) - SpatialPoint(arguments[1])).SquaredLength); break;
                default: throw new FormatException("不支持此几何度量的符号定义。");
            }
            int placeholder = terms.Count;
            string name = "m_" + placeholder;
            while (_objects.ContainsKey(name) || text.Contains(name, StringComparison.Ordinal)) name = "m_" + ++placeholder;
            terms.Add(MathS.Var(name), value);
            text = text[..match.Index] + name + text[end..];
        }
        var expression = ComputerAlgebra.Parse(text.Replace("°", "*pi/180"));
        foreach (var variable in expression.Vars)
        {
            if (bound?.Contains(variable.ToString()) == true) continue;
            var item = _objects.GetValueOrDefault(variable.ToString());
            if (item is null) continue;
            if (!_visiting.Add(item.Label)) throw new FormatException("证明依赖存在循环。");
            try
            {
                if (!item.Fixed && double.TryParse(item.Definition, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                    continue;
                expression = expression.Substitute(variable, Scalar(item.Definition, bound));
            }
            finally { _visiting.Remove(item.Label); }
        }
        return expression.Substitute(terms);
    }

    private (Entity A, Entity B, Entity C) Through((Entity X, Entity Y) a, (Entity X, Entity Y) b)
    {
        var line = (A: a.Y - b.Y, B: b.X - a.X, C: a.X * b.Y - b.X * a.Y);
        _conditions.Add((Square(line.A) + Square(line.B)).Simplify());
        return line;
    }

    private (Entity A, Entity B, Entity C) Line(string text)
    {
        Budget();
        if (!MathSyntax.Call(Definition(text), out string command, out var args)) throw new FormatException("证明需要由点构造的直线。");
        if (command.Equals("LineEquation", StringComparison.OrdinalIgnoreCase))
        {
            var equation = Scalar(MathSyntax.EquationDifference(args[0]), new HashSet<string>(StringComparer.Ordinal) { "x", "y" });
            Entity.Variable x = MathS.Var("x"), y = MathS.Var("y");
            var a = equation.Differentiate(x).Simplify();
            var b = equation.Differentiate(y).Simplify();
            if (a.Vars.Contains(x) || a.Vars.Contains(y) || b.Vars.Contains(x) || b.Vars.Contains(y))
                throw new FormatException("直线方程必须对坐标为一次式。");
            _conditions.Add((Square(a) + Square(b)).Simplify());
            return (a, b, equation.Substitute(x, 0).Substitute(y, 0));
        }
        if (command.ToLowerInvariant() is "line" or "segment" or "ray") return Through(Point(args[0]), Point(args[1]));
        if (command.ToLowerInvariant() is "parallel" or "parallelline" or "perpendicular" or "perpendicularline")
        {
            var point = Point(args[0]); var original = Line(args[1]);
            Entity a = original.A, b = original.B;
            if (command.StartsWith("perpendicular", StringComparison.OrdinalIgnoreCase)) (a, b) = (-b, a);
            return (a, b, -a * point.X - b * point.Y);
        }
        if (command.Equals("PerpendicularBisector", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length == 1 && MathSyntax.Call(Definition(args[0]), out string segment, out var endpoints)
                && segment.Equals("Segment", StringComparison.OrdinalIgnoreCase)) args = endpoints;
            if (args.Length != 2) throw new FormatException("垂直平分线证明需要线段或两个端点。");
            var a = Point(args[0]); var b = Point(args[1]);
            Entity x = b.X - a.X, y = b.Y - a.Y;
            _conditions.Add((Square(x) + Square(y)).Simplify());
            return (x, y, -(Square(b.X) + Square(b.Y) - Square(a.X) - Square(a.Y)) / 2);
        }
        throw new FormatException("此直线的构造尚不支持严格证明 · " + command);
    }
}
