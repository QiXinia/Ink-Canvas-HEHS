using System.Numerics;
using System.Text.Json;
using System.Text.Encodings.Web;
using InkCanvas.AnalyticGeometry;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;

namespace InkCanvas.Modules.AnalyticGeometry;

internal static class PlotShapes
{
    public const string Provider = "inkcanvas.analytic-geometry";
    public static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static List<InkStroke> Create(PlotMesh mesh, PlotDefinition definition, StrokeAttributes attributes, Matrix3x2 placement, Guid? group = null, bool preview = false)
    {
        var result = new List<InkStroke>();
        var source = new ShapeSource(group ?? Guid.NewGuid(), Provider, JsonSerializer.Serialize(definition, JsonOptions));
        foreach (var path in mesh.Paths) Add(path, false);
        if (definition.IncludeAxes)
        {
            if (definition.XMin <= 0 && definition.XMax >= 0)
            {
                double x = -definition.XMin * PlotSampler.Width / (definition.XMax - definition.XMin);
                Add(new[] { new PlotPoint(x, 0), new PlotPoint(x, PlotSampler.Height) }, true);
            }
            if (definition.YMin <= 0 && definition.YMax >= 0)
            {
                double y = definition.YMax * PlotSampler.Height / (definition.YMax - definition.YMin);
                Add(new[] { new PlotPoint(0, y), new PlotPoint(PlotSampler.Width, y) }, true);
            }
        }
        return result;
        void Add(IReadOnlyList<PlotPoint> path, bool axis)
        {
            var style = attributes.Clone(); style.TaperKind = TaperKind.None; style.Nib = PenNib.Round; style.IsHighlighter = false;
            if (axis) { style.Color = new InkColor(255, 125, 133, 143); style.BaseWidth = Math.Min(1, style.BaseWidth); }
            if (preview) style.Color = style.Color.WithAlpha(170);
            var stroke = new InkStroke(style) { IsRecognizedShape = true, ShapeSource = source };
            stroke.RestorePoints(path.Select(point => new InkPoint((float)point.X, (float)point.Y, .5f, style.BaseWidth, 0)));
            stroke.Complete(); stroke.ObjectFrame = new SelectionFrame(new Rect(0, 0, (float)PlotSampler.Width, (float)PlotSampler.Height), Matrix3x2.Identity);
            stroke.Transform(placement, scaleWidth: false); result.Add(stroke);
        }
    }
}
