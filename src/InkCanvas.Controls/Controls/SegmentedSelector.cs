using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>Inline choices for floating tools that keep focus in the presentation.</summary>
public sealed class SegmentedSelector : UserControl
{
    private readonly List<ToggleButton> _buttons = new();
    private int _selectedIndex = -1;
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
