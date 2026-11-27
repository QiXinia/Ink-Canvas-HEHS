namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    internal sealed record RegularSolidTemplate(string Name, GeoVector[] Vertices, int[][] Faces, double Volume);

    // Face and vertex slots are part of the GeoGebra construction format.
    private static readonly Dictionary<string, RegularSolidTemplate> RegularSolids = CreateRegularSolids();

    internal static RegularSolidTemplate FindRegularSolid(string command) => RegularSolids.GetValueOrDefault(command);

    private GeometryValue RegularSolid(string command, string[] args)
    {
        var template = FindRegularSolid(command) ?? throw new FormatException("未知的正多面体。");
        if (args.Length is < 1 or > 3) throw new FormatException("正多面体需要底面或相邻顶点。");

        var first = Evaluate(args[0]);
        var basis = first.Kind == GeometryKind.Polygon ? first.Points : null;
        if (basis is not null && (args.Length > 2 || basis.Count != template.Faces[0].Length))
            throw new FormatException("底面顶点数与正多面体不匹配。");
        if (basis is null && args.Length < 2) throw new FormatException("正多面体缺少第二个顶点。");

        var origin = basis is null ? Point(first) : basis[0];
        var x = (basis is null ? Point(Evaluate(args[1])) : basis[1]) - origin;
        double size = x.Length;
        if (!double.IsFinite(size) || size < 1e-10) throw new FormatException("正多面体的相邻顶点必须不同。");
        bool direct = true;
        GeoVector z;
        IReadOnlyList<GeoVector> fixedVertices = basis;
        if (basis is not null)
        {
            z = x.Unit().Cross((basis[2] - origin) / size).Unit();
            direct = args.Length == 1 || Condition(args[1]);
        }
        else if (args.Length == 3)
        {
            var third = Evaluate(args[2]);
            if (third.Kind == GeometryKind.Point)
            {
                z = x.Unit().Cross((third.Origin - origin) / size).Unit();
                fixedVertices = [origin, origin + x, third.Origin];
            }
            else
            {
                z = third.Kind switch
                {
                    GeometryKind.Plane => third.Normal,
                    GeometryKind.Polygon => (third.Points[1] - third.Points[0]).Cross(third.Points[2] - third.Points[0]).Unit(),
                    _ => Direction(third).Unit()
                };
                if (Math.Abs(z.Dot(x.Unit())) > 1e-8) throw new FormatException("底面方向与给定边不兼容。");
            }
        }
        else
        {
            var yDefault = CircleBasis(x.Unit()).U;
            z = x.Unit().Cross(yDefault);
        }

        var y = z.Cross(x);
        var height = z * (direct ? size : -size);
        var vertices = template.Vertices.Select(vertex => origin + x * vertex.X + y * vertex.Y + height * vertex.Z).ToArray();
        vertices[0] = origin;
        vertices[1] = origin + x;
        if (fixedVertices is not null)
        {
            for (int index = 0; index < fixedVertices.Count; index++)
            {
                if ((vertices[index] - fixedVertices[index]).Length > size * 1e-8)
                    throw new FormatException("给定顶点不构成所需正多边形。");
                vertices[index] = fixedVertices[index];
            }
        }
        var solid = Polyhedron(vertices, template.Faces, template.Volume * size * size * size, template.Name);
        if (basis is null) return solid;

        // The input polygon owns its edges. GeoGebra serializes only the remaining edges as outputs.
        var linkedEdges = SolidEdges([template.Faces[0]]);
        var linkedSet = linkedEdges.ToHashSet();
        var newEdges = SolidEdges(template.Faces.Skip(1)).Where(edge => !linkedSet.Contains(edge));
        return solid with { Paths = newEdges.Concat(linkedEdges).Select(edge => new[] { vertices[edge.First], vertices[edge.Second] }).ToArray() };
    }

    private static Dictionary<string, RegularSolidTemplate> CreateRegularSolids()
    {
        double triangleRadius = Math.Sqrt(3) / 3;
        double tetraHeight = Math.Sqrt(2.0 / 3);
        double icosaRadius = (Math.Sqrt(3) + Math.Sqrt(15)) / 6;
        double pentagonRadius = .5 / Math.Sin(Math.PI / 5);
        double dodecaRadius = .5 / Math.Sin(Math.PI / 5) * (1 + Math.Sqrt(5)) / 2;
        var triangle = Ring(3, triangleRadius, -150, 0, triangleRadius / 2);
        var pentagon = Ring(5, pentagonRadius, -126, 0, .5 / Math.Tan(Math.PI / 5));
        double pentagonCenter = .5 / Math.Tan(Math.PI / 5);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["Cube"] = new("立方体", [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
                new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)],
                [[3, 0, 1, 2], [4, 0, 3, 7], [1, 0, 4, 5], [2, 1, 5, 6], [3, 2, 6, 7], [4, 7, 6, 5]], 1),
            ["Tetrahedron"] = new("正四面体", [..triangle, new(.5, triangleRadius / 2, tetraHeight)],
                [[2, 0, 1], [3, 0, 2], [1, 0, 3], [2, 1, 3]], tetraHeight / (4 * Math.Sqrt(3))),
            ["Octahedron"] = new("正八面体", [..triangle, ..Ring(3, triangleRadius, -90, tetraHeight, triangleRadius / 2)],
                [[2, 0, 1], [5, 0, 2], [3, 0, 5], [1, 0, 3], [4, 1, 3], [2, 1, 4], [5, 2, 4], [5, 4, 3]], Math.Sqrt(2) / 3),
            ["Icosahedron"] = new("正二十面体", [..triangle, ..Ring(3, icosaRadius, -90, triangleRadius, triangleRadius / 2),
                ..Ring(3, icosaRadius, -150, icosaRadius, triangleRadius / 2),
                ..Ring(3, triangleRadius, -90, triangleRadius + icosaRadius, triangleRadius / 2)],
                [[2, 0, 1], [5, 0, 2], [6, 0, 5], [3, 0, 6], [1, 0, 3], [7, 1, 3], [4, 1, 7], [2, 1, 4], [8, 2, 4], [5, 2, 8],
                    [10, 4, 7], [8, 4, 10], [11, 8, 10], [5, 8, 11], [9, 10, 7], [11, 10, 9], [6, 11, 9], [5, 11, 6], [3, 9, 7], [6, 9, 3]],
                5 * (3 + Math.Sqrt(5)) / 12),
            ["Dodecahedron"] = new("正十二面体", [..pentagon, ..Ring(5, dodecaRadius, -54, pentagonRadius, pentagonCenter),
                ..Ring(5, dodecaRadius, -90, dodecaRadius, pentagonCenter),
                ..Ring(5, pentagonRadius, -90, pentagonRadius + dodecaRadius, pentagonCenter)],
                [[4, 0, 1, 2, 3], [9, 0, 4, 8, 14], [1, 0, 9, 10, 5], [2, 1, 5, 11, 6], [3, 2, 6, 12, 7], [4, 3, 7, 13, 8],
                    [14, 8, 13, 18, 19], [9, 14, 19, 15, 10], [5, 10, 15, 16, 11], [6, 11, 16, 17, 12], [7, 12, 17, 18, 13], [19, 18, 17, 16, 15]],
                (15 + 7 * Math.Sqrt(5)) / 4),
        };

        static GeoVector[] Ring(int count, double radius, double angle, double z, double centerY)
            => Enumerable.Range(0, count).Select(index =>
            {
                double theta = angle * Math.PI / 180 + Math.Tau * index / count;
                return new GeoVector(.5 + radius * Math.Cos(theta), centerY + radius * Math.Sin(theta), z);
            }).ToArray();
    }
}
