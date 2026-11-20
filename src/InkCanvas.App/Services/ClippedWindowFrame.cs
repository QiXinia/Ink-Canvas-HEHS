using System.ComponentModel;
using System.Runtime.InteropServices;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

/// <summary>Keeps native frames and their rectangular shadows off windows clipped to XAML cards.</summary>
internal sealed class ClippedWindowFrame : IDisposable
{
    private const int FrameStyles = 0x00C40000; // WS_CAPTION | WS_THICKFRAME
    private const int EdgeStyles = 0x00020300; // WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE
    private const nuint SubclassId = 0x49434652;
    private readonly Window _window;
    private readonly IntPtr _handle;
    private readonly SubclassProc _callback;
    private bool _disposed, _pending, _applying;
    public Action StopSurfaceTracking { get; set; }

    public ClippedWindowFrame(Window window)
    {
        _window = window;
        _handle = WindowChrome.GetHwnd(window);
        _callback = ProcessMessage;
        if (!SetWindowSubclass(_handle, _callback, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        window.Closed += OnClosed;
        Apply();
    }

    private void Apply()
    {
        if (_disposed || _applying) return;
        _applying = true;
        try
        {
            int style = NativeMethods.GetWindowLongW(_handle, NativeMethods.GWL_STYLE);
            int extended = NativeMethods.GetWindowLongW(_handle, NativeMethods.GWL_EXSTYLE);
            bool frameChanged = (style & FrameStyles) != 0 || (extended & EdgeStyles) != 0;
            if ((style & FrameStyles) != 0)
                NativeMethods.SetWindowLongW(_handle, NativeMethods.GWL_STYLE, style & ~FrameStyles);
            if ((extended & EdgeStyles) != 0)
                NativeMethods.SetWindowLongW(_handle, NativeMethods.GWL_EXSTYLE, extended & ~EdgeStyles);

            // Disabling DWM alone leaves the classic extended window edge available to paint.
            // Apply after WinUI processes system/theme changes and backdrop style changes.
            SetAttribute(2, 1); // DWMWA_NCRENDERING_POLICY = DWMNCRP_DISABLED
            SetAttribute(33, 1); // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_DONOTROUND
            SetAttribute(34, unchecked((int)0xFFFFFFFE)); // DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE
            SetAttribute(3, 1); // DWMWA_TRANSITIONS_FORCEDISABLED
            if (frameChanged)
                NativeMethods.SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE
                    | 0x0004 /* SWP_NOZORDER */ | 0x0020 /* SWP_FRAMECHANGED */);
        }
        finally { _applying = false; }
    }

    private void SetAttribute(int attribute, int value)
    {
        // Avoid generating another non-client notification when the policy already matches.
        int query = attribute == 2 ? 1 : attribute;
        int expected = attribute == 2 ? 0 : value;
        if (DwmGetWindowAttribute(_handle, query, out int current, sizeof(int)) != 0 || current != expected)
            DwmSetWindowAttribute(_handle, attribute, ref value, sizeof(int));
    }

    private void QueueApply()
    {
        if (_disposed || _pending || _applying) return;
        _pending = true;
        if (!_window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pending = false;
            Apply();
        })) _pending = false;
    }

    private IntPtr ProcessMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == 0x0082 /* WM_NCDESTROY */)
        {
            Dispose();
            return DefSubclassProc(window, message, wParam, lParam);
        }
        if (message == 0x0085 /* WM_NCPAINT */) return IntPtr.Zero;
        if (message == 0x0014 /* WM_ERASEBKGND */) return new IntPtr(1);
        if (message == 0x0086 /* WM_NCACTIVATE */)
            lParam = new IntPtr(-1); // Update activation without repainting a classic frame.

        var result = DefSubclassProc(window, message, wParam, lParam);
        switch (message)
        {
            case 0x0018: // WM_SHOWWINDOW
            case 0x001A: // WM_SETTINGCHANGE
            case 0x007D: // WM_STYLECHANGED
            case 0x0086: // WM_NCACTIVATE
            case 0x02E0: // WM_DPICHANGED
            case 0x031A: // WM_THEMECHANGED
            case 0x031E: // WM_DWMCOMPOSITIONCHANGED
            case 0x031F: // WM_DWMNCRENDERINGCHANGED
                QueueApply();
                break;
        }
        return result;
    }

    private void OnClosed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closed -= OnClosed;
        StopSurfaceTracking?.Invoke();
        StopSurfaceTracking = null;
        RemoveWindowSubclass(_handle, _callback, SubclassId);
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
