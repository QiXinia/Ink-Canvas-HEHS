using InkCanvas.Controls.Controls;
using InkCanvas.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using InkCanvas.Platform.System;
using InkCanvas.Controls.Lifecycle;

namespace InkCanvas.App.Views;

/// <summary>
/// Borderless, topmost, semi-transparent WinUI 3 window hosting ONLY the floating bar row.
/// Draggable by its background. The 更多设置 / 手势选项 popups live in separate
/// <see cref="BarPanelWindow"/>s so opening them never resizes or shifts this row.
/// </summary>
public sealed class FloatingBarWindow : Window
{
    private readonly FloatingBar _bar = new();
    private readonly Viewbox _scaledBar;
    private readonly Border _root;
    private readonly List<FoldedBarHandleWindow> _foldHandles = new();
    private readonly Dictionary<BarPanelKind, BarPanelWindow> _panels = new();
    private bool _isFolded;
    private bool _userPlaced;
    private double _bottomMargin = 100, _scalePercent = 100;
    private bool _initialDpiPlacementDone;
    private double _lastDpiScale = 1;
    private XamlRoot _trackedXamlRoot;
    private readonly WindowIdleResources _idleResources;

    public FloatingBarWindow()
    {
        Title = "Ink Canvas 工具栏";
        _scaledBar = new Viewbox
        {
            Child = _bar,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(0),
            Child = _scaledBar,
        };
        Content = _root;

        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        EnableTransparency(this);
        WindowChrome.ClipToSurfaces(this, () => _bar.ChromeSurfaces);
        _idleResources = new WindowIdleResources(this, () => SystemBackdrop = null, () =>
        {
            if (SystemBackdrop is null) EnableTransparency(this);
            UpdateAcrylicTint(this, _bar.Theme);
        });
        WindowChrome.MoveToMonitor(this, ScreenService.AtCursor());
        WindowChrome.EnableDragMove(this, _root, () => { _userPlaced = true; ClosePanels(); }, fromAnywhere: true,
            onDragCompleted: RefreshDisplayPlacement);
        AttachTapRouting();
        _root.Loaded += (_, _) =>
        {
            AttachDpiTracking();
            if (!_initialDpiPlacementDone)
            {
                _initialDpiPlacementDone = true;
                Reposition(_bottomMargin, _scalePercent);
                UpdateAcrylicTint(this, _bar.Theme);
            }
        };

        _bar.LayoutUpdated += OnBarLayoutUpdated;
        _bar.MainCollapseAnimationChanged += OnMainCollapseAnimationChanged;
        _bar.PanelRequested += OnPanelRequested;
        _bar.SwitchSideRequested += SwitchSide;
        _bar.ThemeRefreshed += () => UpdateAcrylicTint(this, _bar.Theme);

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (!_initialDpiPlacementDone)
            {
                _initialDpiPlacementDone = true;
                Reposition(_bottomMargin, _scalePercent);
                UpdateAcrylicTint(this, _bar.Theme);
            }
        };
        Closed += (_, _) =>
        {
            _bar.FinishMainCollapseAnimation();
            if (_trackedXamlRoot is not null) _trackedXamlRoot.Changed -= OnXamlRootChanged;
            _bar.LayoutUpdated -= OnBarLayoutUpdated;
            _bar.MainCollapseAnimationChanged -= OnMainCollapseAnimationChanged;
            _bar.PanelRequested -= OnPanelRequested;
            _bar.SwitchSideRequested -= SwitchSide;
            ClosePanels(includeDetached: true);
            CloseFoldHandles();
        };
    }

    internal double UserScale => Math.Clamp(_scalePercent, 50, 200) / 100.0;

    private void SwitchSide()
    {
        var work = WindowChrome.GetWorkArea(this);
        var position = AppWindow.Position;
        int width = AppWindow.Size.Width;
        int mirrored = work.X + work.Width - (position.X - work.X) - width;
        if (Math.Abs(mirrored - position.X) < 2) mirrored = work.X + work.Width - width - 12;
        _userPlaced = true;
        ClosePanels();
        WindowChrome.Place(this, Math.Clamp(mirrored, work.X, work.X + Math.Max(0, work.Width - width)),
            position.Y, width, AppWindow.Size.Height);
    }

    private void AttachDpiTracking()
    {
        if (_trackedXamlRoot is not null) _trackedXamlRoot.Changed -= OnXamlRootChanged;
        _trackedXamlRoot = _root.XamlRoot;
        if (_trackedXamlRoot is not null) _trackedXamlRoot.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double newScale = WindowChrome.GetDpiScale(this);
        if (Math.Abs(newScale - _lastDpiScale) < 0.001) return;
        _lastDpiScale = newScale;
        Reposition(_bottomMargin, _scalePercent, recenter: false);
    }

    public FloatingBar Bar => _bar;

    public Windows.Graphics.RectInt32 GetToolAnchor(InkCanvas.Ink.InkTool tool)
        => WindowChrome.GetScreenBounds(this, _bar.GetToolButton(tool));

    public bool IsFolded => _isFolded;
    internal IReadOnlyList<FoldedBarHandleWindow> FoldHandles => _foldHandles;

    public void SetFolded(bool folded)
    {
        if (_isFolded == folded) return;
        _isFolded = folded;
        if (folded)
        {
            _bar.FinishMainCollapseAnimation();
            ClosePanels();
            CloseFoldHandles();
            AppWindow.Hide();
            CreateFoldHandles();
            _bar.SetFoldedVisual(true);
        }
        else
        {
            CloseFoldHandles();
            _bar.SetFoldedVisual(false);
            Reposition(_bottomMargin, _scalePercent, recenter: false);
            WindowChrome.ShowWithoutActivation(this);
        }
    }

    private void CreateFoldHandles()
    {
        var screen = ScreenService.FromWindow(WindowChrome.GetHwnd(this));
        var work = screen.WorkArea;
        int width = (int)Math.Ceiling(30 * screen.DpiScale), height = (int)Math.Ceiling(50 * screen.DpiScale);
        int y = work.Y + (work.Height - height) / 2;
        // Keep the whole HWND on this display. Off-screen handles overlap adjacent monitors.
        _foldHandles.Add(new FoldedBarHandleWindow(this, true, width, height, work.X, y));
        _foldHandles.Add(new FoldedBarHandleWindow(this, false, width, height, work.X + work.Width - width, y));
        foreach (var handle in _foldHandles) handle.ShowHandle();
    }

    public void RefreshDisplayPlacement()
    {
        Reposition(_bottomMargin, _scalePercent, recenter: false);
        if (_isFolded)
        {
            CloseFoldHandles();
            CreateFoldHandles();
        }
    }

    private void CloseFoldHandles()
    {
        foreach (var handle in _foldHandles.ToArray()) handle.Close();
        _foldHandles.Clear();
    }

    public void Configure(InkCanvas.Ink.InkController controller, IShellActions shell)
    {
        _bar.Controller = controller;
        _bar.Shell = shell;
        _bar.Build();
    }

    public void Reposition(double bottomMargin, double scalePercent, bool recenter = true)
    {
        _bottomMargin = bottomMargin;
        _scalePercent = scalePercent;
        if (recenter) _userPlaced = false;
        double dpiScale = WindowChrome.GetDpiScale(this);
        _lastDpiScale = dpiScale;
        var (widthPx, heightPx, _) = GetPhysicalSize(dpiScale);
        PlaceBottomCentre(widthPx, heightPx, dpiScale, keepPosition: !recenter && _initialDpiPlacementDone);
        WindowChrome.EnsureAbove(this);
        ReanchorPanels();
    }

    private (int Width, int Height, double DpiScale) GetPhysicalSize(double dpiScale)
    {
        double userScale = UserScale;
        var (widthDip, heightDip) = _bar.ComputeLayoutSize();
        var expanded = _bar.ComputeLayoutSize(expanded: true);
        if (_bar.IsMainCollapseAnimating)
        {
            widthDip = expanded.Width;
            _bar.Width = widthDip;
        }
        else _bar.Width = double.NaN;
        var work = WindowChrome.GetWorkArea(this);
        userScale = Math.Min(userScale, Math.Min(work.Width / (expanded.Width * dpiScale), work.Height / (heightDip * dpiScale)));
        // Viewbox measures the design at its natural size before scaling. RenderTransform
        // scales after layout, so small windows clip the bar before the transform is applied.
        _scaledBar.Width = widthDip * userScale;
        _scaledBar.Height = heightDip * userScale;
        int widthPx = (int)Math.Ceiling(widthDip * userScale * dpiScale);
        int heightPx = (int)Math.Ceiling(heightDip * userScale * dpiScale);
        return (widthPx, heightPx, dpiScale);
    }

    private void PlaceBottomCentre(int width, int height, double dpiScale, bool keepPosition = false)
    {
        var work = WindowChrome.GetWorkArea(this);
        bool keep = keepPosition || _userPlaced || (_isFolded && _foldHandles.Count > 0);
        var current = WindowChrome.GetClientPosition(this);
        int x = keep ? current.X : work.X + (work.Width - width) / 2;
        int y = keep ? current.Y
            : work.Y + work.Height - height - (int)Math.Ceiling(Math.Max(0, _bottomMargin) * dpiScale);
        var fit = new ScreenRect(x, y, width, height).FitInside(new(work.X, work.Y, work.Width, work.Height));
        WindowChrome.PlaceClient(this, fit.X, fit.Y, fit.Width, fit.Height);
    }

    private int _lastButtons = -1, _lastSeparators = -1;
    private bool _lastExpanded, _lastCollapsed;
    private double _lastMainExpansion = -1;

    private void OnMainCollapseAnimationChanged()
    {
        if (!_initialDpiPlacementDone) return;
        double dpi = WindowChrome.GetDpiScale(this);
        var size = GetPhysicalSize(dpi);
        PlaceBottomCentre(size.Width, size.Height, dpi, keepPosition: true);
    }

    private void OnBarLayoutUpdated(object sender, object e)
    {
        if (!_initialDpiPlacementDone) return;
        var (buttons, separators) = _bar.CountVisibleItems();
        bool expanded = _bar.IsExpanded;
        bool collapsed = _bar.IsMainCollapsed;
        double mainExpansion = _bar.MainExpansion;
        if (buttons == _lastButtons && separators == _lastSeparators && expanded == _lastExpanded && collapsed == _lastCollapsed
            && mainExpansion == _lastMainExpansion) return;
        _lastButtons = buttons;
        _lastSeparators = separators;
        _lastExpanded = expanded;
        _lastCollapsed = collapsed;
        _lastMainExpansion = mainExpansion;
        // The swap chain stays at its expanded size for the entire transition. Only the
        // painted XAML cards and their native region change while the smiley animates.
        if (_bar.IsMainCollapseAnimating) return;
        // Refit in place: keep the left/top edge so the primary buttons never shift; a smiley
        // collapse shrinks the window from the RIGHT only.
        double dpi = WindowChrome.GetDpiScale(this);
        var (w, h, _) = GetPhysicalSize(dpi);
        PlaceBottomCentre(w, h, dpi, keepPosition: true);
        WindowChrome.EnsureAbove(this);
        ReanchorPanels();
    }

    // ---- sub-popups (own windows, anchored above the bar) ----

    private void OnPanelRequested(BarPanelKind kind, bool open)
    {
        if (!open)
        {
            if (_panels.TryGetValue(kind, out var closing)) closing.DismissOnInput();
            return;
        }
        foreach (var other in _panels.Where(pair => pair.Key != kind).Select(pair => pair.Value).ToArray()) other.DismissOnInput();
        if (!_panels.TryGetValue(kind, out var panel))
        {
            panel = new BarPanelWindow(this, _bar, kind);
            _panels.Add(kind, panel);
        }
        var position = AppWindow.Position;
        panel.ShowAboveBar(position.X, position.Y, AppWindow.Size.Width);
    }

    internal void OnPanelClosed(BarPanelKind kind, BarPanelWindow window)
    {
        if (!_panels.TryGetValue(kind, out var current) || !ReferenceEquals(current, window)) return;
        _panels.Remove(kind);
        _bar.NotifyPanelClosed(kind);
    }

    internal BarPanelWindow GetOpenPanel(BarPanelKind kind) => _panels.GetValueOrDefault(kind);

    internal void EnsureHandlesAbove(IntPtr overlay = default)
    {
        foreach (var h in _foldHandles) WindowChrome.EnsureAbove(h, overlay);
        foreach (var panel in _panels.Values) WindowChrome.EnsureAbove(panel, overlay);
    }

    internal void ClosePanels(bool includeDetached = false)
    {
        foreach (var panel in _panels.Values.ToArray())
            if (includeDetached || !panel.IsDetached) panel.Close();
    }

    internal void DismissPanelsOnInput()
    {
        foreach (var panel in _panels.Values.ToArray()) panel.DismissOnInput();
    }

    private void ReanchorPanels()
    {
        var position = AppWindow.Position;
        foreach (var panel in _panels.Values.ToArray())
            panel.PlaceAboveBar(position.X, position.Y, AppWindow.Size.Width);
    }

    // ---- tap routing for smiley / PPT plate ----

    private DependencyObject _tapSource;
    private Windows.Foundation.Point _tapPos;

    private void AttachTapRouting()
    {
        _root.PointerPressed += (_, e) =>
        {
            _tapSource = e.OriginalSource as DependencyObject;
            _tapPos = e.GetCurrentPoint(_root).Position;
        };
        _root.PointerReleased += (_, e) =>
        {
            var source = _tapSource;
            _tapSource = null;
            if (source is null) return;
            var p = e.GetCurrentPoint(_root).Position;
            if (Math.Abs(p.X - _tapPos.X) <= 10 && Math.Abs(p.Y - _tapPos.Y) <= 10)
                _bar.TryHandleTap(source);
        };
        _root.PointerCanceled += (_, _) => _tapSource = null;
    }

    /// <summary>Use themed acrylic with an opaque card fallback when desktop composition is unavailable.</summary>
    public static void EnableTransparency(Window window)
    {
        var backdrop = new ChromeBackdrop();
        int theme = (window.Content as FrameworkElement)?.RequestedTheme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        backdrop.SetTheme(theme);
        window.SystemBackdrop = backdrop;
    }

    /// <summary>
    /// Keep the material and its fallback in the same theme as the painted surface.
    /// </summary>
    public static void UpdateAcrylicTint(Window window, int theme)
    {
        (window.SystemBackdrop as ChromeBackdrop)?.SetTheme(theme);
    }
}
