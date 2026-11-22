using InkCanvas.Controls.Theming;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared pen and highlighter colours with a persistent, editable custom palette.</summary>
public sealed class InkColorPalette : UserControl
{
    private CanvasSettings _settings;
    private readonly bool _highlighter, _compact;
    private readonly Grid _grid = new();
    private readonly Button _edit = new() { Content = "编辑颜色", MinHeight = 32, Padding = new Thickness(8, 2, 8, 2) };
    private readonly SolidColorBrush _foreground = new(), _border = new();
    private InkColor _selected = InkColor.Red;
    public event Action<InkColor> ColorPicked;
    public event Action PaletteChanged;
    public Func<InkColor, Task<InkColor?>> EditColorAsync { get; set; }
    public bool IsEditingColor { get; private set; }
    public bool LightColorScheme { get; set; }

    public InkColorPalette(CanvasSettings settings, bool highlighter, bool light, bool compact = false)
    {
        _settings = settings; _highlighter = highlighter; _compact = compact; LightColorScheme = light;
        AutomationProperties.SetName(this, highlighter ? "荧光笔色板" : "画笔色板");
        var scroll = new ScrollViewer { Content = _grid, MaxHeight = compact && highlighter ? 87 : 132,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(scroll, highlighter ? "荧光笔颜色" : "画笔颜色");
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(scroll); panel.Children.Add(_edit); Content = panel;
        AutomationProperties.SetName(_edit, "编辑自定义颜色");
        _edit.Click += async (_, _) =>
        {
            int index = _settings.CustomColors.FindIndex(value => (value & 0x00ffffff) == (_selected.PackedArgb & 0x00ffffff));
            await EditAsync(index < 0 ? null : _settings.CustomColors[index]);
        };
        ApplyTheme(light ? 1 : 2);
    }

    private ColorPaletteKind Mode => _settings.ColorPalette is { } mode && Enum.IsDefined(mode)
        && (mode != ColorPaletteKind.Custom || _settings.EnableCustomColorPalette)
        ? mode : LightColorScheme ? ColorPaletteKind.Light : ColorPaletteKind.Dark;

    public void ApplyTheme(int theme)
    {
        _foreground.Color = PopupBrushes.IsDark(theme) ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        _border.Color = PopupBrushes.IsDark(theme) ? Windows.UI.Color.FromArgb(153, 255, 255, 255) : Windows.UI.Color.FromArgb(204, 0, 0, 0);
        _edit.Foreground = _foreground;
        Refresh(_selected);
    }

    public void Refresh(InkColor selected, CanvasSettings settings = null)
    {
        if (settings is not null) _settings = settings;
        _selected = selected;
        _grid.Children.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear();
        int columns = _highlighter ? 3 : 5;
        double cellWidth = _compact ? (_highlighter ? 40 : 48) : 44;
        double cellHeight = _compact && _highlighter ? 29 : 44;
        for (int i = 0; i < columns; i++) _grid.ColumnDefinitions.Add(new() { Width = new GridLength(cellWidth) });
        var mode = Mode;
        var next = mode switch
        {
            ColorPaletteKind.Light => ColorPaletteKind.Dark,
            ColorPaletteKind.Dark when _settings.EnableCustomColorPalette => ColorPaletteKind.Custom,
            _ => ColorPaletteKind.Light,
        };
        UIElement switcherContent;
        if (_compact && _highlighter)
            switcherContent = new TextBlock { Text = Caption(next), FontSize = 10, Foreground = _foreground };
        else
            switcherContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children =
            {
                new FontIcon { Glyph = next == ColorPaletteKind.Custom ? Glyphs.Pen : next == ColorPaletteKind.Light ? "\uF8BA" : "\uF33C",
                    FontFamily = IconFontStyle.Font, FontSize = 14, Foreground = _foreground },
                new TextBlock { Text = Caption(next), FontSize = 10, Foreground = _foreground, HorizontalAlignment = HorizontalAlignment.Center },
            } };
        var switcher = Cell(switcherContent, circular: !_highlighter);
        SetAccessibleName(switcher, _settings.EnableCustomColorPalette ? "切换亮系/暗系/自定义墨迹颜色" : "切换亮系/暗系墨迹颜色",
            "当前" + Caption(mode) + "，切换到" + Caption(next));
        switcher.Click += (_, _) => SelectMode(next);
        var schemes = new MenuFlyout();
        foreach (var kind in Enum.GetValues<ColorPaletteKind>())
        {
            if (kind == ColorPaletteKind.Custom && !_settings.EnableCustomColorPalette) continue;
            var item = new ToggleMenuFlyoutItem { Text = Caption(kind), IsChecked = kind == mode };
            item.Click += (_, _) => SelectMode(kind); schemes.Items.Add(item);
        }
        switcher.ContextFlyout = schemes;
        Add(switcher, 0);
        int position = 1;
        if (mode == ColorPaletteKind.Custom)
        {
            foreach (uint value in _settings.CustomColors)
            {
                var color = InkColor.FromPackedArgb(value).WithAlpha(255);
                var button = Swatch(color, $"自定义颜色 {position} · #{color.R:X2}{color.G:X2}{color.B:X2}");
                var menu = new MenuFlyout();
                var edit = new MenuFlyoutItem { Text = "编辑颜色" };
                edit.Click += async (_, _) => await EditAsync(value);
                var remove = new MenuFlyoutItem { Text = "删除颜色" };
                remove.Click += (_, _) => { _settings.CustomColors.Remove(value); Refresh(_selected); PaletteChanged?.Invoke(); };
                menu.Items.Add(edit); menu.Items.Add(remove); button.ContextFlyout = menu;
                Add(button, position++);
            }
            var add = Cell(new FontIcon { Glyph = Glyphs.Add, FontFamily = IconFontStyle.Font, FontSize = 18, Foreground = _foreground }, circular: !_highlighter);
            SetAccessibleName(add, "添加自定义颜色", "使用取色器、RGB 或 HEX 添加颜色");
            add.Click += async (_, _) => await EditAsync(null);
            Add(add, position);
        }
        else
        {
            var colors = _highlighter ? OriginalMenuCatalog.HighlighterColors : OriginalMenuCatalog.PenColors;
            foreach (var swatch in colors)
                Add(Swatch(mode == ColorPaletteKind.Light ? swatch.LightTheme : swatch.DarkTheme, swatch.Name), position++);
        }
        _edit.Visibility = mode == ColorPaletteKind.Custom && _settings.CustomColors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        void Add(Button button, int index)
        {
            while (_grid.RowDefinitions.Count <= index / columns) _grid.RowDefinitions.Add(new() { Height = new GridLength(cellHeight) });
            Grid.SetColumn(button, index % columns); Grid.SetRow(button, index / columns); _grid.Children.Add(button);
        }
    }

    private void SelectMode(ColorPaletteKind mode)
    {
        _settings.ColorPalette = mode;
        Refresh(_selected);
        PaletteChanged?.Invoke();
    }

    private Button Swatch(InkColor color, string name)
    {
        var contrast = ColorContrast.Foreground(color);
        var check = new FontIcon { Glyph = "\uF295", FontFamily = IconFontStyle.Font, FontSize = 16,
            Foreground = new SolidColorBrush(ToWindowsColor(contrast)),
            Visibility = (color.PackedArgb & 0x00ffffff) == (_selected.PackedArgb & 0x00ffffff) ? Visibility.Visible : Visibility.Collapsed };
        var button = Cell(check, circular: !_highlighter, color);
        SetAccessibleName(button, name, "选择此墨迹颜色，自定义颜色可长按或右键编辑");
        button.Click += (_, _) => ColorPicked?.Invoke(color);
        return button;
    }

    private Button Cell(UIElement content, bool circular, InkColor? color = null)
    {
        double width = _compact && _highlighter ? 36 : 40;
        double height = _compact && _highlighter ? 25 : 40;
        return new Button { MinWidth = 0, MinHeight = 0, Width = width, Height = height, Margin = new Thickness(2), Padding = new Thickness(0),
            BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(circular ? width / 2 : 4),
                BorderThickness = new Thickness(1), BorderBrush = _border, Child = content,
                Background = new SolidColorBrush(color.HasValue ? ToWindowsColor(color.Value) : Microsoft.UI.Colors.Transparent) } };
    }

    private async Task EditAsync(uint? original)
    {
        if (IsEditingColor || EditColorAsync is null || !_settings.EnableCustomColorPalette) return;
        IsEditingColor = true; _grid.IsHitTestVisible = false; _edit.IsEnabled = false;
        try
        {
            var chosen = await EditColorAsync(original.HasValue ? InkColor.FromPackedArgb(original.Value).WithAlpha(255) : _selected.WithAlpha(255));
            if (!chosen.HasValue || !_settings.EnableCustomColorPalette) return;
            uint value = chosen.Value.WithAlpha(255).PackedArgb;
            int index = original.HasValue ? _settings.CustomColors.IndexOf(original.Value) : -1;
            if (index < 0) _settings.CustomColors.Add(value);
            else _settings.CustomColors[index] = value;
            Refresh(chosen.Value);
            ColorPicked?.Invoke(chosen.Value.WithAlpha(255));
        }
        finally { IsEditingColor = false; _grid.IsHitTestVisible = true; _edit.IsEnabled = true; }
    }

    private static string Caption(ColorPaletteKind kind) => kind switch { ColorPaletteKind.Light => "亮系", ColorPaletteKind.Dark => "暗系", _ => "自定义" };
    private static Windows.UI.Color ToWindowsColor(InkColor color) => Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
    private static void SetAccessibleName(DependencyObject target, string name, string help)
    { AutomationProperties.SetName(target, name); AutomationProperties.SetHelpText(target, help); ToolTipService.SetToolTip(target, help); }
}
