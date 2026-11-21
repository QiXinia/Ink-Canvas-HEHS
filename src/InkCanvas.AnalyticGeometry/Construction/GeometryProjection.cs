using System.Numerics;

namespace InkCanvas.AnalyticGeometry.Construction;

/// <summary>One projection shared by the native view, vector exports and canvas insertion.</summary>
public sealed class GeometryProjection(ConstructionSnapshot view, double width, double height)
{
    private readonly double _scale = Math.Min(width / (view.XMax - view.XMin), height / (view.YMax - view.YMin));
    public Vector2 Project(GeoVector point)
    {
        double x = point.X, y = point.Y;
        if (view.ThreeDimensional)
        {
            double horizontal = point.X * Math.Cos(view.Yaw) - point.Y * Math.Sin(view.Yaw);
            double depth = point.X * Math.Sin(view.Yaw) + point.Y * Math.Cos(view.Yaw);
            x = horizontal;
            y = point.Z * Math.Cos(view.Pitch) - depth * Math.Sin(view.Pitch);
            if (view.Oblique) { x = point.X + point.Y * .3535533906; y = point.Z + point.Y * .3535533906; }
            if (view.Perspective)
            {
                double distance = (view.XMax - view.XMin) * 2;
                double scale = distance / Math.Max(distance * .15, distance - Depth(point));
                x *= scale;
                y *= scale;
            }
        }
        return new((float)(width / 2 + (x - (view.XMin + view.XMax) / 2) * _scale),
            (float)(height / 2 - (y - (view.YMin + view.YMax) / 2) * _scale));
    }

    public double Depth(GeoVector point) => point.Z * Math.Sin(view.Pitch)
        + (point.X * Math.Sin(view.Yaw) + point.Y * Math.Cos(view.Yaw)) * Math.Cos(view.Pitch);

    public GeoVector Unproject(Vector2 point, double z = 0)
    {
        double x = (view.XMin + view.XMax) / 2 + (point.X - width / 2) / _scale;
        double y = (view.YMin + view.YMax) / 2 - (point.Y - height / 2) / _scale;
        if (!view.ThreeDimensional) return new(x, y, z);
        if (view.Oblique)
        {
            double planeY = (y - z) / .3535533906;
            return new(x - planeY * .3535533906, planeY, z);
        }
        double depth;
        double horizontal;
        if (view.Perspective)
        {
            double distance = (view.XMax - view.XMin) * 2;
            double divisor = distance * Math.Sin(view.Pitch) - y * Math.Cos(view.Pitch);
            if (Math.Abs(divisor) < 1e-8) throw new InvalidOperationException("当前视角平行于绘图平面，请旋转视图后操作。");
            depth = (distance * z * Math.Cos(view.Pitch) - y * (distance - z * Math.Sin(view.Pitch))) / divisor;
            horizontal = x * (distance - z * Math.Sin(view.Pitch) - depth * Math.Cos(view.Pitch)) / distance;
        }
        else
        {
            if (Math.Abs(Math.Sin(view.Pitch)) < 1e-8) throw new InvalidOperationException("当前视角平行于绘图平面，请旋转视图后操作。");
            depth = (z * Math.Cos(view.Pitch) - y) / Math.Sin(view.Pitch);
            horizontal = x;
        }
        return new(horizontal * Math.Cos(view.Yaw) + depth * Math.Sin(view.Yaw),
            -horizontal * Math.Sin(view.Yaw) + depth * Math.Cos(view.Yaw), z);
    }

    public IEnumerable<Vector2[]> Paths(GeometryValue value)
    {
        IEnumerable<GeoVector[]> paths = value.Paths;
        if (value.Kind is GeometryKind.Line or GeometryKind.Ray)
        {
            double reach = 4 * (Math.Abs(view.XMin) + Math.Abs(view.XMax) + Math.Abs(view.YMin) + Math.Abs(view.YMax) + value.Origin.Length);
            var direction = value.Direction.Unit() * reach;
            paths = new[] { new[] { value.Kind == GeometryKind.Line ? value.Origin - direction : value.Origin, value.Origin + direction } };
        }
        foreach (var path in paths)
        {
            var current = new List<Vector2>();
            for (int i = 1; i < path.Length; i++)
            {
                var a = Project(path[i - 1]);
                var b = Project(path[i]);
                if (Clip(ref a, ref b, (float)width, (float)height))
                {
                    if (current.Count > 0 && Vector2.DistanceSquared(current[^1], a) > .01f)
                    {
                        if (current.Count > 1) yield return current.ToArray();
                        current.Clear();
                    }
                    if (current.Count == 0) current.Add(a);
                    current.Add(b);
                }
                else if (current.Count > 0)
                {
                    if (current.Count > 1) yield return current.ToArray();
                    current.Clear();
                }
            }
            if (current.Count > 1) yield return current.ToArray();
        }
    }

    private static bool Clip(ref Vector2 a, ref Vector2 b, float width, float height)
    {
        if (!float.IsFinite(a.X + a.Y + b.X + b.Y)) return false;
        var start = a;
        var delta = b - a;
        float low = 0, high = 1;
        bool Edge(float p, float q)
        {
            if (Math.Abs(p) < 1e-8) return q >= 0;
            float t = q / p;
            if (p < 0) low = Math.Max(low, t);
            else high = Math.Min(high, t);
            return low <= high;
        }
        if (!Edge(-delta.X, a.X) || !Edge(delta.X, width - a.X)
            || !Edge(-delta.Y, a.Y) || !Edge(delta.Y, height - a.Y)) return false;
        a = start + delta * low;
        b = start + delta * high;
        return true;
    }
}
