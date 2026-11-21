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

    private GeometryValue Intersect(GeometryValue a, GeometryValue b)
    {
        bool IsLine(GeometryValue item) => item.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment;
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
        if (a.Faces.Count > 0 && b.Faces.Count > 0) return SurfaceIntersection(a, b);
        throw new FormatException("交点支持直线、圆、平面及多面体截线。");
    }

    private GeometryValue Tangent(GeoVector point, GeometryValue value)
    {
        if (value.Kind == GeometryKind.Function)
        {
            var expression = ComputerAlgebra.Parse(value.Expression); double y = ComputerAlgebra.Real(expression.Substitute(value.Variable, point.X));
            double derivative = ComputerAlgebra.Real(expression.Differentiate(value.Variable).Substitute(value.Variable, point.X));
            return Line(new(point.X, y), new(1, derivative));
        }
        if (value.Kind != GeometryKind.Circle) throw new FormatException("切线支持函数或圆。");
        var delta = point - value.Origin; double squared = delta.Dot(delta), r2 = value.Radius * value.Radius;
        if (Math.Abs(delta.Dot(value.Normal)) > 1e-8) throw new FormatException("切线点必须与圆共面。");
        if (squared < 1e-12) throw new FormatException("圆心处不存在切线。");
        if (squared < r2 - 1e-9) throw new FormatException("圆内点没有实切线。");
        var foot = value.Origin + delta * (r2 / squared);
        var offset = value.Normal.Cross(delta) * (value.Radius * Math.Sqrt(Math.Max(0, squared - r2)) / squared);
        if (offset.Length < 1e-8) return Line(point, value.Normal.Cross(delta));
        return Collection(new[] { Line(point, foot + offset - point), Line(point, foot - offset - point) });
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

    private GeometryValue SpecialPoints(GeometryValue function, string kind)
    {
        if (function.Kind != GeometryKind.Function) throw new FormatException("此操作需要函数对象。");
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
                    if (root >= view.XMin && root <= view.XMax) roots.Add(root);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
        double left = view.XMin, first = evaluate(left, 0, 0, 0, 0, 0);
        for (int index = 1; index <= 512; index++)
        {
            Sample(); double right = view.XMin + (view.XMax - view.XMin) * index / 512;
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
        double epsilon = (view.XMax - view.XMin) * 1e-5;
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
        var f = Compile(first.Expression); var g = second is null ? null : Compile(second.Expression);
        var path = new List<GeoVector>(); double area = 0;
        for (int index = 0; index <= 256; index++)
        {
            double x = min + (max - min) * index / 256;
            double y = f(x, 0, 0, 0, 0, 0); path.Add(new(x, y));
            area += (y - (g?.Invoke(x, 0, 0, 0, 0, 0) ?? 0)) * (index is 0 or 256 ? .5 : 1) * (max - min) / 256;
        }
        for (int index = 256; index >= 0; index--) { double x = min + (max - min) * index / 256; path.Add(new(x, g?.Invoke(x, 0, 0, 0, 0, 0) ?? 0)); }
        path.Add(path[0]);
        return new() { Kind = GeometryKind.Region, Number = area, Paths = new[] { path.ToArray() }, Display = "积分面积 ≈ " + area.ToString("G8") };
    }
    private GeometryValue ArcLength(GeometryValue function, double min, double max)
    {
        if (function.Kind != GeometryKind.Function) throw new FormatException("区间弧长需要函数对象。");
        var derivative = ComputerAlgebra.Compile(ComputerAlgebra.Parse(function.Expression).Differentiate(function.Variable));
        double sum = 0;
        const int intervals = 512;
        double h = (max - min) / intervals;
        for (int i = 0; i <= intervals; i++)
        {
            Sample();
            double slope = derivative(min + h * i, 0, 0, 0, 0, 0);
            sum += Math.Sqrt(1 + slope * slope) * (i is 0 or intervals ? 1 : i % 2 == 0 ? 2 : 4);
        }
        return GeometryValue.Scalar(Math.Abs(sum * h / 3));
    }
    private GeometryValue SlopeField(string expression)
    {
        var function = Compile(expression); var paths = new List<GeoVector[]>();
        double length = Math.Min(view.XMax - view.XMin, view.YMax - view.YMin) / 30;
        for (int x = 0; x <= 20; x++) for (int y = 0; y <= 16; y++)
        {
            Sample(); double px = view.XMin + x * (view.XMax - view.XMin) / 20, py = view.YMin + y * (view.YMax - view.YMin) / 16;
            double slope = function(px, py, 0, 0, 0, 0); if (!double.IsFinite(slope)) continue;
            var d = new GeoVector(1, slope).Unit() * length;
            paths.Add(new[] { new GeoVector(px, py) - d, new GeoVector(px, py) + d });
        }
        return new() { Kind = GeometryKind.Curve, Paths = paths, Display = "方向场" };
    }
    private GeometryValue SolveOde(string[] args)
    {
        var function = Compile(args[0]); double x = N(args[1]), y = N(args[2]); double end = N(args[3]);
        double h = (end - x) / 1024; var points = new List<GeoVector> { new(x, y) };
        for (int i = 0; i < 1024; i++)
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
        double start = N(args[2]), end = N(args[3]), step = args.Length > 4 ? N(args[4]) : 1;
        if (step == 0 || (end - start) / step is < 0 or > 512) throw new FormatException("序列应包含 1–513 个元素，步长须与范围同向。");
        var items = new List<GeometryValue>();
        for (int index = 0; index <= Math.Floor((end - start) / step); index++)
        {
            Sample(); string value = ComputerAlgebra.Number(start + index * step);
            items.Add(Evaluate(System.Text.RegularExpressions.Regex.Replace(args[0], @"\b" + args[1].Trim() + @"\b", "(" + value + ")")));
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
            return new() { Kind = GeometryKind.Conic, Origin = vertex, Direction = axis, Radius = p, Points = new[] { first }, Paths = paths, Expression = command, Display = "抛物线" };
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
