using System.Runtime.InteropServices;

namespace InkCanvas.Platform.Windows;

/// <summary>
/// Snapshot of a top-level window's screen geometry and the monitor it currently sits on.
/// <para>
/// <see cref="IsFullScreen"/> is a geometry-only heuristic: a window that covers at least
/// 98% of its monitor in both dimensions is treated as a slideshow/presentation window.
/// It cannot tell a maximized editor apart from a real slideshow, so it must never be used
/// alone to decide that a presentation started.
/// </para>
/// </summary>
public readonly record struct ShowWindowInfo(
    nint Handle,
    int X,
    int Y,
    int Width,
    int Height,
    uint Dpi,
    int MonitorX,
    int MonitorY,
    int MonitorWidth,
    int MonitorHeight,
    bool IsVisible,
    bool IsFullScreen);

/// <summary>Defensive, exception-free reader for top-level window geometry.</summary>
public static class ShowWindowTracker
{
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint FallbackDpi = 96;
    private const int FullScreenTolerancePercent = 2;

    /// <summary>
    /// Reads the geometry of <paramref name="hwnd"/>. Returns <c>false</c> (and a default
    /// <paramref name="info"/>) for invalid, hidden or destroyed windows, and swallows every
    /// exception so callers polling the foreground window never crash.
    /// </summary>
    public static bool TryGet(nint hwnd, out ShowWindowInfo info)
    {
        info = default;
        if (hwnd == 0) return false;

        try
        {
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
            if (!GetWindowRect(hwnd, out RECT rect)) return false;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;

            int monitorX = 0, monitorY = 0, monitorWidth = 0, monitorHeight = 0;
            nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != 0)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(monitor, ref monitorInfo))
                {
                    monitorX = monitorInfo.rcMonitor.Left;
                    monitorY = monitorInfo.rcMonitor.Top;
                    monitorWidth = monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left;
                    monitorHeight = monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top;
                }
            }

            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = FallbackDpi;

            bool isFullScreen = monitorWidth > 0 && monitorHeight > 0
                && (long)width * 100 >= (long)monitorWidth * (100 - FullScreenTolerancePercent)
                && (long)height * 100 >= (long)monitorHeight * (100 - FullScreenTolerancePercent);

            info = new ShowWindowInfo(hwnd, rect.Left, rect.Top, width, height, dpi,
                monitorX, monitorY, monitorWidth, monitorHeight, true, isFullScreen);
            return true;
        }
        catch
        {
            info = default;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hWnd);
}