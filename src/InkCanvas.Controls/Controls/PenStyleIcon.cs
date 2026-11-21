using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using System.Numerics;
using System.Collections.Concurrent;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared vector thumbnails computed once per style with the actual ink engine.</summary>
public sealed class PenStyleIcon : UserControl
{
    private sealed record Sample(Vector2[][] Contours, double Opacity);
    private static readonly ConcurrentDictionary<TaperKind, Lazy<Task<Sample[]>>> Samples = new();
    private readonly TaperKind _kind;
    private readonly Canvas _canvas = new() { Width = 96, Height = 32 };
    private bool _populated;
    private int _loadVersion;
    private ContentPresenter _foregroundOwner;
    private long _foregroundToken;

    public PenStyleIcon(int style)
    {
        _kind = OriginalMenuCatalog.ToTaperKind(style);
        Content = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(this, OriginalMenuCatalog.PenStyles[Math.Clamp(style, 0, OriginalMenuCatalog.PenStyles.Count - 1)]);
        IsHitTestVisible = false;
        Loaded += Populate;
        Unloaded += (_, _) =>
        {
            _loadVersion++;
            DisconnectForeground();
        };
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => UpdateForeground());
    }

    private static Sample[] BuildSamples(TaperKind kind)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 8, TaperKind = kind, InkWashSeed = 137 });
        float time = 0;
        for (int point = 0; point <= 32; point++)
        {
            float t = point / 32f;
            time += 20 - 17 * t;
            stroke.AddPoint(6 + 84 * t, 16 + MathF.Sin(t * MathF.PI * 2) * 6, .5f, time);
        }
        stroke.Complete();
        var layers = InkBrush.Layers(stroke);
        var washContours = InkBrush.UsesInkWash(stroke) ? InkWashPreviewGeometry.Build(stroke) : null;
        var samples = new Sample[layers.Length];
        for (int layer = 0; layer < layers.Length; layer++)
            samples[layer] = new(washContours is null ? StrokeGeometry.BuildUnionContours(stroke, layers[layer].WidthScale) : washContours[layer], layers[layer].Opacity / 255d);
        return samples;
    }

    private async void Populate(object sender, RoutedEventArgs args)
    {
        ConnectForeground();
        if (_populated) return;
        int version = ++_loadVersion;
        // These samples are pure data. Cold icon generation must not block popup construction.
        var samples = await Samples.GetOrAdd(_kind, static kind => new Lazy<Task<Sample[]>>(
            () => Task.Run(() => BuildSamples(kind)))).Value;
        if (!IsLoaded || version != _loadVersion) return;
        foreach (var sample in samples)
        {
            var path = new Path { Data = CreateGeometry(sample.Contours), Opacity = sample.Opacity, Fill = EffectiveForeground };
            _canvas.Children.Add(path);
        }
        _populated = true;
    }

    private Brush EffectiveForeground => ReadLocalValue(ForegroundProperty) == DependencyProperty.UnsetValue
        ? _foregroundOwner?.Foreground ?? Foreground : Foreground;

    private void ConnectForeground()
    {
        DisconnectForeground();
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ContentPresenter presenter) continue;
            _foregroundOwner = presenter;
            _foregroundToken = presenter.RegisterPropertyChangedCallback(ContentPresenter.ForegroundProperty, (_, _) => UpdateForeground());
            break;
        }
        UpdateForeground();
    }

    private void DisconnectForeground()
    {
        _foregroundOwner?.UnregisterPropertyChangedCallback(ContentPresenter.ForegroundProperty, _foregroundToken);
        _foregroundOwner = null;
    }

    private void UpdateForeground()
    {
        foreach (var path in _canvas.Children.OfType<Path>()) path.Fill = EffectiveForeground;
    }

    internal static PathGeometry CreateGeometry(InkStroke stroke, float widthScale)
        => CreateGeometry(StrokeGeometry.BuildUnionContours(stroke, widthScale));

    internal static PathGeometry CreateGeometry(Vector2[][] contours)
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var contour in contours)
        {
            var figure = new PathFigure { StartPoint = new Point(contour[0].X, contour[0].Y), IsClosed = true, IsFilled = true };
            var segment = new PolyLineSegment();
            for (int point = 1; point < contour.Length; point++) segment.Points.Add(new Point(contour[point].X, contour[point].Y));
            figure.Segments.Add(segment);
            geometry.Figures.Add(figure);
        }
        return geometry;
    }
}
