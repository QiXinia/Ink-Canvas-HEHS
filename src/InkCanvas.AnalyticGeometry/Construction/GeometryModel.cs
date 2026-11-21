using System.Text.Json.Serialization;

namespace InkCanvas.AnalyticGeometry.Construction;

public readonly record struct GeoVector(double X, double Y, double Z = 0)
{
    public static GeoVector operator +(GeoVector a, GeoVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static GeoVector operator -(GeoVector a, GeoVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static GeoVector operator *(GeoVector a, double factor) => new(a.X * factor, a.Y * factor, a.Z * factor);
    public static GeoVector operator /(GeoVector a, double factor) => a * (1 / factor);
    public double Dot(GeoVector other) => X * other.X + Y * other.Y + Z * other.Z;
    public GeoVector Cross(GeoVector other) => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
    [JsonIgnore] public double Length => Math.Sqrt(Dot(this));
    [JsonIgnore] public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public GeoVector Unit() => Length > 1e-10 ? this / Length : throw new InvalidOperationException("方向向量不能为零。");
    public override string ToString() => $"({X:G6}, {Y:G6}" + (Z == 0 ? ")" : $", {Z:G6})");
}

public enum GeometryKind { Scalar, Point, Vector, Line, Ray, Segment, Polygon, Circle, Conic, Function, Curve, Plane, Surface, Solid, Region, Boolean, List, Text, Complex }
public sealed record GeoTriangle(GeoVector A, GeoVector B, GeoVector C);
public sealed record GeometryValue
{
    public GeometryKind Kind { get; init; }
    public string Display { get; init; } = "";
    public string Expression { get; init; } = "";
    public string Variable { get; init; } = "x";
    public double Number { get; init; }
    public GeoVector Origin { get; init; }
    public GeoVector Direction { get; init; }
    public GeoVector Normal { get; init; } = new(0, 0, 1);
    public double Radius { get; init; }
    public IReadOnlyList<GeoVector> Points { get; init; } = Array.Empty<GeoVector>();
    public IReadOnlyList<GeoVector[]> Paths { get; init; } = Array.Empty<GeoVector[]>();
    public IReadOnlyList<GeoTriangle> Faces { get; init; } = Array.Empty<GeoTriangle>();
    public IReadOnlyList<GeoVector[]> FaceLoops { get; init; } = Array.Empty<GeoVector[]>();
    public IReadOnlyList<GeometryValue> Items { get; init; } = Array.Empty<GeometryValue>();
    public string Error { get; init; }
    public string ControlKind { get; init; } = "";
    public string ControlTarget { get; init; } = "";
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    [JsonIgnore] public bool Defined => Error is null;
    public static GeometryValue Scalar(double value) => new() { Kind = GeometryKind.Scalar, Number = value, Display = value.ToString("G8") };
    public static GeometryValue Point(GeoVector point) => point.Finite
        ? new() { Kind = GeometryKind.Point, Origin = point, Points = new[] { point }, Display = point.ToString() }
        : throw new FormatException("点坐标必须为有限实数。");
}

public sealed record ConstructionObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Label { get; init; }
    public string Definition { get; init; }
    public bool Visible { get; init; } = true;
    public bool Fixed { get; init; }
    public bool Trace { get; init; }
    public bool ShowLabel { get; init; } = true;
    public uint Color { get; init; } = 0xFF2269DE;
    public double LineWidth { get; init; } = 2;
    public double Opacity { get; init; } = .2;
    public double SliderMin { get; init; } = -10;
    public double SliderMax { get; init; } = 10;
    public double SliderStep { get; init; } = .1;
    public bool Animate { get; init; }
    public string OnUpdate { get; init; } = "";
    public string OnClick { get; init; } = "";
}

public sealed record ConstructionSnapshot
{
    public int FormatVersion { get; init; } = 2;
    public List<ConstructionObject> Objects { get; init; } = new();
    public double XMin { get; init; } = -10;
    public double XMax { get; init; } = 10;
    public double YMin { get; init; } = -7.5;
    public double YMax { get; init; } = 7.5;
    public double Yaw { get; init; } = -.6;
    public double Pitch { get; init; } = .65;
    public bool ThreeDimensional { get; init; }
    public bool Perspective { get; init; }
    public bool Oblique { get; init; }
    public bool PolarGrid { get; init; }
    public bool Axes { get; init; } = true;
    public bool Grid { get; init; } = true;
    public List<ConstructionTool> Tools { get; init; } = new();
}

public sealed record ConstructionTool(string Name, string[] Inputs, string Definition);

public sealed record ConstructionEvaluation(IReadOnlyDictionary<string, GeometryValue> Values, int EvaluatedObjects, TimeSpan Elapsed);
