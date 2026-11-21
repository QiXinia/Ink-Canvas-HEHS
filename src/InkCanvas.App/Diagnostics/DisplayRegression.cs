using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.System;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class DisplayRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.AutoSavedStrokesLocation = Path.Combine(directory, "captures");
        using var shell = new ShellController(services);
        shell.Start();
        shell.StartInClickThrough();
        try
        {
            var screens = ScreenService.All();
            checks.Add(new { check = "physical monitor topology", monitors = screens.Select(s => new
                { s.Bounds, s.WorkArea, s.DpiScale }), mixedDpiHardwareAvailable = screens.Select(s => s.DpiScale).Distinct().Count() > 1 });
            var overlay = shell.Overlay;
            Check("annotation viewport covers virtual desktop", ScreenService.Virtual ==
                new ScreenRect(overlay.Window.X, overlay.Window.Y, overlay.Window.Width, overlay.Window.Height));
            Check("desktop screenshots include all physical monitors", shell.GetScreenshotBounds() == ScreenService.Virtual);
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            await Task.Delay(250);
            int cornerResult = DwmGetWindowAttribute(WindowChrome.GetHwnd(bar), 33, out int corner, sizeof(int));
            int nonClientResult = DwmGetWindowAttribute(WindowChrome.GetHwnd(bar), 1 /* DWMWA_NCRENDERING_ENABLED */, out int nonClient, sizeof(int));
            Check("clipped toolbar suppresses rectangular native corner and shadow", cornerResult == 0 && corner == 1 && nonClientResult == 0 && nonClient == 0);
            foreach (var screen in screens)
            {
                WindowChrome.MoveToMonitor(bar, screen);
                Check("desktop capture extent is independent of toolbar monitor", shell.GetScreenshotBounds() == ScreenService.Virtual);
                foreach (double scale in new[] { 50d, 100, 200 })
                {
                    bar.Reposition(100, scale);
                    await Task.Delay(120);
                    Check($"toolbar fits monitor {screen.Bounds.X},{screen.Bounds.Y} at {scale}%", Fits(bar, screen.WorkArea));
                    Check("toolbar retains opaque controls", bar.Bar.Opacity == 1);
                }
                bar.Reposition(100, 100);
                foreach (bool highlighter in new[] { false, true })
                {
                    var popup = new PenPaletteWindow(services.OverlayInk, highlighter, 1, 200);
                    popup.PlaceAboveBar(() => bar.GetToolAnchor(highlighter ? InkTool.Highlighter : InkTool.Pen), WindowChrome.GetHwnd(bar));
                    WindowChrome.ShowWithoutActivation(popup);
                    await Task.Delay(120);
                    Check("palette fits owner monitor", Fits(popup, screen.WorkArea));
                    Check("palette uses owner monitor DPI", Math.Abs(WindowChrome.GetDpiScale(popup) - screen.DpiScale) < .01);
                    popup.Close();
                }
                for (int side = 0; side < 2; side++)
                {
                    shell.ToggleFold();
                    await Task.Delay(100);
                    Check("both folded handles stay on owner monitor", bar.FoldHandles.Count == 2 && bar.FoldHandles.All(h => Fits(h, screen.WorkArea)));
                    var button = (Button)bar.FoldHandles[side].Content;
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Task.Delay(1050);
                    Check("edge handle restores toolbar on owner monitor", !bar.IsFolded && Fits(bar, screen.WorkArea));
                }
                shell.OpenTimer();
                await Task.Delay(160);
                var timer = shell.OpenWindows.OfType<CountdownWindow>().Single();
                Check("countdown opens on toolbar monitor", Fits(timer, screen.WorkArea));
                timer.Close();
            }
            Check("mode and popup switches retain exact toolbar origin", await SelfTest.CheckToolbarPosition(shell, checks));
            bool? translucent = await SelfTest.CheckBackdropTransparency(shell, checks);
            checks.Add(new { check = "desktop translucency readback", passed = translucent, skipped = translucent is null });

            // Exercise real DIB/Win2D reallocation with negative origins and different aspect ratios.
            shell.ToggleClickThrough();
            var controller = services.OverlayInk;
            controller.RecognizeShapes = false;
            controller.Tool = InkTool.Pen;
            controller.ViewTransform = Matrix3x2.CreateScale(1.25f) * Matrix3x2.CreateTranslation(15.5f, -8.25f);
            var start = new OverlayPointer(791, 100, 120, .5f, 0, 0, OverlayPointerKind.Pen, 100);
            controller.OnPointerDown(start);
            controller.OnPointerMove(start with { X = 200, Y = 140, TimeMs = 120 });
            controller.OnPointerUp(start with { X = 250, Y = 160, TimeMs = 140 });
            var stroke = controller.Document.Strokes.Single();
            Vector2 world = stroke.Points[stroke.Points.Count / 2].Position;
            Vector2 initial = DesktopPoint(world);
            foreach (var bounds in new[] { new ScreenRect(-400, -200, 1600, 1000), new(0, 0, 900, 700), ScreenService.Virtual })
            {
                overlay.SetDisplayBounds(bounds);
                Check("viewport resize preserves desktop ink position", Vector2.Distance(initial, DesktopPoint(world)) < .01);
                Check("viewport resize preserves ink and undo", controller.Document.Strokes.Single() == stroke && controller.History.CanUndo);
                Check("viewport size matches new DIB", overlay.Window.Width == bounds.Width && overlay.Window.Height == bounds.Height);
                var pixel = Vector2.Transform(world, controller.ViewTransform) * overlay.Window.DpiScale;
                int pixelOffset = ((int)pixel.Y * bounds.Width + (int)pixel.X) * 4 + 3;
                Check("ink redraws at the preserved position after DIB resize", System.Runtime.InteropServices.Marshal.ReadByte(overlay.Window.PixelBuffer, pixelOffset) > 16);
                controller.Undo();
                Check("undo works after viewport change", controller.Document.Count == 0);
                controller.Redo();
                Check("redo works after viewport change", controller.Document.Strokes.Single() == stroke);
            }
            overlay.Hide();
            overlay.SetDisplayBounds(new(-100, -100, 1200, 800));
            Check("hidden viewport remains hidden after resize", !overlay.Window.IsVisible);
            overlay.RefreshDisplayBounds();
            overlay.Show();
            Check("resized hidden viewport restores", overlay.Window.IsVisible && Vector2.Distance(initial, DesktopPoint(world)) < .01);
            await CheckTopologyChanges();
            try { await CheckScreenshots(); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 5 or 6)
            {
                checks.Add(new { check = "desktop pixel capture availability", skipped = true, error = ex.NativeErrorCode, detail = ex.Message });
            }
            await CheckReplayLayout(bar);
            CheckIndependentTouch();
            await CheckNativeTouch();
            await CheckDisplaySettings();

            Vector2 DesktopPoint(Vector2 p) => Vector2.Transform(p, controller.ViewTransform) * overlay.Window.DpiScale
                + new Vector2(overlay.Window.X, overlay.Window.Y);
        }
        catch (Exception ex) { checks.Add(new { check = "display regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "displays.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });

        async Task CheckTopologyChanges()
        {
            var window = shell.Overlay.Window;
            var ink = services.OverlayInk;
            var settings = services.Settings.Display;
            bool all = settings.AllDisplays;
            var enabled = settings.EnabledDevices.ToArray();
            ScreenInfo[] layout =
            {
                new(new(111), new(-800, -200, 800, 1000), default, 1.5) { DeviceName = "test-left" },
                new(new(222), new(0, -200, 1200, 1000), default, 1) { DeviceName = "test-right" },
            };
            try
            {
                settings.AllDisplays = false;
                settings.EnabledDevices.Clear(); settings.EnabledDevices.Add("test-left");
                shell.Overlay.ConfigureDisplays(settings);
                shell.RefreshDisplays(layout);
                var desktop = new ScreenRect(window.X, window.Y, window.Width, window.Height);
                Check("monitor snapshot resizes the overlay to the full physical union", desktop == new ScreenRect(-800, -200, 2000, 1000));
                Check("custom writing displays refresh with the monitor snapshot", window.AllowsInput(-400, 100) && !window.AllowsInput(100, 100));
                window.ProcessTouch(new() { Source = new(77), Id = 1, X = -40000, Y = 10000, Flags = 2, Time = 1 });
                Check("touch is active before the monitor layout changes", ink.HasActiveInteraction);
                layout = new[]
                {
                    layout[0] with { Bounds = new(-800, -200, 1000, 1000), DpiScale = 2 },
                    layout[1] with { Bounds = new(200, -200, 1000, 1000), DpiScale = 1.25 },
                };
                shell.RefreshDisplays(layout);
                Check("same outer bounds still refresh monitor input areas", desktop == new ScreenRect(window.X, window.Y, window.Width, window.Height)
                    && window.AllowsInput(100, 100) && !window.AllowsInput(300, 100));
                Check("monitor changes cancel contacts without leaving stuck ink", !ink.HasActiveInteraction);
                OverlayPointer updatedContact = default;
                void CaptureUpdatedContact(OverlayPointer pointer) => updatedContact = pointer;
                window.PointerDown += CaptureUpdatedContact;
                try
                {
                    window.ProcessTouch(new() { Source = new(77), Id = 1, X = 10000, Y = 10000, Flags = 2, Mask = 4, Width = 800, Height = 800, Time = 2 });
                    window.ProcessTouch(new() { Source = new(77), Id = 1, X = 10000, Y = 10000, Flags = 4, Mask = 4, Width = 800, Height = 800, Time = 3 });
                    Check("unchanged viewport uses the updated monitor DPI for touch", updatedContact.InputSurface == 111
                        && Math.Abs(updatedContact.ContactWidth - 4) < .01 && !ink.HasActiveInteraction);
                }
                finally { window.PointerDown -= CaptureUpdatedContact; }
                shell.RefreshDisplays(Array.Empty<ScreenInfo>());
                Check("transient empty enumeration retains the last working viewport", desktop == new ScreenRect(window.X, window.Y, window.Width, window.Height)
                    && window.AllowsInput(100, 100));
                shell.RefreshDisplays(new[] { layout[1] });
                Check("disconnecting an enabled monitor releases the remaining desktop for input", !window.AllowsInput(500, 100));
                NativeMethods.SendMessageW(window.Handle, 0x0219, new(0x0007), IntPtr.Zero);
                await Task.Delay(150);
                Check("native hotplug notification restores the connected monitor topology", ScreenService.Virtual == new ScreenRect(window.X, window.Y, window.Width, window.Height));
                shell.RefreshDisplays(layout);
                NativeMethods.SendMessageW(window.Handle, NativeMethods.WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(150);
                Check("native resolution notification restores the connected monitor topology", ScreenService.Virtual == new ScreenRect(window.X, window.Y, window.Width, window.Height));
            }
            finally
            {
                window.CancelInput();
                settings.AllDisplays = all;
                settings.EnabledDevices.Clear(); settings.EnabledDevices.AddRange(enabled);
                shell.RefreshDisplays(ScreenService.All());
                shell.Overlay.ConfigureDisplays(settings);
            }
        }

        async Task CheckScreenshots()
        {
            foreach (var screen in ScreenService.All())
            {
                int x = screen.Bounds.X + screen.Bounds.Width / 2, y = screen.Bounds.Y + screen.Bounds.Height / 2;
                using var marker = new LayeredInkWindow(x, y, 32, 32) { ClickThrough = true };
                var color = new byte[32 * 32 * 4];
                for (int index = 0; index < color.Length; index += 4) { color[index + 2] = 255; color[index + 3] = 255; }
                System.Runtime.InteropServices.Marshal.Copy(color, 0, marker.PixelBuffer, color.Length);
                marker.Present();
                await Task.Delay(120);
                var image = ScreenshotService.Capture(x, y, 32, 32);
                int center = (16 * 32 + 16) * 4;
                Check("screenshot includes layered content on monitor " + screen.DeviceName,
                    image.bgra[center + 2] > 240 && image.bgra[center] < 16 && image.bgra[center + 1] < 16);
                Check("GDI screenshots encode an opaque alpha channel", Enumerable.Range(0, 32 * 32).All(index => image.bgra[index * 4 + 3] == 255));
            }
            await shell.ScreenshotAsync();
            string screenshot = Directory.GetFiles(Path.Combine(services.Settings.Automation.AutoSavedStrokesLocation, "Screenshots"), "*.png", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).First();
            using var bitmap = await Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), screenshot);
            var bounds = ScreenService.Virtual;
            Check("normal screenshot command saves a full virtual desktop PNG", bitmap.SizeInPixels.Width == bounds.Width && bitmap.SizeInPixels.Height == bounds.Height);
        }

        async Task CheckReplayLayout(FloatingBarWindow owner)
        {
            using var ink = new InkController();
            var stroke = new InkStroke(new StrokeAttributes()); stroke.AddPoint(100, 100); stroke.AddPoint(200, 120); stroke.Complete();
            ink.Document.Add(stroke);
            foreach (var screen in ScreenService.All())
            foreach (double scale in new[] { 50d, 100d, 150d })
            {
                WindowChrome.MoveToMonitor(owner, screen);
                ink.BeginPlayback();
                var replay = new ReplayWindow(ink, 1, scale, owner);
                WindowChrome.ShowWithoutActivation(replay);
                try
                {
                    await Task.Delay(200);
                    replay.RefreshDisplayPlacement();
                    await Task.Delay(80);
                    var root = (FrameworkElement)replay.Content;
                    bool contentFits = Descendants(root).OfType<Control>().All(control =>
                    {
                        var bounds = control.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
                        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
                    });
                    Check("replay toolbar fits loaded controls and monitor " + scale, contentFits && Fits(replay, screen.WorkArea));
                    Check("replay toolbar keeps nonactivating input", NativeMethods.HasExStyle(WindowChrome.GetHwnd(replay), NativeMethods.WS_EX_NOACTIVATE));
                    if (scale == 100) await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "replay-" + screen.Handle + ".png"));
                }
                finally { replay.Close(); }
            }
        }

        void CheckIndependentTouch()
        {
            var overlay = shell.Overlay;
            var window = overlay.Window;
            var ink = services.OverlayInk;
            overlay.SetDisplayBounds(new(-800, -200, 2000, 1000));
            window.SetScreenLayout(new[]
            {
                new ScreenInfo(new IntPtr(111), new(-800, -200, 800, 600), new(-800, -200, 800, 600), 1.5) { DeviceName = "test-left" },
                new ScreenInfo(new IntPtr(222), new(0, 0, 1200, 800), new(0, 0, 1200, 800), 1.25) { DeviceName = "test-right" },
            });
            window.SetInputAreas(null);
            ink.Document.Clear(); ink.History.Clear(); ink.ViewTransform = Matrix3x2.Identity;
            ink.Palm.Advanced = null; ink.MultiTouchEnabled = false; ink.PalmErases = false;
            var received = new List<OverlayPointer>();
            void Capture(OverlayPointer pointer) => received.Add(pointer);
            window.PointerDown += Capture;
            try
            {
                Contact(11, -40000, 2, 0); Contact(22, 40000, 2, 0);
                Contact(11, -35000, 1, 30); Contact(22, 45000, 1, 30);
                Contact(11, -34000, 4, 40); Contact(22, 46000, 4, 40);
                Check("two legacy digitizers with identical contact IDs produce separate strokes", received.Count == 2
                    && received[0].Id != received[1].Id && ink.Document.Count == 2 && !ink.HasActiveInteraction);
                Check("mixed DPI touch groups keep physical screen coordinates", received.Count == 2
                    && received[0].InputSurface == 111 && received[1].InputSurface == 222
                    && Math.Abs(received[0].X * window.DpiScale + window.X + 400) < .01
                    && Math.Abs(received[1].X * window.DpiScale + window.X - 400) < .01);
                Check("contact dimensions use each touch monitor DPI", received.Count == 2
                    && Math.Abs(received[0].ContactWidth - 8 / 1.5f) < .01 && Math.Abs(received[1].ContactWidth - 8 / 1.25f) < .01);
                window.SetInputAreas(new[] { new ScreenRect(-800, -200, 800, 600) });
                int before = ink.Document.Count;
                Contact(22, 40000, 2, 50); Contact(22, 45000, 4, 60);
                Check("disabled screen rejects new contacts", ink.Document.Count == before && !window.AllowsInput(400, 100) && window.AllowsInput(-400, 100));
                window.SetInputAreas(Array.Empty<ScreenRect>());
                Check("empty screen selection releases the entire input surface", !window.AllowsInput(-400, 100) && !window.AllowsInput(400, 100));
            }
            finally
            {
                window.PointerDown -= Capture; window.CancelInput(); window.SetInputAreas(null);
                overlay.RefreshDisplayBounds(); overlay.ConfigureDisplays(services.Settings.Display); services.ApplySettings();
            }
            void Contact(int device, int x, uint flags, uint time) => window.ProcessTouch(new LayeredInkWindow.TouchInput
                { Source = new(device), Id = 1, X = x, Y = 10000, Flags = flags, Mask = 4, Width = 800, Height = 800, Time = time });
        }

        async Task CheckDisplaySettings()
        {
            var settings = new SettingsWindow(services);
            settings.Activate(); await Task.Delay(160);
            try
            {
                Check("settings search exposes monitor configuration", settings.SearchSettings("显示器").Any());
                settings.SelectCategory("显示器");
                ((FrameworkElement)settings.Content).UpdateLayout();
                await Task.Delay(100);
                var all = Descendants(settings.Content).OfType<ToggleSwitch>().Single(toggle => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(toggle) == "所有显示器均可书写");
                all.IsOn = false;
                Check("custom screen mode initializes connected displays", !services.Settings.Display.AllDisplays
                    && ScreenService.All().All(screen => services.Settings.Display.EnabledDevices.Contains(screen.DeviceName)));
                var screenToggle = Descendants(settings.Content).OfType<ToggleSwitch>().First(toggle => !ReferenceEquals(toggle, all));
                screenToggle.IsOn = false;
                var first = ScreenService.All().OrderByDescending(screen => screen.IsPrimary).ThenBy(screen => screen.DeviceName).First();
                Check("monitor toggle applies input range and saves immediately", !shell.Overlay.Window.AllowsInput(first.Bounds.X + 10, first.Bounds.Y + 10)
                    && !services.Store.Load().Display.EnabledDevices.Contains(first.DeviceName));
                all.IsOn = true;
                Check("all screens restores writing range", shell.Overlay.Window.AllowsInput(first.Bounds.X + 10, first.Bounds.Y + 10));
                await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, "display-settings.png"));
            }
            finally { settings.Close(); }
        }

        async Task CheckNativeTouch()
        {
            if (!InitializeTouchInjection(2, 3))
            {
                checks.Add(new { check = "native touch injection available", available = false,
                    error = System.Runtime.InteropServices.Marshal.GetLastWin32Error() });
                return;
            }
            var ink = services.OverlayInk;
            ink.MultiTouchEnabled = true; ink.PalmErases = false; ink.Palm.Advanced = null;
            int before = ink.Document.Count;
            var downEvents = new List<OverlayPointer>();
            void RecordDown(OverlayPointer pointer) => downEvents.Add(pointer);
            shell.Overlay.Window.PointerDown += RecordDown;
            var work = ScreenService.Primary;
            var input = new[] { Contact(0, work.X + 150, work.Y + 180), Contact(1, work.X + 350, work.Y + 180) };
            bool injected = InjectTouchInput(2, input);
            int downError = injected ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            int moveError = 0, upError = 0;
            try
            {
                await Task.Delay(80);
                for (int index = 0; index < input.Length; index++)
                {
                    input[index].pointerInfo.pointerFlags = 0x20006;
                    input[index].pointerInfo.ptPixelLocation.X += 60;
                }
                bool moved = InjectTouchInput(2, input);
                moveError = moved ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                injected &= moved;
                await Task.Delay(80);
            }
            finally
            {
                for (int index = 0; index < input.Length; index++) input[index].pointerInfo.pointerFlags = 0x40000;
                bool ended = InjectTouchInput(2, input);
                upError = ended ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                injected &= ended;
                await Task.Delay(120);
                shell.Overlay.Window.PointerDown -= RecordDown;
            }
            checks.Add(new { check = "native Windows touch dispatch completes two strokes without mouse duplicates", passed = injected
                && ink.Document.Count == before + 2 && !ink.HasActiveInteraction, injected, downError, moveError, upError,
                before, after = ink.Document.Count, live = ink.LiveStrokes.Count, downEvents });
            shell.Overlay.Window.CancelInput(); services.ApplySettings();

            NativeMethods.POINTER_TOUCH_INFO Contact(uint id, int x, int y) => new()
            {
                pointerInfo = new() { pointerType = 2, pointerId = id, pointerFlags = 0x10006,
                    ptPixelLocation = new() { X = x, Y = y } },
                touchMask = 1, rcContact = new() { Left = x - 2, Right = x + 2, Top = y - 2, Bottom = y + 2 },
            };
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static bool Fits(Window window, ScreenRect work)
    {
        var position = WindowChrome.GetClientPosition(window);
        var size = window.AppWindow.ClientSize;
        return position.X >= work.X && position.Y >= work.Y
            && position.X + size.Width <= work.X + work.Width && position.Y + size.Height <= work.Y + work.Height;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint mode);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [System.Runtime.InteropServices.In] NativeMethods.POINTER_TOUCH_INFO[] input);
}
