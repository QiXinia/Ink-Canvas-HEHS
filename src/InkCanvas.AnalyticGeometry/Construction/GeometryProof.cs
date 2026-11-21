using AngouriMath;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

/// <summary>Exact polynomial proofs for supported planar point and line constructions.</summary>
internal sealed class GeometryProof
{
    private readonly ConstructionSnapshot _state;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, (Entity X, Entity Y)> _points = new();
    private readonly HashSet<string> _visiting = new();
    private readonly List<string> _conditions = new();
    private int _operations;
    private GeometryProof(ConstructionSnapshot state, CancellationToken token) { _state = state; _token = token; }

    public static GeometryValue Evaluate(ConstructionSnapshot state, string predicate, bool details, CancellationToken token)
    {
        var proof = new GeometryProof(state, token);
        var referenced = state.Objects.FirstOrDefault(item => item.Label == predicate);
        if (referenced is not null) predicate = referenced.Definition;
        if (!MathSyntax.Call(predicate, out string command, out var args)) throw new FormatException("证明需要 AreParallel、ArePerpendicular、AreCollinear、AreConcyclic 或 AreEqual 命题。");
        Entity polynomial;
        switch (command.ToLowerInvariant())
        {
            case "areparallel":
                var l = proof.Line(args[0]); var m = proof.Line(args[1]);
                polynomial = l.A * m.B - m.A * l.B; break;
            case "areperpendicular":
                var p = proof.Line(args[0]); var q = proof.Line(args[1]);
                polynomial = p.A * q.A + p.B * q.B; break;
            case "arecollinear":
                var line = proof.Through(proof.Point(args[0]), proof.Point(args[1])); var c = proof.Point(args[2]);
                polynomial = line.A * c.X + line.B * c.Y + line.C; break;
            case "areequal":
                var a = proof.Point(args[0]); var b = proof.Point(args[1]);
                polynomial = Square(a.X - b.X) + Square(a.Y - b.Y); break;
            case "areconcyclic":
                var points = args.Select(proof.Point).ToArray();
                if (points.Length != 4) throw new FormatException("共圆证明需要四个点。");
                var rows = points.Select(point => new Entity[] { Square(point.X) + Square(point.Y), point.X, point.Y, 1 }).ToArray();
                var matrix = MathS.Matrix(4, 4, (row, column) => rows[row][column]);
                polynomial = matrix.Determinant; break;
            default: throw new FormatException("严格证明目前支持平行、垂直、三点共线、四点共圆和点重合命题。");
        }
        token.ThrowIfCancellationRequested();
        string remainder = polynomial.Expand().Simplify().ToString();
        bool proven = remainder == "0";
        string display = proven ? "true · 符号恒等式证明" : "未证明 · 化简余式为 " + remainder;
        if (details && proof._conditions.Count > 0)
            display += "\n非退化条件 " + string.Join("，", proof._conditions.Distinct());
        return new() { Kind = proven ? GeometryKind.Boolean : GeometryKind.Text, Number = proven ? 1 : 0, Display = display, Expression = remainder };
    }

    private static Entity Square(Entity value) => value * value;
    private void Budget()
    {
        _token.ThrowIfCancellationRequested();
        if (++_operations > 256) throw new FormatException("证明构造过于复杂。");
    }
    private string Definition(string text) => _state.Objects.FirstOrDefault(item => item.Label == text)?.Definition ?? text;
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
                var item = _state.Objects.FirstOrDefault(item => item.Label == label);
                point = item is not null && !item.Fixed
                    ? (MathS.Var(label + "_x"), MathS.Var(label + "_y"))
                    : (ComputerAlgebra.Parse(coordinates[0]), ComputerAlgebra.Parse(coordinates[1]));
            }
            else if (MathSyntax.Call(definition, out string command, out var args))
            {
                switch (command.ToLowerInvariant())
                {
                    case "midpoint":
                        var a = Point(args[0]); var b = Point(args[1]); point = ((a.X + b.X) / 2, (a.Y + b.Y) / 2); break;
                    case "intersect":
                        var l = Line(args[0]); var m = Line(args[1]);
                        Entity determinant = l.A * m.B - m.A * l.B;
                        _conditions.Add(determinant.Simplify().ToString() + " ≠ 0");
                        point = ((l.B * m.C - m.B * l.C) / determinant, (l.C * m.A - m.C * l.A) / determinant); break;
                    case "translate":
                        var original = Point(args[0]); var vector = Point(args[1]); point = (original.X + vector.X, original.Y + vector.Y); break;
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

    private (Entity A, Entity B, Entity C) Through((Entity X, Entity Y) a, (Entity X, Entity Y) b)
    {
        var line = (A: a.Y - b.Y, B: b.X - a.X, C: a.X * b.Y - b.X * a.Y);
        _conditions.Add((Square(line.A) + Square(line.B)).Simplify().ToString() + " ≠ 0");
        return line;
    }

    private (Entity A, Entity B, Entity C) Line(string text)
    {
        Budget();
        if (!MathSyntax.Call(Definition(text), out string command, out var args)) throw new FormatException("证明需要由点构造的直线。");
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
            var a = Point(args[0]); var b = Point(args[1]);
            Entity x = b.X - a.X, y = b.Y - a.Y;
            return (x, y, -(Square(b.X) + Square(b.Y) - Square(a.X) - Square(a.Y)) / 2);
        }
        throw new FormatException("此直线的构造尚不支持严格证明 · " + command);
    }
}
