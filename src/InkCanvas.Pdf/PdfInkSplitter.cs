using System.Numerics;
using Clipper2Lib;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;

namespace InkCanvas.Pdf;

/// <summary>Assigns continuous-document ink to editable page-local fragments.</summary>
public static class PdfInkSplitter
{
    public static IReadOnlyDictionary<int, IReadOnlyList<InkStroke>> Split(InkStroke stroke, int sourcePage, PdfContinuousLayout layout)
    {
        var original = new Dictionary<int, IReadOnlyList<InkStroke>> { [sourcePage] = new[] { stroke } };
        if (stroke.Count == 0) return original;
        var documentBounds = layout.ToDocumentBounds(sourcePage, stroke.Bounds);
        var candidates = layout.VisiblePages(documentBounds.Top, documentBounds.Bottom).ToArray();
        if (candidates.All(page => page == sourcePage)) return original;
        var documentStroke = stroke.Clone();
        documentStroke.Transform(layout.PageView(sourcePage, Matrix3x2.Identity), scaleWidth: false);
        var result = new Dictionary<int, IReadOnlyList<InkStroke>>();
        foreach (int page in candidates)
        {
            var clipped = Clip(documentStroke, layout[page]);
            if (clipped.Count == 0) continue;
            Matrix3x2.Invert(layout.PageView(page, Matrix3x2.Identity), out var inverse);
            foreach (var fragment in clipped) fragment.Transform(inverse, scaleWidth: false);
            result[page] = clipped;
        }
        // Keep existing margin annotations when they do not reach another page.
        return result.Keys.All(page => page == sourcePage) ? original : result;
    }

    public static Dictionary<int, IReadOnlyList<InkStroke>> Distribute(
        IReadOnlyDictionary<int, IReadOnlyList<InkStroke>> pages, PdfContinuousLayout layout)
    {
        var result = pages.Keys.ToDictionary(page => page, _ => new List<InkStroke>());
        foreach (var pair in pages)
        foreach (var stroke in pair.Value)
        foreach (var part in Split(stroke, pair.Key, layout))
        {
            if (!result.TryGetValue(part.Key, out var ink)) result[part.Key] = ink = new();
            ink.AddRange(part.Value);
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InkStroke>)pair.Value
            .OrderBy(stroke => stroke.LayerOrder ?? long.MaxValue).ToArray());
    }

    private static List<InkStroke> Clip(InkStroke stroke, Rect bounds)
    {
        var result = new List<InkStroke>();
        if (stroke.ShapeSource?.Geometry?.IsFill == true)
        {
            const double scale = 1000;
            var subject = new Path64(stroke.Points.Select(point => new Point64(point.X * scale, point.Y * scale)));
            var rectangle = new Path64 { new(bounds.Left * scale, bounds.Top * scale), new(bounds.Right * scale, bounds.Top * scale),
                new(bounds.Right * scale, bounds.Bottom * scale), new(bounds.Left * scale, bounds.Bottom * scale) };
            foreach (var path in Clipper.Intersect(new Paths64 { subject }, new Paths64 { rectangle }, FillRule.NonZero))
            {
                var points = path.Select(point => new InkPoint((float)(point.X / scale), (float)(point.Y / scale),
                    stroke.Points[0].Pressure, stroke.Points[0].Width, stroke.Points[0].TimeMs)).ToArray();
                if (points.Length >= 3) result.Add(Fragment(points));
            }
            return result;
        }
        var current = new List<InkPoint>();
        if (stroke.Count == 1 && bounds.Contains(stroke.Points[0].Position)) current.Add(stroke.Points[0]);
        for (int index = 1; index < stroke.Count; index++)
        {
            var a = stroke.Points[index - 1]; var b = stroke.Points[index];
            float enter = 0, exit = 1;
            var delta = b.Position - a.Position;
            if (!Boundary(-delta.X, a.X - bounds.Left, ref enter, ref exit)
                || !Boundary(delta.X, bounds.Right - a.X, ref enter, ref exit)
                || !Boundary(-delta.Y, a.Y - bounds.Top, ref enter, ref exit)
                || !Boundary(delta.Y, bounds.Bottom - a.Y, ref enter, ref exit))
            { Flush(); continue; }
            var first = Interpolate(a, b, enter); var last = Interpolate(a, b, exit);
            if (current.Count > 0 && Vector2.DistanceSquared(current[^1].Position, first.Position) > .0001f) Flush();
            if (current.Count == 0) current.Add(first);
            if (Vector2.DistanceSquared(current[^1].Position, last.Position) > .0001f) current.Add(last);
            if (exit < 1) Flush();
        }
        Flush();
        return result;

        void Flush()
        {
            if (current.Count == 0) return;
            result.Add(Fragment(current));
            current = new();
        }
        InkStroke Fragment(IReadOnlyList<InkPoint> points)
        {
            var fragment = new InkStroke(stroke.Attributes.Clone()) { LayerOrder = stroke.LayerOrder,
                IsRecognizedShape = stroke.IsRecognizedShape, ShapeSource = stroke.ShapeSource };
            fragment.RestorePoints(points); fragment.Complete();
            if (stroke.ShapeSource?.Geometry is { } geometry)
            {
                var extent = fragment.Bounds;
                fragment.ShapeSource = ShapeSource.FromGeometry(stroke.ShapeSource.GroupId, geometry with
                {
                    Construction = ShapeConstruction.Bounds, Parameters = new[] { extent.Left, extent.Top, extent.Right, extent.Bottom },
                    Vertices = points.SelectMany(point => new[] { point.X, point.Y }).ToArray(),
                    Transform = new float[] { 1, 0, 0, 1, 0, 0 }, StartNode = null, EndNode = null,
                });
            }
            return fragment;
        }
    }

    private static bool Boundary(float direction, float distance, ref float enter, ref float exit)
    {
        if (Math.Abs(direction) < 1e-8f) return distance >= 0;
        float value = distance / direction;
        if (direction < 0) enter = Math.Max(enter, value);
        else exit = Math.Min(exit, value);
        return enter <= exit;
    }

    private static InkPoint Interpolate(InkPoint a, InkPoint b, float t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Pressure + (b.Pressure - a.Pressure) * t,
        a.Width + (b.Width - a.Width) * t, a.TimeMs + (b.TimeMs - a.TimeMs) * t,
        a.HasPressure || b.HasPressure, a.TiltX + (b.TiltX - a.TiltX) * t, a.TiltY + (b.TiltY - a.TiltY) * t);
}
