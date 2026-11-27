namespace InkCanvas.AnalyticGeometry.Construction;

// Symmetric homogeneous matrix for Ax² + 2Bxy + Cy² + 2Dx + 2Ey + F.
internal readonly record struct PlanarConic(double A, double B, double C, double D, double E, double F)
{
    public (double X, double Y, double Constant) Polar(GeoVector point)
        => (A * point.X + B * point.Y + D, B * point.X + C * point.Y + E, D * point.X + E * point.Y + F);

    public (double X, double Y, double Constant) PolarDirection(GeoVector direction)
        => (A * direction.X + B * direction.Y, B * direction.X + C * direction.Y, D * direction.X + E * direction.Y);

    public IReadOnlyList<double> Intersect(GeoVector origin, GeoVector direction)
    {
        var polar = Polar(origin);
        double a = A * direction.X * direction.X + 2 * B * direction.X * direction.Y + C * direction.Y * direction.Y;
        double b = 2 * (polar.X * direction.X + polar.Y * direction.Y);
        double c = polar.X * origin.X + polar.Y * origin.Y + polar.Constant;
        double scale = Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c)));
        if (scale == 0 || !double.IsFinite(scale)) return [];
        var roots = Intercepts(a / scale, b / scale, c / scale).Order().ToArray();
        return roots.Length == 2 && Math.Abs(roots[1] - roots[0]) <= 1e-8 * Math.Max(1, Math.Abs(roots[0]))
            ? [roots[0]] : roots;
    }

    public bool HasFiniteTangency(GeoVector origin, GeoVector direction)
    {
        var q = AtOrigin(origin).Normalize();
        double a = q.A * direction.X * direction.X + 2 * q.B * direction.X * direction.Y + q.C * direction.Y * direction.Y;
        double b = 2 * (q.D * direction.X + q.E * direction.Y);
        double ac = 4 * a * q.F;
        return Math.Abs(a) > 1e-12 && Math.Abs(b * b - ac) <= 1e-8 * (b * b + Math.Abs(ac) + 1e-12);
    }

    public PlanarConic? Transform(Func<GeoVector, GeoVector> transform)
    {
        var offset = transform(default);
        var x = transform(new(1, 0)) - offset;
        var y = transform(new(0, 1)) - offset;
        double determinant = x.X * y.Y - x.Y * y.X;
        if (Math.Abs(determinant) < 1e-12 || Math.Abs(x.Z) + Math.Abs(y.Z) > 1e-10) return null;
        double i = y.Y / determinant, j = -y.X / determinant;
        double k = -x.Y / determinant, l = x.X / determinant;
        double m = -i * offset.X - j * offset.Y, n = -k * offset.X - l * offset.Y;
        double dx = A * m + B * n + D, dy = B * m + C * n + E;
        return new(A * i * i + 2 * B * i * k + C * k * k,
            A * i * j + B * (i * l + j * k) + C * k * l,
            A * j * j + 2 * B * j * l + C * l * l,
            dx * i + dy * k, dx * j + dy * l,
            (dx + D) * m + (dy + E) * n + F);
    }

    public static PlanarConic? FromGeometry(GeometryValue value)
    {
        if (value.ConicEquation is { } stored) return stored;
        var center = value.Origin;
        if (value.Kind == GeometryKind.Circle)
            return new(1, 0, 1, -center.X, -center.Y, center.X * center.X + center.Y * center.Y - value.Radius * value.Radius);
        if (value.Kind != GeometryKind.Conic) return null;
        var axis = value.Direction;
        var normal = value.Normal;
        if (value.Expression == "parabola")
        {
            double perpendicular = normal.Dot(center);
            return new(normal.X * normal.X, normal.X * normal.Y, normal.Y * normal.Y,
                -normal.X * perpendicular - 2 * value.Radius * axis.X,
                -normal.Y * perpendicular - 2 * value.Radius * axis.Y,
                perpendicular * perpendicular + 4 * value.Radius * axis.Dot(center));
        }
        double first = 1 / (value.Radius * value.Radius);
        double second = (value.Expression == "hyperbola" ? -1 : 1) / (value.Number * value.Number);
        double a = first * axis.X * axis.X + second * normal.X * normal.X;
        double b = first * axis.X * axis.Y + second * normal.X * normal.Y;
        double c = first * axis.Y * axis.Y + second * normal.Y * normal.Y;
        double d = -a * center.X - b * center.Y, e = -b * center.X - c * center.Y;
        return new(a, b, c, d, e, -d * center.X - e * center.Y - 1);
    }

    public static IReadOnlyList<GeoVector> Intersections(PlanarConic first, PlanarConic second, GeoVector origin, CancellationToken token)
    {
        first = first.AtOrigin(origin).Normalize();
        second = second.AtOrigin(origin).Normalize();
        var points = new List<GeoVector>();
        bool haveResultant = false;
        for (int chart = 0; chart < 2; chart++)
        {
            token.ThrowIfCancellationRequested();
            bool swap = chart == 1;
            var p = Coefficients(first, swap);
            var q = Coefficients(second, swap);
            var resultant = Resultant(p.Quadratic, p.Linear, p.Constant, q.Quadratic, q.Linear, q.Constant);
            if (resultant.All(coefficient => Math.Abs(coefficient) < 1e-25)) continue;
            haveResultant = true;
            var roots = Roots(resultant).Concat(Roots(resultant.Reverse().ToArray()).Where(root => Math.Abs(root) is > 1e-14 and < 1)
                .Select(root => 1 / root));
            foreach (double root in roots)
            {
                token.ThrowIfCancellationRequested();
                double pLinear = At(p.Linear, root), pConstant = At(p.Constant, root);
                double qLinear = At(q.Linear, root), qConstant = At(q.Constant, root);
                var candidates = Math.Abs(p.Quadratic) + Math.Abs(pLinear) >= Math.Abs(q.Quadratic) + Math.Abs(qLinear)
                    ? Intercepts(p.Quadratic, pLinear, pConstant) : Intercepts(q.Quadratic, qLinear, qConstant);
                foreach (double other in candidates)
                {
                    var point = swap ? new GeoVector(other, root) : new GeoVector(root, other);
                    point = Refine(point, first, second);
                    if (!point.Finite || !OnConic(first, point) || !OnConic(second, point)) continue;
                    if (!points.Any(existing => (point - existing).Length <= 1e-7 * Math.Max(1, point.Length)))
                        points.Add(point);
                }
            }
        }
        if (!haveResultant) throw new FormatException("圆锥曲线重合或包含共同分支。");
        return points.OrderBy(point => point.X).ThenBy(point => point.Y).Select(point => point + origin).ToArray();

        static GeoVector Refine(GeoVector point, PlanarConic first, PlanarConic second)
        {
            for (int iteration = 0; iteration < 4; iteration++)
            {
                var a = first.Polar(point);
                var b = second.Polar(point);
                double f = a.X * point.X + a.Y * point.Y + a.Constant;
                double g = b.X * point.X + b.Y * point.Y + b.Constant;
                double determinant = 2 * (a.X * b.Y - a.Y * b.X);
                if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-15) break;
                var step = new GeoVector((f * b.Y - g * a.Y) / determinant, (g * a.X - f * b.X) / determinant);
                if (!step.Finite || step.Length > 1 || step.Length < 1e-13) break;
                point -= step;
            }
            return point;
        }

        static (double Quadratic, double[] Linear, double[] Constant) Coefficients(PlanarConic conic, bool swap)
            => swap ? (conic.A, [2 * conic.D, 2 * conic.B], [conic.F, 2 * conic.E, conic.C])
                : (conic.C, [2 * conic.E, 2 * conic.B], [conic.F, 2 * conic.D, conic.A]);

        static bool OnConic(PlanarConic conic, GeoVector point)
        {
            var polar = conic.Polar(point);
            double residual = polar.X * point.X + polar.Y * point.Y + polar.Constant;
            double scale = Math.Abs(conic.A * point.X * point.X) + Math.Abs(2 * conic.B * point.X * point.Y)
                + Math.Abs(conic.C * point.Y * point.Y) + Math.Abs(2 * conic.D * point.X)
                + Math.Abs(2 * conic.E * point.Y) + Math.Abs(conic.F);
            return Math.Abs(residual) <= 1e-8 * (scale + 1e-12);
        }
    }

    private PlanarConic AtOrigin(GeoVector origin)
    {
        var offset = Polar(origin);
        return new(A, B, C, offset.X, offset.Y, offset.X * origin.X + offset.Y * origin.Y + offset.Constant);
    }

    private PlanarConic Normalize()
    {
        double norm = new[] { A, B, C, D, E, F }.Max(Math.Abs);
        if (!double.IsFinite(norm) || norm == 0) throw new FormatException("圆锥曲线系数无效。");
        return new(A / norm, B / norm, C / norm, D / norm, E / norm, F / norm);
    }

    private PlanarConic Dual()
    {
        var dual = new PlanarConic(C * F - E * E, D * E - B * F, A * F - D * D,
            B * E - C * D, B * D - A * E, A * C - B * B);
        return dual.Normalize();
    }

    public static IReadOnlyList<(GeoVector Point, GeoVector Direction)> CommonTangents(
        PlanarConic first, PlanarConic second, CancellationToken token)
    {
        var p = first.Dual();
        var q = second.Dual();
        var found = new List<(double X, double Y, double C)>();
        for (int chart = 0; chart < 2; chart++)
        {
            bool swap = chart == 1;
            var pLinear = swap ? new[] { 2 * p.E, 2 * p.D } : new[] { 2 * p.D, 2 * p.E };
            var pConstant = swap ? new[] { p.C, 2 * p.B, p.A } : new[] { p.A, 2 * p.B, p.C };
            var qLinear = swap ? new[] { 2 * q.E, 2 * q.D } : new[] { 2 * q.D, 2 * q.E };
            var qConstant = swap ? new[] { q.C, 2 * q.B, q.A } : new[] { q.A, 2 * q.B, q.C };
            var resultant = Resultant(p.F, pLinear, pConstant, q.F, qLinear, qConstant);
            if (resultant.All(coefficient => Math.Abs(coefficient) < 1e-26))
                throw new FormatException("圆锥曲线重合或退化，无法枚举有限的公切线。");
            foreach (double slope in Roots(resultant))
            {
                token.ThrowIfCancellationRequested();
                double x = swap ? slope : 1, y = swap ? 1 : slope;
                double b1 = At(pLinear, slope), c1 = At(pConstant, slope);
                double b2 = At(qLinear, slope), c2 = At(qConstant, slope);
                var candidates = Math.Abs(p.F) + Math.Abs(b1) >= Math.Abs(q.F) + Math.Abs(b2)
                    ? Intercepts(p.F, b1, c1) : Intercepts(q.F, b2, c2);
                foreach (double intercept in candidates)
                {
                    if (!TangentAtFinitePoint(p.F, b1, c1, intercept) || !TangentAtFinitePoint(q.F, b2, c2, intercept))
                        continue;
                    double length = Math.Sqrt(x * x + y * y);
                    if (x < -1e-9 || (Math.Abs(x) <= 1e-9 && y < 0)) length = -length;
                    var line = (X: x / length, Y: y / length, C: intercept / length);
                    if (!found.Any(other => Math.Abs(other.X - line.X) + Math.Abs(other.Y - line.Y)
                        + Math.Abs(other.C - line.C) / Math.Max(1, Math.Abs(line.C)) < 1e-6)) found.Add(line);
                }
            }
        }
        return found.OrderBy(line => Math.Atan2(line.Y, line.X)).ThenBy(line => line.C)
            .Select(line => (new GeoVector(-line.C * line.X, -line.C * line.Y), new GeoVector(-line.Y, line.X))).ToArray();
    }

    private static double[] Resultant(double pQuadratic, double[] pLinear, double[] pConstant,
        double qQuadratic, double[] qLinear, double[] qConstant)
    {
        var mixed = Subtract(Multiply(pLinear, qConstant), Multiply(qLinear, pConstant));
        if (Math.Abs(pQuadratic) + Math.Abs(qQuadratic) < 1e-14) return mixed;
        var linear = Subtract(Scale(qLinear, pQuadratic), Scale(pLinear, qQuadratic));
        var constant = Subtract(Scale(qConstant, pQuadratic), Scale(pConstant, qQuadratic));
        return Subtract(Multiply(constant, constant), Multiply(linear, mixed));
    }

    private static bool TangentAtFinitePoint(double a, double b, double c, double intercept)
    {
        // The homogeneous contact point is dual * line. Its weight vanishes at an asymptote.
        double weight = a * intercept + b / 2;
        return Math.Abs(weight) > 1e-10 * (Math.Abs(a * intercept) + Math.Abs(b) + 1e-8)
            && Math.Abs((a * intercept + b) * intercept + c)
                <= 1e-7 * (Math.Abs(a * intercept * intercept) + Math.Abs(b * intercept) + Math.Abs(c) + 1e-8);
    }

    private static IEnumerable<double> Intercepts(double a, double b, double c)
    {
        if (Math.Abs(a) < 1e-14)
        {
            if (Math.Abs(b) >= 1e-14) yield return -c / b;
            yield break;
        }
        double discriminant = b * b - 4 * a * c;
        if (discriminant < -1e-9 * (b * b + Math.Abs(4 * a * c) + 1e-12)) yield break;
        double stable = -(b + Math.CopySign(Math.Sqrt(Math.Max(0, discriminant)), b)) / 2;
        yield return stable / a;
        if (stable != 0) yield return c / stable;
    }

    private static List<double> Roots(double[] polynomial)
    {
        double magnitude = polynomial.Max(Math.Abs);
        if (magnitude == 0) return new();
        var terms = polynomial.Select(value => value / magnitude).ToArray();
        int degree = Array.FindLastIndex(terms, value => Math.Abs(value) > 1e-14);
        if (degree < 1) return new();
        if (degree == 1)
        {
            double root = -terms[0] / terms[1];
            return root is >= -1.00000001 and <= 1.00000001 ? new() { Math.Clamp(root, -1, 1) } : new();
        }
        var limits = Roots(Enumerable.Range(1, degree).Select(i => i * terms[i]).ToArray());
        limits.Insert(0, -1);
        limits.Add(1);
        var roots = new List<double>();
        for (int index = 0; index < limits.Count; index++)
        {
            double left = limits[index], first = At(terms, left);
            if (Math.Abs(first) < 1e-10) roots.Add(left);
            if (index == limits.Count - 1) continue;
            double right = limits[index + 1], last = At(terms, right);
            if (first * last >= 0) continue;
            for (int iteration = 0; iteration < 56; iteration++)
            {
                double middle = (left + right) / 2;
                double value = At(terms, middle);
                if (first * value > 0) { left = middle; first = value; }
                else right = middle;
            }
            roots.Add((left + right) / 2);
        }
        return roots;
    }

    private static double At(double[] coefficients, double x)
    {
        double result = 0;
        for (int i = coefficients.Length - 1; i >= 0; i--) result = result * x + coefficients[i];
        return result;
    }

    private static double[] Multiply(double[] first, double[] second)
    {
        var result = new double[first.Length + second.Length - 1];
        for (int i = 0; i < first.Length; i++)
            for (int j = 0; j < second.Length; j++) result[i + j] += first[i] * second[j];
        return result;
    }

    private static double[] Subtract(double[] first, double[] second)
    {
        var result = new double[Math.Max(first.Length, second.Length)];
        first.CopyTo(result, 0);
        for (int i = 0; i < second.Length; i++) result[i] -= second[i];
        return result;
    }

    private static double[] Scale(double[] coefficients, double factor) => coefficients.Select(value => value * factor).ToArray();
}
