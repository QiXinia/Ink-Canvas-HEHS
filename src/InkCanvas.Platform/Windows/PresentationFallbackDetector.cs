#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace InkCanvas.Platform.Windows;

/// <summary>A full-screen presentation window found without COM automation.</summary>
public sealed record FallbackShowWindow(
    nint Handle,
    string ProcessName,
    string ClassName,
    ShowWindowInfo Bounds);

/// <summary>
/// Degraded slideshow detection used when the PowerPoint/WPS COM object is unavailable.
/// <para>
/// It only answers "which window looks like a slideshow right now" — it deliberately knows
/// nothing about slide numbers. Callers must still treat the result as a hint, since any
/// full-screen media player or browser can match the geometry heuristic.
/// </para>
/// </summary>
public static class PresentationFallbackDetector
{
    private const int MaxClassNameLength = 256;
    private const string PowerPointSlideShowClassFragment = "screenClass";

    private static readonly string[] PresentationProcessNames =
    [
        "POWERPNT", // Microsoft PowerPoint
        "WPP",      // WPS Presentation
        "wps",      // WPS Office
        "KWPP",     // Kingsoft WPS Presentation
    ];

    private static readonly EnumWindowsProc EnumWindowsCallback = OnEnumWindow;

    [ThreadStatic]
    private static FallbackShowWindow? _found;

    /// <summary>
    /// Returns the first visible, full-screen top-level window owned by a presentation
    /// process (or carrying PowerPoint's slideshow window class), or <c>null</c> when none
    /// matches. Never throws.
    /// </summary>
    public static FallbackShowWindow? Detect()
    {
        try
        {
            _found = null;
            if (!EnumWindows(EnumWindowsCallback, 0)) return _found;
            return _found;
        }
        catch
        {
            return null;
        }
        finally
        {
            _found = null;
        }
    }

    private static bool OnEnumWindow(nint hwnd, nint lParam)
    {
        try
        {
            // EnumWindows only yields top-level windows; hidden ones are filtered by TryGet.
            if (!ShowWindowTracker.TryGet(hwnd, out ShowWindowInfo bounds)) return true;
            if (!bounds.IsVisible || !bounds.IsFullScreen) return true;

            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0 || processId == (uint)Environment.ProcessId) return true;

            string className = GetClassName(hwnd);
            bool classMatches = className.Contains(PowerPointSlideShowClassFragment,
                StringComparison.OrdinalIgnoreCase);

            string processName = GetProcessName(processId);
            bool processMatches = processName.Length > 0 && Array.Exists(PresentationProcessNames,
                name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));

            if (!processMatches && !classMatches) return true;

            _found = new FallbackShowWindow(hwnd, processName, className, bounds);
            return false; // stop enumeration at the first match
        }
        catch
        {
            return true; // a single bad window must not abort the scan
        }
    }

    private static string GetClassName(nint hwnd)
    {
        try
        {
            var buffer = new StringBuilder(MaxClassNameLength);
            int length = GetClassNameW(hwnd, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, Math.Min(length, buffer.Length)) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName ?? string.Empty;
        }
        catch
        {
            // Packaged / protected processes refuse Process access; fall back to class matching.
            return string.Empty;
        }
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hWnd, StringBuilder lpClassName, int nMaxCount);
}