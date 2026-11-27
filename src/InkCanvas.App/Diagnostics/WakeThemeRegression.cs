using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Theming;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InkCanvas.App.Diagnostics;

internal static class WakeThemeRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        using var shell = new ShellController(services);
        var keeper = new Window { Content = new Grid() };
        keeper.Activate(); keeper.AppWindow.Hide();
        NativeMethods.GetCursorPos(out var savedCursor);
        shell.Start(); shell.StartInClickThrough();
        var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
        int presses = 0;
        Window colors = null;
        try
        {
            await Task.Delay(200);
            WindowChrome.EnsureAbove(bar);
            ((FrameworkElement)bar.Content).AddHandler(UIElement.PointerPressedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => presses++), true);
            bool nativeCursor = SetCursorPos(savedCursor.X, savedCursor.Y);
            if (!nativeCursor) checks.Add(new { check = "native mouse injection", available = false, error = Marshal.GetLastWin32Error() });
            foreach (bool stall in new[] { false, true })
            {
                if (!nativeCursor) break;
                var point = HandleCenter();
                bool cursorSet = SetCursorPos(point.X, point.Y);
                await Task.Delay(45);
                Check("drag target is reachable", cursorSet && GetAncestor(WindowFromPoint(point), 2) == WindowChrome.GetHwnd(bar),
                    new { point, bar.AppWindow.IsVisible, bar.IsFolded, bar.AppWindow.Position, bar.AppWindow.Size,
                        target = GetAncestor(WindowFromPoint(point), 2).ToInt64(), expected = WindowChrome.GetHwnd(bar).ToInt64() });
                Mouse(true);
                await Task.Delay(60);
                var before = bar.AppWindow.Position;
                SetCursorPos(point.X + 35, point.Y - 20);
                await Task.Delay(90);
                Check("mouse drag follows physical position", bar.AppWindow.Position != before, new { stall, before, after = bar.AppWindow.Position, presses });
                var stopped = bar.AppWindow.Position;
                if (stall) Thread.Sleep(1150);
                else
                {
                    Power(0x0004);
                    Power(0x0012);
                    await Task.Delay(120);
                    stopped = bar.AppWindow.Position;
                }
                SetCursorPos(point.X + 90, point.Y - 50);
                await Task.Delay(100);
                Check("interrupted drag does not follow a held pointer", bar.AppWindow.Position == stopped, stall);
                Mouse(false);
                await Task.Delay(80);
                bool collapsed = bar.Bar.IsMainCollapsed;
                point = HandleCenter(); SetCursorPos(point.X, point.Y);
                await Task.Delay(45);
                Mouse(true); await Task.Delay(40); Mouse(false); await Task.Delay(160);
                Check("smiley remains clickable after interruption", collapsed != bar.Bar.IsMainCollapsed, stall);
                bar.Bar.FinishMainCollapseAnimation();
            }

            // Identical monitor topology must still repair wake-time placement and hit testing.
            WindowChrome.Place(bar, -30000, -30000, bar.AppWindow.Size.Width, bar.AppWindow.Size.Height);
            Power(0x0012); await Task.Delay(180);
            var work = WindowChrome.GetWorkArea(bar);
            var position = WindowChrome.GetClientPosition(bar);
            Check("resume refreshes unchanged monitor topology", position.X >= work.X && position.Y >= work.Y
                && position.X + bar.AppWindow.ClientSize.Width <= work.X + work.Width
                && position.Y + bar.AppWindow.ClientSize.Height <= work.Y + work.Height);

            await TouchDrag();

            var root = new StackPanel { Width = 440, Spacing = 12, Padding = new Thickness(16) };
            var header = new PopupHeader("面板颜色检查", 1) { IsPinned = true };
            var primary = new TouchIconButton { Glyph = Glyphs.Pen, Label = "主操作",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var style = new PenStylePicker { SelectedIndex = 0 };
            root.Children.Add(header); root.Children.Add(primary); root.Children.Add(style);
            colors = new Window { Title = "唤醒与颜色检查", Content = root };
            WindowChrome.MakeStandard(colors, 480, 300, topmost: false);
            WindowChrome.ShowWithoutActivation(colors);
            foreach (int theme in new[] { 1, 2, 0, 1 })
            foreach (Color accent in new[] { Color.FromArgb(255, 0, 103, 192), Color.FromArgb(255, 255, 245, 157) })
            {
                WindowChrome.ApplyTheme(colors, theme); header.ApplyTheme(theme); style.Update(theme);
                var ink = ColorContrast.Foreground(ToInk(accent));
                var fill = new SolidColorBrush(accent);
                var text = new SolidColorBrush(Color.FromArgb(ink.A, ink.R, ink.G, ink.B));
                primary.Background = fill; primary.Foreground = text;
                primary.Resources["AccentButtonBackgroundPointerOver"] = fill;
                primary.Resources["AccentButtonBackgroundPressed"] = fill;
                primary.Resources["AccentButtonForegroundPointerOver"] = text;
                primary.Resources["AccentButtonForegroundPressed"] = text;
                var pin = Descendants(header).OfType<ToggleButton>().Single();
                foreach (string suffix in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
                {
                    pin.Resources["ToggleButtonBackground" + suffix] = fill;
                    pin.Resources["ToggleButtonForeground" + suffix] = text;
                }
                await Task.Delay(80);
                foreach (string state in new[] { "Normal", "PointerOver", "Pressed" })
                {
                    VisualStateManager.GoToState(primary, state, false);
                    var presenter = Descendants(primary).OfType<ContentPresenter>().First();
                    var foreground = ((SolidColorBrush)presenter.Foreground).Color;
                    Check("custom icon and label follow button state foreground", Descendants(primary.Content as DependencyObject)
                        .OfType<FontIcon>().All(icon => ((SolidColorBrush)icon.Foreground).Color == foreground)
                        && Descendants(primary.Content as DependencyObject).OfType<TextBlock>()
                            .All(label => ((SolidColorBrush)label.Foreground).Color == foreground)
                        && ColorContrast.Ratio(ToInk(accent), ToInk(foreground)) >= 4.5,
                        new { theme, accent = accent.ToString(), state });
                }
                foreach (string state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
                {
                    VisualStateManager.GoToState(pin, state, false);
                    var presenter = Descendants(pin).OfType<ContentPresenter>().First();
                    var foreground = ((SolidColorBrush)presenter.Foreground).Color;
                    var icon = (FontIcon)pin.Content;
                    Check("pinned panel icon follows selected foreground", ((SolidColorBrush)icon.Foreground).Color == foreground
                        && ColorContrast.Ratio(ToInk(accent), ToInk(foreground)) >= 4.5,
                        new { theme, accent = accent.ToString(), state });
                }
                foreach (var stop in PopupBrushes.FloatBarColorful(theme).GradientStops)
                foreach (byte backdrop in new byte[] { 0, 255 })
                {
                    var mixed = InkColor.Opaque(Blend(stop.Color.R), Blend(stop.Color.G), Blend(stop.Color.B));
                    Check("colorful toolbar retains readable foreground", ColorContrast.Ratio(mixed,
                        PopupBrushes.IsDark(theme) ? InkColor.White : InkColor.Black) >= 4.5, new { theme, backdrop });
                    byte Blend(byte component) => (byte)((component * stop.Color.A + backdrop * (255 - stop.Color.A)) / 255);
                }
                await BoardWindowRegression.SaveVisual((FrameworkElement)colors.Content,
                    Path.Combine(directory, $"buttons-{theme}-{accent.R}.png"));
            }
        }
        catch (Exception ex) { Check("wake and theme exception", false, ex.ToString()); }
        finally
        {
            Mouse(false); SetCursorPos(savedCursor.X, savedCursor.Y);
            colors?.Close();
            keeper.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "wake-theme.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }

        void Check(string name, bool passed, object detail = null) => checks.Add(new { check = name, passed, detail });
        void Power(int code) => NativeMethods.SendMessageW(shell.Overlay.Window.Handle,
            NativeMethods.WM_POWERBROADCAST, new IntPtr(code), IntPtr.Zero);
        NativeMethods.POINT HandleCenter()
        {
            var bounds = WindowChrome.GetScreenBounds(bar, bar.Bar.ChromeSurfaces.First());
            return new() { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
        }

        async Task TouchDrag()
        {
            if (!InitializeTouchInjection(1, 3))
            {
                checks.Add(new { check = "native touch injection", available = false, error = Marshal.GetLastWin32Error() });
                return;
            }
            var point = HandleCenter();
            var contact = new[] { new NativeMethods.POINTER_TOUCH_INFO
            {
                pointerInfo = new() { pointerType = 2, pointerId = 0, pointerFlags = 0x10006, ptPixelLocation = point },
                touchMask = 1, rcContact = new() { Left = point.X - 2, Right = point.X + 2, Top = point.Y - 2, Bottom = point.Y + 2 },
            } };
            bool injected = InjectTouchInput(1, contact);
            if (!injected)
            {
                checks.Add(new { check = "native touch injection", available = false, error = Marshal.GetLastWin32Error() });
                return;
            }
            try
            {
                await Task.Delay(60);
                var before = bar.AppWindow.Position;
                contact[0].pointerInfo.pointerFlags = 0x20006;
                contact[0].pointerInfo.ptPixelLocation.X += 40;
                injected &= InjectTouchInput(1, contact);
                await Task.Delay(100);
                Check("touch drag follows contact coordinates", injected && bar.AppWindow.Position != before,
                    new { injected, error = Marshal.GetLastWin32Error(), before, after = bar.AppWindow.Position, presses });
                Power(0x0004); Power(0x0012); await Task.Delay(120);
                var stopped = bar.AppWindow.Position;
                contact[0].pointerInfo.ptPixelLocation.X += 50;
                injected &= InjectTouchInput(1, contact);
                await Task.Delay(80);
                Check("resume clears an active touch drag", injected && bar.AppWindow.Position == stopped);
            }
            finally
            {
                contact[0].pointerInfo.pointerFlags = 0x40000;
                InjectTouchInput(1, contact); await Task.Delay(60);
            }
        }
    }

    private static InkColor ToInk(Color color) => new(color.A, color.R, color.G, color.B);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Mouse(bool down)
    {
        bool swapped = NativeMethods.GetSystemMetrics(23) != 0;
        mouse_event(down ? (swapped ? 0x0008u : 0x0002u) : (swapped ? 0x0010u : 0x0004u), 0, 0, 0, UIntPtr.Zero);
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InitializeTouchInjection(uint maximum, uint mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectTouchInput(uint count, [In] NativeMethods.POINTER_TOUCH_INFO[] input);
}
