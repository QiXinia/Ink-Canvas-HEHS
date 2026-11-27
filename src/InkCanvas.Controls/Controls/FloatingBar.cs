using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using InkCanvas.Extensibility;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace InkCanvas.Controls.Controls;

/// <summary>App-level actions the bar triggers but does not own.</summary>
public interface IShellActions
{
    void ToggleBoard();
    void OpenSettings();
    void OpenTimer();
    void OpenLottery();
    void OpenLotteryQuick();
    void SaveStrokes();
    void OpenStrokes();
    void OpenSpotlight() { }
    void OpenMagnifier() { }
    void OpenReplay();
    void OpenGuide();
    void OpenPenPalette(bool highlighter);
    Task EditCustomColorAsync(FrameworkElement anchor, InkCanvas.Core.Ink.InkColor color, Action<InkCanvas.Core.Ink.InkColor> changed)
        => Task.CompletedTask;
    void OpenShapePicker();
    void InsertImage();
    void InsertVideo() { }
    Task ScreenshotAsync();
    bool ClearCanvas() => false;
    bool ClearHandwriting() => false;
    void ToggleFold();
    void ToggleClickThrough();
    void PptNext();
    void PptPrevious();
    void PptExit();
    void PptOpenNavigation();
    void ToggleBlackScreen() { }
    void PptGotoSlide(int oneBasedIndex) { }
    void ToggleSlideNavigator() { }
    void RotatePptNavigationCorner() { }
    void CollapsePptNavigation() { }
    void TogglePptNavigationWindow() { }
    void OpenAiAssistant() { }
    bool AiAssistantAvailable => false;
    void PasteSpotlightToBoard() { }
    void AskAiFromSpotlight() { }
    void Exit();
    IReadOnlyList<ExtensionCommand> ExtensionCommands => Array.Empty<ExtensionCommand>();
    Task ExecuteExtensionCommandAsync(ExtensionCommand command) => Task.CompletedTask;
}

/// <summary>The two WPF sub-popups that live in their own window above the bar.</summary>
public enum BarPanelKind { More, Gesture, Eraser, Selection }

/// <summary>
/// 1:1 visual port of the WPF 浮动批注主窗口 (MainWindow.xaml ViewboxFloatingBar). The original
/// lives in a Viewbox of Height=50 over 36px-tall content (uniform 50/36 scale); every metric is
/// pre-multiplied by <see cref="WpfScale"/> so FloatingBarScale=100 matches WPF on screen.
/// <para>
/// The bar window holds ONLY the row of separate rounded rects: smiley drag handle, main tools
/// box, 手势 box and (in slideshow) the PPT-exit plate, with 2px WPF gaps between them. The 更多设置
/// and 手势选项 popups are separate windows (<see cref="BarPanelKind"/>) raised via
/// <see cref="PanelRequested"/> so opening them never resizes or shifts this row.
/// </para>
/// </summary>
public sealed class FloatingBar : UserControl
{
    /// <summary>WPF Viewbox Height=50 over 36px content.</summary>
    public const double WpfScale = 50.0 / 36.0;
    public const double ChromeHeight = 36 * WpfScale;
    public const double ChromeCornerRadius = 5 * WpfScale;
    public const double ChromeBorderThickness = WpfScale;
    private const double S = WpfScale;
    private const double PartH = ChromeHeight;
    private const double BtnW = 30 * S;           // 41.7
    private const double BtnH = 32 * S;           // 44.4
    private const double IconSz = 16 * S;         // 22.2
    private const double LabelSz = 8 * S;         // 11.1
    private const double PartCorner = ChromeCornerRadius;
    private const double PartBorder = ChromeBorderThickness;
    private const double PartGap = 2 * S;         // 2.78 inter-rect gap (WPF Margin 2,0,0,0)
    private const double MainPadH = 4 * S;        // padding(2)+margin(2)
    private const double MainPadV = 1 * S;
    private const double MoreBtn = 52 * S;        // 72.2
    private const double MoreIcon = 22 * S;       // 30.6
    private const double MoreLabel = 9 * S;       // 12.5
    private const double PptExitW = 34 * S;       // 47.2
    private const double GestureRowH = 25 * S;
    private const double GestureRowSpacing = -2.5 * S;

    private readonly StackPanel _primary = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly StackPanel _canvasControls = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly StackPanel _barRow = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly StackPanel _moreRows = new() { Orientation = Orientation.Vertical, Spacing = 0 };
    private readonly ScrollViewer _moreScroll = new() { MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private InsertFlyout _insertFlyout;
    private readonly StackPanel _gestureRows = new() { Orientation = Orientation.Vertical, Spacing = GestureRowSpacing };
    private readonly Border _mainHost, _dragHandle, _gestureBorder, _pptExitBorder;
    private readonly FontIcon _dragIcon;

    private TouchIconButton _cursorButton, _penButton, _highlighterButton, _clearButton;
    private TouchIconButton _eraserButton, _selectButton, _shapeButton;
    private TouchIconButton _undoButton, _redoButton, _clearMouseButton;
    private TouchIconButton _boardButton, _toolsButton, _foldButton, _gestureButton, _hideControlButton, _lrSwitchButton;
    private TouchIconButton _exitButton, _aiButton;
    private readonly List<TouchIconButton> _captioned = new();
    private readonly List<StackPanel> _gestureOptionRows = new();
    private ToggleSwitch _multiTouchSwitch, _translateSwitch, _zoomSwitch, _rotateSwitch;

    private bool _pptMode;
    private bool _mainCollapsed;
    private DispatcherQueueTimer _collapseTimer;
    private long _collapseStarted;
    private double _collapseFrom, _collapseDuration, _expandedMainWidth;
    public double MainExpansion { get; private set; } = 1;
    public bool IsMainCollapseAnimating => _collapseTimer?.IsRunning == true;
    public event Action MainCollapseAnimationChanged;
    private bool _moreOpen, _gestureOpen, _eraserOpen, _selectionOpen;
    private InkController _controller;
    private int _theme = 1;
    private Brush _fg, _bg, _border, _hover, _pressed, _selected;

    public FloatingBar()
    {
        _moreScroll.Content = _moreRows;
        _fg = PopupBrushes.FloatBarForeground(1);
        _bg = PopupBrushes.FloatBarBackground(1);
        _border = PopupBrushes.FloatBarBorder(1);
        _hover = PopupBrushes.FloatBarButtonHover(1);
        _pressed = PopupBrushes.FloatBarButtonPressed(1);
        _selected = PopupBrushes.FloatBarSelected();

        _dragIcon = new FontIcon
        {
            FontFamily = IconFontStyle.Font,
            Glyph = Glyphs.Emoji,
            FontSize = 20 * S,
            Width = 28 * S,
            Height = 22 * S,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dragHandle = MakePart(new Grid { Children = { _dragIcon } }, PartH, PartH);
        ToolTipService.SetToolTip(_dragHandle, "拖动移动工具栏；点击收起/展开主工具栏");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_dragHandle, "拖动移动工具栏");

        _mainHost = MakePart(_primary, double.NaN, PartH);
        _mainHost.Margin = new Thickness(PartGap, 0, 0, 0);
        _mainHost.Padding = new Thickness(2 * S, MainPadV, 2 * S, MainPadV);
        _primary.Margin = new Thickness(2 * S, 0, 2 * S, 0);

        _gestureBorder = MakePart(null, PartH, PartH);
        _gestureBorder.Margin = new Thickness(PartGap, 0, 0, 0);
        _pptExitBorder = MakePart(BuildPptExitIcon(), PptExitW, PartH);
        _pptExitBorder.Margin = new Thickness(PartGap, 0, 0, 0);
        // Slideshow-only plate (WPF): Poll only calls SetPptModeVisible on state CHANGES,
        // so the initial state must be collapsed here or the plate leaks into normal mode.
        _pptExitBorder.Visibility = Visibility.Collapsed;
        _pptExitBorder.CornerRadius = new CornerRadius(4 * S);
        ToolTipService.SetToolTip(_pptExitBorder, "退出放映");

        _barRow.Children.Add(_dragHandle);
        _barRow.Children.Add(_mainHost);
        _barRow.Children.Add(_gestureBorder);
        _barRow.Children.Add(_pptExitBorder);
        _barRow.HorizontalAlignment = HorizontalAlignment.Left;
        _barRow.VerticalAlignment = VerticalAlignment.Top;

        Content = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Child = _barRow,
        };

        Loaded += (_, _) => Motion.PlayEntrance(this);
        Unloaded += (_, _) => FinishMainCollapseAnimation();
        ActualThemeChanged += (_, _) => ApplyThemeBrushes();
    }

    /// <summary>Live settings source; the shell assigns Services.Settings. Null = WPF defaults.</summary>
    public AppSettings Settings { get; set; }

    /// <summary>Raised when a sub-popup should open/close in its own window above the bar.</summary>
    public event Action<BarPanelKind, bool> PanelRequested;

    /// <summary>Raised after theme brushes are recomputed so popup chrome can follow.</summary>
    public event Action ThemeRefreshed;
    public event Action SettingsEdited;

    public InkController Controller
    {
        get => _controller;
        set
        {
            if (ReferenceEquals(_controller, value)) return;
            if (_controller is not null)
            {
                _controller.SelectionChanged -= OnControllerSelectionChanged;
                _controller.History.Changed -= OnHistoryChanged;
            }
            _controller = value;
            if (_controller is not null)
            {
                _controller.SelectionChanged += OnControllerSelectionChanged;
                _controller.History.Changed += OnHistoryChanged;
            }
            RefreshSelection();
            LayoutChanged?.Invoke();
        }
    }

    public event Action LayoutChanged;
    public event Action SwitchSideRequested;

    private void OnControllerSelectionChanged()
    {
        RefreshSelection();
        LayoutChanged?.Invoke();
    }

    private void OnHistoryChanged() => RefreshUndoRedo();

    public IShellActions Shell { get; set; }

    public bool IsExpanded => _moreOpen;

    public bool IsMainCollapsed => _mainCollapsed;

    /// <summary>Called by the host when a popup window closed on its own (outside click/Escape).</summary>
    public void NotifyPanelClosed(BarPanelKind kind)
    {
        if (kind == BarPanelKind.More) { _moreOpen = false; _insertFlyout?.Hide(); _insertFlyout = null; }
        else if (kind == BarPanelKind.Gesture) _gestureOpen = false;
        else if (kind == BarPanelKind.Selection) _selectionOpen = false;
        else _eraserOpen = false;
    }

    public int Theme => _theme;

    public bool ColorfulBackground => Cfg.Appearance.IsColorfulViewboxFloatingBar;
    public bool ShouldAutoDismissPanel(BarPanelKind kind) => kind == BarPanelKind.More || Cfg.Appearance.AutoDismissAnnotationToolPanels;

    /// <summary>The 更多设置 rows, hosted by <see cref="BarPanelKind.More"/> popup window.</summary>
    public UIElement CreateMorePanelContent() => _moreScroll;

    /// <summary>The 手势选项 rows, hosted by <see cref="BarPanelKind.Gesture"/> popup window.</summary>
    public UIElement CreateGesturePanelContent() => _gestureRows;

    public Func<Func<InkController>, int, UIElement> SelectionExtension { get; set; }

    public UIElement CreateSelectionPanelContent()
    {
        var palette = new SelectionPalette(() => Controller, Theme, SelectionExtension?.Invoke(() => Controller, Theme));
        palette.ModePicked += mode =>
        {
            Cfg.Canvas.SelectionMode = mode;
            SettingsEdited?.Invoke();
        };
        return palette;
    }

    public UIElement CreateEraserPanelContent()
    {
        var palette = new EraserPalette(Controller, OriginalMenuCatalog.GetEraserDiameter(Cfg.Canvas), Theme);
        palette.ModePicked += mode =>
        {
            Cfg.Canvas.EraserType = mode switch
            {
                InkEraserMode.Area => 1,
                InkEraserMode.WholeStroke => 2,
                _ => 3,
            };
            Controller.EraserMode = mode;
            Controller.EraserRadius = OriginalMenuCatalog.ToEraserRadius(Cfg.Canvas);
            SettingsEdited?.Invoke();
            SetTool(InkTool.Eraser);
            if (ShouldAutoDismissPanel(BarPanelKind.Eraser)) PanelRequested?.Invoke(BarPanelKind.Eraser, false);
        };
        palette.DiameterChanged += diameter =>
        {
            Cfg.Canvas.EraserDiameter = diameter;
            Controller.EraserRadius = (float)(diameter / 2);
        };
        palette.SettingsChanged += () => SettingsEdited?.Invoke();
        return palette;
    }

    public void SetFoldedVisual(bool folded)
    {
        if (folded) ClosePanels();
        if (_foldButton is null) return;
        ToolTipService.SetToolTip(_foldButton, folded ? "展开工具栏" : "收纳工具栏到屏幕侧边");
        LayoutChanged?.Invoke();
    }

    private void ClosePanels()
    {
        _insertFlyout?.Hide();
        _insertFlyout = null;
        _moreOpen = false;
        _gestureOpen = false;
        _eraserOpen = false;
        _selectionOpen = false;
        PanelRequested?.Invoke(BarPanelKind.Selection, false);
        PanelRequested?.Invoke(BarPanelKind.Eraser, false);
        PanelRequested?.Invoke(BarPanelKind.More, false);
        PanelRequested?.Invoke(BarPanelKind.Gesture, false);
    }

    /// <summary>Diagnostics: how many buttons the primary row actually holds.</summary>
    public int PrimaryChildCount => _primary.Children.Count;

    /// <summary>
    /// Natural size of the bar row only (the popups live in their own window). Measured
    /// child-by-child so a rooted window cannot feed its constrained size back in.
    /// </summary>
    public (double Width, double Height) ComputeLayoutSize(bool expanded = false)
    {
        double primary = MeasureRow(_primary);
        double mainWidth = primary + 2 * (MainPadH + PartBorder);
        double rowWidth = PartH; // drag handle always present
        double progress = expanded ? 1 : MainExpansion;
        if (expanded || _mainHost.Visibility == Visibility.Visible) rowWidth += progress * (PartGap + mainWidth);
        if (expanded ? ShouldShowGesture : _gestureBorder.Visibility == Visibility.Visible) rowWidth += progress * (PartGap + PartH);
        if (_pptExitBorder.Visibility == Visibility.Visible) rowWidth += PartGap + PptExitW;
        return (rowWidth, PartH);
    }

    /// <summary>Separate painted cards; gaps between them must remain outside the window region.</summary>
    public IEnumerable<Border> ChromeSurfaces => new[] { _dragHandle, _mainHost, _gestureBorder, _pptExitBorder };

    public FrameworkElement GetToolButton(InkTool tool) => tool switch
    {
        InkTool.Highlighter => _highlighterButton,
        InkTool.Shape => _shapeButton,
        InkTool.Select => _selectButton,
        InkTool.Eraser => _eraserButton,
        _ => _penButton,
    };

    public FrameworkElement GetPanelButton(bool more) => GetPanelButton(more ? BarPanelKind.More : BarPanelKind.Gesture);
    public FrameworkElement GetPanelButton(BarPanelKind kind) => kind switch
    {
        BarPanelKind.More => _toolsButton,
        BarPanelKind.Eraser => _eraserButton,
        BarPanelKind.Selection => _selectButton,
        _ => _gestureButton,
    };

    private static double MeasureRow(StackPanel row)
    {
        double total = 0;
        int visible = 0;
        foreach (var child in row.Children)
        {
            if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible) continue;
            double margin = fe.Margin.Left + fe.Margin.Right;
            if (fe is StackPanel nested)
            {
                double nestedWidth = MeasureRow(nested);
                if (nestedWidth <= 0) continue;
                total += nestedWidth + margin;
            }
            else
            {
                double w;
                if (!double.IsNaN(fe.Width) && fe.Width > 0) w = fe.Width;
                else if (fe.ActualWidth > 0) w = fe.ActualWidth;
                else
                {
                    fe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    w = Math.Max(fe.DesiredSize.Width - margin, fe.MinWidth);
                }
                total += w + margin;
            }
            visible++;
        }
        if (visible > 1) total += (visible - 1) * row.Spacing;
        return total;
    }

    /// <summary>
    /// Visible button/separator counts for the bar row. The host window re-fits in place when
    /// this tuple changes (canvas row, smiley collapse, PPT plate, settings gates).
    /// </summary>
    public (int Buttons, int Separators) CountVisibleItems()
    {
        int buttons = 0, separators = 0;
        if (_mainHost.Visibility == Visibility.Visible) Count(_primary, ref buttons, ref separators);
        if (_gestureBorder.Visibility == Visibility.Visible) buttons++;
        if (_pptExitBorder.Visibility == Visibility.Visible) buttons++;
        buttons++; // drag handle
        return (buttons, separators);
    }

    private static void Count(StackPanel row, ref int buttons, ref int separators)
    {
        foreach (var child in row.Children)
        {
            if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible) continue;
            if (fe is StackPanel nested) Count(nested, ref buttons, ref separators);
            else if (fe is Border) separators++;
            else buttons++;
        }
    }

    /// <summary>Diagnostics: natural width computed for the primary row.</summary>
    public double ComputedPrimaryWidth => MeasureRow(_primary);

    public double PrimaryRowActualWidth => _primary.ActualWidth;

    public double SecondaryRowActualWidth => _moreRows.ActualWidth;

    public bool ClickThroughActive { get; set; }

    /// <summary>Build the buttons once the controller and shell are available.</summary>
    public void Build()
    {
        FinishMainCollapseAnimation();
        _primary.Children.Clear();
        _canvasControls.Children.Clear();
        _moreRows.Children.Clear();
        _gestureRows.Children.Clear();
        _captioned.Clear();
        _gestureOptionRows.Clear();
        _badgeBorders.Clear();
        _gestureIcons.Clear();
        _gestureLabels.Clear();

        // ---- primary row, WPF StackPanelFloatingBar order ----
        _cursorButton = AddPrimary(Glyphs.Cursor, "鼠标", "鼠标模式（点击穿透到下方程序）— 快捷键 Alt+Q", () =>
        {
            if (!ClickThroughActive) Shell?.ToggleClickThrough();
            RefreshSelection();
        });
        _cursorButton.Margin = new Thickness(-PartGap, 0, -PartGap, 0);

        _penButton = AddPrimary(Glyphs.PenWpf, "批注", "批注（选中后再次点击打开选项）— 快捷键 Alt+D", () =>
        {
            bool active = !ClickThroughActive && Controller?.Tool == InkTool.Pen;
            SetTool(InkTool.Pen);
            if (active) Shell?.OpenPenPalette(false);
        });
        // The port splits WPF's unified 墨迹选项 palette, so the highlighter gets its own entry.
        _highlighterButton = AddPrimary(Glyphs.HighlightWpf, "荧光笔", "荧光笔（选中后再次点击打开选项）", () =>
        {
            bool active = !ClickThroughActive && Controller?.Tool == InkTool.Highlighter;
            SetTool(InkTool.Highlighter);
            if (active) Shell?.OpenPenPalette(true);
        });
        _highlighterButton.IconFont = IconFontStyle.Family;
        _clearButton = AddPrimary(_primary, Glyphs.Delete, "清空", "单击清屏，长按仅清除手写墨迹 · Ctrl+E", () =>
        {
            if (Shell?.ClearCanvas() != true) Controller?.Clear();
        }, () =>
        {
            if (Shell?.ClearHandwriting() != true) Controller?.ClearHandwriting();
        });

        _aiButton = AddPrimary(Glyphs.Ai, "AI 助手", "打开通用 AI 助手", () => Shell?.OpenAiAssistant());
        _aiButton.Visibility = Shell?.AiAssistantAvailable == true ? Visibility.Visible : Visibility.Collapsed;
        _primary.Children.Add(_canvasControls);
        _eraserButton = AddPrimary(_canvasControls, Glyphs.Eraser, "橡皮", "橡皮（选中后再次点击打开选项）— 快捷键 Alt+E", () =>
        {
            bool active = !ClickThroughActive && Controller?.Tool == InkTool.Eraser;
            SetTool(InkTool.Eraser);
            if (!active) return;
            _eraserOpen = !_eraserOpen;
            PanelRequested?.Invoke(BarPanelKind.Eraser, _eraserOpen);
        });
        _selectButton = AddPrimary(_canvasControls, Glyphs.SelectWpf, "选择", "选择（选中后再次点击全选并打开选项）— 快捷键 Alt+S", () =>
        {
            bool active = !ClickThroughActive && Controller?.Tool == InkTool.Select;
            SetTool(InkTool.Select);
            if (!active) return;
            _selectionOpen = !_selectionOpen;
            if (_selectionOpen) Controller.SelectAll();
            PanelRequested?.Invoke(BarPanelKind.Selection, _selectionOpen);
        });
        _shapeButton = AddPrimary(_canvasControls, Glyphs.ShapesWpf, "图形", "图形（选中后再次点击打开选项）", () =>
        {
            bool active = !ClickThroughActive && Controller?.Tool == InkTool.Shape;
            SetTool(InkTool.Shape);
            if (active) Shell?.OpenShapePicker();
        });
        _undoButton = AddPrimary(_canvasControls, Glyphs.Undo, "撤销", "撤销 — 快捷键 Ctrl+Z", () => { if (Controller?.History.CanUndo == true) Controller.Undo(); });
        _redoButton = AddPrimary(_canvasControls, Glyphs.Redo, "重做", "重做 — 快捷键 Ctrl+Y", () => { if (Controller?.History.CanRedo == true) Controller.Redo(); });
        _clearMouseButton = AddComposite(_canvasControls, Glyphs.Cursor, Glyphs.Delete, 12 * S, "清并鼠", "清空并进入鼠标模式", () =>
        {
            ClearOrDeleteSelection();
            if (!ClickThroughActive) Shell?.ToggleClickThrough();
            RefreshSelection();
        });

        _hideControlButton = AddPrimary(Glyphs.HideControl, "隐藏控制", "收起/展开主工具栏", ToggleMainCollapsed);
        _lrSwitchButton = AddPrimary(Glyphs.LRSwitch, "左右切换", "将工具栏移动到当前屏幕的另一侧", () => SwitchSideRequested?.Invoke());

        _boardButton = AddPrimary(Glyphs.BoardWpf, "画板", "画板 — 快捷键 Alt+B", () => Shell?.ToggleBoard());
        _toolsButton = AddPrimary(Glyphs.Tools, "工具", "更多设置", ToggleOverflow);
        _foldButton = AddPrimary(Glyphs.FoldWpf, "隐藏", "收纳工具栏到屏幕侧边 — 快捷键 Alt+V", () => Shell?.ToggleFold());
        _foldButton.Margin = new Thickness(-PartGap, 0, -PartGap, 0);

        // ---- 手势 box (part of the bar row, WPF EnableTwoFingerGestureBorder) ----
        _gestureButton = new TouchIconButton { Glyph = Glyphs.Gesture, Label = "手势" };
        ConfigureBarButton(_gestureButton, more: false);
        _gestureButton.Width = 34 * S;
        _gestureButton.Height = 34 * S;
        _gestureButton.IconSize = 20 * S;
        // WPF gesture caption Margin 0,-2,0,-1 (MainWindow.xaml EnableTwoFingerGestureBorder).
        _gestureButton.LabelMargin = new Thickness(0, -2 * S, 0, -1 * S);
        ToolTipService.SetToolTip(_gestureButton, "手势选项");
        _gestureButton.Click += (_, _) => ToggleGesturePanel();
        _gestureBorder.Child = _gestureButton;
        _captioned.Add(_gestureButton);

        BuildMorePanel();

        // ---- 手势选项 rows (WPF TwoFingerGestureBorder), hosted in a popup window ----
        _multiTouchSwitch = AddGestureRow(Glyphs.People, "多指书写", v => Gesture.IsEnableMultiTouchMode = v);
        _translateSwitch = AddGestureRow(Glyphs.GestureTranslate, "双指移动", v => Gesture.IsEnableTwoFingerTranslate = v);
        _zoomSwitch = AddGestureRow(Glyphs.GestureZoom, "双指缩放", v => Gesture.IsEnableTwoFingerZoom = v);
        _rotateSwitch = AddGestureRow(Glyphs.GestureRotate, "双指旋转", v => Gesture.IsEnableTwoFingerRotation = v);

        ApplyThemeBrushes();
        RefreshSettingsVisuals();
        RefreshSelection();
    }

    private AppSettings Cfg => Settings ?? _defaultSettings;
    private static readonly AppSettings _defaultSettings = new();
    public GestureSettings GestureSettingsOverride { get; set; }
    public bool SupportsViewRotation { get; set; } = true;
    private bool _syncingGestures;
    private GestureSettings Gesture => GestureSettingsOverride ?? Cfg.Gesture;

    private void ClearOrDeleteSelection()
    {
        if (Controller is null) return;
        if (Controller.HasSelection) Controller.DeleteSelection();
        else if (Shell?.ClearCanvas() != true) Controller.Clear();
    }

    private TouchIconButton AddPrimary(string glyph, string label, string tooltip, Action onClick)
        => AddPrimary(_primary, glyph, label, tooltip, onClick);

    private TouchIconButton AddPrimary(StackPanel host, string glyph, string label, string tooltip, Action onClick, Action onHold = null)
    {
        var button = new TouchIconButton { Glyph = glyph, Label = label };
        ConfigureBarButton(button, more: false);
        if (onHold is null) Wire(button, tooltip, onClick);
        else
        {
            ToolTipService.SetToolTip(button, tooltip);
            ButtonHoldAction.Attach(button, onClick, onHold);
        }
        host.Children.Add(button);
        _captioned.Add(button);
        return button;
    }

    private TouchIconButton AddComposite(StackPanel host, string baseGlyph, string overlayGlyph, double overlaySize, string label, string tooltip, Action onClick)
    {
        var button = new TouchIconButton { Label = label };
        ConfigureBarButton(button, more: false);
        var grid = new Grid { Width = BtnW, Height = IconSz + 4 };
        var baseIcon = NewIcon(baseGlyph, IconSz);
        baseIcon.HorizontalAlignment = HorizontalAlignment.Center;
        baseIcon.VerticalAlignment = VerticalAlignment.Center;
        var overlay = NewIcon(overlayGlyph, overlaySize);
        overlay.HorizontalAlignment = HorizontalAlignment.Right;
        overlay.VerticalAlignment = VerticalAlignment.Bottom;
        overlay.Margin = new Thickness(0, 0, 2 * S, 0);
        grid.Children.Add(baseIcon);
        grid.Children.Add(overlay);
        button.SetContent(grid);
        button.AddFontIcon(baseIcon);
        button.AddFontIcon(overlay);
        Wire(button, tooltip, onClick);
        host.Children.Add(button);
        _captioned.Add(button);
        return button;
    }

    private static FontIcon NewIcon(string glyph, double size) => new()
    {
        FontFamily = IconFontStyle.Font,
        Glyph = glyph,
        FontSize = size,
        IsTextScaleFactorEnabled = false,
    };

    private void ConfigureBarButton(TouchIconButton button, bool more)
    {
        button.Width = more ? MoreBtn : BtnW;
        button.Height = more ? MoreBtn : BtnH;
        button.MinWidth = 0;
        button.MinHeight = 0;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(PartCorner);
        button.SelectionCornerRadius = 4 * S;
        button.IconSize = more ? MoreIcon : IconSz;
        button.LabelFontSize = more ? MoreLabel : LabelSz;
        // WPF centers the glyph in a fixed 22-DIP icon box (30x22 FontIcon) with the caption
        // below inside the cell; the fixed area keeps every icon on the same center line.
        button.IconAreaHeight = (more ? 28 : 22) * S;
        button.LabelMargin = new Thickness(0, -1 * S, 0, 0);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void Wire(TouchIconButton button, string tooltip, Action onClick)
    {
        ToolTipService.SetToolTip(button, tooltip);
        if (onClick is null)
        {
            button.IsEnabled = false;
            button.Opacity = 0.4;
            return;
        }
        button.Click += (_, _) => onClick();
    }

    public void RefreshExtensionCommands()
    {
        var aiVisibility = Shell?.AiAssistantAvailable == true ? Visibility.Visible : Visibility.Collapsed;
        bool aiChanged = _aiButton.Visibility != aiVisibility;
        _aiButton.Visibility = aiVisibility;
        BuildMorePanel();
        ApplyThemeBrushes();
        RefreshSettingsVisuals();
        if (aiChanged) LayoutChanged?.Invoke();
    }

    private void BuildMorePanel()
    {
        _insertFlyout?.Hide();
        _insertFlyout = null;
        _moreRows.Children.Clear();
        _badgeBorders.Clear();
        StackPanel tools = null;
        AddTool(Glyphs.TimerWpf, "倒计时", () => Shell?.OpenTimer());
        AddTool(Glyphs.Rand, "随机抽选", () => Shell?.OpenLottery());
        AddTool(Glyphs.Rand, "单次抽选", () => Shell?.OpenLotteryQuick(), badge: true);
        AddTool(Glyphs.ReplayWpf, "播放墨迹", () => Shell?.OpenReplay());
        TouchIconButton insert = null;
        insert = AddTool(Glyphs.InsertMedia, "插入", () => ShowInsertMenu(insert), closePanel: false);
        AddTool(Glyphs.Camera, "截屏", async () => { if (Shell is not null) await Shell.ScreenshotAsync(); });

        var commands = (Shell?.ExtensionCommands ?? Array.Empty<ExtensionCommand>())
            .Where(command => command.PluginId is not ("inkcanvas.ai-assistant" or "inkcanvas.library")
                && !(command.PluginId == "inkcanvas.pdf" && command.CommandId == "import"));
        foreach (var command in commands)
            AddTool(ExtensionCommandIcons.For(command), command.Title, async () => { if (Shell is not null) await Shell.ExecuteExtensionCommandAsync(command); });

        var files = MoreRow();
        files.Margin = new Thickness(0, 8, 0, 4);
        AddMore(files, Glyphs.SaveStrokes, "保存墨迹", () => Shell?.SaveStrokes());
        AddMore(files, Glyphs.OpenStrokes, "打开文件", () => Shell?.OpenStrokes());
        AddMore(files, Glyphs.Settings, "设置", () => Shell?.OpenSettings());
        _exitButton = AddMore(files, Glyphs.ExitWpf, "退出", () => Shell?.Exit());
        _moreRows.Children.Add(files);

        TouchIconButton AddTool(string glyph, string label, Action action, bool badge = false, bool closePanel = true)
        {
            if (tools is null || tools.Children.Count == 4)
            {
                tools = MoreRow();
                if (_moreRows.Children.Count == 0) tools.Margin = new Thickness(0, 4 * S, 0, 0);
                _moreRows.Children.Add(tools);
            }
            return AddMore(tools, glyph, label, action, badge, closePanel);
        }
    }

    private void ShowInsertMenu(FrameworkElement anchor)
    {
        var pdf = Shell?.ExtensionCommands.FirstOrDefault(command => command.PluginId == "inkcanvas.pdf" && command.CommandId == "import");
        _insertFlyout = new InsertFlyout(_theme,
            () => RunInsertion(() => Shell?.InsertImage()),
            () => RunInsertion(() => Shell?.InsertVideo()),
            pdf is null ? null : () => RunInsertion(async () => { if (Shell is not null) await Shell.ExecuteExtensionCommandAsync(pdf); }));
        _insertFlyout.ShowAt(anchor);
    }

    private void RunInsertion(Action action)
    {
        _moreOpen = false;
        PanelRequested?.Invoke(BarPanelKind.More, false);
        action();
    }

    private StackPanel MoreRow() => new() { Orientation = Orientation.Horizontal, Spacing = 0,
        Width = MoreBtn * 4, Height = MoreBtn, HorizontalAlignment = HorizontalAlignment.Center };

    private TouchIconButton AddMore(StackPanel row, string glyph, string label, Action onClick, bool badge = false, bool closePanel = true)
    {
        var button = new TouchIconButton { Glyph = glyph, Label = label };
        ConfigureBarButton(button, more: true);
        button.ShowLabel = true; // more-panel captions always visible in WPF
        button.LabelMaxWidth = MoreBtn - 6;
        if (badge)
        {
            var grid = new Grid { Width = MoreBtn, Height = MoreIcon };
            var icon = NewIcon(glyph, MoreIcon);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var circle = new Border
            {
                Width = 9 * S,
                Height = 9 * S,
                CornerRadius = new CornerRadius(4.5 * S),
                BorderThickness = new Thickness(PartBorder),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(-2 * S, 0, 0, -2 * S),
                Child = new TextBlock
                {
                    Text = "1",
                    FontSize = 7 * S,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                },
            };
            grid.Children.Add(icon);
            grid.Children.Add(circle);
            button.SetContent(grid);
            button.AddFontIcon(icon);
            _badgeBorders.Add(circle);
        }
        ToolTipService.SetToolTip(button, label);
        if (onClick is null)
        {
            button.IsEnabled = false;
            button.Opacity = 0.4;
        }
        else
        {
            button.Click += (_, _) =>
            {
                // Dismiss the launching popup before an action opens another window.
                if (closePanel)
                {
                    _moreOpen = false;
                    PanelRequested?.Invoke(BarPanelKind.More, false);
                }
                onClick();
            };
        }
        row.Children.Add(button);
        return button;
    }

    private readonly List<Border> _badgeBorders = new();

    private ToggleSwitch AddGestureRow(string glyph, string label, Action<bool> setter)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8 * S,
            Height = GestureRowH,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = NewIcon(glyph, 16 * S);
        icon.Width = 20 * S;
        icon.Height = 20 * S;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = new TextBlock { Text = label, FontSize = 12 * S, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new ToggleSwitch { MinWidth = 0, Width = 42 * S, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
        toggle.Toggled += (_, _) =>
        {
            if (_syncingGestures || !toggle.IsEnabled) return;
            setter(toggle.IsOn);
            if (Controller is not null)
            {
                Controller.MultiTouchEnabled = Gesture.IsEnableMultiTouchMode;
                Controller.ConfigureGestures(Gesture.IsEnableTwoFingerZoom, Gesture.IsEnableTwoFingerTranslate,
                    SupportsViewRotation && Gesture.IsEnableTwoFingerRotation);
            }
            RefreshGestureDim();
            SettingsEdited?.Invoke();
        };
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(toggle);
        _gestureRows.Children.Add(row);
        _gestureOptionRows.Add(row);
        _gestureIcons.Add(icon);
        _gestureLabels.Add(text);
        return toggle;
    }

    private readonly List<FontIcon> _gestureIcons = new();
    private readonly List<TextBlock> _gestureLabels = new();

    private Border MakePart(UIElement child, double width, double height)
    {
        var border = new Border
        {
            Background = _bg,
            BorderBrush = _border,
            BorderThickness = new Thickness(PartBorder),
            CornerRadius = new CornerRadius(PartCorner),
            Child = child,
        };
        if (!double.IsNaN(width)) border.Width = width;
        if (!double.IsNaN(height)) border.Height = height;
        return border;
    }

    private UIElement BuildPptExitIcon()
    {
        // WPF PPTExitNormal DrawingImage: screen outline + X + stand, in a 32x30 box.
        var canvas = new Grid { Width = 32, Height = 30 };
        var outline = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 24,
            Height = 16,
            Stroke = _fg,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3, 3, 0, 0),
        };
        var x = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"M13.5355339,10.9497475L10,14.4852814 11.4142136,15.8994949 14.9497475,12.363961 18.4852814,15.8994949 19.8994949,14.4852814 16.363961,10.9497475 19.8994949,7.41421356 18.4852814,6 14.9497475,9.53553391 11.4142136,6 10,7.41421356 13.5355339,10.9497475z\"/>");
        x.Fill = _fg;
        canvas.Children.Add(outline);
        canvas.Children.Add(x);
        canvas.Children.Add(Rect(14, 19, 2, 8, _fg));
        canvas.Children.Add(Rect(0, 2, 30, 2, _fg));
        canvas.Children.Add(Rect(13, 0, 4, 2, _fg));
        canvas.Children.Add(Rect(7, 26, 16, 2, _fg));
        _pptExitShapes.Add(outline);
        _pptExitShapes.Add(x);
        return new Viewbox { Width = 30 * S, Height = 20 * S, Child = canvas };
    }

    private readonly List<Microsoft.UI.Xaml.Shapes.Shape> _pptExitShapes = new();

    private static Microsoft.UI.Xaml.Shapes.Rectangle Rect(double x, double y, double w, double h, Brush fill) => new()
    {
        Width = w,
        Height = h,
        Fill = fill,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(x, y, 0, 0),
    };

    public void SetPptModeVisible(bool visible)
    {
        _pptMode = visible;
        _pptExitBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RefreshSelection();
        LayoutChanged?.Invoke();
    }

    private void SetTool(InkTool tool)
    {
        if (Controller is null) return;
        if (ClickThroughActive) Shell?.ToggleClickThrough();
        _mainCollapsed = false;
        FinishMainCollapseAnimation();
        Controller.Tool = tool;
        RefreshSelection();
    }

    public void RefreshSelection()
    {
        if (IsMainCollapseAnimating) FinishMainCollapseAnimation();
        if (_selectionOpen && (Controller?.Tool != InkTool.Select || ClickThroughActive))
        {
            _selectionOpen = false;
            PanelRequested?.Invoke(BarPanelKind.Selection, false);
        }
        var tool = Controller?.Tool ?? InkTool.Pen;
        bool ink = !ClickThroughActive;
        if (_penButton is not null) _penButton.IsSelected = ink && tool == InkTool.Pen;
        if (_highlighterButton is not null) _highlighterButton.IsSelected = ink && tool == InkTool.Highlighter;
        if (_eraserButton is not null) _eraserButton.IsSelected = ink && tool == InkTool.Eraser;
        if (_selectButton is not null) _selectButton.IsSelected = ink && tool == InkTool.Select;
        if (_shapeButton is not null) _shapeButton.IsSelected = ink && tool == InkTool.Shape;
        if (_cursorButton is not null) _cursorButton.IsSelected = false;
        var color = Controller?.CurrentAttributes.Color;
        _penButton?.SetInkColorBadge(color);
        _highlighterButton?.SetInkColorBadge(color);
        _shapeButton?.SetInkColorBadge(color);

        if (_canvasControls is not null)
            _canvasControls.Visibility = ClickThroughActive ? Visibility.Collapsed : Visibility.Visible;
        RefreshGestureBorderVisibility();
        RefreshUndoRedo();
        // Surface brushes own transparency. Fading the subtree also fades labels and icons.
        Opacity = 1;
    }

    private void RefreshUndoRedo()
    {
        if (_undoButton is not null) _undoButton.Opacity = Controller?.History.CanUndo == true ? 1 : 0.35;
        if (_redoButton is not null) _redoButton.Opacity = Controller?.History.CanRedo == true ? 1 : 0.35;
    }

    private bool ShouldShowGesture => !ClickThroughActive && !_pptMode && Cfg.Appearance.IsShowModeFingerToggleSwitch;

    private void RefreshGestureBorderVisibility()
    {
        bool show = ShouldShowGesture && (!_mainCollapsed || IsMainCollapseAnimating)
            && _mainHost.Visibility == Visibility.Visible;
        _gestureBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleOverflow()
    {
        bool open = !_moreOpen;
        _moreOpen = open;
        if (open) _gestureOpen = false;
        PanelRequested?.Invoke(BarPanelKind.More, open);
        if (open) PanelRequested?.Invoke(BarPanelKind.Gesture, false);
        if (_toolsButton is not null)
            ToolTipService.SetToolTip(_toolsButton, open ? "收起更多设置" : "更多设置");
    }

    private void ToggleGesturePanel()
    {
        bool open = !_gestureOpen;
        _gestureOpen = open;
        if (open) _moreOpen = false;
        PanelRequested?.Invoke(BarPanelKind.Gesture, open);
        if (open) PanelRequested?.Invoke(BarPanelKind.More, false);
    }

    private void ToggleMainCollapsed()
    {
        _mainCollapsed = !_mainCollapsed;
        if (_mainCollapsed) ClosePanels();
        if (!IsLoaded || !Motion.AnimationsEnabled)
        {
            FinishMainCollapseAnimation();
            return;
        }
        _collapseTimer ??= CreateCollapseTimer();
        _collapseTimer.Stop();
        _collapseFrom = MainExpansion;
        _collapseStarted = Stopwatch.GetTimestamp();
        _collapseDuration = Motion.Normal.TotalMilliseconds * Math.Abs((_mainCollapsed ? 0 : 1) - _collapseFrom);
        if (_collapseDuration <= 0) { FinishMainCollapseAnimation(); return; }
        _expandedMainWidth = MeasureRow(_primary) + 2 * (MainPadH + PartBorder);
        _primary.Width = MeasureRow(_primary);
        _primary.HorizontalAlignment = HorizontalAlignment.Left;
        _mainHost.Visibility = Visibility.Visible;
        _mainHost.IsHitTestVisible = _gestureBorder.IsHitTestVisible = false;
        _collapseTimer.Start();
        MainCollapseAnimationChanged?.Invoke();
        RefreshGestureBorderVisibility();
        ApplyMainExpansion(_collapseFrom);
    }

    private DispatcherQueueTimer CreateCollapseTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) =>
        {
            if (!Motion.AnimationsEnabled) { FinishMainCollapseAnimation(); return; }
            double progress = Math.Clamp(Stopwatch.GetElapsedTime(_collapseStarted).TotalMilliseconds / _collapseDuration, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            ApplyMainExpansion(_collapseFrom + ((_mainCollapsed ? 0 : 1) - _collapseFrom) * eased);
            if (progress >= 1) FinishMainCollapseAnimation();
        };
        return timer;
    }

    private void ApplyMainExpansion(double progress)
    {
        MainExpansion = progress;
        double mainWidth = _expandedMainWidth * progress, gestureWidth = PartH * progress;
        _mainHost.Width = mainWidth;
        _gestureBorder.Width = gestureWidth;
        _mainHost.Margin = _gestureBorder.Margin = new Thickness(PartGap * progress, 0, 0, 0);
        _mainHost.Clip = new RectangleGeometry { Rect = new(0, 0, mainWidth, PartH) };
        _gestureBorder.Clip = new RectangleGeometry { Rect = new(0, 0, gestureWidth, PartH) };
    }

    public void FinishMainCollapseAnimation()
    {
        bool wasAnimating = IsMainCollapseAnimating;
        _collapseTimer?.Stop();
        MainExpansion = _mainCollapsed ? 0 : 1;
        _mainHost.Visibility = _mainCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _mainHost.Width = _primary.Width = double.NaN;
        _gestureBorder.Width = PartH;
        _mainHost.Margin = _gestureBorder.Margin = new Thickness(PartGap, 0, 0, 0);
        _mainHost.Clip = _gestureBorder.Clip = null;
        _mainHost.IsHitTestVisible = _gestureBorder.IsHitTestVisible = true;
        RefreshGestureBorderVisibility();
        if (wasAnimating) MainCollapseAnimationChanged?.Invoke();
    }

    /// <summary>
    /// Tap routing for the non-button framed parts (smiley handle, PPT exit plate). The window
    /// calls this on pointer release when the press started on one of them and moved &lt;=10 DIP.
    /// </summary>
    public bool TryHandleTap(DependencyObject source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, _dragHandle)) { ToggleMainCollapsed(); return true; }
            if (ReferenceEquals(d, _pptExitBorder)) { Shell?.PptExit(); return true; }
        }
        return false;
    }

    private void SyncGestureSwitches()
    {
        if (_multiTouchSwitch is null) return;
        _syncingGestures = true;
        try
        {
        _multiTouchSwitch.IsOn = Gesture.IsEnableMultiTouchMode;
        _translateSwitch.IsOn = Gesture.IsEnableTwoFingerTranslate;
        _zoomSwitch.IsOn = Gesture.IsEnableTwoFingerZoom;
        _rotateSwitch.IsOn = Gesture.IsEnableTwoFingerRotation;
        }
        finally { _syncingGestures = false; }
        RefreshGestureDim();
    }

    private void RefreshGestureDim()
    {
        bool multi = Gesture.IsEnableMultiTouchMode;
        for (int i = 1; i < _gestureOptionRows.Count; i++)
            _gestureOptionRows[i].Opacity = multi ? 0.5 : 1;
        if (_translateSwitch is not null) _translateSwitch.IsEnabled = !multi;
        if (_zoomSwitch is not null) _zoomSwitch.IsEnabled = !multi;
        if (_rotateSwitch is not null) _rotateSwitch.IsEnabled = SupportsViewRotation && !multi;
        if (_gestureButton is not null)
            _gestureButton.Opacity = multi || !Gesture.IsEnableTwoFingerGesture ? 0.5 : 1;
    }

    /// <summary>
    /// Re-read settings after ShellController.OnSettingsChanged: caption text, colourful bar,
    /// transparent button bodies and the IsShow* visibility gates.
    /// </summary>
    public void RefreshSettingsVisuals()
    {
        var a = Cfg.Appearance;
        foreach (var button in _captioned) button.ShowLabel = a.IsEnableDisPlayFloatBarText;
        if (_eraserButton is not null)
            _eraserButton.Visibility = a.IsShowEraserButton ? Visibility.Visible : Visibility.Collapsed;
        if (_exitButton is not null)
            _exitButton.Visibility = a.IsShowExitButton ? Visibility.Visible : Visibility.Collapsed;
        if (_hideControlButton is not null)
            _hideControlButton.Visibility = a.IsShowHideControlButton ? Visibility.Visible : Visibility.Collapsed;
        if (_lrSwitchButton is not null)
            _lrSwitchButton.Visibility = a.IsShowLRSwitchButton ? Visibility.Visible : Visibility.Collapsed;
        ApplyThemeBrushes();
        RefreshGestureBorderVisibility();
        RefreshGestureDim();
        SyncGestureSwitches();
        RefreshSelection();
        LayoutChanged?.Invoke();
    }

    private void ApplyThemeBrushes()
    {
        _theme = ActualTheme switch
        {
            ElementTheme.Dark => 2,
            ElementTheme.Light => 1,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark ? 2 : 1,
        };
        _fg = PopupBrushes.FloatBarForeground(_theme);
        _bg = PopupBrushes.FloatBarBackground(_theme);
        _border = PopupBrushes.FloatBarBorder(_theme);
        _hover = PopupBrushes.FloatBarButtonHover(_theme);
        _pressed = PopupBrushes.FloatBarButtonPressed(_theme);
        _selected = PopupBrushes.FloatBarSelected();
        Brush background = ColorfulBackground ? PopupBrushes.FloatBarColorful(_theme) : _bg;
        Brush buttonBase = Cfg.Appearance.IsTransparentButtonBackground
            ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : new SolidColorBrush(_theme == 2 ? Windows.UI.Color.FromArgb(20, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0));

        foreach (var part in new[] { _dragHandle, _mainHost, _gestureBorder, _pptExitBorder })
        {
            part.Background = background;
            part.BorderBrush = _border;
        }
        _dragIcon.Foreground = _fg;
        foreach (var badge in _badgeBorders)
        {
            badge.BorderBrush = _fg;
            if (badge.Child is TextBlock t) t.Foreground = _fg;
        }
        foreach (var icon in _gestureIcons) icon.Foreground = _fg;
        foreach (var label in _gestureLabels) label.Foreground = _fg;
        foreach (var shape in _pptExitShapes)
        {
            if (shape is Microsoft.UI.Xaml.Shapes.Rectangle r && r.Fill is null) r.Stroke = _fg;
            else shape.Fill = _fg;
        }

        foreach (var button in AllButtons())
        {
            button.ContentForeground = _fg;
            button.SelectionBackground = _selected;
            button.HoverBackground = _hover;
            button.PressedBackground = _pressed;
            button.Background = buttonBase;
            button.ApplyHoverResources(_fg);
            button.RefreshVisual();
        }
        RefreshSelection();
        ThemeRefreshed?.Invoke();
    }

    private IEnumerable<TouchIconButton> AllButtons()
    {
        foreach (var child in _primary.Children)
            if (child is TouchIconButton b) yield return b;
        foreach (var child in _canvasControls.Children)
            if (child is TouchIconButton b) yield return b;
        foreach (var row in _moreRows.Children)
            if (row is StackPanel sp)
                foreach (var child in sp.Children)
                    if (child is TouchIconButton b) yield return b;
        if (_gestureButton is not null) yield return _gestureButton;
    }

    /// <summary>
    /// Shape catalogue shared with the shape picker window and the board flyout, in the WPF
    /// popup's row-major order (BorderDrawShape): lines / coordinates / center-rect+circles+
    /// ellipses / conics (up, with-foci, right) / rectangle+solids. Ellipse (bounding-box) and
    /// Tetrahedron are Collapsed in the original menu and stay hidden here. The visual grid
    /// metrics live in <see cref="ShapeMenuCatalog"/>.
    /// </summary>
    public static readonly (ShapeKind, string)[] ShapeCatalog =
    {
        // Row 1 — lines (long-press sticky row in WPF).
        (ShapeKind.Line, "直线"),
        (ShapeKind.DashedLine, "虚线"),
        (ShapeKind.DotLine, "点线"),
        (ShapeKind.Arrow, "箭头"),
        (ShapeKind.ParallelLine, "平行线"),
        // Row 2 — coordinate systems.
        (ShapeKind.CoordinateBoth, "坐标系（双向）"),
        (ShapeKind.CoordinateXOneWay, "坐标系（x 单向）"),
        (ShapeKind.CoordinateYOneWay, "坐标系（y 单向）"),
        (ShapeKind.CoordinateFirstQuadrant, "坐标系（第一象限）"),
        (ShapeKind.Coordinate3D, "空间坐标系"),
        // Row 3 — center rectangle, circles, center ellipses.
        (ShapeKind.RectangleFromCenter, "中心矩形"),
        (ShapeKind.Circle, "圆"),
        (ShapeKind.DashedCircle, "虚线圆"),
        (ShapeKind.EllipseFromCenter, "中心椭圆"),
        (ShapeKind.EllipseWithFoci, "椭圆（含焦点）"),
        // Row 4 — conics.
        (ShapeKind.Hyperbola, "双曲线"),
        (ShapeKind.HyperbolaWithFoci, "双曲线（含焦点）"),
        (ShapeKind.ParabolaUp, "抛物线（上开口）"),
        (ShapeKind.ParabolaWithFoci, "抛物线（含焦点）"),
        (ShapeKind.ParabolaRight, "抛物线（右开口）"),
        // Row 5 — rectangle and solids.
        (ShapeKind.Rectangle, "矩形"),
        (ShapeKind.Cylinder, "圆柱"),
        (ShapeKind.Cone, "圆锥"),
        (ShapeKind.Cuboid, "长方体"),
    };
}
