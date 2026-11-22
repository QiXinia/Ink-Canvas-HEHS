using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InkCanvas.Controls.Theming;

/// <summary>
/// Exact WPF popup-window palette (Resources/Styles/Light-PopupWindow.xaml and
/// Dark-PopupWindow.xaml upstream), resolved per window theme at construction.
/// <para>
/// Code-behind cannot use {ThemeResource}: Application.Current.Resources lookups resolve
/// against the APPLICATION theme, while aux windows follow the board-aware effective theme
/// (light iff whiteboard in board mode). WPF also fixes the theme at window open, so brushes
/// captured here at construction match the original behaviour.
/// </para>
/// 0 = follow system (treated as light, like the WPF default), 1 = light, 2 = dark.
/// </summary>
public static class PopupBrushes
{
    public static SolidColorBrush ToolPanelBorder(int theme) => new(IsDark(theme)
        ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(32, 0, 0, 0));
    public static bool IsDark(int theme) => theme == 2;

    /// <summary>PopupWindowBackground: #F0F3F9 @0.95 light / #1f1f1f @0.95 dark.</summary>
    public static SolidColorBrush Surface(int theme) => new(SurfaceColor(theme, 242));

    /// <summary>Opaque window foundation, matching the WPF popup card's own colour.</summary>
    public static SolidColorBrush WindowSurface(int theme) => new(SurfaceColor(theme, 255));

    private static Color SurfaceColor(int theme, byte alpha) => IsDark(theme)
        ? Color.FromArgb(alpha, 31, 31, 31) : Color.FromArgb(alpha, 240, 243, 249);

    /// <summary>PopupWindowBorderBrush: #0066BF @0.8 light / white @0.9 dark.</summary>
    public static SolidColorBrush Border(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(230, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(204, 0, 102, 191));

    /// <summary>PopupWindowForeground: black light / white dark.</summary>
    public static SolidColorBrush Foreground(int theme) => IsDark(theme)
        ? new SolidColorBrush(Microsoft.UI.Colors.White)
        : new SolidColorBrush(Microsoft.UI.Colors.Black);

    /// <summary>PopupWindowDarkBlueBorderBackground: #0066BF @0.9 light / #1e3a8a dark.</summary>
    public static SolidColorBrush BadgeBackground(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(230, 30, 58, 138))
        : new SolidColorBrush(Color.FromArgb(230, 0, 102, 191));

    public static SolidColorBrush BadgeForeground(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(204, 255, 255, 255))
        : new SolidColorBrush(Microsoft.UI.Colors.White);

    /// <summary>Accent blue #0066BF (both themes).</summary>
    public static SolidColorBrush Accent() => new(Color.FromArgb(255, 0, 102, 191));

    /// <summary>Danger red #E32A34 (close circles, both themes).</summary>
    public static SolidColorBrush Danger() => new(Color.FromArgb(255, 227, 42, 52));

    /// <summary>Disabled cover gray #BFBFBF (both themes).</summary>
    public static SolidColorBrush DisabledCover() => new(Color.FromArgb(255, 191, 191, 191));

    /// <summary>Countdown ring track #EAEAEA light / #3A3A3A dark.</summary>
    public static SolidColorBrush RingTrack(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(255, 58, 58, 58))
        : new SolidColorBrush(Color.FromArgb(255, 234, 234, 234));

    /// <summary>Countdown ring progress #0067C1.</summary>
    public static SolidColorBrush RingActive() => new(Color.FromArgb(255, 0, 103, 193));

    /// <summary>Countdown ring paused #1A71C8.</summary>
    public static SolidColorBrush RingPaused() => new(Color.FromArgb(255, 26, 113, 200));

    /// <summary>Yes-button green #15803d, hover #166534, pressed #306534.</summary>
    public static SolidColorBrush YesGreen() => new(Color.FromArgb(255, 21, 128, 61));
    public static SolidColorBrush YesGreenHover() => new(Color.FromArgb(255, 22, 101, 52));
    public static SolidColorBrush YesGreenPressed() => new(Color.FromArgb(255, 48, 101, 52));

    // ---- Floating bar (upstream Resources/Styles/Light.xaml & Dark.xaml) ----

    /// <summary>FloatBarBackground: white @0.75 light / black @0.6 dark.</summary>
    public static SolidColorBrush FloatBarBackground(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(153, 0, 0, 0))
        : new SolidColorBrush(Color.FromArgb(191, 255, 255, 255));

    /// <summary>FloatBarBorderBrush: black @0.8 light / white @0.6 dark.</summary>
    public static SolidColorBrush FloatBarBorder(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(153, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(204, 0, 0, 0));

    /// <summary>FloatBarForeground: black light / white dark.</summary>
    public static SolidColorBrush FloatBarForeground(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));

    /// <summary>FloatBarTitleBackground: #588BFC @0.25 light / #2156C9 @0.5 dark.</summary>
    public static SolidColorBrush FloatBarTitleBackground(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(128, 33, 86, 201))
        : new SolidColorBrush(Color.FromArgb(64, 88, 139, 252));

    /// <summary>Neutral Fluent hover surface.</summary>
    public static SolidColorBrush FloatBarButtonHover(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(14, 0, 0, 0));

    /// <summary>Neutral Fluent pressed surface.</summary>
    public static SolidColorBrush FloatBarButtonPressed(int theme) => IsDark(theme)
        ? new SolidColorBrush(Color.FromArgb(16, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(24, 0, 0, 0));

    /// <summary>Neutral surface used by compatible module selection properties.</summary>
    public static SolidColorBrush FloatBarSelected() => new(Color.FromArgb(20, 128, 128, 128));

    /// <summary>IsColorfulViewboxFloatingBar gradient: #9580B0FF → #95C0FFC0, (0,0)→(1,1).</summary>
    public static LinearGradientBrush FloatBarColorful()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(149, 128, 176, 255), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(149, 192, 255, 192), Offset = 1 });
        return brush;
    }
}
