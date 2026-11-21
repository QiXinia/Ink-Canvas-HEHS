using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private static GeometryValue Invert(GeometryValue value, GeometryValue mirror)
    {
        double radiusSquared = mirror.Radius * mirror.Radius;
        GeoVector Map(GeoVector point)
        {
            var delta = point - mirror.Origin;
            if (Math.Abs(delta.Dot(mirror.Normal)) > 1e-8) throw new FormatException("圆反演对象须与反演圆共面。");
            if (delta.Dot(delta) < 1e-15) throw new FormatException("反演圆心的像在无穷远处。");
            return mirror.Origin + delta * (radiusSquared / delta.Dot(delta));
        }
        if (value.Kind == GeometryKind.Point) return GeometryValue.Point(Map(value.Origin));
        if (value.Kind == GeometryKind.Line)
        {
            var direction = value.Direction.Unit();
            var foot = value.Origin + direction * (mirror.Origin - value.Origin).Dot(direction);
            var delta = foot - mirror.Origin;
            if (delta.Length < 1e-10) return value;
            var center = mirror.Origin + delta * (radiusSquared / (2 * delta.Dot(delta)));
            return Circle(center, radiusSquared / (2 * delta.Length), mirror.Normal);
        }
        if (value.Kind == GeometryKind.Circle)
        {
            var delta = value.Origin - mirror.Origin;
            double denominator = delta.Dot(delta) - value.Radius * value.Radius;
            if (Math.Abs(denominator) < 1e-10)
                return Line(mirror.Origin + delta * (radiusSquared / (2 * delta.Dot(delta))), mirror.Normal.Cross(delta));
            double factor = radiusSquared / denominator;
            return Circle(mirror.Origin + delta * factor, Math.Abs(factor) * value.Radius, mirror.Normal);
        }
        var paths = new List<GeoVector[]>();
        foreach (var path in value.Paths)
        {
            var result = new List<GeoVector>();
            for (int i = 1; i < path.Length; i++)
            for (int j = 0; j < 8; j++)
            {
                var point = path[i - 1] + (path[i] - path[i - 1]) * (j / 8d);
                if ((point - mirror.Origin).Length < 1e-8)
                { if (result.Count > 1) paths.Add(result.ToArray()); result.Clear(); continue; }
                result.Add(Map(point));
            }
            if (path.Length > 0 && (path[^1] - mirror.Origin).Length > 1e-8) result.Add(Map(path[^1]));
            if (result.Count > 1) paths.Add(result.ToArray());
        }
        return new() { Kind = GeometryKind.Curve, Paths = paths, Display = "圆反演 · " + value.Display };
    }
    private GeometryValue RigidPolygon(string[] args)
    {
        if (args.Length != 3 || Evaluate(args[0]).Kind != GeometryKind.List)
            throw new FormatException("格式为 RigidPolygon({原始顶点,...},平移锚点,方向点)，拖动方向点只改变旋转角。");
        var original = Evaluate(args[0]).Items.Select(Point).ToArray();
        if (original.Length < 3) throw new FormatException("刚体多边形至少需要三个顶点。");
        var anchor = Point(Evaluate(args[1])); var heading = Point(Evaluate(args[2])) - anchor;
        var basis = original[1] - original[0];
        if (heading.Length < 1e-9 || basis.Length < 1e-9) throw new FormatException("方向点不能与锚点重合。");
        double angle = Math.Atan2(heading.Y, heading.X) - Math.Atan2(basis.Y, basis.X);
        return Polygon(original.Select(point => anchor + Rotate(point - original[0], new(0, 0, 1), angle)).ToArray());
    }

    private GeometryValue VectorPolygon(string[] args)
    {
        if (args.Length != 2) throw new FormatException("格式为 VectorPolygon(起点,{边向量,...})，最后一条边自动闭合。");
        var origin = Point(Evaluate(args[0])); var list = Evaluate(args[1]);
        if (list.Kind != GeometryKind.List || list.Items.Count < 2) throw new FormatException("至少需要两个边向量。");
        var points = new List<GeoVector> { origin };
        foreach (var item in list.Items) { origin += Point(item); points.Add(origin); }
        if ((points[^1] - points[0]).Length < 1e-9) points.RemoveAt(points.Count - 1);
        return Polygon(points.ToArray());
    }

    private GeometryValue FivePointConic(GeoVector[] points)
    {
        if (points.Length != 5 || points.Any(point => Math.Abs(point.Z) > 1e-9)) throw new FormatException("五点圆锥曲线需要五个平面点。");
        var matrix = new double[5, 6];
        for (int i = 0; i < points.Length; i++)
        {
            double x = points[i].X, y = points[i].Y;
            double[] terms = { x * x, x * y, y * y, x, y, 1 };
            for (int j = 0; j < 6; j++) matrix[i, j] = terms[j];
        }
        var pivots = new List<int>();
        int row = 0;
        for (int column = 0; column < 6 && row < 5; column++)
        {
            int pivot = Enumerable.Range(row, 5 - row).MaxBy(i => Math.Abs(matrix[i, column]));
            if (Math.Abs(matrix[pivot, column]) < 1e-10) continue;
            for (int j = 0; j < 6; j++) (matrix[row, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[row, j]);
            double scale = matrix[row, column];
            for (int j = 0; j < 6; j++) matrix[row, j] /= scale;
            for (int i = 0; i < 5; i++)
            {
                if (i == row) continue;
                double factor = matrix[i, column];
                for (int j = 0; j < 6; j++) matrix[i, j] -= factor * matrix[row, j];
            }
            pivots.Add(column); row++;
        }
        if (pivots.Count != 5) throw new FormatException("五点退化，不能确定唯一圆锥曲线。");
        int free = Enumerable.Range(0, 6).Single(column => !pivots.Contains(column));
        var coefficients = new double[6]; coefficients[free] = 1;
        for (int i = 0; i < 5; i++) coefficients[pivots[i]] = -matrix[i, free];
        string[] monomials = { "x^2", "x*y", "y^2", "x", "y", "1" };
        string equation = string.Join('+', coefficients.Select((coefficient, index) => "(" + ComputerAlgebra.Number(coefficient) + ")*" + monomials[index])) + "=0";
        return Implicit(equation) with { Display = "过五点圆锥曲线 · " + equation };
    }
}
