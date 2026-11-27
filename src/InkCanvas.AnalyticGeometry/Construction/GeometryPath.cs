using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

public static class GeometryPath
{
    public static bool IsAttachableRegion(GeometryValue value)
        => value.Kind is GeometryKind.Circle or GeometryKind.Plane or GeometryKind.Polygon
            || value.Kind == GeometryKind.Conic && value.Expression == "ellipse";

    public static bool TryGetParameter(GeometryValue path, GeoVector point, out double parameter)
    {
        parameter = 0;
        if (!path.Defined || !point.Finite) return false;
        if (path.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment)
        {
            double length = path.Direction.Dot(path.Direction);
            if (length < 1e-12) return false;
            parameter = (point - path.Origin).Dot(path.Direction) / length;
            parameter = path.Kind == GeometryKind.Segment ? Math.Clamp(parameter, 0, 1)
                : path.Kind == GeometryKind.Ray ? Math.Max(0, parameter) : parameter;
            return true;
        }
        if (path.Kind == GeometryKind.Circle)
        {
            var delta = point - path.Origin;
            var (u, v) = CircleBasis(path.Normal);
            parameter = Math.Atan2(delta.Dot(v), delta.Dot(u));
            return true;
        }
        if (path.Kind == GeometryKind.Function) { parameter = point.X; return true; }
        if (path.Kind is GeometryKind.Curve or GeometryKind.Conic or GeometryKind.Polygon or GeometryKind.Region)
        {
            double traversed = 0, closest = double.PositiveInfinity, result = 0;
            foreach (var points in path.Paths)
            for (int index = 1; index < points.Length; index++)
            {
                var start = points[index - 1]; var end = points[index]; var delta = end - start;
                double length = delta.Length;
                if (length <= 1e-12) continue;
                double fraction = Math.Clamp((point - start).Dot(delta) / (length * length), 0, 1);
                var offset = point - (start + delta * fraction);
                double distance = offset.Dot(offset);
                if (distance < closest) { closest = distance; result = traversed + length * fraction; }
                traversed += length;
            }
            if (traversed > 1e-12) { parameter = Math.Clamp(result / traversed, 0, 1); return true; }
        }
        return false;
    }

    public static bool TryGetRegionDefinition(string target, GeometryValue region, GeoVector point, out string definition)
    {
        definition = null;
        if (!TryRegionCoordinates(region, point, out int face, out double u, out double v)) return false;
        definition = RegionDefinition(target, face, u, v);
        return true;
    }

    internal static string RegionDefinition(string target, int face, double u, double v)
        => $"PointIn({target},{face},{ComputerAlgebra.Number(u)},{ComputerAlgebra.Number(v)})";

    internal static bool TryRegionCoordinates(GeometryValue region, GeoVector point, out int face, out double u, out double v, bool project = true)
    {
        face = 0;
        u = v = 0;
        if (!region.Defined || !point.Finite) return false;
        bool ellipse = region.Kind == GeometryKind.Conic && region.Expression == "ellipse";
        if (ellipse || region.Kind is GeometryKind.Circle or GeometryKind.Plane)
        {
            var offset = point - region.Origin;
            var basis = ellipse ? (U: region.Direction, V: region.Normal) : CircleBasis(region.Normal);
            u = offset.Dot(basis.U);
            v = offset.Dot(basis.V);
            if (!project && (offset - basis.U * u - basis.V * v).Length > 1e-8 * Math.Max(1, offset.Length)) return false;
            if (region.Kind == GeometryKind.Plane) return true;
            u /= region.Radius;
            v /= ellipse ? region.Number : region.Radius;
            double length = Math.Sqrt(u * u + v * v);
            if (!project) return length <= 1 + 1e-8;
            double scale = Math.Max(1, length);
            u /= scale;
            v /= scale;
            return true;
        }
        if (region.Kind != GeometryKind.Polygon) return false;
        double distance = double.PositiveInfinity;
        Span<(double U, double V)> edgeCoordinates = stackalloc (double, double)[3];
        for (int index = 0; index < region.Faces.Count; index++)
        {
            var triangle = region.Faces[index];
            var ab = triangle.B - triangle.A;
            var ac = triangle.C - triangle.A;
            var delta = point - triangle.A;
            double abSquared = ab.Dot(ab), acSquared = ac.Dot(ac), abAc = ab.Dot(ac);
            double denominator = abSquared * acSquared - abAc * abAc;
            if (denominator <= 0) continue;
            double alongAb = ab.Dot(delta), alongAc = ac.Dot(delta);
            double candidateU = (acSquared * alongAb - abAc * alongAc) / denominator;
            double candidateV = (abSquared * alongAc - abAc * alongAb) / denominator;
            double tolerance = project ? 0 : 1e-8;
            if (candidateU >= -tolerance && candidateV >= -tolerance && candidateU + candidateV <= 1 + tolerance)
            {
                var projected = triangle.A + ab * candidateU + ac * candidateV;
                double separation = (point - projected).Length;
                if (separation < distance) { distance = separation; (face, u, v) = (index, candidateU, candidateV); }
                if (distance < 1e-10 || !project && separation <= 1e-8 * Math.Max(1, Math.Max(ab.Length, ac.Length))) return true;
                continue;
            }
            if (!project) continue;
            double alongOppositeEdge = Along(ac - ab, point - triangle.B);
            edgeCoordinates[0] = (Along(ab, delta), 0);
            edgeCoordinates[1] = (0, Along(ac, delta));
            edgeCoordinates[2] = (1 - alongOppositeEdge, alongOppositeEdge);
            foreach (var candidate in edgeCoordinates)
            {
                var projected = triangle.A + ab * candidate.U + ac * candidate.V;
                double separation = (point - projected).Length;
                if (separation < distance) { distance = separation; (face, u, v) = (index, candidate.U, candidate.V); }
            }
        }
        return project && double.IsFinite(distance);
    }

    internal static GeoVector RegionPoint(GeometryValue region, int face, double u, double v)
    {
        if (region.Kind == GeometryKind.Conic && region.Expression == "ellipse")
            return region.Origin + (region.Direction * (region.Radius * u) + region.Normal * (region.Number * v))
                / Math.Max(1, Math.Sqrt(u * u + v * v));
        if (region.Kind is GeometryKind.Circle or GeometryKind.Plane)
        {
            var (first, second) = CircleBasis(region.Normal);
            if (region.Kind == GeometryKind.Plane) return region.Origin + first * u + second * v;
            return region.Origin + (first * u + second * v) * (region.Radius / Math.Max(1, Math.Sqrt(u * u + v * v)));
        }
        if (region.Kind != GeometryKind.Polygon || face < 0 || face >= region.Faces.Count)
            throw new FormatException("区域中没有可附着的面。");
        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1 - u);
        var triangle = region.Faces[face];
        return triangle.A + (triangle.B - triangle.A) * u + (triangle.C - triangle.A) * v;
    }

    internal static (GeoVector U, GeoVector V) CircleBasis(GeoVector normal)
    {
        var n = normal.Unit();
        var u = Math.Abs(n.Z) > .99 ? (new GeoVector(1, 0, 0) - n * n.X).Unit() : new GeoVector(0, 0, 1).Cross(n).Unit();
        return (u, n.Cross(u));
    }

    private static double Along(GeoVector direction, GeoVector offset)
        => Math.Clamp(direction.Dot(offset) / direction.Dot(direction), 0, 1);
}
