using InkCanvas.Controls.Theming;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace InkCanvas.Controls.Controls;

/// <summary>The shared second-level eraser menu for desktop annotation and the board.</summary>
public sealed class EraserPalette : UserControl
{
    private readonly List<(InkEraserMode Mode, TouchIconButton Button)> _modes = new();
    private readonly PointerSlider _size = new()
    {
        Minimum = OriginalMenuCatalog.EraserDiameterMinimum,
        Maximum = OriginalMenuCatalog.EraserDiameterMaximum,
        StepFrequency = 1, MinHeight = 32, AllowFocusOnInteraction = false,
    };
    private readonly Ellipse _preview = new()
    {
        StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _sizeLabel = new() { Text = "直径", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _previewBorder = new()
    {
        Height = PenStrokePreview.StandardHeight, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
    };
    private readonly TextBlock _sizeValue = new() { MinWidth = 50, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly DeferredSettingsChange _sizeChanges;
    public event Action<InkEraserMode> ModePicked;
    public event Action<double> DiameterChanged;
    public event Action SettingsChanged;
    public event Action ClearRequested;

    public EraserPalette(InkController controller, int size, int theme, bool includeClear = false)
        : this(controller, OriginalMenuCatalog.ToEraserRadius(size) * 2d, theme, includeClear) { }

    public EraserPalette(InkController controller, double diameter, int theme, bool includeClear = false)
        : this(controller, diameter, theme, PenPalette.PreviewWidth, includeClear) { }

    public EraserPalette(InkController controller, double diameter, int theme, double previewWidth, bool includeClear = false)
    {
        _sizeChanges = new DeferredSettingsChange(this, () => SettingsChanged?.Invoke(), () => _size.IsAdjusting);
        _size.AdjustmentCompleted += _sizeChanges.Flush;
        var foreground = PopupBrushes.FloatBarForeground(theme);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (mode, label, badge) in new[]
        {
            (InkEraserMode.Area, "面积擦", Glyphs.EraserOverlayCircle),
            (InkEraserMode.WholeStroke, "墨迹擦", Glyphs.EraserOverlayStroke),
            (InkEraserMode.Lasso, "套索擦", Glyphs.Select),
        })
        {
            var button = new TouchIconButton
            {
                Glyph = Glyphs.Eraser, Label = label, Width = 68, Height = 62, Padding = new Thickness(2),
                IconSize = 24, LabelFontSize = 12, ContentForeground = foreground,
                SelectionBackground = PopupBrushes.FloatBarSelected(),
                HoverBackground = PopupBrushes.FloatBarButtonHover(theme),
                PressedBackground = PopupBrushes.FloatBarButtonPressed(theme),
                IsSelected = controller.EraserMode == mode,
            };
            var icon = new Grid { Width = 30, Height = 30 };
            var eraser = new FontIcon { Glyph = Glyphs.Eraser, FontFamily = new(IconFontStyle.Family), FontSize = 24 };
            var overlay = new FontIcon { Glyph = badge, FontFamily = eraser.FontFamily, FontSize = mode == InkEraserMode.WholeStroke ? 10 : 14,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            icon.Children.Add(eraser); icon.Children.Add(overlay);
            button.SetContent(icon); button.AddFontIcon(eraser); button.AddFontIcon(overlay);
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                FlushSizeChanges();
                foreach (var item in _modes) item.Button.IsSelected = item.Mode == mode;
                ModePicked?.Invoke(mode);
            };
            _modes.Add((mode, button)); modes.Children.Add(button);
        }
        _size.Value = double.IsFinite(diameter)
            ? Math.Clamp(diameter, _size.Minimum, _size.Maximum) : 90;
        AutomationProperties.SetName(_size, "面积擦大小");
        AutomationProperties.SetHelpText(_size, "擦除直径，范围 10 到 200 像素，步长 1 像素");
        _sizeValue.Foreground = foreground;
        _preview.Stroke = foreground;
        _preview.Fill = PopupBrushes.FloatBarButtonHover(theme);
        AutomationProperties.SetName(_preview, "面积擦范围预览");
        RefreshPreview();
        _size.ValueChanged += (_, args) =>
        {
            RefreshPreview();
            DiameterChanged?.Invoke(args.NewValue);
            _sizeChanges.Schedule();
        };
        var sizeRow = new Grid { ColumnSpacing = 8 };
        sizeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sizeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        sizeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sizeRow.Children.Add(_sizeLabel);
        Grid.SetColumn(_size, 1); sizeRow.Children.Add(_size);
        Grid.SetColumn(_sizeValue, 2); sizeRow.Children.Add(_sizeValue);
        var previewSurface = new Grid { IsHitTestVisible = false };
        previewSurface.Children.Add(_preview);
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(12), Width = previewWidth + 24 };
        _previewBorder.Child = previewSurface;
        panel.Children.Add(_previewBorder);
        panel.Children.Add(modes); panel.Children.Add(sizeRow);
        if (includeClear)
        {
            var clear = new Button { Content = "清空画布…", HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = foreground };
            clear.Click += (_, _) => { FlushSizeChanges(); ClearRequested?.Invoke(); };
            panel.Children.Add(clear);
        }
        Content = panel;
        ApplyTheme(theme);
    }

    public void FlushSizeChanges() => _sizeChanges.Flush();

    public void ApplyTheme(int theme)
    {
        var foreground = PopupBrushes.FloatBarForeground(theme);
        _sizeLabel.Foreground = _sizeValue.Foreground = foreground;
        _preview.Stroke = foreground;
        _preview.Fill = PopupBrushes.FloatBarButtonHover(theme);
        _previewBorder.Background = PopupBrushes.FloatBarButtonHover(theme);
        _previewBorder.BorderBrush = PopupBrushes.ToolPanelBorder(theme);
        foreach (var (_, button) in _modes)
        {
            button.ContentForeground = foreground;
            button.HoverBackground = PopupBrushes.FloatBarButtonHover(theme);
            button.PressedBackground = PopupBrushes.FloatBarButtonPressed(theme);
        }
    }

    private void RefreshPreview()
    {
        // Use one display scale for the full slider range, leaving the actual diameter unchanged.
        _preview.Width = _preview.Height = (_previewBorder.Height - 8) * _size.Value / _size.Maximum;
        _sizeValue.Text = _size.Value.ToString("0.#") + " px";
        AutomationProperties.SetHelpText(_preview, "擦除直径 " + _sizeValue.Text);
    }
}
