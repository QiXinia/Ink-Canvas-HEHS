using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace InkCanvas.Controls.Controls;

/// <summary>Reads the rendered water-ink paths, including feibai holes, for native XAML previews.</summary>
internal static class InkWashPreviewGeometry
{
    public static Vector2[][][] Build(InkStroke stroke)
    {
        var layers = CalligraphyStrokeRenderer.CreatePreviewGeometry(CanvasDevice.GetSharedDevice(), stroke);
        try
        {
            var result = new Vector2[layers.Length][][];
            for (int i = 0; i < layers.Length; i++)
            {
                var receiver = new ContourReceiver();
                if (layers[i] is not null)
                {
                    using var lines = layers[i].Simplify(CanvasGeometrySimplification.Lines);
                    lines.SendPathTo(receiver);
                }
                result[i] = receiver.Contours.ToArray();
            }
            return result;
        }
        finally { foreach (var layer in layers) layer?.Dispose(); }
    }

    private sealed class ContourReceiver : ICanvasPathReceiver
    {
        public readonly List<Vector2[]> Contours = new();
        private readonly List<Vector2> _points = new();
        public void BeginFigure(Vector2 startPoint, CanvasFigureFill figureFill) { _points.Clear(); _points.Add(startPoint); }
        public void AddLine(Vector2 point) => _points.Add(point);
        public void EndFigure(CanvasFigureLoop figureLoop) { if (_points.Count >= 3) Contours.Add(_points.ToArray()); }
        public void SetFilledRegionDetermination(CanvasFilledRegionDetermination value) { }
        public void SetSegmentOptions(CanvasFigureSegmentOptions value) { }
        public void AddCubicBezier(Vector2 a, Vector2 b, Vector2 end) => throw new InvalidOperationException("Expected simplified lines.");
        public void AddQuadraticBezier(Vector2 control, Vector2 end) => throw new InvalidOperationException("Expected simplified lines.");
        public void AddArc(Vector2 end, float radiusX, float radiusY, float rotation, CanvasSweepDirection direction, CanvasArcSize size)
            => throw new InvalidOperationException("Expected simplified lines.");
    }
}
