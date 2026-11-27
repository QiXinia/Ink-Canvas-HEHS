using System.Text.RegularExpressions;

namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private GeometryValue Inequality(string text)
    {
        var tests = new List<Func<double, double, bool>>();
        foreach (string clause in text.Split(new[] { "&&", "∧" }, StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(clause, @"^(.+?)(<=|>=|<|>)(.+)$");
            if (!match.Success) throw new FormatException("不等式请输入两个表达式，并以 && 连接条件。");
            var difference = Compile($"({match.Groups[1].Value})-({match.Groups[3].Value})");
            string comparison = match.Groups[2].Value;
            tests.Add((x, y) =>
            {
                double value = difference(x, y, 0, 0, 0, 0);
                return double.IsFinite(value) && (comparison switch
                {
                    "<" => value < 0, "<=" => value <= 0,
                    ">" => value > 0, _ => value >= 0,
                });
            });
        }
        var faces = new List<GeoTriangle>();
        int columns = _curveSamples < 512 ? 50 : 100;
        int rows = _curveSamples < 512 ? 40 : 80;
        double dx = (view.XMax - view.XMin) / columns, dy = (view.YMax - view.YMin) / rows;
        var filled = new bool[columns, rows];
        for (int i = 0; i < columns; i++)
        for (int j = 0; j < rows; j++)
        {
            Sample();
            double x = view.XMin + i * dx, y = view.YMin + j * dy;
            if (!tests.All(test => test(x + dx / 2, y + dy / 2))) continue;
            filled[i, j] = true;
            var a = new GeoVector(x, y);
            var b = new GeoVector(x + dx, y);
            var c = new GeoVector(x + dx, y + dy);
            var d = new GeoVector(x, y + dy);
            faces.Add(new(a, b, c));
            faces.Add(new(a, c, d));
        }
        var boundary = new List<GeoVector[]>();
        bool Inside(int x, int y) => x >= 0 && x < columns && y >= 0 && y < rows && filled[x, y];
        for (int i = 0; i < columns; i++)
        for (int j = 0; j < rows; j++)
        {
            if (!filled[i, j]) continue;
            var a = new GeoVector(view.XMin + i * dx, view.YMin + j * dy);
            var b = a + new GeoVector(dx, 0);
            var c = a + new GeoVector(dx, dy);
            var d = a + new GeoVector(0, dy);
            if (!Inside(i, j - 1)) boundary.Add(new[] { a, b });
            if (!Inside(i + 1, j)) boundary.Add(new[] { b, c });
            if (!Inside(i, j + 1)) boundary.Add(new[] { c, d });
            if (!Inside(i - 1, j)) boundary.Add(new[] { d, a });
        }
        return new() { Kind = GeometryKind.Region, Expression = text, Display = text, Faces = faces, Paths = boundary };
    }
}
