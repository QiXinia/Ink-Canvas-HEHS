using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Views;

/// <summary>
/// Enforces a minimum window size (and monitor-correct maximize bounds) on a borderless WinUI
/// window via a WM_GETMINMAXINFO subclass — WinUI's OverlappedPresenter has no min-size API.
/// The delegate is rooted for the window's lifetime and the original WndProc is restored on
/// dispose; the hook never throws across the native boundary.
/// </summary>
internal sealed class MinSizeHook : IDisposable
{
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const int GWLP_WNDPROC = -4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly int _minWidthDip;
    private readonly int _minHeightDip;
    private readonly WndProcDelegate _proc;
    private IntPtr _oldProc;
    private bool _installed;

    public MinSizeHook(Window window, int minWidthDip, int minHeightDip)
    {
        _hwnd = WindowChrome.GetHwnd(window);
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
        if (_hwnd == IntPtr.Zero) return;
        _proc = Hook;
        try
        {
            _oldProc = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
            _installed = _oldProc != IntPtr.Zero;
        }
        catch
        {
            _installed = false;
        }
    }

    private IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                // Let the original procedure fill the defaults first, then adjust.
                var result = CallWindowProcW(_oldProc, hWnd, msg, wParam, lParam);
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                double scale = DpiScale(hWnd);
                if (_minWidthDip > 0) info.ptMinTrackSize.X = (int)Math.Ceiling(_minWidthDip * scale);
                if (_minHeightDip > 0) info.ptMinTrackSize.Y = (int)Math.Ceiling(_minHeightDip * scale);
                if (TryGetMonitorInfo(hWnd, out MONITORINFO mi))
                {
                    info.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                    info.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                    info.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    info.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                }
                Marshal.StructureToPtr(info, lParam, false);
                return result;
            }
        }
        catch
        {
            // Fall through to the original procedure on any marshaling failure.
        }
        return CallWindowProcW(_oldProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>Full bounds (physical px) of the monitor nearest to the window.</summary>
    internal static bool TryGetMonitorBounds(IntPtr hwnd, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (!TryGetMonitorInfo(hwnd, out MONITORINFO mi)) return false;
        left = mi.rcMonitor.Left;
        top = mi.rcMonitor.Top;
        width = mi.rcMonitor.Right - mi.rcMonitor.Left;
        height = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        return true;
    }

    private static bool TryGetMonitorInfo(IntPtr hwnd, out MONITORINFO mi)
    {
        mi = default;
        try
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;
            mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return GetMonitorInfoW(monitor, ref mi);
        }
        catch
        {
            return false;
        }
    }

    private static double DpiScale(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public void Dispose()
    {
        if (!_installed) return;
        _installed = false;
        try { SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _oldProc); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
