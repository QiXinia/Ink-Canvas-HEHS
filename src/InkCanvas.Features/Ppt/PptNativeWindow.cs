using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using InkCanvas.Platform.Interop;

namespace InkCanvas.Features.Ppt;

/// <summary>PowerPoint's late-bound SlideShowWindow does not expose HWND in every version.</summary>
internal static class PptNativeWindow
{
    public static nint Find(object show)
    {
        nint result = 0;
        dynamic expectedShow = show;
        bool fullScreen = Convert.ToInt32(expectedShow.IsFullScreen) != 0;
        var processIds = Process.GetProcessesByName("POWERPNT").Concat(Process.GetProcessesByName("wpp"))
            .Select(process => { using (process) return (uint)process.Id; }).ToHashSet();
        double showLeft = Convert.ToDouble(expectedShow.Left), showTop = Convert.ToDouble(expectedShow.Top);
        double showWidth = Convert.ToDouble(expectedShow.Width), showHeight = Convert.ToDouble(expectedShow.Height);
        object presentation = expectedShow.Presentation;
        nint expectedPresentation = Marshal.GetIUnknownForObject(presentation);
        nint expected = Marshal.GetIUnknownForObject(show);
        try
        {
            EnumWindows((root, _) =>
            {
                if (!NativeMethods.IsWindowVisible(root)) return true;
                NativeMethods.GetWindowThreadProcessId(root, out uint processId);
                if (!processIds.Contains(processId)) return true;
                var name = new StringBuilder(128);
                GetClassNameW(root, name, name.Capacity);
                string rootClass = name.ToString();
                if (rootClass is not ("PPTFrameClass" or "screenClass" or "wppSlideShow")) return true;
                bool matches = Matches(root);
                if (!matches) EnumChildWindows(root, (pane, _) =>
                {
                    GetClassNameW(pane, name, name.Capacity);
                    if (name.ToString() != "mdiClass" && !name.ToString().StartsWith("paneClassDC", StringComparison.Ordinal)) return true;
                    matches = Matches(pane);
                    return !matches;
                }, 0);
                if (!matches)
                {
                    var title = new StringBuilder(512);
                    GetWindowTextW(root, title, title.Capacity);
                    bool sameName = title.ToString().Contains((string)((dynamic)presentation).Name, StringComparison.OrdinalIgnoreCase);
                    if (sameName && fullScreen && rootClass == "screenClass") matches = true;
                    if (sameName && !fullScreen && GetWindowRect(root, out var rect))
                    {
                        double scale = NativeMethods.GetDpiForWindow(root) / 72d;
                        matches = Math.Abs(rect.Left - showLeft * scale) < 25 && Math.Abs(rect.Top - showTop * scale) < 25
                            && Math.Abs(rect.Right - rect.Left - showWidth * scale) < 25
                            && Math.Abs(rect.Bottom - rect.Top - showHeight * scale) < 25;
                    }
                }
                if (matches) result = root;
                return result == 0;
            }, 0);
        }
        finally { Marshal.Release(expected); Marshal.Release(expectedPresentation); Marshal.ReleaseComObject(presentation); }
        return result;

        bool Matches(nint window)
        {
            var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
            if (AccessibleObjectFromWindow(window, 0xfffffff0 /* OBJID_NATIVEOM */, ref dispatch, out var value) != 0 || value is null) return false;
            try
            {
                nint actual = Marshal.GetIUnknownForObject(value);
                try
                {
                    if (actual == expected) return true;
                    // Office can expose a distinct automation wrapper for the native pane.
                    try
                    {
                        if ((Convert.ToInt32(((dynamic)value).IsFullScreen) != 0) != fullScreen) return false;
                        object document = ((dynamic)value).Presentation;
                        try
                        {
                            nint identity = Marshal.GetIUnknownForObject(document);
                            try { return identity == expectedPresentation; }
                            finally { Marshal.Release(identity); }
                        }
                        finally { Marshal.ReleaseComObject(document); }
                    }
                    catch (COMException) { return false; }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
                }
                finally { Marshal.Release(actual); }
            }
            finally { Marshal.ReleaseComObject(value); }
        }
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeMethods.RECT bounds);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(nint window, uint objectId,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.IDispatch)] out object value);
}
