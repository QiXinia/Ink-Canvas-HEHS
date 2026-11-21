using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Ppt;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

public enum PptNavigationPlacement { BottomLeft, BottomRight, SideLeft, SideRight }

/// <summary>
/// PPT slideshow navigation panel, corresponding to the four WPF controls
/// <c>PPTNavigationBottomLeft/Right</c> and <c>PPTNavigationSidesLeft/Right</c>.
/// It is an independent WinUI window so it can sit over a slideshow without being clipped by
/// the bottom toolbar's window bounds. A single instance now moves between the four corners
/// and is placed by <see cref="NavigationLayout"/> against the show window's rectangle.
/// </summary>
public sealed class PptNavigationWindow : Window
{
    private const string RotateGlyph = "\uE72C";
    private const string CollapseGlyph = "\uE711";
    private const double IdleOpacity = 0.35;
    private const int ButtonDip = 44;
    private const int SpacingDip = 2;

    private readonly IShellActions _shell;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _counter;
    private readonly Button _penToggle;
    private readonly Button _slideList;
    private readonly Button _rotate;
    private readonly Button _collapse;
    private readonly TextBlock _counterText;
    private readonly StackPanel _panel;
    private readonly Border _root;
    private PptNavigationPlacement _placement;
    private ShowWindowInfo _showBounds;
    private bool _counterVisible = true;
    private bool _closed;
    private XamlRoot _trackedRoot;

    public PptNavigationWindow(PptNavigationPlacement placement, IShellActions shell, int theme)
    {
        _placement = placement;
        _shell = shell;
        Title = "PowerPoint 导航";

        _previous = NavButton(Glyphs.ChevronLeft, "上一页", () => _shell.PptPrevious());
        _next = NavButton(Glyphs.ChevronRight, "下一页", () => _shell.PptNext());
        _counterText = new TextBlock
        {
            Text = "0 / 0",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _counter = new Button
        {
            Content = _counterText,
            MinWidth = 52,
            MinHeight = 40,
            Padding = new Thickness(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(_counter, "打开 PowerPoint 幻灯片导航窗格并切换到鼠标模式");
        _counter.Click += (_, _) => _shell.PptOpenNavigation();

        _penToggle = NavButton(Glyphs.Pen, "切换批注/穿透（鼠标）模式", () => _shell.ToggleClickThrough());
        _slideList = NavButton(Glyphs.SlideList, "课件页导航：按批注/收藏跳页", () => _shell.ToggleSlideNavigator());
        _rotate = NavButton(RotateGlyph, "切换导航栏位置", () => _shell.RotatePptNavigationCorner());
        _collapse = NavButton(CollapseGlyph, "收起导航栏", () => _shell.CollapsePptNavigation());

        _panel = new StackPanel { Spacing = SpacingDip };
        _panel.Children.Add(_previous);
        _panel.Children.Add(_counter);
        _panel.Children.Add(_next);
        _panel.Children.Add(_slideList);
        _panel.Children.Add(_penToggle);
        _panel.Children.Add(_rotate);
        _panel.Children.Add(_collapse);
        ApplyOrientation();

        _root = new Border
        {
            Background = (Brush)Application.Current.Resources["IcSurfaceAltBrush"],
            BorderBrush = (Brush)Application.Current.Resources["IcStrokeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["IcBarCorner"],
            Padding = new Thickness(2),
            Opacity = IdleOpacity,
            Child = _panel,
        };
        // Low opacity at rest so the panel does not obscure slide content; full opacity only
        // while the pointer is over it. Click-through state is deliberately not touched here.
        _root.PointerEntered += (_, _) => _root.Opacity = 1.0;
        _root.PointerExited += (_, _) => _root.Opacity = IdleOpacity;
        Content = _root;

        WindowChrome.MakeBorderless(this, topmost: true);
        WindowChrome.ApplyTheme(this, theme);
        NativeMethods.AddExStyle(WindowChrome.GetHwnd(this), NativeMethods.WS_EX_NOACTIVATE);
        Closed += OnClosed;
        Activated += OnActivated;
        _root.Loaded += (_, _) => TrackRoot();
        Place(_counterVisible);
        SetVisible(false);
    }

    /// <summary>Corner the window is currently docked to; the shell rotates it on demand.</summary>
    public PptNavigationPlacement Placement
    {
        get => _placement;
        set => _placement = value;
    }

    private static Button NavButton(string glyph, string name, Action invoke)
    {
        var b = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Assets/SegoeFluentIcons.ttf#Segoe Fluent Icons"),
                FontSize = 16,
            },
            MinWidth = ButtonDip,
            MinHeight = ButtonDip,
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(b, name);
        b.Click += (_, _) => invoke();
        return b;
    }

    public void Update(PptSnapshot snapshot, bool panelVisible, bool counterVisible)
        => Update(snapshot, panelVisible, counterVisible, default);

    /// <summary>
    /// Positions the panel inside <paramref name="showBounds"/> (the slideshow window) and
    /// refreshes the slide counter. A default/invalid rectangle falls back to the work area.
    /// </summary>
    public void Update(PptSnapshot snapshot, bool panelVisible, bool counterVisible, ShowWindowInfo showBounds)
    {
        _counter.Visibility = counterVisible ? Visibility.Visible : Visibility.Collapsed;
        _counterVisible = counterVisible;
        _showBounds = showBounds;

        int position = snapshot?.ShowPosition > 0 ? snapshot.ShowPosition : snapshot?.Slide?.Index ?? 0;
        int count = snapshot?.Presentation?.SlideCount ?? 0;
        _counterText.Text = $"{position} / {count}";

        ApplyOrientation();
        Place(counterVisible);
        SetVisible(panelVisible);
    }

    private void SetVisible(bool visible)
    {
        if (visible) NativeMethods.ShowWindow(WindowChrome.GetHwnd(this), NativeMethods.SW_SHOWNOACTIVATE);
        else NativeMethods.ShowWindow(WindowChrome.GetHwnd(this), NativeMethods.SW_HIDE);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Activated -= OnActivated;
        if (_trackedRoot is not null) _trackedRoot.Changed -= OnRootChanged;
        _trackedRoot = null;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_closed) return;
        TrackRoot();
    }

    private void TrackRoot()
    {
        if (_closed) return;
        try
        {
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnRootChanged;
            _trackedRoot = (Content as FrameworkElement)?.XamlRoot;
            if (_trackedRoot is not null) _trackedRoot.Changed += OnRootChanged;
            Place(_counterVisible);
        }
        catch (Exception)
        {
            // The window may already be torn down; nothing to track or place any more.
        }
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_closed) return;
        Place(_counterVisible);
    }

    private NavigationCorner Corner => _placement switch
    {
        PptNavigationPlacement.BottomRight => NavigationCorner.BottomRight,
        PptNavigationPlacement.SideLeft => NavigationCorner.SideLeft,
        PptNavigationPlacement.SideRight => NavigationCorner.SideRight,
        _ => NavigationCorner.BottomLeft,
    };

    private bool IsVertical => _placement is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight;

    private void ApplyOrientation()
    {
        bool vertical = IsVertical;
        _panel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        if (_previous.Content is FontIcon previous) previous.Glyph = vertical ? Glyphs.ChevronUp : Glyphs.ChevronLeft;
        if (_next.Content is FontIcon next) next.Glyph = vertical ? Glyphs.ChevronDown : Glyphs.ChevronRight;
        _counter.MinWidth = vertical ? ButtonDip : 52;
        _counter.MinHeight = vertical ? 48 : 40;
    }

    /// <summary>Physical pixel size needed by the six controls plus border/padding chrome.</summary>
    private static (int Width, int Height) ContentSize(bool vertical, double dpiScale, bool counterVisible)
    {
        double scale = dpiScale > 0 ? dpiScale : 1;
        int length = ButtonDip * 6 + SpacingDip * 5 + (counterVisible ? SpacingDip + (vertical ? 48 : 52) : 0);
        int contentWidth = vertical ? ButtonDip : length;
        int contentHeight = vertical ? length : ButtonDip;
        return ((int)Math.Ceiling((contentWidth + 6) * scale),
                (int)Math.Ceiling((contentHeight + 6) * scale));
    }

    private void Place(bool counterVisible)
    {
        var work = WindowChrome.GetWorkArea(this);
        ScreenBox box = _showBounds.Width > 0 && _showBounds.Height > 0
            ? new ScreenBox(_showBounds.X, _showBounds.Y, _showBounds.Width, _showBounds.Height)
            : new ScreenBox(work.X, work.Y, work.Width, work.Height);

        double dpi = _trackedRoot?.RasterizationScale ?? WindowChrome.GetDpiScale(this);
        NavigationPlacementResult placement = NavigationLayout.Place(Corner, box, dpi, counterVisible);

        bool vertical = IsVertical;
        var (neededWidth, neededHeight) = ContentSize(vertical, dpi, counterVisible);
        int width = Math.Max(placement.Width, neededWidth);
        int height = Math.Max(placement.Height, neededHeight);

        int x = placement.Corner is NavigationCorner.BottomRight or NavigationCorner.SideRight
            ? placement.X + placement.Width - width
            : placement.X;
        int y = placement.Corner is NavigationCorner.SideLeft or NavigationCorner.SideRight
            ? placement.Y + placement.Height / 2 - height / 2
            : placement.Y + placement.Height - height;

        int maxX = box.X + Math.Max(0, box.Width - width);
        int maxY = box.Y + Math.Max(0, box.Height - height);
        x = maxX >= box.X ? Math.Clamp(x, box.X, maxX) : box.X;
        y = maxY >= box.Y ? Math.Clamp(y, box.Y, maxY) : box.Y;

        var position = AppWindow.Position;
        if (position.X != x || position.Y != y || AppWindow.Size.Width != width || AppWindow.Size.Height != height)
            WindowChrome.Place(this, x, y, width, height);
    }
}