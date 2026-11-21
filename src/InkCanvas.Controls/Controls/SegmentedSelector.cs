using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Theming;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace InkCanvas.Controls.Controls;

/// <summary>Inline choices for floating tools that keep focus in the presentation.</summary>
public sealed class SegmentedSelector : UserControl
{
    private readonly List<ToggleButton> _buttons = new();
    private int _selectedIndex = -1;
    private readonly UISettings _systemColors = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly SolidColorBrush _checkedBackground = new(), _checkedForeground = new();
    private static readonly string[] CheckedStates = { "Checked", "CheckedPointerOver", "CheckedPressed" };
    private bool _observingColors;

    public SegmentedSelector(IReadOnlyList<string> options, int columns = 0)
    {
        columns = columns > 0 ? columns : options.Count;
        var row = new Grid { ColumnSpacing = 2, RowSpacing = 2 };
        for (int column = 0; column < columns; column++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int index = 0; index < (options.Count + columns - 1) / columns; index++)
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var button = new ToggleButton
            {
                Content = options[i],
                FontSize = 12,
                MinWidth = 0,
                MinHeight = 32,
                Padding = new Thickness(3, 2, 3, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                AllowFocusOnInteraction = false,
            };
            AutomationProperties.SetName(button, options[i]);
            button.Checked += (_, _) => SelectedIndex = index;
            button.Unchecked += (_, _) => { if (_selectedIndex == index) button.IsChecked = true; };
            button.Click += (_, _) => SelectedIndex = index;
            Grid.SetColumn(button, i % columns);
            Grid.SetRow(button, i / columns);
            row.Children.Add(button);
            _buttons.Add(button);
        }
        Content = row;
        ActualThemeChanged += (_, _) => RefreshAccent();
        Loaded += (_, _) =>
        {
            if (!_observingColors) { _systemColors.ColorValuesChanged += OnColorsChanged; _observingColors = true; }
            RefreshAccent();
        };
        Unloaded += (_, _) =>
        {
            if (_observingColors) _systemColors.ColorValuesChanged -= OnColorsChanged;
            _observingColors = false;
        };
        RefreshAccent();
    }

    private void OnColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) RefreshAccent(); });

    private void RefreshAccent()
    {
        if (_accessibility.HighContrast)
        {
            foreach (var state in CheckedStates)
            {
                Resources.Remove("ToggleButtonBackground" + state);
                Resources.Remove("ToggleButtonForeground" + state);
            }
            return;
        }
        ApplyAccent(_systemColors.GetColorValue(ActualTheme == ElementTheme.Dark ? UIColorType.AccentLight2 : UIColorType.Accent));
    }

    private void ApplyAccent(Color color)
    {
        color.A = 255;
        _checkedBackground.Color = color;
        var foreground = ColorContrast.Foreground(new InkColor(255, color.R, color.G, color.B));
        _checkedForeground.Color = Color.FromArgb(255, foreground.R, foreground.G, foreground.B);
        foreach (var state in CheckedStates)
        {
            Resources["ToggleButtonBackground" + state] = _checkedBackground;
            Resources["ToggleButtonForeground" + state] = _checkedForeground;
        }
    }

    public void SetOptionContent(int index, UIElement content, double minHeight = 0)
    {
        _buttons[index].Content = content;
        if (minHeight <= 0) return;
        _buttons[index].MinHeight = minHeight;
        _buttons[index].Padding = new Thickness(0);
    }
    public void SetOptionHelp(int index, string text)
    {
        AutomationProperties.SetHelpText(_buttons[index], text);
        ToolTipService.SetToolTip(_buttons[index], text);
    }

    public event Action SelectionChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value >= _buttons.Count) return;
            bool changed = _selectedIndex != value;
            _selectedIndex = value;
            for (int i = 0; i < _buttons.Count; i++) _buttons[i].IsChecked = i == value;
            if (changed) SelectionChanged?.Invoke();
        }
    }
}
