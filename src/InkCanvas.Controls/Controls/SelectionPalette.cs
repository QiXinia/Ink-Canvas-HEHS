using InkCanvas.Controls.Theming;
using InkCanvas.Core.Selection;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>Selection shape and bulk selection actions shared by all canvas hosts.</summary>
public sealed class SelectionPalette : UserControl
{
    private readonly Func<InkController> _controller;
    private readonly TouchIconButton _rectangle, _lasso;
    public event Action<InkSelectionMode> ModePicked;

    public SelectionPalette(Func<InkController> controller, int theme)
    {
        _controller = controller;
        var modes = new Grid { ColumnSpacing = 8 };
        modes.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        modes.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _rectangle = Button("矩形选择", Glyphs.SelectWpf, () => PickMode(InkSelectionMode.Rectangle));
        _lasso = Button("套索选择", Glyphs.Select, () => PickMode(InkSelectionMode.Lasso));
        var path = new PathFigure { StartPoint = new(5, 22), IsClosed = false };
        path.Segments.Add(new BezierSegment { Point1 = new(24, 31), Point2 = new(34, 8), Point3 = new(19, 4) });
        path.Segments.Add(new BezierSegment { Point1 = new(2, -1), Point2 = new(-1, 16), Point3 = new(10, 22) });
        path.Segments.Add(new BezierSegment { Point1 = new(15, 25), Point2 = new(20, 20), Point3 = new(15, 17) });
        _lasso.SetContent(new Microsoft.UI.Xaml.Shapes.Path { Width = 30, Height = 30,
            Data = new PathGeometry { Figures = { path } }, Stroke = PopupBrushes.FloatBarForeground(theme), StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 2, 1 }, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        modes.Children.Add(_rectangle); Grid.SetColumn(_lasso, 1); modes.Children.Add(_lasso);
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.Children.Add(Button("全选", Glyphs.Select, () => _controller().SelectAll()));
        var invert = Button("反选", Glyphs.LRSwitch, () => _controller().InvertSelection());
        Grid.SetColumn(invert, 1); actions.Children.Add(invert);
        Content = new StackPanel { Width = 224, Padding = new Thickness(12), Spacing = 8, Children = { modes, actions } };
        Refresh();

        TouchIconButton Button(string label, string glyph, Action click)
        {
            var button = new TouchIconButton { Label = label, Glyph = glyph, MinWidth = 0, Height = 60, IconSize = 24,
                LabelFontSize = 12, ContentForeground = PopupBrushes.FloatBarForeground(theme),
                HoverBackground = PopupBrushes.FloatBarButtonHover(theme), PressedBackground = PopupBrushes.FloatBarButtonPressed(theme),
                SelectionBackground = PopupBrushes.FloatBarSelected() };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            button.Click += (_, _) => click();
            return button;
        }
    }

    private void PickMode(InkSelectionMode mode)
    {
        _controller().SelectionMode = mode;
        ModePicked?.Invoke(mode);
        Refresh();
    }

    public void Refresh()
    {
        _rectangle.IsSelected = _controller().SelectionMode == InkSelectionMode.Rectangle;
        _lasso.IsSelected = _controller().SelectionMode == InkSelectionMode.Lasso;
    }
}
