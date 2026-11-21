using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using InkCanvas.Core.Ink;
using InkCanvas.Core.ScreenTracking;
using InkCanvas.Platform.Interop;

namespace InkCanvas.Platform.System;

/// <summary>Reads view metadata from a running Office instance without selecting or editing content.</summary>
internal static class OfficeScreenView
{
    internal static ScreenViewSnapshot Read(TrackedApplication kind, nint target, Rect client, out string error)
    {
        error = null;
        var owned = new List<object>();
        object Own(object value) { if (value is not null && Marshal.IsComObject(value)) owned.Add(value); return value; }
        nint pointer = 0;
        try
        {
            dynamic window = Own(ReadNativeWindow(target, kind, out var nativeBounds));
            dynamic app = null, presentation = null, view = null, slide = null;
            bool slideShow = false;
            bool nativeWindow = window is not null;
            bool verifiedApplication = nativeWindow;
            if (nativeWindow)
            {
                app = Own(window.Application);
                if (kind == TrackedApplication.PowerPoint)
                {
                    // Modern Office exposes both DocumentWindow and windowed SlideShowWindow
                    // from mdiClass. IsFullScreen exists on a show even when its value is false.
                    try { _ = Convert.ToInt32(window.IsFullScreen); slideShow = true; }
                    catch (COMException) { }
                    catch (MissingMemberException) { }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    if (slideShow && !nativeBounds.IsEmpty) client = ScreenContentReader.Intersection(client, nativeBounds);
                }
            }
            else
            {
                if (kind == TrackedApplication.PowerPoint)
                {
                    app = Own(ReadApplicationForProcess(target));
                    verifiedApplication = app is not null;
                }
                if (app is null)
                {
                    string program = kind == TrackedApplication.Word ? "Word.Application" : "PowerPoint.Application";
                    if (CLSIDFromProgID(program, out var id) != 0 || GetActiveObject(ref id, 0, out pointer) != 0 || pointer == 0) return null;
                    app = Own(Marshal.GetObjectForIUnknown(pointer));
                    Marshal.Release(pointer); pointer = 0;
                }
                if (kind == TrackedApplication.PowerPoint)
                {
                    dynamic shows = Own(app.SlideShowWindows);
                    int count = (int)shows.Count;
                    for (int index = 1; index <= count; index++)
                    {
                        dynamic show = Own(shows.Item(index));
                        dynamic candidate = Own(show.Presentation);
                        bool matches = false;
                        try { matches = Convert.ToInt64(show.HWND) == target.ToInt64(); }
                        catch (COMException) { }
                        catch (MissingMemberException) { }
                        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                        // Several Office versions remove HWND from IDispatch. Resolve only
                        // screenClass windows in the same verified Office process in that case.
                        if (!matches && verifiedApplication)
                        {
                            var className = new StringBuilder(128); GetClassNameW(target, className, className.Capacity);
                            var title = new StringBuilder(512); GetWindowTextW(target, title, title.Capacity);
                            string name = Path.GetFileNameWithoutExtension((string)candidate.Name);
                            matches = className.ToString().Equals("screenClass", StringComparison.Ordinal)
                                && (count == 1 || title.ToString().Contains(name, StringComparison.OrdinalIgnoreCase));
                        }
                        if (!matches) continue;
                        window = show; presentation = candidate; view = Own(show.View);
                        slide = Own(view.Slide); slideShow = true; break;
                    }
                    if (window is null) return null;
                }
                else window = Own(app.ActiveWindow);
            }
            NativeMethods.GetWindowThreadProcessId(target, out uint targetPid);
            if (!nativeWindow && !(slideShow && verifiedApplication))
            {
                nint handle = new(Convert.ToInt64(window.HWND));
                NativeMethods.GetWindowThreadProcessId(handle, out uint officePid);
                if (officePid != targetPid || GetAncestor(handle, 2) != GetAncestor(target, 2)) return null;
            }
            view ??= Own(window.View);
            if (kind == TrackedApplication.Word)
            {
                dynamic document = Own(window.Document);
                dynamic zoom = Own(view.Zoom);
                uint dpi = NativeMethods.GetDpiForWindow(target);
                float scale = Convert.ToSingle(zoom.Percentage) / 100 * (dpi == 0 ? 1 : dpi / 96f);
                string identity = "Word:" + targetPid + ":" + ScreenContentReader.Hash((string)document.FullName);
                return new ScreenViewSnapshot { ContextKey = identity, Scale = scale, HasZoomMetrics = true };
            }
            presentation ??= Own(window.Presentation);
            slide ??= Own(view.Slide);
            dynamic setup = Own(presentation.PageSetup);
            float width = Convert.ToSingle(setup.SlideWidth), height = Convert.ToSingle(setup.SlideHeight);
            string key = "PowerPoint:" + targetPid + ":" + ScreenContentReader.Hash((string)presentation.FullName) + ":" + Convert.ToInt32(slide.SlideID);
            Vector2 origin;
            float pixelsPerPoint;
            if (slideShow)
            {
                pixelsPerPoint = Math.Min(client.Width / width, client.Height / height);
                origin = client.Center - new Vector2(width, height) * pixelsPerPoint / 2;
            }
            else
            {
                float x = Convert.ToSingle(window.PointsToScreenPixelsX(0f));
                float y = Convert.ToSingle(window.PointsToScreenPixelsY(0f));
                pixelsPerPoint = Math.Abs(Convert.ToSingle(window.PointsToScreenPixelsX(100f)) - x) / 100;
                origin = new Vector2(x, y);
            }
            var slideBounds = new Rect(origin.X, origin.Y, origin.X + width * pixelsPerPoint, origin.Y + height * pixelsPerPoint);
            var viewport = ScreenContentReader.Intersection(client, slideBounds);
            return new ScreenViewSnapshot { ContextKey = key, Scale = pixelsPerPoint, Viewport = viewport,
                ScrollPixels = new Vector2(viewport.Left, viewport.Top) - origin, HasScrollMetrics = true, HasZoomMetrics = true };
        }
        catch (Exception ex) { error = ex.ToString(); return null; }
        finally
        {
            if (pointer != 0) Marshal.Release(pointer);
            for (int index = owned.Count - 1; index >= 0; index--)
                if (Marshal.IsComObject(owned[index])) Marshal.ReleaseComObject(owned[index]);
        }
    }

    private static object ReadApplicationForProcess(nint target)
    {
        NativeMethods.GetWindowThreadProcessId(target, out uint targetPid);
        object app = null;
        EnumWindows((root, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(root, out uint pid);
            if (pid != targetPid || root == target) return true;
            object window = ReadNativeWindow(root, TrackedApplication.PowerPoint, out var bounds, includeHidden: true);
            if (window is null) return true;
            try { app = ((dynamic)window).Application; }
            catch (COMException) { }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally { Marshal.ReleaseComObject(window); }
            return app is null;
        }, 0);
        return app;
    }

    private static object ReadNativeWindow(nint root, TrackedApplication kind, out Rect bounds, bool includeHidden = false)
    {
        bounds = Rect.Empty;
        nint pane = 0; int largestArea = 0;
        EnumChildWindows(root, (window, _) =>
        {
            var name = new StringBuilder(128); GetClassNameW(window, name, name.Capacity);
            bool matching = IsDocumentPane(kind, name.ToString());
            if (matching && (includeHidden || NativeMethods.IsWindowVisible(window)) && GetClientRect(window, out var rect))
            {
                int area = (rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > largestArea) { largestArea = area; pane = window; }
            }
            return true;
        }, 0);
        if (pane == 0) return null;
        if (GetClientRect(pane, out var client))
        {
            var origin = new NativeMethods.POINT();
            ClientToScreen(pane, ref origin);
            bounds = new Rect(origin.X, origin.Y, origin.X + client.Right, origin.Y + client.Bottom);
        }
        var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
        return AccessibleObjectFromWindow(pane, 0xfffffff0, ref dispatch, out object value) == 0 ? value : null;
    }

    internal static bool IsDocumentPane(TrackedApplication kind, string name) => kind == TrackedApplication.Word
        ? name == "_WwG" : name == "mdiClass" || name.StartsWith("paneClassDC", StringComparison.Ordinal);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string value, out Guid id);
    [DllImport("oleaut32.dll")] private static extern int GetActiveObject(ref Guid id, nint reserved, out nint value);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeMethods.RECT bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativeMethods.POINT point);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(nint window, uint objectId, ref Guid id, [MarshalAs(UnmanagedType.IDispatch)] out object value);
}
