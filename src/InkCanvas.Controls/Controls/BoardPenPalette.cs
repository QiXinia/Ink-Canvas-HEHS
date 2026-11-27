using InkCanvas.Core.Ink;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>WPF BoardPenPalette: one ink-options panel with pen and highlighter columns.</summary>
public sealed class BoardPenPalette : UserControl
{
    private readonly InkController _controller;
    private readonly AppSettings _settings;
    private readonly SolidColorBrush _foreground;
    private readonly InkColorPalette _penColors, _highlighterColors;
    private readonly FrameworkElement _nibModeRow;
    private readonly ToggleSwitch _nibMode;
    private bool _refreshingNibMode;
    public const double PreviewWidth = 268;
    private readonly PenStrokePreview _penPreview = new() { PreviewHeight = PenStrokePreview.StandardHeight };
    private readonly PenStrokePreview _highlighterPreview = new() { PreviewHeight = 96 };
    private readonly PenStylePicker _styles = new();
    private readonly PointerSlider _width = new() { Minimum = 1, StepFrequency = 1, Width = 106, MinHeight = 0, Height = 28 };
    private readonly PointerSlider _highlighterWidth = new() { Minimum = 1, Maximum = OriginalMenuCatalog.HighlighterWidthMaximum, StepFrequency = 1, MinHeight = 28 };
    private readonly DeferredSettingsChange _sliderChanges;
    private bool _refreshingWidth;
    private readonly PointerSlider _alpha = new()
    {
        Minimum = 0, Maximum = 100, StepFrequency = 1, MinHeight = 0, Height = 28,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _alphaValue = new() { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private bool _refreshingAlpha;
    public event Action SettingsChanged;
    public event Action ColorPicked;
    public PopupHeader Header { get; }

    public BoardPenPalette(InkController controller, AppSettings settings, int theme)
    {
        _controller = controller; _settings = settings;
        _sliderChanges = new DeferredSettingsChange(this, () => SettingsChanged?.Invoke(),
            () => _width.IsAdjusting || _highlighterWidth.IsAdjusting || _alpha.IsAdjusting);
        _width.AdjustmentCompleted += _sliderChanges.Flush;
        _highlighterWidth.AdjustmentCompleted += _sliderChanges.Flush;
        _alpha.AdjustmentCompleted += _sliderChanges.Flush;
        _penPreview.ApplyTheme(theme); _highlighterPreview.ApplyTheme(theme);
        _styles.Update(theme);
        _foreground = new(theme == 2 ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        var columns = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 10, 12, 12) };
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(PreviewWidth) });
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(176) });
        var pen = new StackPanel { Spacing = 6 };
        var options = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        options.ColumnDefinitions.Add(new() { Width = new GridLength(150) });
        options.ColumnDefinitions.Add(new() { Width = new GridLength(110) });
        options.RowDefinitions.Add(new() { Height = GridLength.Auto }); options.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _styles.SelectedIndex = Math.Clamp(settings.Canvas.InkStyle, 0, OriginalMenuCatalog.PenStyles.Count - 1);
        _styles.SelectionChanged += () =>
        {
            if (_refreshingWidth) return;
            settings.Canvas.InkStyle = _styles.SelectedIndex;
            _controller.CurrentAttributes.TaperKind = OriginalMenuCatalog.ToTaperKind(_styles.SelectedIndex);
            NotifySettingsChanged();
            RefreshWidth();
        };
        Add(options, Text("书写笔迹", 12), 0, 0);
        var nibMode = Toggle("笔尖模式", settings.Startup.IsEnableNibMode, value => settings.Startup.IsEnableNibMode = value);
        _nibModeRow = nibMode;
        _nibMode = ((StackPanel)nibMode).Children.OfType<ToggleSwitch>().Single();
        nibMode.Visibility = settings.Appearance.IsEnableDisPlayNibModeToggler ? Visibility.Visible : Visibility.Collapsed;
        Add(options, nibMode, 0, 1);
        AutomationProperties.SetName(_width, "画笔粗细");
        RefreshWidth();
        _width.ValueChanged += (_, e) =>
        {
            if (_refreshingWidth) return;
            if (_controller.CurrentAttributes.TaperKind == TaperKind.InkWash)
            {
                settings.Canvas.InkWashWidth = e.NewValue;
                _controller.InkWashWidth = (float)e.NewValue;
            }
            else
            {
                settings.Canvas.InkWidth = e.NewValue / 2;
                _controller.CurrentAttributes.BaseWidth = (float)(e.NewValue / 2);
            }
            NotifySliderChanged();
        };
        Add(options, Row("粗细", _width), 1, 0);
        Add(options, Toggle("墨迹识别", settings.InkToShape.IsInkToShapeEnabled, value => settings.InkToShape.IsInkToShapeEnabled = value), 1, 1);
        pen.Children.Add(options);
        var nib = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, FontSize = 12 };
        foreach (string name in OriginalMenuCatalog.PenNibs) nib.Items.Add(name);
        nib.SelectedIndex = Enum.IsDefined(settings.Canvas.PenNib) ? (int)settings.Canvas.PenNib : 0;
        AutomationProperties.SetName(nib, "笔尖");
        nib.SelectionChanged += (_, _) =>
        {
            if (nib.SelectedIndex < 0) return;
            settings.Canvas.PenNib = (PenNib)nib.SelectedIndex;
            controller.CurrentAttributes.Nib = settings.Canvas.PenNib;
            NotifySettingsChanged();
        };
        var nibRow = new Grid { ColumnSpacing = 8 };
        nibRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        nibRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Add(nibRow, Text("笔尖", 12), 0, 0); Add(nibRow, nib, 0, 1);
        pen.Children.Add(nibRow);
        pen.Children.Add(_penPreview);
        _penColors = new InkColorPalette(settings.Canvas, false, theme == 1, compact: true);
        _penColors.ApplyTheme(theme);
        _penColors.ColorPicked += color => PickColor(color, false);
        _penColors.PaletteChanged += NotifySettingsChanged;
        pen.Children.Add(_penColors);
        pen.Children.Add(_styles);
        var highlighter = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        highlighter.Children.Add(Text("荧光笔", 13));
        highlighter.Children.Add(Text("粗细", 10));
        var highlighterWidth = _highlighterWidth;
        highlighterWidth.Value = Math.Clamp((settings.Canvas.HighlighterWidth ?? settings.Canvas.InkWidth) * 2, 1, OriginalMenuCatalog.HighlighterWidthMaximum);
        AutomationProperties.SetName(highlighterWidth, "荧光笔粗细");
        highlighterWidth.ValueChanged += (_, args) =>
        {
            settings.Canvas.HighlighterWidth = args.NewValue / 2;
            NotifySliderChanged();
        };
        highlighter.Children.Add(highlighterWidth);
        var alphaLabel = new Grid();
        alphaLabel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        alphaLabel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _alphaValue.FontSize = 10;
        _alphaValue.Foreground = _foreground;
        Add(alphaLabel, Text("不透明度", 10), 0, 0);
        Add(alphaLabel, _alphaValue, 0, 1);
        highlighter.Children.Add(alphaLabel);
        AutomationProperties.SetName(_alpha, "荧光笔不透明度");
        AutomationProperties.SetHelpText(_alpha, "范围 0% 到 100%，步长 1%");
        RefreshAlpha();
        _alpha.ValueChanged += (_, e) =>
        {
            if (_refreshingAlpha) return;
            settings.Canvas.InkAlpha = OriginalMenuCatalog.ToStoredHighlighterAlpha(e.NewValue);
            _controller.CurrentAttributes.HighlighterAlpha = (byte)settings.Canvas.InkAlpha;
            _alphaValue.Text = e.NewValue.ToString("0") + "%";
            NotifySliderChanged();
        };
        highlighter.Children.Add(_alpha);
        highlighter.Children.Add(_highlighterPreview);
        _highlighterColors = new InkColorPalette(settings.Canvas, true, theme == 1, compact: true);
        _highlighterColors.ApplyTheme(theme);
        _highlighterColors.ColorPicked += color => PickColor(color, true);
        _highlighterColors.PaletteChanged += NotifySettingsChanged;
        highlighter.Children.Add(_highlighterColors);
        Add(columns, pen, 0, 0); Add(columns, highlighter, 0, 1);
        var content = new StackPanel();
        Header = new PopupHeader("墨迹选项", theme);
        content.Children.Add(Header);
        content.Children.Add(columns);
        Content = new Border { Width = 482, Background = new SolidColorBrush(theme == 2 ? Windows.UI.Color.FromArgb(191, 0, 0, 0) : Windows.UI.Color.FromArgb(191, 255, 255, 255)),
            BorderBrush = Theming.PopupBrushes.ToolPanelBorder(theme), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = content };
        RefreshSwatches();
    }

    private FrameworkElement Toggle(string name, bool value, Action<bool> update)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0, Width = 44,
            RenderTransform = new ScaleTransform { ScaleX = 0.8, ScaleY = 0.8 }, RenderTransformOrigin = new(0, 0.5) };
        AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) => { if (!_refreshingNibMode) { update(toggle.IsOn); NotifySettingsChanged(); } };
        return Row(name, toggle);
    }

    public void RefreshNibMode()
    {
        _refreshingNibMode = true;
        try
        {
            _nibMode.IsOn = _settings.Startup.IsEnableNibMode;
            _nibModeRow.Visibility = _settings.Appearance.IsEnableDisPlayNibModeToggler ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _refreshingNibMode = false; }
    }
    public void RefreshSettings()
    {
        RefreshNibMode();
        RefreshWidth();
        RefreshAlpha();
        RefreshSwatches();
    }
    private void RefreshAlpha()
    {
        _refreshingAlpha = true;
        try
        {
            _alpha.Value = OriginalMenuCatalog.ToHighlighterOpacityPercent(_settings.Canvas.InkAlpha);
            _alphaValue.Text = _alpha.Value.ToString("0") + "%";
        }
        finally { _refreshingAlpha = false; }
    }
    private StackPanel Row(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Text(label, 12)); row.Children.Add(control); return row;
    }
    private TextBlock Text(string text, double size) => new() { Text = text, FontSize = size, Foreground = _foreground, VerticalAlignment = VerticalAlignment.Center };
    public bool IsEditingColor => _penColors.IsEditingColor || _highlighterColors.IsEditingColor;
    public Func<FrameworkElement, InkColor, Action<InkColor>, Task> EditColorAsync
    {
        set { _penColors.EditColorAsync = value; _highlighterColors.EditColorAsync = value; }
    }

    private void PickColor(InkColor color, bool highlighter)
    {
        _controller.Tool = highlighter ? InkTool.Highlighter : InkTool.Pen;
        _controller.CurrentAttributes.Color = color;
        RefreshSwatches();
        ColorPicked?.Invoke();
    }

    private void NotifySettingsChanged()
    {
        SettingsChanged?.Invoke();
        RefreshSwatches();
    }
    private void NotifySliderChanged()
    {
        _controller.ConfigurePenWidths((float)_settings.Canvas.InkWidth,
            (float)(_settings.Canvas.HighlighterWidth ?? _settings.Canvas.InkWidth));
        RefreshPreviews();
        _sliderChanges.Schedule();
    }

    public void FlushSliderChanges() => _sliderChanges.Flush();
    private void RefreshWidth()
    {
        _refreshingWidth = true;
        try
        {
            _styles.SelectedIndex = OriginalMenuCatalog.ToPenStyle(_controller.CurrentAttributes.TaperKind);
            bool waterInk = _controller.CurrentAttributes.TaperKind == TaperKind.InkWash;
            _width.Maximum = waterInk ? 120 : OriginalMenuCatalog.PenWidthMaximum;
            _width.Value = Math.Clamp(waterInk ? _settings.Canvas.InkWashWidth : _settings.Canvas.InkWidth * 2, 1, _width.Maximum);
            AutomationProperties.SetHelpText(_width, waterInk ? "水墨笔刷宽度，范围 1 到 120 像素" : "画笔粗细，范围 1 到 30，步长 1");
        }
        finally { _refreshingWidth = false; }
    }
    private void RefreshPreviews()
    {
        var pen = _controller.GetDrawingAttributes(false);
        if (pen.EffectiveTaper != TaperKind.InkWash) pen.BaseWidth = (float)_settings.Canvas.InkWidth;
        _penPreview.Update(pen, false);
        var highlighter = _controller.CurrentAttributes.Clone();
        highlighter.BaseWidth = (float)(_settings.Canvas.HighlighterWidth ?? _settings.Canvas.InkWidth);
        _highlighterPreview.Update(highlighter, true);
    }
    private void RefreshSwatches()
    {
        RefreshPreviews();
        _penColors.Refresh(_controller.GetDrawingAttributes(false).Color, _settings.Canvas);
        _highlighterColors.Refresh(_controller.GetDrawingAttributes(true).Color, _settings.Canvas);
    }
    private static void Add(Grid grid, FrameworkElement element, int row, int column)
    { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
}
