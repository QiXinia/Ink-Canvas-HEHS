using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace InkCanvas.Controls.Controls;

/// <summary>WPF CycleProcessBar geometry, kept as XAML vectors when its parent Viewbox scales.</summary>
public sealed class CountdownRing : UserControl
{
    private readonly SolidColorBrush _progressBrush = new(Windows.UI.Color.FromArgb(255, 0, 103, 193));
    private readonly ArcSegment _arc = new() { Size = new Size(14, 14), SweepDirection = SweepDirection.Clockwise };
    private readonly ArcSegment _secondHalf = new() { Size = new Size(14, 14), SweepDirection = SweepDirection.Clockwise, Point = new Point(17, 3) };
    private readonly PathFigure _figure = new() { StartPoint = new Point(17, 3), IsClosed = false, IsFilled = false };
    private readonly Microsoft.UI.Xaml.Shapes.Path _progress;
    private double _value;
    private bool _isPaused;

    public CountdownRing()
    {
        var canvas = new Canvas { Width = 34, Height = 34, IsHitTestVisible = false };
        var track = new Ellipse { Width = 29.5, Height = 29.5, StrokeThickness = 1.5,
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 234, 234)) };
        Canvas.SetLeft(track, 2.25); Canvas.SetTop(track, 2.25);
        canvas.Children.Add(track);
        _figure.Segments.Add(_arc);
        var geometry = new PathGeometry(); geometry.Figures.Add(_figure);
        _progress = new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = _progressBrush,
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Visibility = Visibility.Collapsed };
        canvas.Children.Add(_progress);
        Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
    }

    /// <summary>Remaining fraction. The track remains visible at zero.</summary>
    public double Value
    {
        get => _value;
        set
        {
            double next = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            if (next == _value) return;
            _value = next;
            _progress.Visibility = next > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool full = next == 1;
            double angle = -Math.PI / 2 + next * Math.PI * 2;
            _arc.Point = full ? new Point(17, 31) : new Point(17 + 14 * Math.Cos(angle), 17 + 14 * Math.Sin(angle));
            _arc.IsLargeArc = !full && next > .5;
            if (full && _figure.Segments.Count == 1) _figure.Segments.Add(_secondHalf);
            else if (!full && _figure.Segments.Count == 2) _figure.Segments.RemoveAt(1);
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value) return;
            _isPaused = value;
            _progressBrush.Color = value ? Windows.UI.Color.FromArgb(255, 26, 113, 200) : Windows.UI.Color.FromArgb(255, 0, 103, 193);
        }
    }
}
