using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private GeometryValue Plane(string[] args, string command)
    {
        var origin = Point(Evaluate(args[0]));
        var second = Evaluate(args[1]);
        GeoVector normal;
        if (command == "parallelplane") normal = second.Normal;
        else if (command == "perpendicularplane") normal = second.Direction.Unit();
        else if (args.Length == 3) normal = (Point(second) - origin).Cross(Point(Evaluate(args[2])) - origin).Unit();
        else normal = second.Kind == GeometryKind.Vector ? second.Origin.Unit() : (origin - second.Origin).Cross(second.Direction).Unit();
        var (u, v) = CircleBasis(normal);
        var corners = new[] { origin - u * 6 - v * 6, origin + u * 6 - v * 6, origin + u * 6 + v * 6, origin - u * 6 + v * 6 };
        var polygon = Polygon(corners);
        return polygon with
        {
            Kind = GeometryKind.Plane,
            Origin = origin,
            Normal = normal,
            Display = $"{normal.X:G5} x + {normal.Y:G5} y + {normal.Z:G5} z = {normal.Dot(origin):G5}"
        };
    }
    private GeometryValue Solid(string command, string[] args)
    {
        GeoVector P(int i) => Point(Evaluate(args[i]));
        double D(int i) => N(args[i]);
        if (command is "prism" or "pyramid")
        {
            var polygon = Evaluate(args[0]);
            if (polygon.Kind != GeometryKind.Polygon) throw new FormatException("棱柱、棱锥需要一个底面多边形。");
            var points = polygon.Points.ToArray();
            var normal = (points[1] - points[0]).Cross(points[2] - points[0]).Unit();
            var argument = Evaluate(args[1]); var offset = argument.Kind == GeometryKind.Scalar ? normal * argument.Number : argument.Origin - points[0];
            if (command == "prism") return Polyhedron(points.Concat(points.Select(p => p + offset)).ToArray(),
                new[] { Enumerable.Range(0, points.Length).Reverse().ToArray(), Enumerable.Range(points.Length, points.Length).ToArray() }
                    .Concat(Enumerable.Range(0, points.Length).Select(i => new[] { i, (i + 1) % points.Length, (i + 1) % points.Length + points.Length, i + points.Length })).ToArray(),
                Area(polygon) * Math.Abs(offset.Dot(normal)), "棱柱");
            var apex = argument.Kind == GeometryKind.Scalar ? points.Aggregate(new GeoVector(), (a, p) => a + p) / points.Length + offset : argument.Origin;
            return Polyhedron(points.Append(apex).ToArray(), new[] { Enumerable.Range(0, points.Length).Reverse().ToArray() }
                .Concat(Enumerable.Range(0, points.Length).Select(i => new[] { i, (i + 1) % points.Length, points.Length })).ToArray(),
                Area(polygon) * Math.Abs((apex - points[0]).Dot(normal)) / 3, "棱锥");
        }
        if (command is "sphere" or "cylinder" or "cone")
        {
            var center = P(0);
            GeoVector top = command == "sphere" ? center : P(1);
            var radiusValue = Evaluate(args[command == "sphere" ? 1 : 2]);
            double radius = radiusValue.Kind == GeometryKind.Scalar ? radiusValue.Number : (radiusValue.Origin - center).Length;
            if (radius <= 0 || !double.IsFinite(radius)) throw new FormatException("半径必须为正数。");
            if (command == "sphere")
            {
                return ParametricSurface((u, v) => center + new GeoVector(Math.Cos(u) * Math.Sin(v), Math.Sin(u) * Math.Sin(v), Math.Cos(v)) * radius,
                    0, Math.Tau, 0, Math.PI, 32, 20) with
                {
                    Kind = GeometryKind.Solid,
                    Origin = center,
                    Radius = radius,
                    Number = 4 * Math.PI * radius * radius * radius / 3,
                    Display = "球"
                };
            }
            var axis = top - center; double height = axis.Length; var basis = CircleBasis(axis);
            var result = ParametricSurface((u, v) => center + axis * v
                + (basis.U * Math.Cos(u) + basis.V * Math.Sin(u)) * (radius * (command == "cone" ? 1 - v : 1)), 0, Math.Tau, 0, 1, 48, 8);
            var faces = result.Faces.ToList();
            for (int i = 0; i < 48; i++)
            {
                var first = basis.U * (radius * Math.Cos(i * Math.Tau / 48)) + basis.V * (radius * Math.Sin(i * Math.Tau / 48));
                var last = basis.U * (radius * Math.Cos((i + 1) * Math.Tau / 48)) + basis.V * (radius * Math.Sin((i + 1) * Math.Tau / 48));
                faces.Add(new(center, center + last, center + first));
                if (command == "cylinder") faces.Add(new(top, top + first, top + last));
            }
            return result with
            {
                Kind = GeometryKind.Solid,
                Faces = faces,
                Origin = center,
                Direction = axis,
                Radius = radius,
                Number = Math.PI * radius * radius * height / (command == "cone" ? 3 : 1),
                Display = command == "cone" ? "圆锥" : "圆柱"
            };
        }
        var origin = args.Length > 0 ? P(0) : new GeoVector();
        double size = args.Length > 1 ? D(1) : 2;
        if (size <= 0 || !double.IsFinite(size)) throw new FormatException("边长必须为正数。");
        if (command == "cube")
        {
            var points = new[] { new GeoVector(0, 0, 0), new GeoVector(1, 0, 0), new GeoVector(1, 1, 0), new GeoVector(0, 1, 0),
                new GeoVector(0, 0, 1), new GeoVector(1, 0, 1), new GeoVector(1, 1, 1), new GeoVector(0, 1, 1) }.Select(p => origin + p * size).ToArray();
            return Polyhedron(points, new[] { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 } }, size * size * size, "立方体");
        }
        if (command == "tetrahedron")
        {
            var points = new[] { new GeoVector(1, 1, 1), new GeoVector(1, -1, -1), new GeoVector(-1, 1, -1), new GeoVector(-1, -1, 1) }.Select(p => origin + p * (size / Math.Sqrt(8))).ToArray();
            return Polyhedron(points, new[] { new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 0, 3, 2 }, new[] { 1, 2, 3 } }, Math.Pow(size, 3) / (6 * Math.Sqrt(2)), "正四面体");
        }
        if (command == "octahedron")
        {
            var p = new[] { new GeoVector(1, 0, 0), new GeoVector(-1, 0, 0), new GeoVector(0, 1, 0), new GeoVector(0, -1, 0), new GeoVector(0, 0, 1), new GeoVector(0, 0, -1) }.Select(v => origin + v * (size / Math.Sqrt(2))).ToArray();
            return Polyhedron(p, new[] { new[] { 0, 2, 4 }, new[] { 2, 1, 4 }, new[] { 1, 3, 4 }, new[] { 3, 0, 4 }, new[] { 2, 0, 5 }, new[] { 1, 2, 5 }, new[] { 3, 1, 5 }, new[] { 0, 3, 5 } }, Math.Sqrt(2) * Math.Pow(size, 3) / 3, "正八面体");
        }
        double phi = (1 + Math.Sqrt(5)) / 2;
        var ico = new[] {new GeoVector(-1,phi,0),new GeoVector(1,phi,0),new GeoVector(-1,-phi,0),new GeoVector(1,-phi,0),
            new GeoVector(0,-1,phi),new GeoVector(0,1,phi),new GeoVector(0,-1,-phi),new GeoVector(0,1,-phi),new GeoVector(phi,0,-1),new GeoVector(phi,0,1),new GeoVector(-phi,0,-1),new GeoVector(-phi,0,1)};
        var polygons = new List<int[]>();
        for (int a = 0; a < ico.Length; a++) for (int b = a + 1; b < ico.Length; b++) for (int c = b + 1; c < ico.Length; c++)
        {
            if (Math.Abs((ico[a] - ico[b]).Length - 2) > 1e-8 || Math.Abs((ico[a] - ico[c]).Length - 2) > 1e-8 || Math.Abs((ico[b] - ico[c]).Length - 2) > 1e-8) continue;
            polygons.Add((ico[b] - ico[a]).Cross(ico[c] - ico[a]).Dot(ico[a]) > 0 ? new[] { a, b, c } : new[] { a, c, b });
        }
        if (command == "icosahedron") return Polyhedron(ico.Select(p => origin + p * (size / 2)).ToArray(), polygons.ToArray(), 5 * (3 + Math.Sqrt(5)) * Math.Pow(size, 3) / 12, "正二十面体");
        // The dual of the icosahedron gives a regular dodecahedron with ordered pentagonal faces.
        var centers = polygons.Select(face => (ico[face[0]] + ico[face[1]] + ico[face[2]]) / 3).ToArray();
        var dualFaces = new List<int[]>();
        for (int vertex = 0; vertex < ico.Length; vertex++)
        {
            var normal = ico[vertex].Unit(); var basis = CircleBasis(normal);
            dualFaces.Add(Enumerable.Range(0, polygons.Count).Where(i => polygons[i].Contains(vertex))
                .OrderBy(i => Math.Atan2(centers[i].Dot(basis.V), centers[i].Dot(basis.U))).ToArray());
        }
        double edge = (centers[dualFaces[0][0]] - centers[dualFaces[0][1]]).Length;
        return Polyhedron(centers.Select(p => origin + p * (size / edge)).ToArray(), dualFaces.ToArray(), (15 + 7 * Math.Sqrt(5)) * Math.Pow(size, 3) / 4, "正十二面体");
    }

    private static GeometryValue Polyhedron(GeoVector[] points, int[][] polygons, double volume, string name)
    {
        var edges = new HashSet<(int, int)>(); var faces = new List<GeoTriangle>();
        foreach (var polygon in polygons)
        {
            for (int i = 0; i < polygon.Length; i++) { int a = polygon[i], b = polygon[(i + 1) % polygon.Length]; edges.Add((Math.Min(a, b), Math.Max(a, b))); }
            faces.AddRange(Triangulate(polygon.Select(index => points[index]).ToArray()));
        }
        return new() { Kind = GeometryKind.Solid, Points = points, Faces = faces, FaceLoops = polygons.Select(face => face.Select(index => points[index]).ToArray()).ToArray(), Paths = edges.Select(edge => new[] { points[edge.Item1], points[edge.Item2] }).ToArray(), Number = volume, Display = name };
    }
    private GeometryValue Surface(string[] args)
    {
        if (args.Length >= 9)
        {
            string first = args[3], second = args[6];
            var x = CompileBound(args[0], (first,"u"),(second,"v"));
            var y = CompileBound(args[1], (first,"u"),(second,"v"));
            var z = CompileBound(args[2], (first,"u"),(second,"v"));
            return ParametricSurface((u, v) => new(x(0, 0, 0, 0, u, v), y(0, 0, 0, 0, u, v), z(0, 0, 0, 0, u, v)), N(args[4]), N(args[5]), N(args[7]), N(args[8]), 36, 36);
        }
        var function = Compile(args[0]); double xmin = args.Length > 1 ? N(args[1]) : view.XMin, xmax = args.Length > 2 ? N(args[2]) : view.XMax;
        double ymin = args.Length > 3 ? N(args[3]) : view.YMin, ymax = args.Length > 4 ? N(args[4]) : view.YMax;
        return ParametricSurface((x, y) => new(x, y, function(x, y, 0, 0, 0, 0)), xmin, xmax, ymin, ymax, 40, 40);
    }
    private GeometryValue ParametricSurface(Func<double, double, GeoVector> function, double umin, double umax, double vmin, double vmax, int rows, int columns)
    {
        if (!double.IsFinite(umin + umax + vmin + vmax) || umin >= umax || vmin >= vmax)
            throw new FormatException("曲面参数区间无效。");
        var points = new GeoVector[rows + 1, columns + 1];
        var faces = new List<GeoTriangle>();
        var paths = new List<GeoVector[]>();
        for (int i = 0; i <= rows; i++)
        for (int j = 0; j <= columns; j++)
        {
            Sample();
            points[i, j] = function(umin + (umax - umin) * i / rows, vmin + (vmax - vmin) * j / columns);
        }
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < columns; j++)
        {
            var a = points[i, j]; var b = points[i + 1, j]; var c = points[i + 1, j + 1]; var d = points[i, j + 1];
            if (!a.Finite || !b.Finite || !c.Finite || !d.Finite || Math.Max(Math.Max(a.Length, b.Length), Math.Max(c.Length, d.Length)) > 1e5) continue;
            if ((b - a).Cross(c - a).Length > 1e-12) faces.Add(new(a, b, c));
            if ((c - a).Cross(d - a).Length > 1e-12) faces.Add(new(a, c, d));
            if (i % 4 == 0) paths.Add(new[] { a, d });
            if (j % 4 == 0) paths.Add(new[] { a, b });
        }
        return new() { Kind = GeometryKind.Surface, Faces = faces, Paths = paths, Display = "参数曲面" };
    }
    private GeometryValue ImplicitSurface(string text)
    {
        var function = Compile(MathSyntax.EquationDifference(text));
        var faces = new List<GeoTriangle>();
        var paths = new List<GeoVector[]>();
        const int divisions = 24;
        const double minimum = -6, step = 12d / divisions;
        var samples = new double[divisions + 1, divisions + 1, divisions + 1];
        for (int x = 0; x <= divisions; x++)
        for (int y = 0; y <= divisions; y++)
        for (int z = 0; z <= divisions; z++)
        {
            Sample();
            samples[x, y, z] = function(minimum + x * step, minimum + y * step, minimum + z * step, 0, 0, 0);
        }
        (int X, int Y, int Z)[] corners = { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1) };
        int[][] tetrahedra = { new[] { 0, 5, 1, 6 }, new[] { 0, 1, 2, 6 }, new[] { 0, 2, 3, 6 }, new[] { 0, 3, 7, 6 }, new[] { 0, 7, 4, 6 }, new[] { 0, 4, 5, 6 } };
        var positions = new GeoVector[8];
        var valuesAtCorners = new double[8];
        for (int x = 0; x < divisions; x++)
        for (int y = 0; y < divisions; y++)
        for (int z = 0; z < divisions; z++)
        {
            token.ThrowIfCancellationRequested();
            for (int c = 0; c < 8; c++)
            {
                var offset = corners[c];
                positions[c] = new(minimum + (x + offset.X) * step, minimum + (y + offset.Y) * step, minimum + (z + offset.Z) * step);
                valuesAtCorners[c] = samples[x + offset.X, y + offset.Y, z + offset.Z];
            }
            foreach (var tetra in tetrahedra)
            {
                var intersections = new List<GeoVector>();
                void Add(GeoVector point) { if (intersections.All(other => (point - other).Length > 1e-9)) intersections.Add(point); }
                for (int a = 0; a < 4; a++)
                for (int b = a + 1; b < 4; b++)
                {
                    int ia = tetra[a], ib = tetra[b]; double fa = valuesAtCorners[ia], fb = valuesAtCorners[ib];
                    if (!double.IsFinite(fa + fb)) continue;
                    if (Math.Abs(fa) < 1e-12) Add(positions[ia]);
                    if (Math.Abs(fb) < 1e-12) Add(positions[ib]);
                    if (fa * fb < 0) Add(positions[ia] + (positions[ib] - positions[ia]) * (fa / (fa - fb)));
                }
                if (intersections.Count < 3) continue;
                var normal = (intersections[1] - intersections[0]).Cross(intersections[2] - intersections[0]);
                if (normal.Length < 1e-12) continue;
                var center = intersections.Aggregate(new GeoVector(), (sum, point) => sum + point) / intersections.Count;
                var basis = CircleBasis(normal);
                var ordered = intersections.OrderBy(point => Math.Atan2((point - center).Dot(basis.V), (point - center).Dot(basis.U))).ToArray();
                for (int i = 1; i < ordered.Length - 1; i++) faces.Add(new(ordered[0], ordered[i], ordered[i + 1]));
                if (faces.Count % 16 == 0) paths.Add(ordered.Append(ordered[0]).ToArray());
            }
        }
        return new() { Kind = GeometryKind.Surface, Display = text, Faces = faces, Paths = paths };
    }
}
