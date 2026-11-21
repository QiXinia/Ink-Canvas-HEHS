namespace InkCanvas.Features.Ppt;

public enum NavigationCorner { BottomLeft, BottomRight, SideLeft, SideRight }

public readonly record struct ScreenBox(int X, int Y, int Width, int Height);

public readonly record struct NavigationPlacementResult(
    int X, int Y, int Width, int Height, NavigationCorner Corner);

/// <summary>
/// Pure placement math mirrored from the navigation window. Sizes are DIPs scaled by the
/// monitor DPI; the result is always clamped inside the supplied screen bounds.
/// </summary>
public static class NavigationLayout
{
    private const double HorizontalWidth = 160;
    private const double HorizontalHeight = 52;
    private const double VerticalWidth = 52;
    private const double VerticalHeight = 160;
    private const double Margin = 20;

    public static NavigationPlacementResult Place(
        NavigationCorner corner, ScreenBox showBounds, double dpiScale, bool counterVisible)
    {
        _ = counterVisible;

        double scale = dpiScale > 0 ? dpiScale : 1;
        bool vertical = corner is NavigationCorner.SideLeft or NavigationCorner.SideRight;
        int width = (int)Math.Ceiling((vertical ? VerticalWidth : HorizontalWidth) * scale);
        int height = (int)Math.Ceiling((vertical ? VerticalHeight : HorizontalHeight) * scale);
        int gap = (int)Math.Ceiling(Margin * scale);

        int boundsWidth = Math.Max(0, showBounds.Width);
        int boundsHeight = Math.Max(0, showBounds.Height);
        width = Math.Min(Math.Max(0, width), boundsWidth);
        height = Math.Min(Math.Max(0, height), boundsHeight);

        int x = corner switch
        {
            NavigationCorner.BottomRight or NavigationCorner.SideRight
                => showBounds.X + boundsWidth - width - gap,
            _ => showBounds.X + gap,
        };
        int y = corner switch
        {
            NavigationCorner.SideLeft or NavigationCorner.SideRight
                => showBounds.Y + (boundsHeight - height) / 2,
            _ => showBounds.Y + boundsHeight - height - gap,
        };

        x = Math.Clamp(x, showBounds.X, showBounds.X + boundsWidth - width);
        y = Math.Clamp(y, showBounds.Y, showBounds.Y + boundsHeight - height);

        return new NavigationPlacementResult(x, y, width, height, corner);
    }

    public static NavigationCorner NextCorner(NavigationCorner corner) => corner switch
    {
        NavigationCorner.BottomLeft => NavigationCorner.BottomRight,
        NavigationCorner.BottomRight => NavigationCorner.SideRight,
        NavigationCorner.SideRight => NavigationCorner.SideLeft,
        _ => NavigationCorner.BottomLeft,
    };
}