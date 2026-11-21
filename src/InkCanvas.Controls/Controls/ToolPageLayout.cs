using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using InkCanvas.Controls.Theming;

namespace InkCanvas.Controls.Controls;

/// <summary>Responsive configuration pane beside a tool's main working area.</summary>
public sealed class ToolPageLayout : UserControl
{
    private readonly ColumnDefinition _left = new() { Width = new GridLength(240) };
    private readonly Border _pane;
    private bool _paneVisible = true;
    public bool PaneVisible
    {
        get => _paneVisible;
        set
        {
            _paneVisible = value;
            _pane.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdatePaneWidth();
        }
    }
    public ToolPageLayout(FrameworkElement settings, FrameworkElement workspace)
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(_left);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _pane = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = PopupBrushes.Border(1),
            Background = PopupBrushes.Surface(1),
            Child = new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch } };
        Grid.SetColumn(workspace, 1);
        root.Children.Add(_pane);
        root.Children.Add(workspace);
        Content = root;
        SizeChanged += (_, _) => UpdatePaneWidth();
        ActualThemeChanged += (_, _) => ApplyTheme();
        Loaded += (_, _) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        int theme = ActualTheme == ElementTheme.Dark ? 2 : 1;
        _pane.Background = PopupBrushes.Surface(theme);
        _pane.BorderBrush = PopupBrushes.Border(theme);
    }

    private void UpdatePaneWidth() => _left.Width = new GridLength(!_paneVisible ? 0 : ActualWidth < 700 ? Math.Clamp(ActualWidth * .38, 210, 240) : 240);
}
