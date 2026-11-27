using AngouriMath;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private static GeometryValue Collection(IEnumerable<GeometryValue> items)
    {
        var array = items.ToArray();
        return new()
        {
            Kind = GeometryKind.List,
            Items = array,
            Display = "{" + string.Join(", ", array.Select(value => value.Display)) + "}",
            Points = array.SelectMany(value => value.Points).ToArray(),
            Paths = array.SelectMany(value => value.Paths).ToArray()
        };
    }

    private static bool IsLinear(GeometryValue value)
        => value.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment;

    private static GeoVector Midpoint(GeometryValue value)
        => value.Kind switch
        {
            GeometryKind.Segment => value.Origin + value.Direction / 2,
            GeometryKind.Circle => value.Origin,
            GeometryKind.Conic when value.Expression is "ellipse" or "hyperbola" => value.Origin,
            _ => throw new FormatException("请选择线段或有中心的圆锥曲线。"),
        };

    private static GeometryValue Measure(GeometryValue value, GeometryValue sources, string name)
    {
        if (value.Kind != GeometryKind.Scalar || sources.Kind != GeometryKind.List || sources.Items.Count == 0)
            throw new FormatException("度量需要数值和定位对象。");
        var items = sources.Items;
        var anchor = items.Count == 3 && name == "角度" ? items[1].Origin
            : items.Aggregate(default(GeoVector), (sum, item) => sum + (item.Kind switch
            {
                GeometryKind.Segment => Midpoint(item),
                GeometryKind.Vector when item.Paths.Count > 0 => item.Paths[0][0] + item.Direction / 2,
                GeometryKind.Polygon => item.Points.Aggregate(default(GeoVector), (total, point) => total + point) / item.Points.Count,
                _ => item.Origin,
            })) / items.Count;
        string label = name == "角度" ? $"{value.Number * 180 / Math.PI:G6}°" : value.Display;
        return value with { TextAnchor = anchor, Display = $"{name} = {label}" };
    }

    private static GeometryValue PolygonAngle(GeometryValue polygon, int index)
    {
        var points = polygon.Points;
        if (index < 0 || index >= points.Count) throw new FormatException("多边形顶点序号超出范围。");
        var vertex = points[index];
        var first = points[(index + points.Count - 1) % points.Count] - vertex;
        var second = points[(index + 1) % points.Count] - vertex;
        double angle = Angle(first, second);
        var normal = polygon.Faces.Aggregate(default(GeoVector), (sum, face) => sum + (face.B - face.A).Cross(face.C - face.A));
        if (first.Cross(second).Dot(normal) > 0) angle = Math.Tau - angle;
        return GeometryValue.Scalar(angle);
    }
    private GeometryValue BisectLines(GeometryValue first, GeometryValue second)
    {
        if (!IsLinear(first) || !IsLinear(second)) throw new FormatException("请选择两条直线、射线或线段。");
        var a = first.Direction.Unit();
        var b = second.Direction.Unit();
        if (a.Cross(b).Length < 1e-9)
        {
            var offset = second.Origin - first.Origin;
            return Line(first.Origin + (offset - a * offset.Dot(a)) / 2, a);
        }
        var intersection = Intersect(Line(first.Origin, a), Line(second.Origin, b));
        if (intersection.Kind != GeometryKind.Point) throw new FormatException("两条直线需要共面。");
        return Collection([Line(intersection.Origin, a + b), Line(intersection.Origin, a - b)]);
    }

    private static GeometryValue FitLine(GeometryValue list)
    {
        if (list.Kind != GeometryKind.List || list.Items.Count < 2 || list.Items.Any(item => item.Kind != GeometryKind.Point))
            throw new FormatException("拟合直线需要至少两个点。");
        var center = list.Items.Aggregate(default(GeoVector), (sum, point) => sum + point.Origin) / list.Items.Count;
        double variance = 0, covariance = 0;
        foreach (var point in list.Items)
        {
            var delta = point.Origin - center;
            variance += delta.X * delta.X;
            covariance += delta.X * delta.Y;
        }
        if (variance == 0) throw new FormatException("拟合直线需要不同的横坐标。");
        return Line(new(center.X, center.Y), new(1, covariance / variance));
    }

    private static GeometryValue PolarLine(GeometryValue source, GeometryValue target, bool diameter)
    {
        if (PlanarConic.FromGeometry(target) is not { } conic) throw new FormatException("请选择圆锥曲线。");
        if (diameter && !IsLinear(source) && source.Kind != GeometryKind.Vector)
            throw new FormatException("请选择直线或向量。");
        RequireHorizontalConics(target, target);
        var point = diameter ? source.Direction : Point(source);
        var (x, y, c) = diameter ? conic.PolarDirection(point) : conic.Polar(point);
        double norm = x * x + y * y;
        if (norm == 0) throw new FormatException("此极线位于无穷远处。");
        return Line(new(-x * c / norm, -y * c / norm, target.Origin.Z), new(-y, x));
    }

    private static GeometryValue ParallelTangents(GeometryValue line, GeometryValue target)
    {
        var direction = line.Direction.Unit();
        if (target.Kind == GeometryKind.Circle)
        {
            if (Math.Abs(direction.Dot(target.Normal)) > 1e-8) throw new FormatException("切线方向必须平行于圆所在平面。");
            var offset = target.Normal.Cross(direction).Unit() * target.Radius;
            return Collection([Line(target.Origin + offset, direction), Line(target.Origin - offset, direction)]);
        }
        if (Math.Abs(direction.Z) > 1e-8 || PlanarConic.FromGeometry(target) is not { } conic)
            throw new FormatException("请选择与圆锥曲线共面方向的直线。");
        var (x, y, c) = conic.PolarDirection(direction);
        double norm = x * x + y * y;
        if (norm == 0) return Collection([]);
        var origin = new GeoVector(-x * c / norm, -y * c / norm, target.Origin.Z);
        var axis = new GeoVector(-y, x).Unit();
        return Collection(conic.Intersect(origin, axis).Select(t => origin + axis * t)
            .Where(point => conic.HasFiniteTangency(point, direction)).Select(point => Line(point, direction)));
    }

    private GeometryValue Intersect(GeometryValue a, GeometryValue b)
    {
        bool IsLine(GeometryValue item) => item.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment;
        if (a.Kind == GeometryKind.Function && (b.Kind == GeometryKind.Function || IsLinear(b)))
            return IntersectFunction(a, b);
        if (b.Kind == GeometryKind.Function && IsLinear(a)) return IntersectFunction(b, a);
        if (a.Kind == GeometryKind.Plane && b.Kind == GeometryKind.Plane)
        {
            var direction = a.Normal.Cross(b.Normal); double denominator = direction.Dot(direction);
            if (denominator < 1e-15) throw new FormatException("平面平行或重合，没有唯一交线。");
            var origin = (b.Normal.Cross(direction) * a.Normal.Dot(a.Origin) + direction.Cross(a.Normal) * b.Normal.Dot(b.Origin)) / denominator;
            return Line(origin, direction);
        }
        if (a.Kind == GeometryKind.Plane && IsLine(b)) return Intersect(b, a);
        if (IsLine(a) && b.Kind == GeometryKind.Plane)
        {
            double denominator = a.Direction.Dot(b.Normal);
            if (Math.Abs(denominator) < 1e-12) throw new FormatException("直线平行于平面。");
            double t = (b.Origin - a.Origin).Dot(b.Normal) / denominator;
            return a.Kind == GeometryKind.Ray && t < 0 || a.Kind == GeometryKind.Segment && (t < 0 || t > 1)
                ? Collection(Array.Empty<GeometryValue>()) : GeometryValue.Point(a.Origin + a.Direction * t);
        }
        if (b.Kind == GeometryKind.Plane && a.Faces.Count > 0) return Intersect(b, a);
        if (a.Kind == GeometryKind.Plane && b.Faces.Count > 0)
        {
            var paths = new List<GeoVector[]>();
            foreach (var triangle in b.Faces)
            {
                var corners = new[] { triangle.A, triangle.B, triangle.C, triangle.A };
                var points = new List<GeoVector>();
                for (int i = 0; i < 3; i++)
                {
                    double first = (corners[i] - a.Origin).Dot(a.Normal), last = (corners[i + 1] - a.Origin).Dot(a.Normal);
                    if (first * last < 0) points.Add(corners[i] + (corners[i + 1] - corners[i]) * (first / (first - last)));
                    if (Math.Abs(first) < 1e-9) points.Add(corners[i]);
                }
                if (points.Count >= 2) paths.Add(points.Take(2).ToArray());
            }
            return new() { Kind = GeometryKind.Curve, Display = "平面截线", Paths = paths };
        }
        if (IsLine(a) && IsLine(b))
        {
            var cross = a.Direction.Cross(b.Direction); double denominator = cross.Dot(cross);
            if (denominator < 1e-15) return Collection(Array.Empty<GeometryValue>());
            var delta = b.Origin - a.Origin;
            if (Math.Abs(delta.Dot(cross)) > 1e-8) return Collection(Array.Empty<GeometryValue>());
            double t = delta.Cross(b.Direction).Dot(cross) / denominator;
            double s = delta.Cross(a.Direction).Dot(cross) / denominator;
            bool In(GeometryValue line, double value) => line.Kind == GeometryKind.Line || value >= -1e-9 && (line.Kind != GeometryKind.Segment || value <= 1 + 1e-9);
            return In(a, t) && In(b, s) ? GeometryValue.Point(a.Origin + a.Direction * t) : Collection(Array.Empty<GeometryValue>());
        }
        if (a.Kind == GeometryKind.Circle && IsLine(b)) return Intersect(b, a);
        if (IsLine(a) && b.Kind == GeometryKind.Circle)
        {
            double planeDirection = a.Direction.Dot(b.Normal);
            double planeOffset = (a.Origin - b.Origin).Dot(b.Normal);
            if (Math.Abs(planeDirection) > 1e-10)
            {
                double t = -planeOffset / planeDirection;
                var point = a.Origin + a.Direction * t;
                bool inRange = a.Kind == GeometryKind.Line || t >= 0 && (a.Kind != GeometryKind.Segment || t <= 1);
                return inRange && Math.Abs((point - b.Origin).Length - b.Radius) < 1e-8
                    ? Collection(new[] { GeometryValue.Point(point) }) : Collection(Array.Empty<GeometryValue>());
            }
            if (Math.Abs(planeOffset) > 1e-8) return Collection(Array.Empty<GeometryValue>());
            var delta = a.Origin - b.Origin; double aa = a.Direction.Dot(a.Direction);
            double bb = 2 * delta.Dot(a.Direction), cc = delta.Dot(delta) - b.Radius * b.Radius;
            double discriminant = bb * bb - 4 * aa * cc;
            if (discriminant < -1e-9) return Collection(Array.Empty<GeometryValue>());
            var roots = new[] { (-bb - Math.Sqrt(Math.Max(0, discriminant))) / (2 * aa), (-bb + Math.Sqrt(Math.Max(0, discriminant))) / (2 * aa) }.Distinct();
            return Collection(roots.Where(t => a.Kind == GeometryKind.Line || t >= 0 && (a.Kind != GeometryKind.Segment || t <= 1))
                .Select(t => GeometryValue.Point(a.Origin + a.Direction * t)));
        }
        if (a.Kind == GeometryKind.Circle && b.Kind == GeometryKind.Circle)
        {
            if (a.Normal.Cross(b.Normal).Length > 1e-8 || Math.Abs((b.Origin - a.Origin).Dot(a.Normal)) > 1e-8)
                throw new FormatException("两圆交点当前支持共面圆。");
            var delta = b.Origin - a.Origin; double d = delta.Length;
            if (d < 1e-12 || d > a.Radius + b.Radius + 1e-9 || d < Math.Abs(a.Radius - b.Radius) - 1e-9) return Collection(Array.Empty<GeometryValue>());
            double x = (a.Radius * a.Radius - b.Radius * b.Radius + d * d) / (2 * d);
            double height = Math.Sqrt(Math.Max(0, a.Radius * a.Radius - x * x));
            var center = a.Origin + delta * (x / d); var offset = a.Normal.Cross(delta) * (height / d);
            return Collection(height < 1e-9 ? new[] { GeometryValue.Point(center) }
                : new[] { GeometryValue.Point(center + offset), GeometryValue.Point(center - offset) });
        }
        if (IsLine(b) && (a.Kind == GeometryKind.Conic || a.ConicEquation.HasValue)) return Intersect(b, a);
        if (IsLine(a) && PlanarConic.FromGeometry(b) is { } conic)
        {
            if (Math.Abs(a.Direction.Z) > 1e-10 || Math.Abs(a.Origin.Z - b.Origin.Z) > 1e-8)
                throw new FormatException("直线和此圆锥曲线必须共面。");
            return Collection(conic.Intersect(a.Origin, a.Direction)
                .Where(t => (a.Kind != GeometryKind.Ray || t >= 0) && (a.Kind != GeometryKind.Segment || t is >= 0 and <= 1))
                .Select(t => GeometryValue.Point(a.Origin + a.Direction * t)));
        }
        if (PlanarConic.FromGeometry(a) is { } firstConic && PlanarConic.FromGeometry(b) is { } secondConic)
        {
            RequireHorizontalConics(a, b);
            var origin = (a.Origin + b.Origin) / 2;
            return Collection(PlanarConic.Intersections(firstConic, secondConic, origin, token).Select(GeometryValue.Point));
        }
        if (a.Faces.Count > 0 && b.Faces.Count > 0) return SurfaceIntersection(a, b);
        throw new FormatException("交点支持直线、圆锥曲线、平面及多面体截线。");
    }

    private GeometryValue IntersectFunction(GeometryValue function, GeometryValue other)
    {
        var x = MathS.Var("x");
        var expression = ComputerAlgebra.Parse(function.Expression).Substitute(function.Variable, x);
        var evaluate = ComputerAlgebra.Compile(expression);
        if (other.Kind != GeometryKind.Function && Math.Abs(other.Direction.Z) + Math.Abs(other.Origin.Z) > 1e-9)
            throw new FormatException("直线和函数图像必须共面。");
        if (other.Kind != GeometryKind.Function && Math.Abs(other.Direction.X) < 1e-12)
        {
            var point = new GeoVector(other.Origin.X, evaluate(other.Origin.X, 0, 0, 0, 0, 0));
            double t = (point.Y - other.Origin.Y) / other.Direction.Y;
            return point.Finite && WithinLine(t) ? GeometryValue.Point(point) : Collection([]);
        }
        Entity difference = other.Kind == GeometryKind.Function
            ? expression - ComputerAlgebra.Parse(other.Expression).Substitute(other.Variable, x)
            : expression - (other.Origin.Y + (x - other.Origin.X) * (other.Direction.Y / other.Direction.X));
        var roots = SpecialPoints(function with { Expression = difference.ToString(), Variable = "x" }, "root");
        return Collection(roots.Items.Where(root => other.Kind == GeometryKind.Function
                || WithinLine((root.Origin.X - other.Origin.X) / other.Direction.X))
            .Select(root => GeometryValue.Point(new(root.Origin.X, evaluate(root.Origin.X, 0, 0, 0, 0, 0)))));

        bool WithinLine(double t) => other.Kind == GeometryKind.Line
            || t >= -1e-9 && (other.Kind != GeometryKind.Segment || t <= 1 + 1e-9);
    }

    private static bool IsTangent(GeometryValue line, GeometryValue curve)
    {
        if (line.Kind != GeometryKind.Line) throw new FormatException("切线检验需要直线。");
        var direction = line.Direction.Unit();
        if (curve.Kind == GeometryKind.Circle)
        {
            var displacement = line.Origin - curve.Origin;
            if (Math.Abs(direction.Dot(curve.Normal)) > 1e-8 || Math.Abs(displacement.Dot(curve.Normal)) > 1e-8) return false;
            return Math.Abs(displacement.Cross(direction).Length - curve.Radius) <= 1e-8 * Math.Max(1, curve.Radius);
        }
        if (PlanarConic.FromGeometry(curve) is not { } equation) throw new FormatException("切线检验需要圆锥曲线。");
        if (Math.Abs(direction.Z) > 1e-8 || Math.Abs(line.Origin.Z - curve.Origin.Z) > 1e-8) return false;
        return equation.HasFiniteTangency(line.Origin, direction);
    }

    private GeometryValue Tangent(GeoVector point, GeometryValue value)
    {
        if (value.Kind == GeometryKind.Function)
        {
            var expression = ComputerAlgebra.Parse(value.Expression); double y = ComputerAlgebra.Real(expression.Substitute(value.Variable, point.X));
            double derivative = ComputerAlgebra.Real(expression.Differentiate(value.Variable).Substitute(value.Variable, point.X));
            return Line(new(point.X, y), new(1, derivative));
        }
        if (value.Kind != GeometryKind.Circle && PlanarConic.FromGeometry(value) is { } conic)
        {
            if (Math.Abs(point.Z - value.Origin.Z) > 1e-8) throw new FormatException("切线点必须与圆锥曲线共面。");
            var polar = conic.Polar(point);
            if (Math.Abs(polar.X * point.X + polar.Y * point.Y + polar.Constant)
                <= 1e-9 * (1 + Math.Abs(polar.X * point.X) + Math.Abs(polar.Y * point.Y) + Math.Abs(polar.Constant)))
                return Line(point, new(-polar.Y, polar.X));
            double norm = polar.X * polar.X + polar.Y * polar.Y;
            if (norm <= 1e-20) return Collection(Array.Empty<GeometryValue>());
            var origin = new GeoVector(-polar.X * polar.Constant / norm, -polar.Y * polar.Constant / norm, point.Z);
            var direction = new GeoVector(-polar.Y, polar.X).Unit();
            return Collection(conic.Intersect(origin, direction)
                .Select(t => Line(point, origin + direction * t - point)));
        }
        if (value.Kind != GeometryKind.Circle) throw new FormatException("切线支持函数、圆或圆锥曲线。");
        var delta = point - value.Origin; double squared = delta.Dot(delta), r2 = value.Radius * value.Radius;
        if (Math.Abs(delta.Dot(value.Normal)) > 1e-8) throw new FormatException("切线点必须与圆共面。");
        if (squared < 1e-12) throw new FormatException("圆心处不存在切线。");
        if (squared < r2 - 1e-9) throw new FormatException("圆内点没有实切线。");
        var foot = value.Origin + delta * (r2 / squared);
        var offset = value.Normal.Cross(delta) * (value.Radius * Math.Sqrt(Math.Max(0, squared - r2)) / squared);
        if (offset.Length < 1e-8) return Line(point, value.Normal.Cross(delta));
        return Collection(new[] { Line(point, foot + offset - point), Line(point, foot - offset - point) });
    }

    private GeometryValue CommonTangents(GeometryValue first, GeometryValue second)
    {
        if (first.Kind == GeometryKind.Circle && second.Kind == GeometryKind.Circle)
        {
            if (first.Normal.Cross(second.Normal).Length > 1e-8
                || Math.Abs((second.Origin - first.Origin).Dot(first.Normal)) > 1e-8)
                throw new FormatException("两圆公切线需要共面圆。");
            var delta = second.Origin - first.Origin;
            double distance = delta.Length;
            if (distance < 1e-12) throw new FormatException("同心圆没有唯一公切线。");
            var direction = delta / distance;
            var perpendicular = first.Normal.Cross(direction).Unit();
            var lines = new List<GeometryValue>();
            foreach (double side in new[] { 1d, -1d })
            {
                double ratio = (first.Radius - side * second.Radius) / distance;
                if (Math.Abs(ratio) > 1 + 1e-10) continue;
                double along = Math.Clamp(ratio, -1, 1);
                double across = Math.Sqrt(Math.Max(0, 1 - along * along));
                foreach (double sign in across < 1e-10 ? new[] { 1d } : new[] { 1d, -1d })
                {
                    var normal = direction * along + perpendicular * (across * sign);
                    var start = first.Origin + normal * first.Radius;
                    lines.Add(Line(start, first.Normal.Cross(normal)));
                }
            }
            return Collection(lines);
        }
        var firstConic = PlanarConic.FromGeometry(first);
        var secondConic = PlanarConic.FromGeometry(second);
        if (firstConic is null || secondConic is null)
            throw new FormatException("公切线支持两个圆锥曲线对象。");
        RequireHorizontalConics(first, second);
        return Collection(PlanarConic.CommonTangents(firstConic.Value, secondConic.Value, token)
            .Select(line => Line(line.Point with { Z = first.Origin.Z }, line.Direction)));
    }

    private static void RequireHorizontalConics(GeometryValue first, GeometryValue second)
    {
        if ((first.Kind == GeometryKind.Circle && Math.Abs(first.Normal.Unit().Z) < 1 - 1e-8)
            || (second.Kind == GeometryKind.Circle && Math.Abs(second.Normal.Unit().Z) < 1 - 1e-8)
            || Math.Abs(first.Origin.Z - second.Origin.Z) > 1e-8)
            throw new FormatException("两个圆锥曲线需要位于同一水平平面。");
    }

    private static GeometryValue Relation(GeometryValue a, GeometryValue b, string relation)
    {
        if (relation.ToLowerInvariant() is "parallel" or "perpendicular" && (a.Direction.Length < 1e-10 || b.Direction.Length < 1e-10))
            throw new FormatException("平行或垂直检验需要两个非零方向对象。");
        bool answer = relation.ToLowerInvariant() switch
        {
            "parallel" => a.Direction.Cross(b.Direction).Length <= 1e-8 * a.Direction.Length * b.Direction.Length,
            "perpendicular" => Math.Abs(a.Direction.Dot(b.Direction)) <= 1e-8 * a.Direction.Length * b.Direction.Length,
            "equal" or "congruent" => Math.Abs(a.Direction.Length - b.Direction.Length) < 1e-8 && Math.Abs(a.Radius - b.Radius) < 1e-8,
            "tangent" => Math.Abs((a.Origin - b.Origin).Length - a.Radius - b.Radius) < 1e-8
                || Math.Abs((a.Origin - b.Origin).Length - Math.Abs(a.Radius - b.Radius)) < 1e-8,
            _ => throw new FormatException("关系支持 parallel、perpendicular、equal、tangent。"),
        };
        return new() { Kind = GeometryKind.Boolean, Number = answer ? 1 : 0, Display = (answer ? "成立" : "不成立") + " · 数值检验" };
    }

    private GeometryValue RootNear(GeometryValue function, double start)
    {
        if (function.Kind != GeometryKind.Function) throw new FormatException("求根需要函数对象。");
        Entity expression = ComputerAlgebra.Parse(function.Expression).Substitute(function.Variable, MathS.Var("x"));
        var evaluate = ComputerAlgebra.Compile(expression);
        var derivative = ComputerAlgebra.Compile(expression.Differentiate("x"));
        double x = start;
        for (int iteration = 0; iteration < 48; iteration++)
        {
            Sample();
            double y = evaluate(x, 0, 0, 0, 0, 0);
            double slope = derivative(x, 0, 0, 0, 0, 0);
            if (double.IsFinite(y) && Math.Abs(y) <= 1e-10) return GeometryValue.Point(new(x, 0));
            if (!double.IsFinite(y + slope) || Math.Abs(slope) < 1e-14) break;
            x -= y / slope;
        }
        throw new FormatException("给定初值未收敛到实根。");
    }

    private GeometryValue SpecialPoints(GeometryValue function, string kind, double? minimum = null, double? maximum = null)
    {
        if (function.Kind != GeometryKind.Function) throw new FormatException("此操作需要函数对象。");
        double min = minimum ?? view.XMin, max = maximum ?? view.XMax;
        if (!double.IsFinite(min + max) || min >= max) throw new FormatException("函数分析区间无效。");
        Entity expression = ComputerAlgebra.Parse(function.Expression);
        Entity derivative = kind == "root" ? expression : kind == "extremum" ? expression.Differentiate(function.Variable) : expression.Differentiate(function.Variable).Differentiate(function.Variable);
        var evaluate = ComputerAlgebra.Compile(derivative.Substitute(function.Variable, MathS.Var("x")));
        var original = ComputerAlgebra.Compile(expression.Substitute(function.Variable, MathS.Var("x")));
        var roots = new List<double>();
        if (derivative.Simplify().ToString() == "0") return Collection(Array.Empty<GeometryValue>());
        if (MathS.Utils.TryGetPolynomial(derivative, MathS.Var(function.Variable), out var polynomial)
            && polynomial.Keys.All(power => int.TryParse(power.ToString(), out int degree) && degree <= 4)
            && derivative.SolveEquation(MathS.Var(function.Variable)) is Entity.Set.FiniteSet exactRoots)
        {
            foreach (var candidate in exactRoots.Elements)
            {
                try
                {
                    double root = ComputerAlgebra.Real(candidate);
                    if (root >= min && root <= max) roots.Add(root);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
        double left = min, first = evaluate(left, 0, 0, 0, 0, 0);
        int sampleCount = _curveSamples;
        for (int index = 1; index <= sampleCount; index++)
        {
            Sample(); double right = min + (max - min) * index / sampleCount;
            double last = evaluate(right, 0, 0, 0, 0, 0);
            if (double.IsFinite(first + last) && (first * last <= 0 || Math.Abs(last) < 1e-8))
            {
                double low = left, high = right, flo = first;
                for (int step = 0; step < 40; step++)
                {
                    double middle = (low + high) / 2, value = evaluate(middle, 0, 0, 0, 0, 0);
                    if (flo * value <= 0) high = middle; else { low = middle; flo = value; }
                }
                double root = (low + high) / 2;
                if (Math.Abs(evaluate(root, 0, 0, 0, 0, 0)) < 1e-5 && roots.All(value => Math.Abs(value - root) > 1e-5)) roots.Add(root);
            }
            left = right; first = last;
        }
        double epsilon = (max - min) * 1e-5;
        return Collection(roots.Order().Where(x => kind == "root"
            || evaluate(x - epsilon, 0, 0, 0, 0, 0) * evaluate(x + epsilon, 0, 0, 0, 0, 0) < 0)
            .Select(x => GeometryValue.Point(new(x, original(x, 0, 0, 0, 0, 0)))));
    }
    private GeometryValue Differential(GeometryValue function, double x, string command)
    {
        var expression = ComputerAlgebra.Parse(function.Expression); var derivative = expression.Differentiate(function.Variable);
        double y = ComputerAlgebra.Real(expression.Substitute(function.Variable, x));
        double first = ComputerAlgebra.Real(derivative.Substitute(function.Variable, x));
        double second = ComputerAlgebra.Real(derivative.Differentiate(function.Variable).Substitute(function.Variable, x));
        if (command == "normal") return Line(new(x, y), new(-first, 1));
        double curvature = second / Math.Pow(1 + first * first, 1.5);
        if (command == "curvature") return GeometryValue.Scalar(Math.Abs(curvature));
        if (Math.Abs(curvature) < 1e-12) throw new FormatException("曲率为零，密切圆半径无穷大。");
        var normal = new GeoVector(-first, 1).Unit();
        return Circle(new GeoVector(x, y) + normal / curvature, 1 / Math.Abs(curvature));
    }
    private GeometryValue IntegralRegion(GeometryValue first, GeometryValue second, double min, double max)
    {
        if (first.Kind != GeometryKind.Function || second is not null && second.Kind != GeometryKind.Function)
            throw new FormatException("积分区域需要函数对象。");
        var f = CompileBound(first.Expression, (first.Variable, "x"));
        var g = second is null ? null : CompileBound(second.Expression, (second.Variable, "x"));
        var path = new List<GeoVector>(); double area = 0;
        int sampleCount = _curveSamples / 2;
        for (int index = 0; index <= sampleCount; index++)
        {
            Sample();
            double x = min + (max - min) * index / sampleCount;
            double y = f(x, 0, 0, 0, 0, 0); path.Add(new(x, y));
            double other = g?.Invoke(x, 0, 0, 0, 0, 0) ?? 0;
            int weight = index is 0 || index == sampleCount ? 1 : index % 2 == 0 ? 2 : 4;
            area += (y - other) * weight;
        }
        for (int index = sampleCount; index >= 0; index--) { double x = min + (max - min) * index / sampleCount; path.Add(new(x, g?.Invoke(x, 0, 0, 0, 0, 0) ?? 0)); }
        path.Add(path[0]);
        area *= (max - min) / (3 * sampleCount);
        return new() { Kind = GeometryKind.Region, Number = area, Paths = new[] { path.ToArray() },
            Expression = double.IsFinite(area) ? ComputerAlgebra.Number(area) : "", Display = "积分面积 ≈ " + area.ToString("G8") };
    }
    private GeometryValue ArcLength(GeometryValue function, double min, double max)
    {
        if (function.Kind != GeometryKind.Function) throw new FormatException("区间弧长需要函数对象。");
        var derivative = ComputerAlgebra.Compile(ComputerAlgebra.Parse(function.Expression).Differentiate(function.Variable));
        double sum = 0;
        int intervals = _curveSamples;
        double h = (max - min) / intervals;
        for (int i = 0; i <= intervals; i++)
        {
            Sample();
            double slope = derivative(min + h * i, 0, 0, 0, 0, 0);
            sum += Math.Sqrt(1 + slope * slope) * (i == 0 || i == intervals ? 1 : i % 2 == 0 ? 2 : 4);
        }
        return GeometryValue.Scalar(Math.Abs(sum * h / 3));
    }
    private GeometryValue SlopeField(string expression)
    {
        var function = Compile(expression); var paths = new List<GeoVector[]>();
        double length = Math.Min(view.XMax - view.XMin, view.YMax - view.YMin) / 30;
        int xDivisions = _curveSamples < 512 ? 10 : 20;
        int yDivisions = _curveSamples < 512 ? 8 : 16;
        for (int x = 0; x <= xDivisions; x++) for (int y = 0; y <= yDivisions; y++)
        {
            Sample(); double px = view.XMin + x * (view.XMax - view.XMin) / xDivisions;
            double py = view.YMin + y * (view.YMax - view.YMin) / yDivisions;
            double slope = function(px, py, 0, 0, 0, 0); if (!double.IsFinite(slope)) continue;
            var d = new GeoVector(1, slope).Unit() * length;
            paths.Add(new[] { new GeoVector(px, py) - d, new GeoVector(px, py) + d });
        }
        return new() { Kind = GeometryKind.Curve, Paths = paths, Display = "方向场" };
    }
    private GeometryValue SolveOde(string[] args)
    {
        var function = Compile(args[0]); double x = N(args[1]), y = N(args[2]); double end = N(args[3]);
        int steps = _curveSamples < 512 ? 256 : 1024;
        double h = (end - x) / steps; var points = new List<GeoVector> { new(x, y) };
        for (int i = 0; i < steps; i++)
        {
            Sample(); double k1 = function(x, y, 0, 0, 0, 0), k2 = function(x + h / 2, y + h * k1 / 2, 0, 0, 0, 0);
            double k3 = function(x + h / 2, y + h * k2 / 2, 0, 0, 0, 0), k4 = function(x + h, y + h * k3, 0, 0, 0, 0);
            y += h * (k1 + 2 * k2 + 2 * k3 + k4) / 6; x += h;
            if (!double.IsFinite(y) || Math.Abs(y) > 1e6) break;
            points.Add(new(x, y));
        }
        return new() { Kind = GeometryKind.Curve, Paths = new[] { points.ToArray() }, Display = "常微分方程数值解 · RK4" };
    }
    private GeometryValue Sequence(string[] args)
    {
        if (args.Length is < 1 or > 5) throw new FormatException("Sequence 参数数量无效。");
        bool hasExpression = args.Length >= 4;
        double start = args.Length == 1 ? 1 : N(args[hasExpression ? 2 : 0]);
        double end = N(args.Length == 1 ? args[0] : args[hasExpression ? 3 : 1]);
        double step = args.Length is 3 or 5 ? N(args[^1]) : hasExpression || args.Length == 1 || start <= end ? 1 : -1;
        double intervals = (end - start) / step;
        if (!double.IsFinite(intervals) || intervals > 512)
            throw new FormatException("序列最多包含 513 个元素，步长须为非零的有限实数。");
        if (intervals < 0) return Collection([]);
        string variable = hasExpression ? args[1].Trim() : "";
        if (hasExpression && !MathSyntax.Identifier(variable)) throw new FormatException("序列变量名称无效。");
        var items = new List<GeometryValue>();
        for (int index = 0; index <= Math.Floor(intervals + 1e-12); index++)
        {
            Sample();
            double value = start + index * step;
            items.Add(hasExpression
                ? Evaluate(System.Text.RegularExpressions.Regex.Replace(args[0], @"\b" + System.Text.RegularExpressions.Regex.Escape(variable) + @"\b",
                    "(" + ComputerAlgebra.Number(value) + ")"))
                : GeometryValue.Scalar(value));
        }
        return Collection(items);
    }
    private GeometryValue Conic(string command, string[] args)
    {
        var first = Point(Evaluate(args[0]));
        if (command == "parabola")
        {
            var directrix = Evaluate(args[1]); var d = directrix.Direction.Unit();
            var foot = directrix.Origin + d * ((first - directrix.Origin).Dot(d));
            var vertex = (first + foot) / 2; var axis = (first - foot).Unit(); double p = (first - foot).Length / 2;
            var paths = SampleCurve(t => vertex + axis * (t * t / (4 * p)) + new GeoVector(-axis.Y, axis.X) * t, -15, 15);
            return new() { Kind = GeometryKind.Conic, Origin = vertex, Direction = axis, Normal = new(-axis.Y, axis.X),
                Radius = p, Points = new[] { first }, Paths = paths, Expression = command, Display = "抛物线" };
        }
        var second = Point(Evaluate(args[1])); var third = Evaluate(args[2]);
        double a = third.Kind == GeometryKind.Scalar ? third.Number : command == "hyperbola"
            ? Math.Abs((third.Origin - first).Length - (third.Origin - second).Length) / 2
            : ((third.Origin - first).Length + (third.Origin - second).Length) / 2;
        double c = (second - first).Length / 2; var center = (first + second) / 2; var u = (second - first).Unit(); var v = new GeoVector(-u.Y, u.X);
        double b = Math.Sqrt(command == "ellipse" ? a * a - c * c : c * c - a * a);
        if (!double.IsFinite(b) || a <= 0 || b <= 0) throw new FormatException("圆锥曲线参数退化。");
        var contours = command == "ellipse" ? SampleCurve(t => center + u * (a * Math.Cos(t)) + v * (b * Math.Sin(t)), 0, Math.Tau)
            : SampleCurve(t => center + u * (a * Math.Cosh(t)) + v * (b * Math.Sinh(t)), -3, 3)
                .Concat(SampleCurve(t => center - u * (a * Math.Cosh(t)) + v * (b * Math.Sinh(t)), -3, 3)).ToList();
        return new()
        {
            Kind = GeometryKind.Conic,
            Origin = center,
            Direction = u,
            Normal = v,
            Radius = a,
            Number = b,
            Points = new[] { first, second },
            Paths = contours,
            Expression = command,
            Display = command == "ellipse" ? "椭圆" : "双曲线"
        };
    }
    private static GeometryValue ConicProperty(GeometryValue value, string command)
    {
        if (value.Kind != GeometryKind.Conic) throw new FormatException("此命令需要用 Ellipse、Hyperbola 或 Parabola 创建的圆锥曲线。");
        if (value.Expression == "parabola") return command switch
        {
            "focus" => GeometryValue.Point(value.Points[0]),
            "vertex" => GeometryValue.Point(value.Origin),
            "eccentricity" => GeometryValue.Scalar(1),
            "directrix" => Line(value.Origin - value.Direction * value.Radius, new(-value.Direction.Y, value.Direction.X)),
            _ => throw new FormatException("抛物线没有有限渐近线。"),
        };
        double c = value.Expression == "ellipse" ? Math.Sqrt(value.Radius * value.Radius - value.Number * value.Number)
            : Math.Sqrt(value.Radius * value.Radius + value.Number * value.Number);
        return command switch
        {
            "focus" => Collection(value.Points.Select(GeometryValue.Point)),
            "vertex" => Collection(new[] { GeometryValue.Point(value.Origin + value.Direction * value.Radius), GeometryValue.Point(value.Origin - value.Direction * value.Radius) }),
            "eccentricity" => GeometryValue.Scalar(value.Expression == "parabola" ? 1 : c / value.Radius),
            "directrix" => Collection(new[] { Line(value.Origin + value.Direction * (value.Radius * value.Radius / c), value.Normal), Line(value.Origin - value.Direction * (value.Radius * value.Radius / c), value.Normal) }),
            "asymptote" when value.Expression == "hyperbola" => Collection(new[] { Line(value.Origin, value.Direction * value.Radius + value.Normal * value.Number), Line(value.Origin, value.Direction * value.Radius - value.Normal * value.Number) }),
            _ => throw new FormatException("此圆锥曲线没有所请求的有限特征对象。"),
        };
    }
}
