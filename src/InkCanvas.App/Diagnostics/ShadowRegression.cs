using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Diagnostics;

internal static class ShadowRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var windows = new List<Window>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        var bar = new FloatingBarWindow();
        bar.Bar.Settings = services.Settings;
        bar.Bar.Controller = services.OverlayInk;
        bar.Bar.Build();
        windows.Add(bar);
        bool shadowRead = GetSystemParameter(0x1024 /* SPI_GETDROPSHADOW */, 0, out int originalShadow, 0);
        try
        {
            WindowChrome.ShowWithoutActivation(bar);
            await Task.Delay(200);
            foreach (bool highlighter in new[] { false, true })
            {
                var palette = new PenPaletteWindow(services.OverlayInk, highlighter, 1, 100);
                windows.Add(palette);
                WindowChrome.ShowWithoutActivation(palette);
            }
            foreach (var kind in new[] { BarPanelKind.More, BarPanelKind.Gesture, BarPanelKind.Eraser })
            {
                var panel = new BarPanelWindow(bar, bar.Bar, kind);
                windows.Add(panel);
                WindowChrome.ShowWithoutActivation(panel);
            }
            var stroke = new InkStroke(new StrokeAttributes());
            stroke.AddPoint(40, 40); stroke.AddPoint(200, 80); stroke.Complete();
            services.OverlayInk.Document.Add(stroke);
            services.OverlayInk.BeginPlayback();
            var replay = new ReplayWindow(services.OverlayInk, 1, 100, bar);
            windows.Add(replay);
            WindowChrome.ShowWithoutActivation(replay);
            await Task.Delay(200);

            foreach (int enabled in new[] { 0, 1 })
            {
                bool set = shadowRead && SetSystemParameter(0x1025 /* SPI_SETDROPSHADOW */, 0, new(enabled), 0);
                Check("system shadow switch " + enabled, set && GetSystemParameter(0x1024, 0, out int actual, 0) && actual == enabled);
                foreach (var window in windows)
                {
                    var hwnd = WindowChrome.GetHwnd(window);
                    SendMessageW(hwnd, 0x001A /* WM_SETTINGCHANGE */, new(0x1025), IntPtr.Zero);
                    await Task.Delay(50);
                    CheckFrame(window, "shadow " + enabled);

                    // Simulate the frame and backdrop state being recreated by the window manager.
                    int nativeFrame = 2, rounded = 2;
                    DwmSetWindowAttribute(hwnd, 2, ref nativeFrame, sizeof(int));
                    DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
                    NativeMethods.AddExStyle(hwnd, 0x00000100 /* WS_EX_WINDOWEDGE */);
                    SendMessageW(hwnd, 0x031A /* WM_THEMECHANGED */, IntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(70);
                    CheckFrame(window, "theme and native frame reset, shadow " + enabled);
                    window.AppWindow.Hide();
                    WindowChrome.ShowWithoutActivation(window);
                    await Task.Delay(50);
                    CheckFrame(window, "hide and restore, shadow " + enabled);
                }
            }
            foreach (double scale in new[] { 50d, 100d, 200d })
            {
                bar.Reposition(100, scale);
                await Task.Delay(100);
                CheckFrame(bar, "toolbar scale " + scale);
                Check("toolbar card clipping fits at " + scale, SelfTest.CheckWindowRegion(bar, bar.Bar.ChromeSurfaces));
                var origin = WindowChrome.GetClientPosition(bar);
                for (int i = 0; i < 10; i++)
                    SendMessageW(WindowChrome.GetHwnd(bar), 0x001A, new(0x1025), IntPtr.Zero);
                await Task.Delay(100);
                Check("repeated shadow notifications keep toolbar position at " + scale,
                    origin == WindowChrome.GetClientPosition(bar));
                Check("floating tools retain nonactivating input at " + scale,
                    NativeMethods.HasExStyle(WindowChrome.GetHwnd(bar), NativeMethods.WS_EX_NOACTIVATE));
            }
            bar.SetFolded(true);
            await Task.Delay(100);
            foreach (var handle in bar.FoldHandles) CheckFrame(handle, "folded edge handle");
            bar.SetFolded(false);
            await Task.Delay(100);
            CheckFrame(bar, "unfolded toolbar");

            var settings = new SettingsWindow(services);
            windows.Add(settings);
            WindowChrome.ShowWithoutActivation(settings);
            var countdown = new CountdownWindow();
            windows.Add(countdown);
            WindowChrome.ShowWithoutActivation(countdown);
            await Task.Delay(100);
            foreach (var standard in new Window[] { settings, countdown })
            {
                int result = DwmGetWindowAttribute(WindowChrome.GetHwnd(standard), 1, out int native, sizeof(int));
                Check("standard window has system frame " + standard.Title,
                    result == 0 && native != 0 && ClassroomWindowRegression.CheckNativeFrame(standard, extended: true));
            }

            // A reused HWND must be able to opt back into a standard system frame.
            var converted = new Window { Content = new Border { Width = 200, Height = 100 }, Title = "Frame transition" };
            windows.Add(converted);
            WindowChrome.MakeBorderless(converted);
            WindowChrome.ClipToSurfaces(converted, () => new[] { (Border)converted.Content });
            WindowChrome.ShowWithoutActivation(converted);
            await Task.Delay(50);
            WindowChrome.MakeStandard(converted, 400, 300);
            SendMessageW(WindowChrome.GetHwnd(converted), 0x031A, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(100);
            var convertedHandle = WindowChrome.GetHwnd(converted);
            DwmGetWindowAttribute(convertedHandle, 1, out int convertedNative, sizeof(int));
            DwmGetWindowAttribute(convertedHandle, 33, out int convertedCorner, sizeof(int));
            var convertedRegion = CreateRectRgn(0, 0, 1, 1);
            int convertedRegionKind = GetWindowRgn(convertedHandle, convertedRegion);
            NativeMethods.DeleteObject(convertedRegion);
            checks.Add(new { check = "standard frame detaches clipped frame policy",
                passed = ClassroomWindowRegression.CheckNativeFrame(converted), convertedNative, convertedCorner, convertedRegionKind,
                style = NativeMethods.GetWindowLongW(convertedHandle, NativeMethods.GWL_STYLE),
                extendedTitle = converted.ExtendsContentIntoTitleBar });
            bool? pixels = await SelfTest.CheckBackdropTransparency(bar, checks);
            checks.Add(new { check = "desktop shadow fringe readback", passed = pixels, available = pixels.HasValue });
        }
        catch (Exception exception)
        {
            checks.Add(new { check = "shadow regression exception", passed = false, error = exception.ToString() });
        }
        finally
        {
            if (shadowRead)
            {
                bool restored = SetSystemParameter(0x1025, 0, new(originalShadow), 0)
                    && GetSystemParameter(0x1024, 0, out int actual, 0) && actual == originalShadow;
                Check("system shadow preference restored", restored);
            }
            foreach (var window in windows.AsEnumerable().Reverse()) window.Close();
            File.WriteAllText(Path.Combine(directory, "shadows.json"), JsonSerializer.Serialize(checks,
                new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }

        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        void CheckFrame(Window window, string stage)
        {
            var hwnd = WindowChrome.GetHwnd(window);
            int result = DwmGetWindowAttribute(hwnd, 1, out int native, sizeof(int));
            int corners = DwmGetWindowAttribute(hwnd, 33, out int corner, sizeof(int));
            int style = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_STYLE);
            int extended = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
            checks.Add(new { check = "clipped window frame", window = window.Title, stage, native, corner, style, extended,
                passed = result == 0 && native == 0 && (corners != 0 || corner == 1)
                    && (style & 0x00C40000) == 0 && (extended & 0x00020300) == 0 });
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool GetSystemParameter(uint action, uint parameter, out int value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SetSystemParameter(uint action, uint parameter, IntPtr value, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
