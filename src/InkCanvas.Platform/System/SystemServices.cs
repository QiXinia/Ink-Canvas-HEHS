using InkCanvas.Platform.Interop;
using System.Diagnostics;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace InkCanvas.Platform.System;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public ScreenRect FitInside(ScreenRect work)
    {
        int workWidth = Math.Max(1, work.Width), workHeight = Math.Max(1, work.Height);
        int width = Math.Clamp(Width, 1, workWidth);
        int height = Math.Clamp(Height, 1, workHeight);
        return new(Math.Clamp(X, work.X, work.X + workWidth - width),
            Math.Clamp(Y, work.Y, work.Y + workHeight - height), width, height);
    }
}

public readonly record struct ScreenInfo(IntPtr Handle, ScreenRect Bounds, ScreenRect WorkArea, double DpiScale)
{
    public string DeviceName { get; init; } = "";
    public bool IsPrimary { get; init; }
}

public static class ScreenService
{
    private const uint Nearest = 2;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeMethods.RECT Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeMethods.RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeMethods.RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    public static ScreenInfo FromRect(ScreenRect rect)
    {
        var native = new NativeMethods.RECT { Left = rect.X, Top = rect.Y,
            Right = rect.X + Math.Max(1, rect.Width), Bottom = rect.Y + Math.Max(1, rect.Height) };
        return ReadMonitor(MonitorFromRect(ref native, Nearest));
    }

    public static ScreenInfo FromWindow(IntPtr window) => ReadMonitor(MonitorFromWindow(window, Nearest));

    public static ScreenInfo AtCursor()
    {
        NativeMethods.GetCursorPos(out var point);
        return FromRect(new(point.X, point.Y, 1, 1));
    }

    public static IReadOnlyList<ScreenInfo> All()
    {
        var screens = new List<ScreenInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr dc, ref NativeMethods.RECT rect, IntPtr data) =>
            { screens.Add(ReadMonitor(monitor)); return true; }, IntPtr.Zero);
        return screens.OrderBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(screen => screen.Bounds.X).ThenBy(screen => screen.Bounds.Y).ToArray();
    }

    /// <summary>Physical desktop bounds from one monitor snapshot, including negative origins.</summary>
    public static ScreenRect GetVirtualBounds(IReadOnlyList<ScreenInfo> screens)
    {
        if (screens.Count == 0) return default;
        int left = screens.Min(screen => screen.Bounds.X);
        int top = screens.Min(screen => screen.Bounds.Y);
        int right = screens.Max(screen => checked(screen.Bounds.X + screen.Bounds.Width));
        int bottom = screens.Max(screen => checked(screen.Bounds.Y + screen.Bounds.Height));
        return new(left, top, checked(right - left), checked(bottom - top));
    }

    private static ScreenInfo ReadMonitor(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return new(monitor, Primary, WorkArea, 1);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96d : 1;
        return new(monitor, Rect(info.Monitor), Rect(info.Work), scale) { DeviceName = info.Device, IsPrimary = (info.Flags & 1) != 0 };
    }

    private static ScreenRect Rect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public static ScreenRect Primary => new(0, 0,
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN));

    public static ScreenRect Virtual => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Primary screen minus the taskbar / docked bars. Windows that carry their own bottom
    /// toolbar must use this, otherwise the toolbar ends up behind the taskbar.
    /// </summary>
    public static ScreenRect WorkArea
    {
        get
        {
            var rect = new NativeMethods.RECT();
            if (NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETWORKAREA, 0, ref rect, 0))
                return new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            return Primary;
        }
    }
}

/// <summary>Identifies the foreground application, used by the auto-fold rules.</summary>
public static class ForegroundWindowService
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, global::System.Text.StringBuilder text, int length);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeMethods.RECT rect);

    public static (string Process, string Title, int Height) DescribeForeground()
    {
        var window = GetForegroundWindow();
        var text = new global::System.Text.StringBuilder(512);
        GetWindowTextW(window, text, text.Capacity);
        GetWindowRect(window, out var rect);
        return (GetForegroundProcessName(), text.ToString(), rect.Bottom - rect.Top);
    }
    public static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public static string GetForegroundProcessName()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return string.Empty;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return string.Empty;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            // The foreground window can vanish between the two calls; not an error.
            return string.Empty;
        }
    }
}

/// <summary>Per-user run-at-logon entry (no elevation required).</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "InkCanvasWinUI";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled, string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (enabled) key.SetValue(ValueName, $"\"{executablePath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* registry may be locked down by policy; leave state unchanged */ }
    }
}
