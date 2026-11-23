using InkCanvas.Controls.Theming;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
    private Flyout _deleteFlyout;
    private int _theme;
    private int ColorSlots => _highlighter ? 7 : 9;
    public event Action<InkColor> ColorPicked;
    public event Action PaletteChanged;
    public Func<FrameworkElement, InkColor, Action<InkColor>, Task> EditColorAsync { get; set; }
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
            if (index < 0 || index >= ColorSlots) return;
            await EditAsync(index, _grid.Children[index + 1] as FrameworkElement);
        };
        Unloaded += (_, _) => _deleteFlyout?.Hide();
        ApplyTheme(light ? 1 : 2);
    }

    private ColorPaletteKind Mode => _settings.ColorPalette is { } mode && Enum.IsDefined(mode)
        && (mode != ColorPaletteKind.Custom || _settings.EnableCustomColorPalette)
        ? mode : LightColorScheme ? ColorPaletteKind.Light : ColorPaletteKind.Dark;

    public void ApplyTheme(int theme)
    {
        _theme = theme;
        _foreground.Color = PopupBrushes.IsDark(theme) ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        _border.Color = PopupBrushes.IsDark(theme) ? Windows.UI.Color.FromArgb(153, 255, 255, 255) : Windows.UI.Color.FromArgb(204, 0, 0, 0);
        _edit.Foreground = _foreground;
        Refresh(_selected);
    }

    public void Refresh(InkColor selected, CanvasSettings settings = null)
    {
        if (settings is not null) _settings = settings;
        _selected = selected;
        _deleteFlyout?.Hide();
        _deleteFlyout = null;
        _grid.Children.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear();
        int columns = _highlighter ? 4 : 5;
        double cellWidth = _compact && !_highlighter ? 48 : 44;
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
            switcherContent = new TextBlock { Text = Caption(mode), FontSize = 10, Foreground = _foreground };
        else
            switcherContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children =
            {
                new FontIcon { Glyph = mode == ColorPaletteKind.Custom ? Glyphs.Pen : mode == ColorPaletteKind.Light ? "\uF8BA" : "\uF33C",
                    FontFamily = IconFontStyle.Font, FontSize = 14, Foreground = _foreground },
                new TextBlock { Text = Caption(mode), FontSize = 10, Foreground = _foreground, HorizontalAlignment = HorizontalAlignment.Center },
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
            for (int slot = 0; slot < ColorSlots; slot++)
            {
                int index = slot;
                if (index >= _settings.CustomColors.Count)
                {
                    var add = Cell(new FontIcon { Glyph = Glyphs.Add, FontFamily = IconFontStyle.Font, FontSize = 16, Foreground = _foreground }, circular: !_highlighter);
                    SetAccessibleName(add, "添加自定义颜色", $"颜色槽位 {index + 1}，使用取色器、RGB 或 HEX 添加颜色");
                    add.Click += async (_, _) => await EditAsync(null, add);
                    Add(add, position++);
                    continue;
                }
                uint value = _settings.CustomColors[index];
                var color = InkColor.FromPackedArgb(value).WithAlpha(255);
                var button = Swatch(color, $"自定义颜色 {position} · #{color.R:X2}{color.G:X2}{color.B:X2}", selectOnClick: false);
                ConfigureLongPress(button, index, color);
                var menu = new MenuFlyout();
                var edit = new MenuFlyoutItem { Text = "编辑颜色" };
                edit.Click += async (_, _) => await EditAsync(index, button);
                var remove = new MenuFlyoutItem { Text = "删除颜色" };
                remove.Click += (_, _) => RemoveColor(index);
                menu.Items.Add(edit); menu.Items.Add(remove); button.ContextFlyout = menu;
                Add(button, position++);
            }
        }
        else
        {
            var colors = _highlighter ? OriginalMenuCatalog.HighlighterColors : OriginalMenuCatalog.PenColors;
            foreach (var swatch in colors)
                Add(Swatch(mode == ColorPaletteKind.Light ? swatch.LightTheme : swatch.DarkTheme, swatch.Name), position++);
        }
        _edit.Visibility = mode == ColorPaletteKind.Custom && _settings.CustomColors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _edit.IsEnabled = !IsEditingColor && _settings.CustomColors.Take(ColorSlots)
            .Any(value => (value & 0x00ffffff) == (_selected.PackedArgb & 0x00ffffff));

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

    private Button Swatch(InkColor color, string name, bool selectOnClick = true)
    {
        var contrast = ColorContrast.Foreground(color);
        var check = new FontIcon { Glyph = "\uF295", FontFamily = IconFontStyle.Font, FontSize = 16,
            Foreground = new SolidColorBrush(ToWindowsColor(contrast)),
            Visibility = (color.PackedArgb & 0x00ffffff) == (_selected.PackedArgb & 0x00ffffff) ? Visibility.Visible : Visibility.Collapsed };
        var button = Cell(check, circular: !_highlighter, color);
        SetAccessibleName(button, name, "选择此墨迹颜色，自定义颜色可长按删除或右键编辑");
        if (selectOnClick) button.Click += (_, _) => ColorPicked?.Invoke(color);
        return button;
    }

    private Button Cell(UIElement content, bool circular, InkColor? color = null)
    {
        double width = 40;
        double height = _compact && _highlighter ? 25 : 40;
        return new Button { MinWidth = 0, MinHeight = 0, Width = width, Height = height, Margin = new Thickness(2), Padding = new Thickness(0),
            BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(circular ? width / 2 : 4),
                BorderThickness = new Thickness(1), BorderBrush = _border, Child = content,
                Background = new SolidColorBrush(color.HasValue ? ToWindowsColor(color.Value) : Microsoft.UI.Colors.Transparent) } };
    }

    private void ConfigureLongPress(Button button, int index, InkColor color)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        Windows.Foundation.Point origin = default;
        uint? pointerId = null;
        bool held = false;
        button.IsHoldingEnabled = false;
        button.AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(button);
            if (pointerId.HasValue) return;
            held = false;
            if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
            pointerId = args.Pointer.PointerId;
            origin = point.Position;
            timer.Start();
        }), true);
        button.AddHandler(PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (pointerId != args.Pointer.PointerId) return;
            var position = args.GetCurrentPoint(button).Position;
            if (Math.Abs(position.X - origin.X) > 8 || Math.Abs(position.Y - origin.Y) > 8) timer.Stop();
        }), true);
        button.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (pointerId != args.Pointer.PointerId) return;
            timer.Stop();
            pointerId = null;
            if (held) args.Handled = true;
        }), true);
        button.PointerCanceled += (_, _) => { timer.Stop(); pointerId = null; };
        button.PointerCaptureLost += (_, _) => { timer.Stop(); pointerId = null; };
        button.KeyDown += (_, _) => held = false;
        button.ContextRequested += (_, args) => { if (held) args.Handled = true; };
        button.Unloaded += (_, _) => timer.Stop();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            held = true;
            ShowDeleteFlyout(button, index);
        };
        button.Click += (_, _) => { if (!held) ColorPicked?.Invoke(color); };
    }

    private void ShowDeleteFlyout(Button target, int index)
    {
        _deleteFlyout?.Hide();
        var remove = new TouchIconButton
        {
            Glyph = Glyphs.Close, IconSize = 16, Width = 40, Height = 40,
            CornerRadius = new CornerRadius(4), ContentForeground = PopupBrushes.FloatBarForeground(_theme),
            HoverBackground = PopupBrushes.FloatBarButtonHover(_theme), PressedBackground = PopupBrushes.FloatBarButtonPressed(_theme),
        };
        SetAccessibleName(remove, "删除自定义颜色", "删除此颜色");
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Top, Content = remove,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter)),
        };
        flyout.FlyoutPresenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
        flyout.FlyoutPresenterStyle.Setters.Add(new Setter(Control.MinWidthProperty, 0d));
        flyout.FlyoutPresenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(6)));
        flyout.FlyoutPresenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty,
            PopupBrushes.IsDark(_theme) ? ElementTheme.Dark : ElementTheme.Light));
        remove.Click += (_, _) => { flyout.Hide(); RemoveColor(index); };
        _deleteFlyout = flyout;
        flyout.ShowAt(target);
    }

    private void RemoveColor(int index)
    {
        if (index < 0 || index >= _settings.CustomColors.Count) return;
        _settings.CustomColors.RemoveAt(index);
        Refresh(_selected);
        PaletteChanged?.Invoke();
    }

    private async Task EditAsync(int? slot, FrameworkElement anchor)
    {
        if (IsEditingColor || EditColorAsync is null || !_settings.EnableCustomColorPalette) return;
        if (slot.HasValue ? slot.Value < 0 || slot.Value >= _settings.CustomColors.Count : _settings.CustomColors.Count >= ColorSlots) return;
        IsEditingColor = true; _grid.IsHitTestVisible = false; _edit.IsEnabled = false;
        try
        {
            uint? original = slot.HasValue ? _settings.CustomColors[slot.Value] : null;
            await EditColorAsync(anchor, original.HasValue ? InkColor.FromPackedArgb(original.Value).WithAlpha(255) : _selected.WithAlpha(255), chosen =>
            {
                if (!_settings.EnableCustomColorPalette) return;
                uint value = chosen.WithAlpha(255).PackedArgb;
                if (slot.HasValue)
                {
                    if (slot.Value >= _settings.CustomColors.Count || _settings.CustomColors[slot.Value] != original) return;
                    _settings.CustomColors[slot.Value] = value;
                }
                else
                {
                    if (_settings.CustomColors.Count >= ColorSlots) return;
                    slot = _settings.CustomColors.Count;
                    _settings.CustomColors.Add(value);
                }
                original = value;
                Refresh(chosen);
                ColorPicked?.Invoke(chosen.WithAlpha(255));
            });
        }
        finally { IsEditingColor = false; _grid.IsHitTestVisible = true; Refresh(_selected); }
    }

    private static string Caption(ColorPaletteKind kind) => kind switch { ColorPaletteKind.Light => "亮系", ColorPaletteKind.Dark => "暗系", _ => "自定义" };
    private static Windows.UI.Color ToWindowsColor(InkColor color) => Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
    private static void SetAccessibleName(DependencyObject target, string name, string help)
    { AutomationProperties.SetName(target, name); AutomationProperties.SetHelpText(target, help); ToolTipService.SetToolTip(target, help); }
}
