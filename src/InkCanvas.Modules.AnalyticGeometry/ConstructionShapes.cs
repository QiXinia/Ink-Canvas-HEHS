using System.Numerics;
using InkCanvas.AnalyticGeometry.Construction;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;

namespace InkCanvas.Modules.AnalyticGeometry;

internal static class ConstructionShapes
{
    public const string Provider = "inkcanvas.geometry-construction";
    public static List<InkStroke> Create(ConstructionSnapshot state, ConstructionEvaluation evaluation, Matrix3x2 placement, Guid? group = null, bool preview = false)
    {
        var source = new ShapeSource(group ?? Guid.NewGuid(), Provider, ConstructionExport.Encode(state));
        var projection = new GeometryProjection(state, ConstructionExport.Width, ConstructionExport.Height);
        var strokes = new List<InkStroke>();
        foreach (var item in state.Objects.Where(item => item.Visible))
        {
            if (!evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            foreach (var path in projection.Paths(value)) Add(path, item.Color, item.LineWidth);
            foreach (var point in value.Points)
            {
                var p = projection.Project(point);
                if (p.X < 0 || p.X > ConstructionExport.Width || p.Y < 0 || p.Y > ConstructionExport.Height) continue;
                Add(new[] { p, p + new Vector2(.01f, 0) }, item.Color, Math.Max(6, item.LineWidth * 2));
            }
        }
        if (state.Axes)
        {
            var axes = state.ThreeDimensional ? new[] { new GeoVector(8, 0), new GeoVector(0, 8), new GeoVector(0, 0, 8) }
                : new[] { new GeoVector(state.XMax, 0), new GeoVector(0, state.YMax) };
            var origins = state.ThreeDimensional ? new[] { new GeoVector(), new GeoVector(), new GeoVector() }
                : new[] { new GeoVector(state.XMin, 0), new GeoVector(0, state.YMin) };
            for (int i = 0; i < axes.Length; i++)
            {
                var axis = new GeometryValue { Paths = new[] { new[] { origins[i], axes[i] } } };
                foreach (var path in projection.Paths(axis)) Add(path, 0xFF818B99, 1);
            }
        }
        if (strokes.Sum(stroke => stroke.Points.Count) > 200000) throw new FormatException("导出的顶点过多，请隐藏部分对象后插入。");
        return strokes;

        void Add(Vector2[] path, uint color, double width)
        {
            var style = new StrokeAttributes { BaseWidth = (float)width, TaperKind = TaperKind.None, Nib = PenNib.Round,
                Color = new InkColor(preview ? (byte)Math.Min(160,color >> 24) : (byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color) };
            var stroke = new InkStroke(style) { IsRecognizedShape = true, ShapeSource = source };
            stroke.RestorePoints(path.Select(point => new InkPoint(point.X, point.Y, .5f, style.BaseWidth, 0)));
            stroke.Complete();
            stroke.ObjectFrame = new SelectionFrame(new Rect(0, 0, (float)ConstructionExport.Width, (float)ConstructionExport.Height), Matrix3x2.Identity);
            stroke.Transform(placement, scaleWidth: false); strokes.Add(stroke);
        }
    }
}
