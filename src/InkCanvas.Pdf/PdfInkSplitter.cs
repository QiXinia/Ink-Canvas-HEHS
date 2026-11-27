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
        if (!TrySplit(stroke, sourcePage, layout, out var fragments))
            return new Dictionary<int, IReadOnlyList<InkStroke>> { [sourcePage] = new[] { stroke } };
        return fragments;
    }

    public static bool TrySplit(InkStroke stroke, int sourcePage, PdfContinuousLayout layout,
        out Dictionary<int, IReadOnlyList<InkStroke>> fragments)
    {
        fragments = null;
        if (stroke.Count == 0) return false;
        var documentBounds = layout.ToDocumentBounds(sourcePage, stroke.Bounds);
        var sourceBounds = layout[sourcePage];
        if (documentBounds.Left >= sourceBounds.Left && documentBounds.Right <= sourceBounds.Right
            && documentBounds.Top >= sourceBounds.Top && documentBounds.Bottom <= sourceBounds.Bottom) return false;
        InkStroke documentStroke = null;
        foreach (int page in layout.VisiblePages(documentBounds.Top, documentBounds.Bottom))
        {
            if (page == sourcePage || !layout[page].Intersects(documentBounds)) continue;
            documentStroke ??= ToDocument();
            var clipped = Clip(documentStroke, layout[page]);
            if (clipped.Count == 0) continue;
            ToPage(page, clipped);
            (fragments ??= new())[page] = clipped;
        }
        if (fragments is null) return false;
        var original = Clip(documentStroke, layout[sourcePage]);
        if (original.Count > 0)
        {
            ToPage(sourcePage, original);
            fragments[sourcePage] = original;
        }
        return true;

        InkStroke ToDocument()
        {
            var copy = stroke.Clone();
            copy.Transform(layout.PageView(sourcePage, Matrix3x2.Identity), scaleWidth: false);
            return copy;
        }

        void ToPage(int page, List<InkStroke> clipped)
        {
            Matrix3x2.Invert(layout.PageView(page, Matrix3x2.Identity), out var inverse);
            foreach (var fragment in clipped) fragment.Transform(inverse, scaleWidth: false);
        }
    }

    public static Dictionary<int, IReadOnlyList<InkStroke>> Distribute(
        IReadOnlyDictionary<int, IReadOnlyList<InkStroke>> pages, PdfContinuousLayout layout, CancellationToken token = default)
    {
        var result = pages.Keys.ToDictionary(page => page, _ => new List<InkStroke>());
        foreach (var pair in pages)
        foreach (var stroke in pair.Value)
        {
            token.ThrowIfCancellationRequested();
            if (!TrySplit(stroke, pair.Key, layout, out var parts))
            {
                result[pair.Key].Add(stroke);
                continue;
            }
            foreach (var part in parts)
            {
                if (!result.TryGetValue(part.Key, out var ink)) result[part.Key] = ink = new();
                ink.AddRange(part.Value);
            }
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InkStroke>)pair.Value
            .OrderBy(stroke => stroke.InkLayerIndex).ThenBy(stroke => stroke.LayerOrder ?? long.MaxValue).ToArray());
    }

    internal static List<InkStroke> Clip(InkStroke stroke, Rect bounds, bool freezeShape = false)
    {
        var result = new List<InkStroke>();
        if (stroke.ShapeSource?.Geometry is { } shape)
            freezeShape |= shape.Kind is ShapeKind.Polygon or ShapeKind.Sector or ShapeKind.ConeFrustum
                || ShapeGenerator.IsPolygonalFrustum(shape.Kind) || ShapeGenerator.IsRoundedRectangle(shape.Kind)
                || shape.FillContourLengths is { Length: > 1 };
        if (freezeShape && stroke.ShapeSource?.Geometry is { Construction: ShapeConstruction.Connector,
            LineStyle: ShapeLineStyle.Dashed or ShapeLineStyle.Dotted })
        {
            foreach (var contour in ShapeDefinition.RenderContours(stroke))
            {
                contour.InkLayerIndex = stroke.InkLayerIndex;
                contour.LayerOrder = stroke.LayerOrder;
                result.AddRange(Clip(contour, bounds, freezeShape: true));
            }
            return result;
        }
        if (stroke.ShapeSource?.Geometry?.IsFill == true)
        {
            const double scale = 1000;
            var subject = FilledInkGeometry.ToPaths(stroke, scale);
            var rectangle = new Path64 { new(bounds.Left * scale, bounds.Top * scale), new(bounds.Right * scale, bounds.Top * scale),
                new(bounds.Right * scale, bounds.Bottom * scale), new(bounds.Left * scale, bounds.Bottom * scale) };
            var clipped = Clipper.Intersect(subject, new Paths64 { rectangle }, FillRule.NonZero);
            if (clipped.Count == 0) return result;
            if (!freezeShape && stroke.ShapeSource.Geometry.IsEditable)
            {
                var sample = stroke.Points[0];
                foreach (var path in clipped)
                    result.Add(Fragment(path.Select(point => sample with { X = (float)(point.X / scale), Y = (float)(point.Y / scale) }).ToArray()));
            }
            else result.Add(FilledInkGeometry.FromPaths(stroke, clipped, scale));
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
            var fragment = new InkStroke(stroke.Attributes.Clone()) { InkLayerIndex = stroke.InkLayerIndex, LayerOrder = stroke.LayerOrder,
                IsRecognizedShape = stroke.IsRecognizedShape, ShapeSource = stroke.ShapeSource };
            fragment.RestorePoints(points); fragment.Complete();
            if (stroke.ShapeSource?.Geometry is { } geometry)
            {
                var extent = fragment.Bounds;
                fragment.ShapeSource = ShapeSource.FromGeometry(stroke.ShapeSource.GroupId, freezeShape ? geometry.AsContours() : geometry with
                {
                    Construction = geometry.IsEditable ? ShapeConstruction.Bounds : geometry.Construction,
                    Parameters = new[] { extent.Left, extent.Top, extent.Right, extent.Bottom },
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
