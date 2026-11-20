using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Features.Ppt;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace InkCanvas.App.Views;

public enum PptNavigationPlacement { BottomLeft, BottomRight, SideLeft, SideRight }

/// <summary>
/// 1:1 port of the four WPF PPT navigation panels (upstream MainWindow.xaml:4994-5345,
/// PPTNavigationBottomLeft/Right and PPTNavigationSidesLeft/Right). Each WPF panel is a
/// Viewbox of 40 DIP over 42 DIP of content (uniform 40/42 scale) holding a Border with
/// Opacity 0.4, 40x40 prev/next cells, a 40x38 page-number cell flanked by 25px separator
/// lines, and EDD5/EDD6 (bottom) or EDD7/EDD8 (sides) glyphs. Independent no-activate
/// WinUI windows so they sit over a slideshow without being clipped by the bar window.
/// </summary>
public sealed class PptNavigationWindow : Window
{
    /// <summary>WPF Viewbox Height/Width=40 over 42 DIP content (MainWindow.xaml:4995,5172).</summary>
    private const double S = 40.0 / 42.0;
    private const double Cell = 40 * S;          // prev/next cell, both axes
    private const double CounterH = 38 * S;      // page-number cell short axis
    private const double IconBox = 26 * S;       // FontIcon Width/Height (MainWindow.xaml:5016-5017)
    private const double IconSz = 20 * S;        // ModernWpf FontIcon default FontSize 20
    private const double TextSz = 9 * S;         // MainWindow.xaml:5051
    private const double LineLen = 25 * S;       // separator Line Y2/X2=25
    private const double LineThick = 1 * S;      // StrokeThickness=1
    private const double BorderThick = 1 * S;
    private const double Corner = 6 * S;

    // Exact WPF glyphs (MainWindow.xaml:5020,5078,5196,5253).
    private const string GlyphPrevBottom = "\uEDD5";
    private const string GlyphNextBottom = "\uEDD6";
    private const string GlyphPrevSide = "\uEDD7";
    private const string GlyphNextSide = "\uEDD8";

    private static readonly FontFamily IconFont = new("ms-appx:///Assets/SegoeFluentIcons.ttf#Segoe Fluent Icons");

    private readonly PptNavigationPlacement _placement;
    private readonly IShellActions _shell;
    private readonly Border _previous;
    private readonly Border _next;
    private readonly Border _counter;
    private readonly TextBlock _counterText;
    private readonly StackPanel _panel;
    private XamlRoot _trackedRoot;
    private IntPtr _showWindow;
    private bool _windowedShow;

    public PptNavigationWindow(PptNavigationPlacement placement, IShellActions shell, int theme)
    {
        _placement = placement;
        _shell = shell;
        bool vertical = placement is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight;
        Title = "PowerPoint 导航";

        Brush foreground = PopupBrushes.FloatBarForeground(theme);
        _previous = NavCell(vertical ? GlyphPrevSide : GlyphPrevBottom, foreground, () => _shell.PptPrevious());
        _next = NavCell(vertical ? GlyphNextSide : GlyphNextBottom, foreground, () => _shell.PptNext());
        _counterText = new TextBlock
        {
            Text = "0/0",
            FontSize = TextSz,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _counter = CounterCell(foreground, vertical);

        _panel = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = 0, // ikw:SimpleStackPanel default
        };
        _panel.Children.Add(_previous);
        _panel.Children.Add(_counter);
        _panel.Children.Add(_next);

        Content = new Border
        {
            Background = PopupBrushes.FloatBarBackground(theme),
            BorderBrush = PopupBrushes.FloatBarBorder(theme),
            BorderThickness = new Thickness(BorderThick),
            CornerRadius = new CornerRadius(Corner),
            Child = _panel,
        };

        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        FloatingBarWindow.EnableTransparency(this);
        FloatingBarWindow.UpdateAcrylicTint(this, theme);
        WindowChrome.ClipToSurfaces(this, () => new[] { (Border)Content });
        WindowChrome.ApplyTheme(this, theme);
        // Never steal focus/activation from the slideshow; tool-window keeps it out of alt-tab
        // (the WPF panels were children of the single main window, not separate taskbar entries).
        Closed += (_, _) =>
        {
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnRootChanged;
        };
        Activated += (_, _) => TrackRoot();
        ((FrameworkElement)Content).Loaded += (_, _) => TrackRoot();
        PlaceOnScreen();
        SetVisible(false);
    }

    /// <summary>WPF prev/next cell: transparent 40x40 Border with a centered 26px FontIcon.</summary>
    private Border NavCell(string glyph, Brush foreground, Action invoke)
    {
        var cell = new Border
        {
            Width = Cell,
            Height = Cell,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontFamily = IconFont,
                FontSize = IconSz,
                Foreground = foreground,
                Width = IconBox,
                Height = IconBox,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        AttachClick(cell, invoke);
        return cell;
    }

    /// <summary>
    /// WPF page-number cell (MainWindow.xaml:5023-5063): 1/36/1 grid with 25px separator
    /// lines and a 9px counter. Click switches to mouse mode and opens PowerPoint's slide
    /// navigation pane (PPTNavigationBtn_Click, MW_PPT.cs:642-658; handled by the shell).
    /// </summary>
    private Border CounterCell(Brush foreground, bool vertical)
    {
        var grid = new Grid();
        var line1 = new Rectangle { Fill = foreground };
        var line2 = new Rectangle { Fill = foreground };
        if (vertical)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LineThick) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36 * S) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LineThick) });
            line1.Width = LineLen; line1.Height = LineThick;
            line2.Width = LineLen; line2.Height = LineThick;
            line1.HorizontalAlignment = HorizontalAlignment.Center;
            line2.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(line1, 0);
            Grid.SetRow(_counterText, 1);
            Grid.SetRow(line2, 2);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LineThick) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36 * S) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LineThick) });
            line1.Width = LineThick; line1.Height = LineLen;
            line2.Width = LineThick; line2.Height = LineLen;
            line1.VerticalAlignment = VerticalAlignment.Center;
            line2.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(line1, 0);
            Grid.SetColumn(_counterText, 1);
            Grid.SetColumn(line2, 2);
        }
        grid.Children.Add(line1);
        grid.Children.Add(_counterText);
        grid.Children.Add(line2);

        var cell = new Border
        {
            Width = Cell,
            Height = CounterH,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = grid,
        };
        AttachClick(cell, () => _shell.PptOpenNavigation());
        return cell;
    }

    /// <summary>
    /// WPF fired these cells on MouseUp only when MouseDown landed on the same Border
    /// (Border_MouseDown, MW_SelectionGestures.cs:22-25; guards MW_PPT.cs:644,692,698).
    /// Capture the pointer so touch releases still arrive, then apply the same bounds guard.
    /// WPF has no hover/pressed visual on these cells; the scale pulse is the app-wide touch
    /// press feedback (TouchIconButton contract) and does not change the resting look.
    /// </summary>
    private static void AttachClick(Border cell, Action invoke)
    {
        bool pressed = false;
        cell.PointerPressed += (_, e) =>
        {
            pressed = true;
            cell.CapturePointer(e.Pointer);
            Motion.Pulse(cell, 0.92f);
            e.Handled = true; // keep focus on the slideshow, never on this no-activate window
        };
        cell.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            cell.ReleasePointerCapture(e.Pointer);
            var position = e.GetCurrentPoint(cell).Position;
            if (position.X >= 0 && position.Y >= 0 && position.X <= cell.ActualWidth && position.Y <= cell.ActualHeight)
                invoke();
            e.Handled = true;
        };
        cell.PointerCanceled += (_, _) => pressed = false;
        cell.PointerCaptureLost += (_, _) => pressed = false;
    }

    public void Update(PptSnapshot snapshot, bool panelVisible, bool counterVisible)
    {
        _showWindow = snapshot?.WindowHandle ?? IntPtr.Zero;
        _windowedShow = snapshot?.IsWindowedShow == true;
        _counter.Visibility = counterVisible ? Visibility.Visible : Visibility.Collapsed;
        int position = snapshot?.ShowPosition > 0 ? snapshot.ShowPosition : snapshot?.Slide?.Index ?? 0;
        int count = snapshot?.Presentation?.SlideCount ?? 0;
        // MW_PPT.cs:403,563: "{position}/{count}" with no spaces.
        string text = $"{position}/{count}";
        if (_counterText.Text != text) _counterText.Text = text;
        PlaceOnScreen();
        SetVisible(panelVisible);
    }

    private void SetVisible(bool visible)
    {
        if (NativeMethods.IsWindowVisible(WindowChrome.GetHwnd(this)) == visible) return;
        if (visible) WindowChrome.ShowWithoutActivation(this);
        else NativeMethods.ShowWindow(WindowChrome.GetHwnd(this), NativeMethods.SW_HIDE);
    }

    private void TrackRoot()
    {
        if (_trackedRoot is not null) _trackedRoot.Changed -= OnRootChanged;
        _trackedRoot = (Content as FrameworkElement)?.XamlRoot;
        if (_trackedRoot is not null) _trackedRoot.Changed += OnRootChanged;
        PlaceOnScreen();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => PlaceOnScreen();

    /// <summary>
    /// WPF placed the panels inside the maximized borderless main window, i.e. against the
    /// full screen, with unscaled Viewbox margins: bottom "2,0,0,2" (right panel flush to
    /// the right edge), sides 6px and vertically centered (MainWindow.xaml:4996-5000 etc.).
    /// The Viewbox reflows when the page-number cell is hidden, so the window shrinks too.
    /// </summary>
    private void PlaceOnScreen()
    {
        var monitor = Platform.System.ScreenService.FromWindow(_showWindow);
        var screen = _windowedShow ? WindowChrome.GetClientBounds(_showWindow).FitInside(monitor.WorkArea) : monitor.Bounds;
        double dpi = monitor.DpiScale;
        bool vertical = _placement is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight;
        bool counter = _counter.Visibility == Visibility.Visible;
        double widthDip = vertical ? 42 * S : (counter ? 122 : 82) * S;
        double heightDip = vertical ? (counter ? 120 : 82) * S : 42 * S;
        int width = (int)Math.Ceiling(widthDip * dpi);
        int height = (int)Math.Ceiling(heightDip * dpi);
        int marginH = (int)Math.Round((_placement switch
        {
            PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight => 6,
            PptNavigationPlacement.BottomLeft => 2,
            _ => 0, // BottomRight: Margin right=0
        }) * dpi);
        int marginV = (int)Math.Round((vertical ? 0 : 2) * dpi);
        int x = _placement is PptNavigationPlacement.SideRight or PptNavigationPlacement.BottomRight
            ? screen.X + screen.Width - width - marginH
            : screen.X + marginH;
        int y = vertical
            ? screen.Y + (screen.Height - height) / 2
            : screen.Y + screen.Height - height - marginV;
        var position = AppWindow.Position;
        if (position.X != x || position.Y != y || AppWindow.Size.Width != width || AppWindow.Size.Height != height)
            WindowChrome.Place(this, x, y, width, height);
    }
}
