using System.Numerics;
using InkCanvas.AnalyticGeometry;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace InkCanvas.Modules.AnalyticGeometry;

public sealed class PlotPreview : UserControl, IDisposable
{
    private readonly CanvasControl _canvas = new() { ClearColor = Microsoft.UI.Colors.White };
    private readonly CanvasTextFormat _text = new() { FontSize = 11, FontFamily = "Segoe UI" };
    private CanvasGeometry _geometry;
    private PlotMesh _mesh;
    private PlotDefinition _definition = new();
    private Matrix3x2 _gesture = Matrix3x2.Identity, _fit = Matrix3x2.Identity;
    private bool _dirty, _disposed;
    public event Action<PlotDefinition> RangeChanged;
    public PlotPreview()
    {
        Content = _canvas; _canvas.Draw += Draw; SizeChanged += (_, _) => _canvas.Invalidate();
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
        ManipulationDelta += (_, e) =>
        {
            var delta = e.Delta;
            var center = new Vector2((float)e.Position.X, (float)e.Position.Y);
            _gesture *= Matrix3x2.CreateScale((float)Math.Clamp(delta.Scale, .5, 2), center)
                * Matrix3x2.CreateTranslation((float)delta.Translation.X, (float)delta.Translation.Y);
            _canvas.Invalidate(); e.Handled = true;
        };
        ManipulationCompleted += (_, _) => CommitView();
        PointerWheelChanged += (_, e) =>
        {
            var point = e.GetCurrentPoint(this); float scale = MathF.Pow(1.12f, point.Properties.MouseWheelDelta / 120f);
            _gesture *= Matrix3x2.CreateScale(scale, new Vector2((float)point.Position.X, (float)point.Position.Y));
            CommitView(); e.Handled = true;
        };
    }
    public void SetPlot(PlotMesh mesh, PlotDefinition definition)
    { if (_disposed) return; _mesh = mesh; _definition = definition; _dirty = true; _gesture = Matrix3x2.Identity; _canvas.Invalidate(); }
    private void CommitView()
    {
        if (_gesture == Matrix3x2.Identity || !Matrix3x2.Invert(_fit * _gesture, out var inverse)) return;
        var first = Vector2.Transform(Vector2.Transform(Vector2.Zero, _fit), inverse);
        var last = Vector2.Transform(Vector2.Transform(new((float)PlotSampler.Width, (float)PlotSampler.Height), _fit), inverse);
        double width = _definition.XMax - _definition.XMin, height = _definition.YMax - _definition.YMin;
        var definition = _definition with { XMin = _definition.XMin + first.X / PlotSampler.Width * width,
            XMax = _definition.XMin + last.X / PlotSampler.Width * width,
            YMax = _definition.YMax - first.Y / PlotSampler.Height * height, YMin = _definition.YMax - last.Y / PlotSampler.Height * height };
        _gesture = Matrix3x2.Identity; RangeChanged?.Invoke(definition);
    }
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || ActualWidth < 20 || ActualHeight < 20) return;
        float scale = (float)Math.Min((ActualWidth - 36) / PlotSampler.Width, (ActualHeight - 36) / PlotSampler.Height);
        _fit = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(((float)ActualWidth - (float)PlotSampler.Width * scale) / 2, ((float)ActualHeight - (float)PlotSampler.Height * scale) / 2);
        var draw = args.DrawingSession; draw.Transform = _fit * _gesture;
        var gridColor = Windows.UI.Color.FromArgb(255, 232, 235, 240);
        double xStep = Tick(_definition.XMax - _definition.XMin), yStep = Tick(_definition.YMax - _definition.YMin);
        for (double x = Math.Ceiling(_definition.XMin / xStep) * xStep; x < _definition.XMax; x += xStep)
        {
            float screen = (float)((x - _definition.XMin) / (_definition.XMax - _definition.XMin) * PlotSampler.Width);
            draw.DrawLine(screen, 0, screen, (float)PlotSampler.Height, gridColor, .7f / scale);
            draw.DrawText(x.ToString("0.###"), screen + 3, (float)PlotSampler.Height - 16, Microsoft.UI.Colors.Gray, _text);
        }
        for (double y = Math.Ceiling(_definition.YMin / yStep) * yStep; y < _definition.YMax; y += yStep)
        {
            float screen = (float)((_definition.YMax - y) / (_definition.YMax - _definition.YMin) * PlotSampler.Height);
            draw.DrawLine(0, screen, (float)PlotSampler.Width, screen, gridColor, .7f / scale);
            draw.DrawText(y.ToString("0.###"), 3, screen + 2, Microsoft.UI.Colors.Gray, _text);
        }
        if (_definition.XMin <= 0 && _definition.XMax >= 0)
        { float x = (float)(-_definition.XMin / (_definition.XMax - _definition.XMin) * PlotSampler.Width); draw.DrawLine(x, 0, x, (float)PlotSampler.Height, Microsoft.UI.Colors.SlateGray, 1 / scale); }
        if (_definition.YMin <= 0 && _definition.YMax >= 0)
        { float y = (float)(_definition.YMax / (_definition.YMax - _definition.YMin) * PlotSampler.Height); draw.DrawLine(0, y, (float)PlotSampler.Width, y, Microsoft.UI.Colors.SlateGray, 1 / scale); }
        if (_dirty)
        {
            _geometry?.Dispose(); _geometry = null;
            if (_mesh?.Paths.Count > 0)
            {
                using var path = new CanvasPathBuilder(sender);
                foreach (var points in _mesh.Paths)
                {
                    if (points.Length < 2) continue;
                    path.BeginFigure((float)points[0].X, (float)points[0].Y);
                    foreach (var point in points.Skip(1)) path.AddLine((float)point.X, (float)point.Y);
                    path.EndFigure(CanvasFigureLoop.Open);
                }
                _geometry = CanvasGeometry.CreatePath(path);
            }
            _dirty = false;
        }
        if (_geometry is not null) draw.DrawGeometry(_geometry, Windows.UI.Color.FromArgb(255, 32, 105, 222), 2 / scale);
        draw.DrawRectangle(0, 0, (float)PlotSampler.Width, (float)PlotSampler.Height, gridColor, 1 / scale);
    }
    private static double Tick(double span)
    { double magnitude = Math.Pow(10, Math.Floor(Math.Log10(span / 7))); return Math.Ceiling(span / 7 / magnitude) * magnitude; }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _geometry?.Dispose(); _text.Dispose();
        _canvas.Draw -= Draw; _canvas.RemoveFromVisualTree(); Content = null;
    }
}
