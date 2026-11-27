using AngouriMath;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private GeometryValue EvaluateCircle(string[] args)
    {
        if (args.Length is not (2 or 3)) throw new FormatException("圆需要圆心和半径、圆心和圆上点，或三个圆上点。");
        var first = Evaluate(args[0]);
        var second = Evaluate(args[1]);
        if (args.Length == 2 && first.Kind == GeometryKind.Line)
        {
            var point = Point(second);
            var axis = first.Direction.Unit();
            var center = first.Origin + axis * (point - first.Origin).Dot(axis);
            return Circle(center, (point - center).Length, axis);
        }

        var origin = Point(first);
        var third = args.Length == 3 ? Evaluate(args[2]) : null;
        if (third?.Kind == GeometryKind.Point) return Circumcircle(origin, Point(second), third.Origin);
        double radius = second.Kind switch
        {
            GeometryKind.Scalar => second.Number,
            GeometryKind.Segment => second.Direction.Length,
            _ => (Point(second) - origin).Length,
        };
        if (third is null) return Circle(origin, radius);
        var normal = third.Kind == GeometryKind.Plane ? third.Normal.Unit() : Direction(third).Unit();
        if (second.Kind == GeometryKind.Point && Math.Abs((second.Origin - origin).Dot(normal)) > 1e-8 * Math.Max(1, radius))
            throw new FormatException("圆上点与给定圆轴方向不兼容。");
        return Circle(origin, radius, normal);
    }

    private GeometryValue EvaluateConic(string[] args)
    {
        if (args.Length == 5) return FivePointConic(Points(args));
        double[] coefficients = args.Length == 1 && Evaluate(args[0]) is { Kind: GeometryKind.List } list
            && list.Items.All(item => item.Kind == GeometryKind.Scalar)
            ? list.Items.Select(item => item.Number).ToArray() : args.Select(N).ToArray();
        if (coefficients.Length != 6) throw new FormatException("圆锥曲线需要五个点或六个系数。");
        return ConicFromCoefficients(new(coefficients[0], coefficients[3] / 2, coefficients[1],
            coefficients[4] / 2, coefficients[5] / 2, coefficients[2]));
    }

    private GeometryValue ConicFromEquation(string equation)
    {
        var expression = ComputerAlgebra.Parse(MathSyntax.EquationDifference(ExpandBound(equation, "x", "y")));
        Entity.Variable x = MathS.Var("x"), y = MathS.Var("y");
        var dx = expression.Differentiate(x);
        var dy = expression.Differentiate(y);
        return ConicFromCoefficients(new(ComputerAlgebra.Real(dx.Differentiate(x).Simplify()) / 2,
            ComputerAlgebra.Real(dx.Differentiate(y).Simplify()) / 2, ComputerAlgebra.Real(dy.Differentiate(y).Simplify()) / 2,
            AtOrigin(dx) / 2, AtOrigin(dy) / 2, AtOrigin(expression)));

        double AtOrigin(Entity value) => ComputerAlgebra.Real(value.Substitute(x, 0).Substitute(y, 0).Simplify());
    }

    private GeometryValue ConicFromCoefficients(PlanarConic equation)
    {
        double scale = Math.Max(Math.Abs(equation.A), Math.Max(Math.Abs(equation.B), Math.Abs(equation.C)));
        if (scale == 0 || !double.IsFinite(scale)) throw new FormatException("圆锥曲线需要有限的二次项。");
        var q = new PlanarConic(equation.A / scale, equation.B / scale, equation.C / scale,
            equation.D / scale, equation.E / scale, equation.F / scale);
        double angle = Math.Atan2(2 * q.B, q.A - q.C) / 2;
        var u = new GeoVector(Math.Cos(angle), Math.Sin(angle));
        var v = new GeoVector(-u.Y, u.X);
        double average = (q.A + q.C) / 2;
        double spread = Math.Sqrt(Math.Pow((q.A - q.C) / 2, 2) + q.B * q.B);
        double lambda = average + spread, mu = average - spread;
        if (Math.Abs(mu) > Math.Abs(lambda)) (lambda, mu, u, v) = (mu, lambda, v, u * -1);
        var linear = new GeoVector(q.D, q.E);
        double du = linear.Dot(u), dv = linear.Dot(v);

        if (Math.Abs(mu) < 1e-10 * Math.Abs(lambda))
        {
            if (Math.Abs(dv) < 1e-12) return SampleDegenerate();
            double x = -du / lambda;
            double y = -(q.F + du * x) / (2 * dv);
            var vertex = u * x + v * y;
            var axis = v * Math.Sign(-lambda * dv);
            var perpendicular = new GeoVector(-axis.Y, axis.X);
            double focusDistance = Math.Abs(dv / (2 * lambda));
            var paths = SampleCurve(t => vertex + axis * (t * t / (4 * focusDistance)) + perpendicular * t, -15, 15);
            return new() { Kind = GeometryKind.Conic, Origin = vertex, Direction = axis, Normal = perpendicular,
                Radius = focusDistance, Points = [vertex + axis * focusDistance], Paths = paths,
                ConicEquation = q, Expression = "parabola", Display = "抛物线" };
        }

        double cu = -du / lambda, cv = -dv / mu;
        var center = u * cu + v * cv;
        double level = -(q.F + du * cu + dv * cv);
        double axis1 = level / lambda, axis2 = level / mu;
        if (axis1 < 0 || (axis2 > axis1 && axis2 > 0)) (axis1, axis2, u, v) = (axis2, axis1, v, u * -1);
        if (axis1 <= 0 || axis2 == 0 || !double.IsFinite(axis1 + axis2)) return SampleDegenerate();
        if (axis2 > 0 && Math.Abs(axis1 - axis2) <= 1e-12 * axis1)
            return Circle(center, Math.Sqrt(axis1));

        bool ellipse = axis2 > 0;
        double a = Math.Sqrt(axis1), b = Math.Sqrt(Math.Abs(axis2)), c = Math.Sqrt(axis1 - axis2);
        var contours = ellipse ? SampleCurve(t => center + u * (a * Math.Cos(t)) + v * (b * Math.Sin(t)), 0, Math.Tau)
            : SampleCurve(t => center + u * (a * Math.Cosh(t)) + v * (b * Math.Sinh(t)), -3, 3)
                .Concat(SampleCurve(t => center - u * (a * Math.Cosh(t)) + v * (b * Math.Sinh(t)), -3, 3)).ToList();
        return new() { Kind = GeometryKind.Conic, Origin = center, Direction = u, Normal = v,
            Radius = a, Number = b, Points = [center - u * c, center + u * c], Paths = contours,
            ConicEquation = q, Expression = ellipse ? "ellipse" : "hyperbola", Display = ellipse ? "椭圆" : "双曲线" };

        GeometryValue SampleDegenerate()
        {
            string[] monomials = ["x^2", "x*y", "y^2", "x", "y", "1"];
            double[] coefficients = [q.A, 2 * q.B, q.C, 2 * q.D, 2 * q.E, q.F];
            string formula = string.Join('+', coefficients.Select((value, index) => $"({ComputerAlgebra.Number(value)})*{monomials[index]}"));
            return Implicit(formula + "=0") with { ConicEquation = q };
        }
    }
}
