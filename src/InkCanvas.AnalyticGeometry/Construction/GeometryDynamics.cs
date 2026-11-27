using System.Text.RegularExpressions;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private GeometryValue AggregateList(GeometryValue input, GeometryValue options, bool mean)
    {
        if (input.Kind != GeometryKind.List) throw new FormatException("此指令需要列表。");
        var items = input.Items;
        int count = items.Count;
        if (!mean && options?.Kind == GeometryKind.Scalar)
        {
            double limit = options.Number;
            if (!double.IsFinite(limit) || limit != Math.Truncate(limit) || limit < 0 || limit > count)
                throw new FormatException("求和项数超出列表范围。");
            count = (int)limit;
            options = null;
        }
        if (options is not null && (options.Kind != GeometryKind.List || options.Items.Count != count
            || options.Items.Any(item => item.Kind != GeometryKind.Scalar || !double.IsFinite(item.Number) || item.Number < 0)))
            throw new FormatException("频数列表必须与数据等长，且包含非负数。");
        double Weight(int index) => options is null ? 1 : options.Items[index].Number;
        double totalWeight = options is null ? count : options.Items.Sum(item => item.Number);
        if (mean && totalWeight <= 0) throw new FormatException("平均值需要至少一个有效数据项。");
        if (count == 0) return GeometryValue.Scalar(0);
        var kind = items[0].Kind;
        if (items.Take(count).Any(item => item.Kind != kind)) throw new FormatException("列表中的数据类型须一致。");
        if (kind == GeometryKind.Scalar)
            return GeometryValue.Scalar(Enumerable.Range(0, count).Sum(index => items[index].Number * Weight(index))
                / (mean ? totalWeight : 1));
        if (mean) throw new FormatException("平均值需要数值列表。");
        if (kind is GeometryKind.Point or GeometryKind.Vector)
        {
            var result = new GeoVector();
            for (int index = 0; index < count; index++) result += items[index].Origin * Weight(index);
            return kind == GeometryKind.Point ? GeometryValue.Point(result) : Vector(result);
        }
        if (kind == GeometryKind.Text && options is null)
            return new() { Kind = GeometryKind.Text, Display = string.Concat(items.Take(count).Select(item => item.Display)) };
        if (kind == GeometryKind.Function)
        {
            string expression = string.Join("+", Enumerable.Range(0, count).Select(index =>
            {
                var item = items[index];
                string body = Regex.Replace(item.Expression, @"\b" + Regex.Escape(item.Variable) + @"\b", "x");
                return $"({ComputerAlgebra.Number(Weight(index))})*({body})";
            }));
            return Function(expression);
        }
        throw new FormatException("此类型的列表无法求和。");
    }

    private static List<GeoTriangle> Triangulate(GeoVector[] points)
    {
        var normal = new GeoVector();
        for (int i = 1; i < points.Length - 1 && normal.Length < 1e-10; i++)
            normal = (points[i] - points[0]).Cross(points[i + 1] - points[0]);
        if (normal.Length < 1e-10) throw new FormatException("顶点共线，无法形成多边形。");
        normal = normal.Unit();
        if (points.Any(point => Math.Abs((point - points[0]).Dot(normal)) > 1e-7)) throw new FormatException("多边形顶点必须共面。");
        double Cross(GeoVector a, GeoVector b, GeoVector c) => (b - a).Cross(c - a).Dot(normal);
        double area = 0;
        for (int i = 0; i < points.Length; i++) area += points[i].Cross(points[(i + 1) % points.Length]).Dot(normal);
        double orientation = Math.Sign(area);
        var indices = Enumerable.Range(0, points.Length).ToList();
        var faces = new List<GeoTriangle>();
        while (indices.Count > 3)
        {
            bool found = false;
            for (int i = 0; i < indices.Count; i++)
            {
                int previous = indices[(i + indices.Count - 1) % indices.Count];
                int current = indices[i], next = indices[(i + 1) % indices.Count];
                var a = points[previous]; var b = points[current]; var c = points[next];
                double turn = Cross(a, b, c) * orientation;
                if (Math.Abs(turn) < 1e-10) { indices.RemoveAt(i); found = true; break; }
                if (turn < 0) continue;
                bool inside = indices.Where(index => index != previous && index != current && index != next)
                    .Any(index => Cross(a, b, points[index]) * orientation >= -1e-10
                        && Cross(b, c, points[index]) * orientation >= -1e-10
                        && Cross(c, a, points[index]) * orientation >= -1e-10);
                if (inside) continue;
                faces.Add(new(a, b, c)); indices.RemoveAt(i); found = true; break;
            }
            if (!found) throw new FormatException("多边形边不能相交或重复。");
        }
        if (indices.Count == 3) faces.Add(new(points[indices[0]], points[indices[1]], points[indices[2]]));
        return faces;
    }

    private GeometryValue Zip(string[] args)
    {
        if (args.Length < 3 || args.Length % 2 == 0) throw new FormatException("格式为 Zip(表达式,变量,列表[,变量,列表])。");
        var lists = Enumerable.Range(0, (args.Length - 1) / 2).Select(index => Evaluate(args[2 + index * 2])).ToArray();
        if (lists.Any(list => list.Kind != GeometryKind.List)) throw new FormatException("Zip 参数必须为列表。");
        int count = lists.Min(list => list.Items.Count);
        if (count > 512) throw new FormatException("Zip 最多 512 项。");
        var result = new List<GeometryValue>();
        for (int i = 0; i < count; i++)
        {
            Sample();
            string expression = args[0];
            for (int j = 0; j < lists.Length; j++)
            {
                var item = lists[j].Items[i];
                string replacement = item.Kind == GeometryKind.Point ? item.Origin.ToString() : item.Display;
                expression = Regex.Replace(expression, @"\b" + Regex.Escape(args[1 + j * 2]) + @"\b", "(" + replacement + ")");
            }
            result.Add(Evaluate(expression));
        }
        return Collection(result);
    }

    private GeometryValue Locus(string[] args, bool envelope)
    {
        if (args.Length != 4 && (envelope || args.Length != 2))
            throw new FormatException("格式为 Locus(依赖点,路径点)、Locus(对象,参数名,起始,结束) 或 Envelope(直线,参数名,起始,结束)。");
        string label = args[0].Trim(), parameter = args[1].Trim();
        var driver = view.Objects.FirstOrDefault(item => item.Label == parameter)
            ?? throw new FormatException("请选择已有路径点或数值对象作为轨迹参数。");
        string pathLabel = null;
        double start, end;
        if (args.Length == 2)
        {
            var driverValue = Evaluate(parameter);
            pathLabel = ConstructionDocument.AttachedPath(driver, driverValue);
            var path = driverValue.AttachmentTarget;
            if (pathLabel is null || path is null || !ConstructionDocument.IsAttachablePath(path)
                || ConstructionDocument.IsRegionPoint(driver) || label == parameter || !ConstructionKernel.DependsOn(view, label, parameter))
                throw new FormatException("轨迹需要依赖点和驱动它的路径点。");
            (start, end) = path.Kind switch
            {
                GeometryKind.Circle => (0, Math.Tau),
                GeometryKind.Function => (view.XMin, view.XMax),
                GeometryKind.Line or GeometryKind.Ray => LocusLineRange(path),
                _ => (0, 1),
            };
        }
        else (start, end) = (N(args[2]), N(args[3]));
        var paths = new List<GeoVector[]>();
        var points = new List<GeoVector>();
        GeometryValue previous = null;
        var kernel = new ConstructionKernel();
        // The trace objects themselves are excluded from the sampled dependency graph.
        var sampledObjects = view.Objects.Where(item => !Regex.IsMatch(item.Definition, @"\b(Locus|Envelope)\s*\(", RegexOptions.IgnoreCase)).ToList();
        int sampleCount = _curveSamples < 512 ? 32 : 96;
        for (int i = 0; i <= sampleCount; i++)
        {
            Sample();
            string number = ComputerAlgebra.Number(start + (end - start) * i / sampleCount);
            string definition = pathLabel is null ? number : $"PointOnObject({pathLabel},{number})";
            var state = view with { Objects = sampledObjects.Select(item => item.Label == parameter ? item with { Definition = definition } : item).ToList() };
            var evaluation = kernel.Evaluate(state, token, interactive: _curveSamples < 512);
            if (!evaluation.Values.TryGetValue(label, out var value) || !value.Defined)
            {
                if (points.Count > 1) paths.Add(points.ToArray());
                points.Clear();
                previous = null;
                continue;
            }
            if (!envelope && value.Kind == GeometryKind.Point) points.Add(value.Origin);
            else if (envelope && previous is not null)
            {
                var intersection = Intersect(previous, value);
                if (intersection.Kind == GeometryKind.Point) points.Add(intersection.Origin);
            }
            previous = value;
        }
        if (points.Count > 1) paths.Add(points.ToArray());
        return new() { Kind = GeometryKind.Curve, Paths = paths, Display = envelope ? "数值包络线" : "数值轨迹" };
    }

    private (double Start, double End) LocusLineRange(GeometryValue line)
    {
        GeoVector[] corners = [new(view.XMin, view.YMin), new(view.XMax, view.YMin),
            new(view.XMin, view.YMax), new(view.XMax, view.YMax)];
        var parameters = corners.Select(point => (point - line.Origin).Dot(line.Direction) / line.Direction.Dot(line.Direction)).ToArray();
        return line.Kind == GeometryKind.Ray ? (Math.Max(0, parameters.Min()), Math.Max(0, parameters.Max()))
            : (parameters.Min(), parameters.Max());
    }

    private GeometryValue Revolve(string[] args)
    {
        var curve = Evaluate(args[0]); var axis = Evaluate(args[1]);
        double angle = args.Length > 2 ? N(args[2]) : Math.Tau;
        if (axis.Kind != GeometryKind.Line) throw new FormatException("旋转轴需为直线。");
        var faces = new List<GeoTriangle>(); var paths = new List<GeoVector[]>();
        foreach (var path in curve.Paths)
        {
            var sampled = path.Where((_, i) => i % Math.Max(1, path.Length / 96) == 0).ToArray();
            int ringCount = _curveSamples < 512 ? 12 : 48;
            var rows = new GeoVector[ringCount + 1][];
            for (int i = 0; i <= ringCount; i++)
            {
                Sample();
                rows[i] = sampled.Select(point => axis.Origin + Rotate(point - axis.Origin, axis.Direction, angle * i / ringCount)).ToArray();
                if (i % Math.Max(1, ringCount / 12) == 0) paths.Add(rows[i]);
            }
            for (int i = 0; i < ringCount; i++)
            for (int j = 0; j < sampled.Length - 1; j++)
            {
                faces.Add(new(rows[i][j], rows[i + 1][j], rows[i + 1][j + 1]));
                faces.Add(new(rows[i][j], rows[i + 1][j + 1], rows[i][j + 1]));
            }
        }
        return new() { Kind = GeometryKind.Surface, Faces = faces, Paths = paths, Display = "旋转曲面" };
    }

    private static GeometryValue Net(GeometryValue solid, double fraction)
    {
        var loops = solid.FaceLoops;
        if (loops.Count < 4) throw new FormatException("展开图需要棱柱、棱锥或正多面体。");
        fraction = Math.Clamp(fraction, 0, 1);
        var transforms = new Func<GeoVector, GeoVector>[loops.Count];
        transforms[0] = point => point;
        var queue = new Queue<int>(); queue.Enqueue(0);
        while (queue.TryDequeue(out int parent))
        {
            for (int child = 0; child < loops.Count; child++)
            {
                if (transforms[child] is not null) continue;
                var shared = loops[parent].Where(point => loops[child].Any(other => (point - other).Length < 1e-8)).ToArray();
                if (shared.Length != 2) continue;
                var inherit = transforms[parent];
                var anchor = inherit(shared[0]); var axis = (inherit(shared[1]) - anchor).Unit();
                GeoVector Normal(GeoVector[] face) => (inherit(face[1]) - inherit(face[0])).Cross(inherit(face[2]) - inherit(face[0])).Unit();
                var from = Normal(loops[child]); var to = Normal(loops[parent]);
                double angle = Math.Atan2(axis.Dot(from.Cross(to)), from.Dot(to)) * fraction;
                transforms[child] = point => anchor + Rotate(inherit(point) - anchor, axis, angle);
                queue.Enqueue(child);
            }
        }
        var faces = new List<GeoTriangle>(); var paths = new List<GeoVector[]>();
        for (int i = 0; i < loops.Count; i++)
        {
            if (transforms[i] is null) throw new FormatException("多面体的面未连通。");
            var face = loops[i].Select(transforms[i]).ToArray();
            faces.AddRange(Triangulate(face)); paths.Add(face.Append(face[0]).ToArray());
        }
        return new() { Kind = GeometryKind.Surface, Faces = faces, Paths = paths, Display = "多面体展开图" };
    }
}
