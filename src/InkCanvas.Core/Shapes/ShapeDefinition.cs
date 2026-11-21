using System.Numerics;
using System.Text.Json.Serialization;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Shapes;

public enum ShapeConstruction { Bounds, Solid, Hyperbola, Connector }
public enum ShapeLineStyle { Original, Solid, Dashed, Dotted }

/// <summary>Editable construction data. Arrays are immutable snapshots in local coordinates.</summary>
public sealed record ShapeDefinition
{
    public const string Provider = "inkcanvas.shape";
    public ShapeKind Kind { get; init; }
    public ShapeConstruction Construction { get; init; }
    public float[] Parameters { get; init; } = Array.Empty<float>();
    public float[] Vertices { get; init; } = Array.Empty<float>();
    public float[] Transform { get; init; } = new float[] { 1, 0, 0, 1, 0, 0 };
    public ShapeLineStyle LineStyle { get; init; }
    public uint? FillColor { get; init; }
    public bool IsFill { get; init; }
    public Guid? StartNode { get; init; }
    public Guid? EndNode { get; init; }

    [JsonIgnore]
    public bool IsValid => Enum.IsDefined(Kind) && Enum.IsDefined(Construction) && Enum.IsDefined(LineStyle)
        && Parameters is { Length: >= 4 } && Parameters.All(float.IsFinite)
        && (Construction is not (ShapeConstruction.Solid or ShapeConstruction.Hyperbola) || Parameters.Length >= 6)
        && Vertices is not null && Vertices.Length % 2 == 0 && Vertices.All(float.IsFinite)
        && Transform is { Length: 6 } && Transform.All(float.IsFinite) && MathF.Abs(Matrix.GetDeterminant()) > 1e-8f;
    [JsonIgnore]
    public Matrix3x2 Matrix => new(Transform[0], Transform[1], Transform[2], Transform[3], Transform[4], Transform[5]);
    [JsonIgnore]
    public bool CanFill => Vertices.Length >= 6 || Kind is ShapeKind.Circle or ShapeKind.CircleFromCenter
        or ShapeKind.DashedCircle or ShapeKind.DashedCircleFromCenter or ShapeKind.Ellipse or ShapeKind.EllipseFromCenter
        or ShapeKind.EllipseWithFoci or ShapeKind.EllipseWithFociFromCenter || Construction == ShapeConstruction.Solid
        || Kind is ShapeKind.Cylinder or ShapeKind.Cone or ShapeKind.Sphere or ShapeKind.Hemisphere or ShapeKind.ConeFrustum or ShapeKind.Tetrahedron;

    public static float[] Write(Matrix3x2 m) => new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };
    public ShapeDefinition Transformed(Matrix3x2 m) => this with { Transform = Write(Matrix * m) };

    public Vector2[] ControlPoints()
    {
        var points = Vertices.Length >= 6 ? Pairs(Vertices).ToArray()
            : Construction == ShapeConstruction.Solid ? new[] { Point(0), Point(2), Point(0) + Point(4) }
            : Construction == ShapeConstruction.Hyperbola ? new[] { Point(0), Point(2), Point(4) }
            : new[] { Point(0), Point(2) };
        return points.Select(point => Vector2.Transform(point, Matrix)).ToArray();
    }

    public ShapeDefinition MoveControlPoint(int index, Vector2 world)
    {
        if (!Matrix3x2.Invert(Matrix, out var inverse) || index < 0 || index >= ControlPoints().Length) return this;
        var local = Vector2.Transform(world, inverse);
        var values = (Vertices.Length >= 6 ? Vertices : Parameters).ToArray();
        if (Vertices.Length < 6 && Construction == ShapeConstruction.Solid && index == 2) local -= Point(0);
        values[index * 2] = local.X; values[index * 2 + 1] = local.Y;
        return Vertices.Length >= 6 ? this with { Vertices = values } : this with { Parameters = values };
    }

    private Vector2 Point(int offset) => new(Parameters[offset], Parameters[offset + 1]);
    private static IEnumerable<Vector2> Pairs(float[] values)
    {
        for (int i = 0; i < values.Length; i += 2) yield return new(values[i], values[i + 1]);
    }

    public static List<InkStroke> Attach(List<InkStroke> strokes, ShapeDefinition definition, Guid? groupId = null)
    {
        var id = groupId ?? Guid.NewGuid();
        foreach (var stroke in strokes) stroke.ShapeSource = ShapeSource.FromGeometry(id, definition);
        return strokes;
    }

    public static ShapeDefinition FromBounds(ShapeKind kind, Vector2 start, Vector2 end, IReadOnlyList<InkStroke> contours)
    {
        bool polygon = kind is ShapeKind.Rectangle or ShapeKind.RectangleFromCenter or ShapeKind.Square or ShapeKind.Triangle
            or ShapeKind.RightTriangle or ShapeKind.Diamond or ShapeKind.Parallelogram or ShapeKind.Trapezoid;
        return new()
        {
            Kind = kind, Parameters = new[] { start.X, start.Y, end.X, end.Y },
            Vertices = polygon && contours.Count > 0 ? contours[0].Points.SkipLast(1).SelectMany(p => new[] { p.X, p.Y }).ToArray() : Array.Empty<float>(),
        };
    }

    public List<InkStroke> Generate(StrokeAttributes attributes, Guid groupId)
    {
        if (!IsValid) return new();
        var a = attributes.Clone(); a.TaperKind = TaperKind.None;
        a.GeneratorSolidLines = LineStyle != ShapeLineStyle.Original;
        List<InkStroke> contours;
        if (Vertices.Length >= 6) contours = new() { Stroke(a, Pairs(Vertices).Append(new(Vertices[0], Vertices[1]))) };
        else if (Construction == ShapeConstruction.Solid) contours = ShapeGenerator.GenerateSolidContours(Kind, Point(0), Point(2), Point(4), a);
        else if (Construction == ShapeConstruction.Hyperbola)
        {
            contours = ShapeGenerator.HasHyperbolaAsymptotes(Kind) ? ShapeGenerator.GenerateContours(Kind, Point(0), Point(2), a) : new();
            float dx = Parameters[2] - Parameters[0];
            float slope = MathF.Abs(dx) < .001f ? 0 : MathF.Abs((Parameters[3] - Parameters[1]) / dx);
            contours.AddRange(ShapeGenerator.GenerateHyperbolaBranches(Point(0), slope, Point(4), ShapeGenerator.HasHyperbolaFoci(Kind), a));
        }
        else contours = ShapeGenerator.GenerateContours(Kind, Point(0), Point(2), a);
        List<Vector2> fill = null;
        if (FillColor.HasValue && CanFill)
        {
            var closed = contours.FirstOrDefault(s => s.Count > 3 && Vector2.DistanceSquared(s.Points[0].Position, s.Points[^1].Position) < .01f);
            fill = closed is not null && Construction != ShapeConstruction.Solid ? closed.Points.Select(p => p.Position).ToList()
                : Hull(contours.SelectMany(s => s.Points).Select(p => p.Position));
        }
        if (Construction != ShapeConstruction.Connector && LineStyle is ShapeLineStyle.Dashed or ShapeLineStyle.Dotted)
            contours = Pattern(contours, a, LineStyle, MathF.Sqrt(MathF.Abs(Matrix.GetDeterminant())));
        foreach (var stroke in contours) stroke.Transform(Matrix, scaleWidth: false);
        Attach(contours, this with { IsFill = false }, groupId);
        if (fill is { Count: >= 3 })
        {
            var fillAttributes = a.Clone(); fillAttributes.Color = InkColor.FromPackedArgb(FillColor.Value); fillAttributes.IsHighlighter = false;
            var stroke = Stroke(fillAttributes, fill); stroke.Transform(Matrix, scaleWidth: false);
            stroke.ShapeSource = ShapeSource.FromGeometry(groupId, this with { IsFill = true });
            contours.Insert(0, stroke);
        }
        return contours;
    }

    private static InkStroke Stroke(StrokeAttributes attributes, IEnumerable<Vector2> points)
    {
        var stroke = ShapeGenerator.Stroke(attributes, points.ToArray());
        stroke.IsRecognizedShape = true;
        stroke.Complete(); return stroke;
    }

    public static IReadOnlyList<InkStroke> RenderContours(InkStroke stroke) =>
        stroke.ShapeSource?.Geometry is { Construction: ShapeConstruction.Connector, LineStyle: ShapeLineStyle.Dashed or ShapeLineStyle.Dotted } data
            ? (data with { Construction = ShapeConstruction.Bounds }).Generate(stroke.Attributes, stroke.ShapeSource.GroupId)
            : new[] { stroke };

    private static List<InkStroke> Pattern(List<InkStroke> paths, StrokeAttributes attributes, ShapeLineStyle style, float scale)
    {
        var result = new List<InkStroke>();
        float dash = (style == ShapeLineStyle.Dotted ? MathF.Max(.5f, attributes.BaseWidth * .1f) : MathF.Max(5, attributes.BaseWidth * 4)) / scale;
        float period = dash + MathF.Max(4, attributes.BaseWidth * 2) / scale;
        foreach (var path in paths)
        {
            if (path.Count < 2) { result.Add(path); continue; }
            float phase = 0;
            for (int i = 1; i < path.Count; i++)
            {
                var from = path.Points[i - 1].Position; var to = path.Points[i].Position;
                float length = Vector2.Distance(from, to), offset = 0;
                while (offset < length)
                {
                    bool visible = phase < dash;
                    float step = MathF.Min(length - offset, (visible ? dash : period) - phase);
                    if (step <= .0001f) { phase = visible ? dash : 0; continue; }
                    if (visible) result.Add(Stroke(attributes, new[] { Vector2.Lerp(from, to, offset / length), Vector2.Lerp(from, to, (offset + step) / length) }));
                    offset += step; phase += step;
                    if (phase >= period - .0001f) phase = 0;
                }
            }
        }
        return result;
    }

    private static List<Vector2> Hull(IEnumerable<Vector2> input)
    {
        var points = input.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        var hull = new List<Vector2>();
        foreach (var p in points) Add(p, 0);
        int lower = hull.Count;
        for (int i = points.Length - 2; i >= 0; i--) Add(points[i], lower - 1);
        if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
        return hull;
        void Add(Vector2 p, int minimum)
        {
            while (hull.Count > minimum + 1)
            {
                var a = hull[^1] - hull[^2]; var b = p - hull[^1];
                if (a.X * b.Y - a.Y * b.X > 0) break;
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(p);
        }
    }
}
