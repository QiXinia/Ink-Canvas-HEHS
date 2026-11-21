using System.ComponentModel;
using System.Runtime.InteropServices;
using InkCanvas.Platform.Interop;

namespace InkCanvas.Platform.Windows;

/// <summary>Reject pointer activation while delivering the original click, pen and touch input.</summary>
public sealed class NonActivatingWindow : IDisposable
{
    private readonly IntPtr _window;
    private readonly HashSet<IntPtr> _attached = new();
    private readonly SubclassProc _callback;
    private bool _disposed;
    private const nuint SubclassId = 0x49434E41;

    public NonActivatingWindow(IntPtr window)
    {
        _window = window;
        _callback = ProcessMessage;
        NativeMethods.AddExStyle(window, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        if (!Attach(window)) throw new Win32Exception(Marshal.GetLastWin32Error());
        RefreshChildren();
    }

    public void RefreshChildren()
    {
        if (_disposed) return;
        EnumChildWindows(_window, (child, context) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(child, out _) == GetCurrentThreadId()) Attach(child);
            return true;
        }, IntPtr.Zero);
    }

    private bool Attach(IntPtr window)
    {
        if (_attached.Contains(window)) return true;
        if (!SetWindowSubclass(window, _callback, SubclassId, 0)) return false;
        _attached.Add(window);
        return true;
    }

    private IntPtr ProcessMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == NativeMethods.WM_MOUSEACTIVATE) return new IntPtr(NativeMethods.MA_NOACTIVATE);
        if (message == NativeMethods.WM_POINTERACTIVATE) return new IntPtr(NativeMethods.PA_NOACTIVATE);
        if (message == 0x0082 /* WM_NCDESTROY */)
        {
            RemoveWindowSubclass(window, _callback, SubclassId);
            _attached.Remove(window);
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _attached) RemoveWindowSubclass(window, _callback, SubclassId);
        _attached.Clear();
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    private delegate bool EnumWindowProc(IntPtr window, IntPtr data);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr window, EnumWindowProc callback, IntPtr data);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
