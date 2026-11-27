using System.Text.RegularExpressions;
using AngouriMath;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private readonly ConstructionSnapshot view;
    private readonly IReadOnlyDictionary<string, GeometryValue> values;
    private readonly CancellationToken token;
    private readonly int _curveSamples;
    private int _samples;
    private readonly Dictionary<string, Func<double, double, double, double, double, double, double>> _compiled = new();

    public GeometryEvaluator(ConstructionSnapshot view, IReadOnlyDictionary<string, GeometryValue> values, CancellationToken token, int curveSamples = 512)
    {
        this.view = view;
        this.values = values;
        this.token = token;
        _curveSamples = Math.Clamp(curveSamples, 64, 512);
    }

    public GeometryValue Evaluate(string text)
    {
        token.ThrowIfCancellationRequested();
        text = SpreadsheetReferences.Expand(MathSyntax.Normalize(text.Trim()), values.Keys);
        if (values.TryGetValue(text, out var reference)) return reference.Defined ? reference : throw new FormatException(text + " 未定义 · " + reference.Error);
        if (text == "xOyPlane") return Plane(["(0,0,0)", "Vector((0,0,1))"], "plane");
        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            var coordinates = MathSyntax.Split(text[1..^1]);
            if (coordinates.Length is 2 or 3) return GeometryValue.Point(new(N(coordinates[0]), N(coordinates[1]), coordinates.Length == 3 ? N(coordinates[2]) : 0));
        }
        if (text.StartsWith('{') && text.EndsWith('}'))
            return Collection(string.IsNullOrWhiteSpace(text[1..^1]) ? [] : MathSyntax.Split(text[1..^1]).Select(Evaluate));
        if (TryReadTextLiteral(text, out string literal))
            return new() { Kind = GeometryKind.Text, Display = literal };
        if (MathSyntax.Call(text, out var command, out var args))
        {
            if (args.Length == 0) throw new FormatException("指令缺少参数。");
            var geometry = Command(command.ToLowerInvariant(), args);
            if (geometry is not null) return geometry;
            if (ComputerAlgebraCommands.Contains(command.ToLowerInvariant()))
            {
                var result = ComputerAlgebra.Evaluate(ExpandAlgebra(text), token);
                bool functionOperation = command.ToLowerInvariant() is "derivative" or "diff"
                    || args.Length <= 2 && command.ToLowerInvariant() is "integral" or "integrate";
                if (functionOperation && values.TryGetValue(args[0], out var source)
                    && source.Kind == GeometryKind.Function && result.Vars.Count <= 1)
                    return Function(result.ToString(), result.Vars.Count == 1 ? result.Vars.Single().ToString() : source.Variable);
                return result is not (Entity.Set or Entity.Matrix) && result.Vars.Count == 1
                    ? Function(result.ToString(),result.Vars.Single().ToString()) : AlgebraValue(result.ToString());
            }
        }
        var arithmetic = Regex.Match(text, @"^([\p{L}_][\p{L}\d_]*)\s*([+-])\s*([\p{L}_][\p{L}\d_]*)$");
        if (arithmetic.Success && values.TryGetValue(arithmetic.Groups[1].Value, out var a) && values.TryGetValue(arithmetic.Groups[3].Value, out var b)
            && a.Kind is GeometryKind.Point or GeometryKind.Vector && b.Kind is GeometryKind.Point or GeometryKind.Vector)
        {
            var result = arithmetic.Groups[2].Value == "+" ? a.Origin + b.Origin : a.Origin - b.Origin;
            return a.Kind == GeometryKind.Vector || b.Kind == GeometryKind.Vector ? Vector(result) : GeometryValue.Point(result);
        }
        string expanded = Expand(text);
        if (Regex.IsMatch(expanded, @"[<>]")) return Inequality(expanded);
        if (expanded.Contains('=') || Regex.IsMatch(expanded, @"(?<![\p{L}\d_])[xy](?![\p{L}\d_])"))
        {
            var equation = MathSyntax.Split(expanded, '=');
            if (equation.Length == 2 && equation[0].Trim() == "y") return Function(equation[1]);
            if (equation.Length == 2 && equation[0].Trim() == "z") return Surface(new[] { equation[1], "-6", "6", "-6", "6" });
            if (equation.Length == 1 && !Regex.IsMatch(expanded, @"\by\b")) return Function(expanded);
            return Implicit(expanded);
        }
        return AlgebraValue(expanded);
    }

    private static readonly HashSet<string> ComputerAlgebraCommands = new(StringComparer.OrdinalIgnoreCase)
    { "simplify", "expand", "factor", "factorize", "numeric", "n", "derivative", "diff", "integral", "integrate", "limit", "solve", "solutions", "substitute", "determinant", "det", "inverse", "transpose", "rank", "real", "imaginary" };

    private GeometryValue AlgebraValue(string expanded)
    {
        var entity = ComputerAlgebra.Evaluate(expanded, token);
        if (entity.Vars.Count > 0) return new() { Kind = GeometryKind.Text, Display = entity.ToString(), Expression = entity.ToString() };
        try
        {
            var value = entity.EvalNumerical().ToNumerics();
            if (Math.Abs(value.Imaginary) > 1e-9) return new() { Kind = GeometryKind.Complex, Origin = new(value.Real, value.Imaginary),
                Expression = entity.ToString(), Display = entity.ToString() };
            return GeometryValue.Scalar(ComputerAlgebra.Real(entity)) with { Expression = entity.ToString(), Display = entity.ToString() };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new() { Kind = GeometryKind.Text, Display = entity.ToString(), Expression = entity.ToString() }; }
    }

    private GeometryValue Command(string command, string[] args)
    {
        GeometryValue G(int i) => Evaluate(args[i]);
        GeoVector P(int i) => Point(G(i));
        double D(int i) => N(args[i]);
        switch (command)
        {
            case "complexpoint":
                var complex = ComputerAlgebra.Parse(Expand(args[0])).EvalNumerical().ToNumerics();
                return GeometryValue.Point(new(complex.Real, complex.Imaginary));
            case "complex":
                var coordinate = P(0);
                return AlgebraValue(ComputerAlgebra.Number(coordinate.X) + "+(" + ComputerAlgebra.Number(coordinate.Y) + ")*i");
            case "point":
            case "pointonobject":
                if (args.Length is < 1 or > 3) throw new FormatException("点需要坐标或一个路径对象。");
                if (args.Length is 2 or 3)
                {
                    var coordinates = args.Select(Evaluate).ToArray();
                    if (coordinates.All(value => value.Kind == GeometryKind.Scalar))
                        return GeometryValue.Point(new(coordinates[0].Number, coordinates[1].Number, args.Length > 2 ? coordinates[2].Number : 0));
                    if (args.Length == 2 && command == "point" && coordinates[0].Kind == GeometryKind.Point
                        && coordinates[1].Kind == GeometryKind.Vector)
                        return GeometryValue.Point(coordinates[0].Origin + coordinates[1].Origin);
                }
                var source = G(0);
                if (args.Length == 1 && command == "point" && source.Kind == GeometryKind.List
                    && source.Items.Count is 2 or 3 && source.Items.All(value => value.Kind == GeometryKind.Scalar))
                    return GeometryValue.Point(new(source.Items[0].Number, source.Items[1].Number,
                        source.Items.Count == 3 ? source.Items[2].Number : 0));
                var input = args.Length > 1 ? G(1) : null;
                double t = input is null ? .5 : input.Kind == GeometryKind.Scalar ? input.Number : D(1);
                GeometryValue Attached(GeoVector position) => GeometryValue.Point(position) with { PathParameter = t, AttachmentTarget = source };
                if (source.Kind == GeometryKind.Circle) return Attached(source.Origin + CircleBasis(source.Normal).U * (source.Radius * Math.Cos(t))
                    + CircleBasis(source.Normal).V * (source.Radius * Math.Sin(t)));
                if (source.Kind is GeometryKind.Line or GeometryKind.Segment or GeometryKind.Ray)
                    return Attached(source.Origin + source.Direction * (source.Kind == GeometryKind.Segment ? Math.Clamp(t,0,1) : source.Kind == GeometryKind.Ray ? Math.Max(0,t) : t));
                if (source.Kind == GeometryKind.Function)
                {
                    var function = CompileBound(source.Expression, (source.Variable, "x"));
                    return Attached(new(t, function(t, 0, 0, 0, 0, 0)));
                }
                if (source.Kind is GeometryKind.Curve or GeometryKind.Conic or GeometryKind.Polygon or GeometryKind.Region)
                    return Attached(PointAlongPaths(source.Paths, t));
                throw new FormatException("点工具需要坐标、直线、圆、函数或曲线。");
            case "isinregion":
                return GeometryBoolean(GeometryPath.TryRegionCoordinates(G(1), P(0), out _, out _, out _, project: false));
            case "pointin":
                var region = G(0);
                if (args.Length == 4)
                    return GeometryValue.Point(GeometryPath.RegionPoint(region, (int)D(1), D(2), D(3))) with { AttachmentTarget = region };
                if (args.Length == 2 && GeometryPath.TryRegionCoordinates(region, P(1), out int face, out double u, out double v))
                    return GeometryValue.Point(GeometryPath.RegionPoint(region, face, u, v)) with { RegionPosition = (face, u, v), AttachmentTarget = region };
                if (args.Length == 1)
                {
                    bool polygonRegion = region.Kind == GeometryKind.Polygon;
                    return GeometryValue.Point(GeometryPath.RegionPoint(region, 0, polygonRegion ? 1d / 3 : 0, polygonRegion ? 1d / 3 : 0))
                        with { AttachmentTarget = region };
                }
                throw new FormatException("区域点需要圆、椭圆、多边形或平面。");
            case "midpoint":
            case "center":
                return args.Length switch
                {
                    1 => GeometryValue.Point(Midpoint(G(0))),
                    2 => GeometryValue.Point((P(0) + P(1)) / 2),
                    _ => throw new FormatException("中点需要两个点、线段或有中心的圆锥曲线。"),
                };
            case "pathparameter":
                if (GeometryPath.TryGetParameter(G(0), P(1), out double parameter)) return GeometryValue.Scalar(parameter);
                throw new FormatException("无法计算点在路径上的位置。");
            case "line":
            case "segment":
            case "ray":
                var start = P(0); var second = G(1);
                GeoVector direction = second.Kind switch
                {
                    GeometryKind.Scalar when command == "segment" => new(second.Number, 0),
                    GeometryKind.Vector => second.Origin,
                    GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment when command == "line" => second.Direction,
                    _ => Point(second) - start,
                };
                return Line(start, direction, command == "segment" ? GeometryKind.Segment : command == "ray" ? GeometryKind.Ray : GeometryKind.Line);
            case "lineequation": return LineEquation(args[0]);
            case "vector":
                var vectorOrigin = P(0);
                return args.Length == 1 ? Vector(vectorOrigin) : Vector(P(1) - vectorOrigin, vectorOrigin);
            case "unitvector":
                var unitSource = G(0);
                return Vector((unitSource.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment ? unitSource.Direction : Point(unitSource)).Unit());
            case "dot": return GeometryValue.Scalar(P(0).Dot(P(1)));
            case "cross": return Vector(P(0).Cross(P(1)));
            case "perpendicularvector": case "unitperpendicularvector":
                var vector = Direction(G(0));
                GeoVector normal = new(-vector.Y, vector.X);
                return Vector(command == "unitperpendicularvector" ? normal.Unit() : normal);
            case "parallel": case "parallelline": return Line(P(0), G(1).Direction);
            case "perpendicular": case "perpendicularline": return PerpendicularLine(args);
            case "perpendicularbisector":
                var endpoints = G(0);
                var first = args.Length == 1 && endpoints.Kind == GeometryKind.Segment ? endpoints.Origin : Point(endpoints);
                var last = args.Length == 1 && endpoints.Kind == GeometryKind.Segment ? endpoints.Origin + endpoints.Direction : P(1);
                var delta = last - first;
                return Line((first + last) / 2, new(-delta.Y, delta.X));
            case "anglebisector":
                if (args.Length == 2) return BisectLines(G(0), G(1));
                var vertex = P(1); return Line(vertex, (P(0) - vertex).Unit() + (P(2) - vertex).Unit());
            case "polygon": return Polygon(args.Length == 1 && G(0) is { Kind: GeometryKind.List } vertices
                ? vertices.Items.Select(Point).ToArray() : Points(args));
            case "centroid":
                var centroidPolygon = G(0);
                if (args.Length != 1 || centroidPolygon.Kind != GeometryKind.Polygon)
                    throw new FormatException("重心需要一个多边形。");
                var weightedCenter = new GeoVector();
                double totalArea = 0;
                foreach (var triangle in centroidPolygon.Faces)
                {
                    double areaWeight = (triangle.B - triangle.A).Cross(triangle.C - triangle.A).Length;
                    weightedCenter += (triangle.A + triangle.B + triangle.C) * areaWeight;
                    totalArea += areaWeight;
                }
                if (totalArea <= 0) throw new FormatException("退化的多边形没有重心。");
                return GeometryValue.Point(weightedCenter / (3 * totalArea));
            case "polygonside":
                var polygon = G(0);
                int side = (int)D(1) - 1;
                if (polygon.Kind != GeometryKind.Polygon || side < 0 || side >= polygon.Points.Count)
                    throw new FormatException("无效的多边形边序号。");
                return Line(polygon.Points[side], polygon.Points[(side + 1) % polygon.Points.Count] - polygon.Points[side], GeometryKind.Segment);
            case "polygonvertex": return IndexedVertex(G(0), (int)D(1) - 1, GeometryKind.Polygon);
            case "solidvertex":
                var vertexSolid = G(0);
                int solidVertex = (int)D(1);
                return IndexedVertex(vertexSolid, solidVertex < 0 ? vertexSolid.Points.Count + solidVertex : solidVertex - 1, GeometryKind.Solid);
            case "prismtopvertex":
                var prism = G(0);
                int topVertex = (int)D(1) - 1;
                if (topVertex < 0) throw new FormatException("顶点序号超出范围。");
                return IndexedVertex(prism, prism.Points.Count / 2 + topVertex, GeometryKind.Solid);
            case "solidface":
                var solid = G(0);
                int faceIndex = (int)D(1) - 1;
                if (faceIndex < 0 || faceIndex >= solid.FaceLoops.Count) throw new FormatException("立体面的序号超出范围。");
                return Polygon(solid.FaceLoops[faceIndex]);
            case "solidedge":
                var solidEdges = G(0);
                int edgeIndex = (int)D(1) - 1;
                if (edgeIndex < 0 || edgeIndex >= solidEdges.Paths.Count) throw new FormatException("立体棱的序号超出范围。");
                var edge = solidEdges.Paths[edgeIndex];
                return Line(edge[0], edge[1] - edge[0], GeometryKind.Segment);
            case "polyline": return new() { Kind = GeometryKind.Curve, Paths = new[] { Points(args) }, Display = "折线" };
            case "rigidpolygon": return RigidPolygon(args);
            case "vectorpolygon": return VectorPolygon(args);
            case "fitline": return FitLine(G(0));
            case "vectorfrompoint":
                var vectorStart = P(0); var copiedVector = G(1);
                if (copiedVector.Kind != GeometryKind.Vector) throw new FormatException("请选择一个向量。");
                return Vector(copiedVector.Direction, vectorStart);
            case "regularpolygon": return RegularPolygon(P(0), P(1), (int)D(2));
            case "circle": return EvaluateCircle(args);
            case "radius":
                var radiusCircle = G(0);
                if (args.Length != 1 || radiusCircle.Kind != GeometryKind.Circle)
                    throw new FormatException("半径需要一个圆。");
                return GeometryValue.Scalar(radiusCircle.Radius);
            case "compass":
                if (args.Length == 3) return Circle(P(2), (P(1) - P(0)).Length);
                if (args.Length == 2 && G(0) is { } compassSource)
                {
                    if (compassSource.Kind == GeometryKind.Segment) return Circle(P(1), compassSource.Direction.Length);
                    if (compassSource.Kind == GeometryKind.Circle) return Circle(P(1), compassSource.Radius);
                }
                return EvaluateCircle(args);
            case "circumcircle": return Circumcircle(P(0), P(1), P(2));
            case "semicircle":
                var c = (P(0) + P(1)) / 2; double angle = Math.Atan2(P(0).Y - c.Y, P(0).X - c.X);
                return Arc(c, (P(0) - c).Length, angle, angle + Math.PI, false);
            case "arc":
            case "sector":
                var origin = P(0); double begin = Math.Atan2(P(1).Y - origin.Y, P(1).X - origin.X);
                double end = Math.Atan2(P(2).Y - origin.Y, P(2).X - origin.X);
                if (end < begin) end += Math.Tau;
                return Arc(origin, (P(1) - origin).Length, begin, end, command == "sector");
            case "circumarc":
            case "circumsector":
                var circum = Circumcircle(P(0), P(1), P(2));
                return CircumArc(circum, P(0), P(1), P(2), command == "circumsector");
            case "intersect": case "intersectpath":
                var intersection = Intersect(G(0), G(1));
                if (args.Length == 3)
                {
                    var selector = G(2);
                    if (selector.Kind == GeometryKind.Scalar) return ConstructionOutput(intersection, (int)selector.Number);
                    if (intersection.Kind == GeometryKind.Point) return intersection;
                    return intersection.Items.Where(item => item.Kind == GeometryKind.Point)
                        .MinBy(item => (item.Origin - Point(selector)).Length) ?? throw new FormatException("两个对象没有可选交点。");
                }
                return intersection;
            case "constructionoutput": return ConstructionOutput(G(0), (int)D(1));
            case "geogebraunsupported": throw new FormatException("此 GeoGebra 构造需要映射 · " + args[0].Trim('"'));
            case "element":
                var list = G(0);
                for (int dimension = 1; dimension < args.Length; dimension++)
                {
                    int index = (int)D(dimension) - 1;
                    if (list.Kind != GeometryKind.List || index < 0 || index >= list.Items.Count)
                        throw new FormatException("元素序号超出范围。");
                    list = list.Items[index];
                }
                if (args.Length == 1) throw new FormatException("Element 需要元素序号。");
                return list;
            case "first": case "last": case "take": case "reverse":
            case "append": case "join": case "flatten": case "sort": case "unique":
                return ListCommand(command, args);
            case "variance": case "sd": case "samplevariance": case "samplesd": case "median": case "product":
                return ListStatistic(command, args);
            case "sum":
            case "mean":
                if (args.Length > 2) return null;
                return AggregateList(G(0), args.Length == 2 ? G(1) : null, command == "mean");
            case "tangent":
                var tangentInput = G(0);
                var tangentTarget = G(1);
                if (PlanarConic.FromGeometry(tangentInput) is not null && PlanarConic.FromGeometry(tangentTarget) is not null)
                    return CommonTangents(tangentInput, tangentTarget);
                if (IsLinear(tangentInput)) return ParallelTangents(tangentInput, tangentTarget);
                return Tangent(tangentInput.Kind == GeometryKind.Scalar ? new(tangentInput.Number, 0) : Point(tangentInput), tangentTarget);
            case "commontangents": return CommonTangents(G(0), G(1));
            case "istangent": return GeometryBoolean(IsTangent(G(0), G(1)));
            case "polar":
            case "diameter":
                var polarSource = G(0);
                var polarTarget = G(1);
                return PolarLine(polarSource, polarTarget, command == "diameter");
            case "translate": return Transform(G(0), point => point + P(1));
            case "rotate":
                var pivot = args.Length > 2 ? P(2) : default; double theta = D(1);
                var axis = args.Length > 3 ? P(3) : new GeoVector(0, 0, 1);
                return Transform(G(0), point => Rotate(point - pivot, axis, theta) + pivot);
            case "dilate":
            case "homothety":
                var anchor = args.Length > 2 ? P(2) : default; double factor = D(1);
                return Transform(G(0), point => anchor + (point - anchor) * factor);
            case "reflect":
                var mirror = G(1);
                if (mirror.Kind == GeometryKind.Circle) return Invert(G(0), mirror);
                var reflected = Transform(G(0), point => mirror.Kind switch
                {
                    GeometryKind.Point => mirror.Origin * 2 - point,
                    GeometryKind.Line or GeometryKind.Segment or GeometryKind.Ray => mirror.Origin + mirror.Direction.Unit() * (2 * (point - mirror.Origin).Dot(mirror.Direction.Unit())) - (point - mirror.Origin),
                    GeometryKind.Plane => point - mirror.Normal * (2 * (point - mirror.Origin).Dot(mirror.Normal)),
                    _ => throw new FormatException("对称对象需为点、直线、平面或圆。"),
                });
                return reflected;
            case "distance":
            case "length":
                if (command == "length" && args.Length == 3) return ArcLength(G(0), D(1), D(2));
                if (args.Length == 2) return GeometryValue.Scalar(Distance(G(0), G(1)));
                var length = G(0); return GeometryValue.Scalar(length.Kind == GeometryKind.Circle ? Math.Tau * length.Radius
                    : length.Kind == GeometryKind.List ? length.Items.Count
                    : length.Kind == GeometryKind.Text ? length.Display.Length
                    : length.Kind == GeometryKind.Vector ? length.Direction.Length
                    : length.Paths.Sum(path => path.Zip(path.Skip(1)).Sum(pair => (pair.Second - pair.First).Length)));
            case "area": return GeometryValue.Scalar(Area(G(0)));
            case "circumference":
            case "perimeter":
                if (args.Length != 1) throw new FormatException("周长需要一个图形。");
                var boundary = G(0);
                if (boundary.Kind == GeometryKind.Circle) return GeometryValue.Scalar(Math.Tau * boundary.Radius);
                if (boundary.Kind == GeometryKind.Conic && boundary.Expression == "ellipse")
                {
                    double a = boundary.Radius, b = boundary.Number;
                    double h = Math.Pow((a - b) / (a + b), 2);
                    return GeometryValue.Scalar(Math.PI * (a + b) * (1 + 3 * h / (10 + Math.Sqrt(4 - 3 * h))));
                }
                if (command == "perimeter" && boundary.Kind is GeometryKind.Polygon or GeometryKind.Curve)
                    return GeometryValue.Scalar(boundary.Paths.Sum(path => path.Zip(path.Skip(1)).Sum(pair => (pair.Second - pair.First).Length)));
                throw new FormatException("该图形没有定义周长。");
            case "slope": var slope = G(0); return GeometryValue.Scalar(slope.Direction.Y / slope.Direction.X);
            case "arg": case "alt":
                if (args.Length == 1 && values.TryGetValue(args[0], out var position) && position.Kind is GeometryKind.Point or GeometryKind.Vector)
                {
                    var point = position.Origin;
                    return GeometryValue.Scalar(command == "arg" ? Math.Atan2(point.Y, point.X)
                        : Math.Atan2(point.Z, Math.Sqrt(point.X * point.X + point.Y * point.Y)));
                }
                return null;
            case "angle":
                if (args.Length is 1 or 2 && G(0) is { Kind: GeometryKind.Polygon } anglePolygon)
                {
                    if (args.Length == 2) return PolygonAngle(anglePolygon, (int)D(1) - 1);
                    return Collection(Enumerable.Range(0, anglePolygon.Points.Count).Select(index => PolygonAngle(anglePolygon, index)));
                }
                return GeometryValue.Scalar(Angle(args.Length == 3 ? P(0) - P(1) : G(0).Direction,
                args.Length == 3 ? P(2) - P(1) : G(1).Direction));
            case "measure": return Measure(G(0), G(1), G(2).Display);
            case "relation": return Relation(G(0), G(1), args.Length > 2 ? args[2].Trim('"') : "parallel");
            case "areparallel": return Relation(G(0), G(1), "parallel");
            case "areperpendicular": return Relation(G(0), G(1), "perpendicular");
            case "areconcurrent":
                var firstIntersection = Intersect(G(0), G(1));
                return GeometryBoolean(firstIntersection.Kind == GeometryKind.Point
                    && Distance(firstIntersection, G(2)) <= 1e-8);
            case "arecongruent":
                var firstSegment = G(0); var secondSegment = G(1);
                if (firstSegment.Kind is not (GeometryKind.Segment or GeometryKind.Vector)
                    || secondSegment.Kind is not (GeometryKind.Segment or GeometryKind.Vector))
                    throw new FormatException("此命题需要两个线段或向量。");
                return GeometryBoolean(Math.Abs(firstSegment.Direction.Length - secondSegment.Direction.Length) <= 1e-8);
            case "arecollinear":
                var ab = P(1) - P(0);
                var ac = P(2) - P(0);
                return GeometryBoolean(ab.Cross(ac).Length <= 1e-8 * Math.Max(1, ab.Length * ac.Length));
            case "arecoplanar":
                if (args.Length != 4) throw new FormatException("共面命题需要四个点。");
                var firstSide = P(1) - P(0);
                var secondSide = P(2) - P(0);
                var thirdSide = P(3) - P(0);
                return GeometryBoolean(Math.Abs(firstSide.Cross(secondSide).Dot(thirdSide))
                    <= 1e-8 * Math.Max(1, firstSide.Length * secondSide.Length * thirdSide.Length));
            case "areequal":
                var left = G(0); var right = G(1);
                return GeometryBoolean(left.Kind == right.Kind && (left.Kind is GeometryKind.Point or GeometryKind.Vector
                    ? (left.Origin - right.Origin).Length <= 1e-8 : left.Display == right.Display));
            case "areconcyclic":
                var circumcircle = Circumcircle(P(0), P(1), P(2));
                var displacement = P(3) - circumcircle.Origin;
                return GeometryBoolean(Math.Abs(displacement.Dot(circumcircle.Normal)) <= 1e-8
                    && Math.Abs(displacement.Length - circumcircle.Radius) <= 1e-8 * Math.Max(1, circumcircle.Radius));
            case "text": return Text(args);
            case "name":
                if (args.Length != 1 || !values.ContainsKey(args[0])) throw new FormatException("Name 需要对象标签。");
                return new() { Kind = GeometryKind.Text, Display = args[0], TextAnchor = default(GeoVector) };
            case "function": return Function(ExpandBound(args[0], args.Length > 1 ? args[1] : "x"), args.Length > 1 ? args[1] : "x",
                args.Length > 2 ? D(2) : null, args.Length > 3 ? D(3) : null);
            case "curve": return Curve(args);
            case "polarcurve": return PolarCurve(args);
            case "parametricderivative":
                if (args.Length != 1 || G(0).ParametricCurve is not { Coordinates.Length: 2 } inputCurve)
                    throw new FormatException("参数求导需要一条平面参数曲线。");
                var dx = ComputerAlgebra.Parse(inputCurve.Coordinates[0]).Differentiate("t").Simplify();
                var dy = ComputerAlgebra.Parse(inputCurve.Coordinates[1]).Differentiate("t").Simplify();
                if (dx.Equals((Entity)0)) throw new FormatException("参数曲线的横坐标导数为零。");
                return ParametricCurve([inputCurve.Coordinates[0], (dy / dx).Simplify().ToString()], inputCurve.Minimum, inputCurve.Maximum);
            case "curvederivative":
                if (args.Length > 2 || G(0).ParametricCurve is not { } curve)
                    throw new FormatException("曲线求导需要参数曲线和可选阶数。");
                return CurveDerivative(curve, args.Length == 2 ? D(1) : 1);
            case "derivative": case "diff":
                if (args.Length > 2) return null;
                if (!values.TryGetValue(args[0], out var derivativeSource)
                    && MathSyntax.Call(args[0], out string derivativeCommand, out _)
                    && derivativeCommand.ToLowerInvariant() is "curve" or "curvederivative" or "derivative" or "diff"
                        or "translate" or "rotate" or "dilate" or "homothety" or "reflect")
                    derivativeSource = G(0);
                return derivativeSource?.ParametricCurve is { } parametric
                    ? CurveDerivative(parametric, args.Length == 2 ? D(1) : 1)
                    : null;
            case "root": case "extremum": case "inflectionpoint":
                var functionPoints = G(0);
                if (args.Length == 2 && command == "root") return RootNear(functionPoints, D(1));
                if (args.Length == 3)
                {
                    var range = SpecialPoints(functionPoints, command, D(1), D(2));
                    return command == "root" ? ConstructionOutput(range, 1) : range;
                }
                return SpecialPoints(functionPoints, command);
            case "integral": case "integrate":
                if (args.Length == 3 || args.Length == 4 && (!MathSyntax.Identifier(args[1])
                    || values.TryGetValue(args[1], out var bound) && bound.Kind == GeometryKind.Scalar))
                {
                    var integral = IntegralRegion(G(0), null, D(1), D(2));
                    return args.Length == 4 && !Condition(args[3]) ? integral with { Number = double.NaN, Display = "积分区域" } : integral;
                }
                return null;
            case "curvature": case "osculatingcircle": case "normal": return Differential(G(0), D(1), command);
            case "integralbetween": return IntegralRegion(G(0), G(1), D(2), D(3));
            case "integralarea": return IntegralRegion(G(0), null, D(1), D(2));
            case "slopefield": return SlopeField(args[0]);
            case "solveode": return SolveOde(args);
            case "sequence": return Sequence(args);
            case "zip": return Zip(args);
            case "locus": case "envelope": return Locus(args, command == "envelope");
            case "plane": case "parallelplane": case "perpendicularplane": return Plane(args, command);
            case "ggbregularsolid": return RegularSolid(args[0].Trim('"'), args[1..]);
            case "sphere":
            case "cylinder":
            case "cone":
            case "prism":
            case "pyramid":
            case "cube":
            case "tetrahedron":
            case "octahedron":
            case "icosahedron":
            case "dodecahedron": return Solid(command, args);
            case "surface": return Surface(args);
            case "surfacefunction": return SurfaceFunction(args);
            case "revolve": return Revolve(args);
            case "net": return Net(Evaluate(args[0]), args.Length > 1 ? D(1) : 1);
            case "prove": case "provedetails": return GeometryProof.Evaluate(view, args[0], command == "provedetails", token);
            case "volume": return GeometryValue.Scalar(G(0).Number);
            case "surfacearea": return GeometryValue.Scalar(G(0).Faces.Sum(face => (face.B - face.A).Cross(face.C - face.A).Length / 2));
            case "conic": return EvaluateConic(args);
            case "conicequation": return ConicFromEquation(args[0]);
            case "ellipse": case "hyperbola": case "parabola": return Conic(command, args);
            case "focus": case "directrix": case "eccentricity": case "asymptote": case "vertex": return ConicProperty(G(0), command);
            case "checkbox":
                bool enabled = Condition(args[0]);
                return new() { Kind = GeometryKind.Boolean, Number = enabled ? 1 : 0, Display = enabled ? "true" : "false", ControlKind = "checkbox" };
            case "button": return new() { Kind = GeometryKind.Text, Display = args[0].Trim('"'), ControlKind = "button" };
            case "inputbox": return new() { Kind = GeometryKind.Text, Display = G(0).Display, ControlKind = "input", ControlTarget = args[0].Trim() };
            case "dropdown":
                var choices = G(0);
                int selected = args.Length > 1 ? (int)D(1) - 1 : 0;
                if (choices.Kind != GeometryKind.List || selected < 0 || selected >= choices.Items.Count) throw new FormatException("Dropdown 需要非空列表与有效序号。");
                return choices.Items[selected] with { ControlKind = "dropdown", ControlTarget = args[0], Options = choices.Items.Select(value => value.Display).ToArray() };
            case "if":
                if (Regex.IsMatch(args[0], @"\b[xy]\b")) return Function(Expand("If(" + string.Join(',', args) + ")"));
                return Condition(args[0]) ? G(1) : args.Length > 2 ? G(2) : new() { Kind = GeometryKind.Text, Display = "" };
            default: return null;
        }
    }

    private static GeometryValue ConstructionOutput(GeometryValue value, int index)
    {
        if (value.Kind != GeometryKind.List && index == 1) return value;
        if (index > 0 && index <= value.Items.Count) return value.Items[index - 1];
        throw new FormatException("构造输出序号超出范围。");
    }

    private static GeometryValue GeometryBoolean(bool value) => new()
        { Kind = GeometryKind.Boolean, Number = value ? 1 : 0, Display = value ? "true" : "false" };

    private static GeometryValue IndexedVertex(GeometryValue value, int index, GeometryKind kind)
    {
        if (value.Kind != kind || index < 0 || index >= value.Points.Count)
            throw new FormatException("顶点序号超出范围。");
        return GeometryValue.Point(value.Points[index]);
    }

    private GeoVector[] Points(string[] args) => args.Select(arg => Point(Evaluate(arg))).ToArray();
    private static GeoVector PointAlongPaths(IReadOnlyList<GeoVector[]> paths, double parameter)
    {
        var lengths = new List<(GeoVector Start, GeoVector End, double Length)>();
        double total = 0;
        foreach (var path in paths)
            for (int index = 1; index < path.Length; index++)
            {
                double length = (path[index] - path[index - 1]).Length;
                if (length <= 1e-12) continue;
                lengths.Add((path[index - 1], path[index], length));
                total += length;
            }
        if (total <= 1e-12) throw new FormatException("此对象没有可附着的路径。");
        double target = Math.Clamp(parameter, 0, 1) * total;
        foreach (var segment in lengths)
        {
            if (target <= segment.Length) return segment.Start + (segment.End - segment.Start) * (target / segment.Length);
            target -= segment.Length;
        }
        return lengths[^1].End;
    }
    private static GeoVector Point(GeometryValue value) => value.Kind is GeometryKind.Point or GeometryKind.Vector ? value.Origin
        : value.Kind == GeometryKind.List && value.Items.Count == 1 ? Point(value.Items[0]) : throw new FormatException("此参数需要点或向量。");
    private double N(string text) => ComputerAlgebra.Real(Expression(text));
    private Entity Expression(string text) => ComputerAlgebra.Parse(Expand(text));
    internal string Expand(string text) => ExpandCore(text, null);
    internal string ExpandAlgebra(string text)
    {
        if (!MathSyntax.Call(text, out var command, out var args)) return Expand(text);
        if (command.ToLowerInvariant() is "derivative" or "diff" && args.Length == 2
            && !MathSyntax.Identifier(args[1]))
        {
            var variable = values.TryGetValue(args[0], out var function) && function.Kind == GeometryKind.Function ? function.Variable : "x";
            return $"{command}({ExpandBound(args[0], variable)},{variable},{Expand(args[1])})";
        }
        if (command.ToLowerInvariant() is "derivative" or "diff" or "integral" or "integrate" or "limit" or "solve" or "solutions" or "substitute")
        {
            string variables = args.Length > 1 ? args[1].Trim().Trim('{','}') : "x";
            return ExpandBound(text,MathSyntax.Split(variables).Where(MathSyntax.Identifier).ToArray());
        }
        return Expand(text);
    }
    private string ExpandCore(string text, IReadOnlySet<string> bound)
    {
        string reference = text.Trim();
        if (bound?.Contains(reference) != true && values.TryGetValue(reference, out var named) && named.Kind == GeometryKind.Function) return named.Expression;
        text = text.Replace("°", "*pi/180");
        var scalarCalls = Regex.Matches(text, @"\b(Distance|Length|Area|Slope|Angle|Dot|Volume|SurfaceArea|Curvature|Eccentricity)\s*\(", RegexOptions.IgnoreCase);
        foreach (Match match in scalarCalls.Reverse())
        {
            int depth = 1, end = match.Index + match.Length;
            for (; end < text.Length && depth > 0; end++)
            { if (text[end] == '(') depth++; else if (text[end] == ')') depth--; }
            if (depth != 0) continue;
            string call = text[match.Index..end];
            var result = Evaluate(call);
            if (result.Kind == GeometryKind.Scalar) text = text[..match.Index] + "(" + ComputerAlgebra.Number(result.Number) + ")" + text[end..];
        }
        text = Regex.Replace(text, @"\b([xyz])\(([\p{L}_][\p{L}\d_]*)\)", match =>
        {
            if (!values.TryGetValue(match.Groups[2].Value, out var value)) return match.Value;
            var p = Point(value);
            return ComputerAlgebra.Number(match.Groups[1].Value == "x" ? p.X : match.Groups[1].Value == "y" ? p.Y : p.Z);
        });
        var calls = Regex.Matches(text, @"\b([\p{L}_][\p{L}\d_]*)\s*\(");
        foreach (Match match in calls.Reverse())
        {
            string name = match.Groups[1].Value;
            if (bound?.Contains(name) == true || !values.TryGetValue(name, out var function) || function.Kind != GeometryKind.Function) continue;
            int start = match.Index + match.Length, end = start, depth = 1;
            for (; end < text.Length && depth > 0; end++)
            { if (text[end] == '(') depth++; else if (text[end] == ')') depth--; }
            if (depth != 0) continue;
            string argument = text[start..(end - 1)];
            string substituted = Regex.Replace(function.Expression, @"\b" + Regex.Escape(function.Variable) + @"\b", _ => "(" + argument + ")");
            text = text[..match.Index] + "(" + substituted + ")" + text[end..];
            if (text.Length > 4096) throw new FormatException("展开后的表达式超过 4096 个字符。");
        }
        return Regex.Replace(text, @"\b[\p{L}_][\p{L}\d_]*\b", match =>
        {
            if (bound?.Contains(match.Value) == true || !values.TryGetValue(match.Value, out var value)) return match.Value;
            if (value.Kind == GeometryKind.Function && !text.AsSpan(match.Index + match.Length).TrimStart().StartsWith("("))
                return "(" + value.Expression + ")";
            return (value.Kind is GeometryKind.Scalar or GeometryKind.Boolean or GeometryKind.Complex
                || value.Kind == GeometryKind.Region && double.IsFinite(value.Number) && value.Expression.Length > 0)
                ? "(" + (value.Expression.Length > 0 ? value.Expression : ComputerAlgebra.Number(value.Number)) + ")" : match.Value;
        });
    }
    private Func<double, double, double, double, double, double, double> Compile(string text)
    {
        string expanded = Expand(text);
        return CompileExpanded(expanded);
    }
    private Func<double, double, double, double, double, double, double> CompileExpanded(string expanded)
    {
        if (!_compiled.TryGetValue(expanded, out var result)) _compiled[expanded] = result = ComputerAlgebra.Compile(ComputerAlgebra.Parse(expanded));
        return result;
    }
    private string ExpandBound(string text, params string[] variables)
    {
        return ExpandCore(text,variables.Select(variable => variable.Trim()).ToHashSet(StringComparer.Ordinal));
    }
    private Func<double, double, double, double, double, double, double> CompileBound(string text, params (string Variable, string Target)[] variables)
        => CompileExpanded(BindExpression(text, variables));

    private string BindExpression(string text, params (string Variable, string Target)[] variables)
    {
        text = ExpandBound(text, variables.Select(variable => variable.Variable).ToArray());
        var replacements = variables.ToDictionary(variable => variable.Variable, variable => variable.Target);
        text = Regex.Replace(text, @"\b[\p{L}_][\p{L}\d_]*\b", match => replacements.GetValueOrDefault(match.Value, match.Value));
        return text;
    }
    private void Sample()
    {
        if (++_samples > 200_000) throw new InvalidOperationException("作图采样达到上限，请缩小范围。");
        if ((_samples & 127) == 0) token.ThrowIfCancellationRequested();
    }
    internal bool Condition(string text)
    {
        if (values.TryGetValue(text, out var flag))
            return flag.Defined && (flag.Kind is GeometryKind.Boolean or GeometryKind.Scalar) && flag.Number != 0;
        var expression = ComputerAlgebra.Parse(Expand(text));
        if (expression.Vars.Count > 0) throw new FormatException("此处需要不含自由变量的逻辑条件。");
        try { return (bool)expression.EvalBoolean(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ComputerAlgebra.Real(expression) != 0; }
    }
    private GeometryValue Text(string[] args)
    {
        if (args.Length is not (1 or 2 or 3 or 4 or 6)) throw new FormatException("Text 参数数量无效。");
        var second = args.Length > 1 ? Evaluate(args[1]) : null;
        bool positioned = second?.Kind == GeometryKind.Point;
        if (args.Length >= 3 && !positioned) throw new FormatException("Text 需要定位点。");
        bool substitute = args.Length == 1 || (positioned
            ? args.Length == 2 || Condition(args[2])
            : Condition(args[1]));
        string body = !substitute && view.Objects.FirstOrDefault(item => item.Label == args[0]) is { } source
            ? source.Definition : TextContent(args[0]);
        return new() { Kind = GeometryKind.Text, Display = body, TextAnchor = positioned ? second.Origin : default(GeoVector) };
    }

    private string TextContent(string expression)
    {
        if (TryReadTextLiteral(expression, out string literal)) return literal;
        if (expression.StartsWith('(') && expression.EndsWith(')') && expression.Contains('"')
            && MathSyntax.Call("Text" + expression, out _, out var group) && group.Length == 1)
            return TextContent(group[0]);
        var terms = MathSyntax.Split(expression, '+');
        if (terms.Length > 1 && terms.Any(term => LikelyText(term)))
            return string.Concat(terms.Select(TextContent));
        return Evaluate(expression).Display;

        bool LikelyText(string term) => term.Contains('"')
            || values.TryGetValue(term, out var value) && value.Kind == GeometryKind.Text
            || MathSyntax.Call(term, out string command, out _) && command.Equals("Name", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadTextLiteral(string expression, out string text)
    {
        text = "";
        if (expression.Length < 2 || expression[0] != '"' || expression[^1] != '"'
            || !TextLiteralPattern.IsMatch(expression))
            return false;
        try { text = System.Text.Json.JsonSerializer.Deserialize<string>(expression); }
        catch (System.Text.Json.JsonException) { text = expression[1..^1]; }
        return true;
    }

    private static readonly Regex TextLiteralPattern = new(@"^""(?:\\.|[^""\\])*""$", RegexOptions.CultureInvariant);

    private GeometryValue Function(string expression, string variable = "x", double? minimum = null, double? maximum = null)
    {
        var function = CompileBound(expression, (variable,"x"));
        var paths = SampleCurve(t => new(t, function(t, 0, 0, 0, 0, 0)), minimum ?? view.XMin, maximum ?? view.XMax);
        return new() { Kind = GeometryKind.Function, Expression = expression, Variable = variable, Display = expression, Paths = paths };
    }
    private GeometryValue Implicit(string expression)
    {
        string expanded = Expand(expression);
        if (Regex.IsMatch(expanded, @"\bz\b")) return ImplicitSurface(expanded);
        // The established contour sampler handles discontinuities and cancellation.
        var definition = new PlotDefinition { Formula = expanded, XMin = view.XMin, XMax = view.XMax, YMin = view.YMin, YMax = view.YMax, IncludeAxes = false };
        var mesh = PlotSampler.Sample(CompiledPlot.Compile(definition), definition, token, interactive: _curveSamples < 512);
        var paths = mesh.Paths.Select(path => path.Select(point => new GeoVector(view.XMin + point.X / PlotSampler.Width * (view.XMax - view.XMin),
            view.YMax - point.Y / PlotSampler.Height * (view.YMax - view.YMin))).ToArray()).ToArray();
        return new() { Kind = GeometryKind.Curve, Display = expression, Expression = expression, Paths = paths };
    }
    private GeometryValue Curve(string[] args)
    {
        if (args.Length is not (5 or 6)) throw new FormatException("参数曲线需要坐标、参数和区间。");
        string variable = args[^3].Trim();
        var coordinates = args[..^3].Select(coordinate => BindExpression(coordinate, (variable, "t"))).ToArray();
        return ParametricCurve(coordinates, N(args[^2]), N(args[^1]));
    }

    private GeometryValue CurveDerivative((string[] Coordinates, double Minimum, double Maximum) curve, double order)
    {
        if (order < 0 || order > 20 || order != Math.Truncate(order))
            throw new FormatException("曲线导数阶数须为 0 到 20 的整数。");
        var coordinates = order == 0 ? curve.Coordinates : curve.Coordinates.Select(coordinate =>
        {
            var expression = ComputerAlgebra.Parse(coordinate);
            for (int index = 0; index < order; index++)
            {
                token.ThrowIfCancellationRequested();
                expression = expression.Differentiate("t").Simplify();
            }
            return expression.ToString();
        }).ToArray();
        return ParametricCurve(coordinates, curve.Minimum, curve.Maximum);
    }

    private GeometryValue ParametricCurve(string[] coordinates, double min, double max)
    {
        var components = coordinates.Select(CompileExpanded).ToArray();
        double Coordinate(int index, double t) => components[index](0, 0, 0, t, 0, 0);
        var paths = SampleCurve(t => new(Coordinate(0, t), Coordinate(1, t), components.Length == 3 ? Coordinate(2, t) : 0), min, max);
        return new() { Kind = GeometryKind.Curve, Display = "参数曲线", Paths = paths, ParametricCurve = (coordinates, min, max) };
    }
    private GeometryValue PolarCurve(string[] args)
    {
        var r = CompileBound(args[0], ("t","t")); double min = args.Length > 1 ? N(args[1]) : 0, max = args.Length > 2 ? N(args[2]) : Math.Tau;
        return new() { Kind = GeometryKind.Curve, Display = "极坐标曲线", Paths = SampleCurve(t => { double radius = r(0, 0, 0, t, 0, 0); return new(radius * Math.Cos(t), radius * Math.Sin(t)); }, min, max) };
    }
    private List<GeoVector[]> SampleCurve(Func<double, GeoVector> evaluate, double min, double max)
    {
        if (!double.IsFinite(min + max) || max <= min) throw new FormatException("参数区间无效。");
        var paths = new List<GeoVector[]>(); var current = new List<GeoVector>();
        double span = Math.Max(view.XMax - view.XMin, view.YMax - view.YMin);
        for (int index = 0; index <= _curveSamples; index++)
        {
            Sample(); var point = evaluate(min + (max - min) * index / _curveSamples);
            bool valid = point.Finite && Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))) < 1e5;
            if (!valid || current.Count > 0 && (point - current[^1]).Length > span * .4)
            { if (current.Count > 1) paths.Add(current.ToArray()); current.Clear(); }
            if (valid) current.Add(point);
        }
        if (current.Count > 1) paths.Add(current.ToArray());
        return paths;
    }
    private static GeometryValue Vector(GeoVector vector, GeoVector start = default) => new()
    {
        Kind = GeometryKind.Vector,
        Origin = vector,
        Direction = vector,
        Paths = new[] { new[] { start, start + vector } },
        Display = vector.ToString()
    };
    private static GeometryValue Line(GeoVector origin, GeoVector direction, GeometryKind kind = GeometryKind.Line)
    {
        if (direction.Length < 1e-10) throw new FormatException("两个点重合，无法确定直线。");
        var unit = direction.Unit();
        return new()
        {
            Kind = kind,
            Origin = origin,
            Direction = direction,
            Display = $"{origin} + t {direction}",
            Paths = new[] { kind == GeometryKind.Segment ? new[] { origin, origin + direction } : kind == GeometryKind.Ray ? new[] { origin, origin + unit * 1000 } : new[] { origin - unit * 1000, origin + unit * 1000 } }
        };
    }
    private static (GeoVector U, GeoVector V) CircleBasis(GeoVector normal)
        => GeometryPath.CircleBasis(normal);

    private GeometryValue LineEquation(string equation)
    {
        var expression = ComputerAlgebra.Parse(MathSyntax.EquationDifference(ExpandBound(equation, "x", "y")));
        Entity.Variable x = MathS.Var("x"), y = MathS.Var("y");
        double a = ComputerAlgebra.Real(expression.Differentiate(x).Simplify());
        double b = ComputerAlgebra.Real(expression.Differentiate(y).Simplify());
        double c = ComputerAlgebra.Real(expression.Substitute(x, 0).Substitute(y, 0));
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        if (scale == 0) throw new FormatException("直线方程需要非零的一次项。");
        a /= scale;
        b /= scale;
        c /= scale;
        var normal = new GeoVector(a, b);
        return Line(normal * (-c / normal.Dot(normal)), new(-b, a));
    }
    private static GeometryValue Circle(GeoVector center, double radius, GeoVector? normal = null)
    {
        if (!double.IsFinite(radius) || radius <= 1e-10) throw new FormatException("半径必须大于零。");
        var basis = CircleBasis(normal ?? new(0, 0, 1));
        var path = Enumerable.Range(0, 129).Select(i => center + basis.U * (radius * Math.Cos(i * Math.Tau / 128)) + basis.V * (radius * Math.Sin(i * Math.Tau / 128))).ToArray();
        return new() { Kind = GeometryKind.Circle, Origin = center, Radius = radius, Normal = normal ?? new(0, 0, 1), Paths = new[] { path }, Display = $"圆心 {center}，r = {radius:G6}" };
    }
    private static GeometryValue Polygon(GeoVector[] points)
    {
        if (points.Length < 3) throw new FormatException("多边形至少需要三个顶点。");
        var faces = Triangulate(points);
        return new()
        {
            Kind = GeometryKind.Polygon,
            Points = points,
            Paths = new[] { points.Append(points[0]).ToArray() },
            Faces = faces,
            FaceLoops = new[] { points },
            Display = points.Length + " 边形"
        };
    }
    private static GeometryValue Arc(GeoVector center, double radius, double start, double end, bool sector)
    {
        var path = Enumerable.Range(0, 97).Select(i => center + new GeoVector(Math.Cos(start + (end - start) * i / 96), Math.Sin(start + (end - start) * i / 96)) * radius).ToList();
        if (sector) { path.Add(center); path.Add(path[0]); }
        return new() { Kind = sector ? GeometryKind.Region : GeometryKind.Curve, Origin = center, Radius = radius,
            Number = sector ? Math.Abs(end - start) * radius * radius / 2 : 0,
            Paths = new[] { path.ToArray() }, Display = sector ? "扇形" : "圆弧" };
    }
    private static GeometryValue CircumArc(GeometryValue circle, GeoVector first, GeoVector middle, GeoVector last, bool sector)
    {
        var basis = CircleBasis(circle.Normal);
        double Parameter(GeoVector point) => Math.Atan2((point - circle.Origin).Dot(basis.V), (point - circle.Origin).Dot(basis.U));
        double start = Parameter(first), pass = Parameter(middle), end = Parameter(last);
        double Positive(double angle) => (angle % Math.Tau + Math.Tau) % Math.Tau;
        double sweep = Positive(end - start);
        if (Positive(pass - start) > sweep) sweep -= Math.Tau;
        var path = Enumerable.Range(0, 97).Select(i => circle.Origin
            + (basis.U * Math.Cos(start + sweep * i / 96) + basis.V * Math.Sin(start + sweep * i / 96)) * circle.Radius).ToList();
        if (sector) { path.Add(circle.Origin); path.Add(path[0]); }
        return new() { Kind = sector ? GeometryKind.Region : GeometryKind.Curve, Origin = circle.Origin, Normal = circle.Normal,
            Radius = circle.Radius, Number = sector ? Math.Abs(sweep) * circle.Radius * circle.Radius / 2 : 0,
            Paths = new[] { path.ToArray() }, Display = sector ? "外接扇形" : "外接圆弧" };
    }
    private static GeometryValue Circumcircle(GeoVector a, GeoVector b, GeoVector c)
    {
        var u = b - a; var v = c - a; var n = u.Cross(v);
        if (n.Dot(n) < 1e-15) throw new FormatException("三点共线，外接圆不存在。");
        var center = a + (v.Cross(n) * u.Dot(u) + n.Cross(u) * v.Dot(v)) / (2 * n.Dot(n));
        return Circle(center, (a - center).Length, n.Unit());
    }
    private static GeometryValue RegularPolygon(GeoVector a, GeoVector b, int count)
    {
        if (count is < 3 or > 256) throw new FormatException("正多边形边数范围为 3–256。");
        var mid = (a + b) / 2; var d = b - a; var center = mid + new GeoVector(-d.Y, d.X) * (.5 / Math.Tan(Math.PI / count));
        return Polygon(Enumerable.Range(0, count).Select(i => center + Rotate(a - center, new(0, 0, 1), Math.Tau * i / count)).ToArray());
    }
    private static GeoVector Rotate(GeoVector vector, GeoVector axis, double angle)
    { var n = axis.Unit(); return vector * Math.Cos(angle) + n.Cross(vector) * Math.Sin(angle) + n * (n.Dot(vector) * (1 - Math.Cos(angle))); }
    private static GeometryValue Transform(GeometryValue value, Func<GeoVector, GeoVector> transform)
    {
        if (value.Kind == GeometryKind.Vector && value.Paths.Count > 0)
        {
            var start = value.Paths[0][0];
            var mapped = transform(start);
            return Vector(transform(start + value.Direction) - mapped, mapped);
        }
        var origin = transform(value.Origin);
        double scale = (transform(value.Origin + new GeoVector(1, 0, 0)) - origin).Length;
        return value with
        {
            Kind = value.Kind == GeometryKind.Function ? GeometryKind.Curve : value.Kind,
            ConicEquation = value.ConicEquation?.Transform(transform),
            ParametricCurve = TransformCurve(value.ParametricCurve, transform),
            TextAnchor = value.TextAnchor is { } textAnchor ? transform(textAnchor) : null,
            Origin = origin,
            Direction = transform(value.Origin + value.Direction) - origin,
            Normal = (transform(value.Origin + value.Normal) - origin).Unit(),
            Number = value.Kind == GeometryKind.Solid ? value.Number * scale * scale * scale
                : value.Kind == GeometryKind.Conic ? value.Number * scale : value.Kind == GeometryKind.Region ? value.Number * scale * scale : value.Number,
            Radius = value.Radius > 0 ? (transform(value.Origin + new GeoVector(value.Radius, 0)) - origin).Length : 0,
            Points = value.Points.Select(transform).ToArray(),
            Paths = value.Paths.Select(path => path.Select(transform).ToArray()).ToArray(),
            FaceLoops = value.FaceLoops.Select(path => path.Select(transform).ToArray()).ToArray(),
            Faces = value.Faces.Select(face => new GeoTriangle(transform(face.A), transform(face.B), transform(face.C))).ToArray(),
            Display = "变换 · " + value.Display
        };
    }
    private static (string[], double, double)? TransformCurve(
        (string[] Coordinates, double Minimum, double Maximum)? curve, Func<GeoVector, GeoVector> transform)
    {
        if (curve is not { } source) return null;
        var origin = transform(default);
        var axes = new[] { transform(new(1, 0, 0)) - origin, transform(new(0, 1, 0)) - origin, transform(new(0, 0, 1)) - origin };
        string Component(Func<GeoVector, double> select)
        {
            var terms = source.Coordinates.Select((expression, index) => (expression, factor: select(axes[index])))
                .Where(term => term.factor != 0)
                .Select(term => term.factor == 1 ? $"({term.expression})"
                    : $"({ComputerAlgebra.Number(term.factor)})*({term.expression})");
            return string.Join('+', terms.Prepend(ComputerAlgebra.Number(select(origin))));
        }
        return ([Component(v => v.X), Component(v => v.Y), Component(v => v.Z)], source.Minimum, source.Maximum);
    }
    private static double Angle(GeoVector a, GeoVector b) => Math.Acos(Math.Clamp(a.Unit().Dot(b.Unit()), -1, 1));
    private static double Area(GeometryValue value) => value.Kind == GeometryKind.Circle ? Math.PI * value.Radius * value.Radius
        : value.Kind == GeometryKind.Conic && value.Expression == "ellipse" ? Math.PI * value.Radius * value.Number
        : value.Kind == GeometryKind.Region && value.Faces.Count == 0 ? Math.Abs(value.Number)
        : value.Faces.Sum(face => (face.B - face.A).Cross(face.C - face.A).Length / 2);
    private static double Distance(GeometryValue a, GeometryValue b)
    {
        if (b.Kind == GeometryKind.Point && a.Kind != GeometryKind.Point) return Distance(b, a);
        if (a.Kind == GeometryKind.Point && b.Kind == GeometryKind.Point) return (a.Origin - b.Origin).Length;
        if (a.Kind == GeometryKind.Point && b.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment)
        {
            double t = (a.Origin - b.Origin).Dot(b.Direction) / b.Direction.Dot(b.Direction);
            if (b.Kind == GeometryKind.Segment) t = Math.Clamp(t, 0, 1);
            if (b.Kind == GeometryKind.Ray) t = Math.Max(0, t);
            return (a.Origin - b.Origin - b.Direction * t).Length;
        }
        if (a.Kind == GeometryKind.Point && b.Kind == GeometryKind.Plane) return Math.Abs((a.Origin - b.Origin).Dot(b.Normal));
        throw new FormatException("距离支持点–点、点–直线和点–平面。");
    }
}
