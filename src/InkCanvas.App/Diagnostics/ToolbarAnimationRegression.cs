using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Animation;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace InkCanvas.App.Diagnostics;

internal static class ToolbarAnimationRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        var window = new FloatingBarWindow();
        window.Bar.Settings = services.Settings;
        window.Configure(services.OverlayInk, null);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        bool read = GetSystemParameter(0x1042, 0, out int originalAnimations, 0);
        try
        {
            if (!read) throw new InvalidOperationException("Cannot read the system animation preference.");
            await File.WriteAllTextAsync(Path.Combine(directory, "original-animation-setting.json"), JsonSerializer.Serialize(new { originalAnimations }));
            WindowChrome.ShowWithoutActivation(window);
            await Task.Delay(400);
            await SetAnimations(true);
            foreach (string mode in new[] { "mouse", "ink", "ppt" })
            foreach (double scale in new[] { 50d, 100d, 200d })
            {
                window.Bar.ClickThroughActive = mode == "mouse";
                window.Bar.SetPptModeVisible(mode == "ppt");
                window.Reposition(100, scale);
                await Task.Delay(80);
                var origin = WindowChrome.GetClientPosition(window);
                int expandedWidth = window.AppWindow.ClientSize.Width;
                var smile = WindowChrome.GetScreenBounds(window, window.Bar.ChromeSurfaces.First());
                var pen = WindowChrome.GetScreenBounds(window, window.Bar.GetToolButton(InkTool.Pen));
                var collapse = await Animate();
                Check($"collapse has intermediate widths {mode} {scale}", collapse.Any(frame => frame.Progress is > 0 and < 1)
                    && collapse.Select(frame => frame.Width).Distinct().Count() > 2, collapse);
                Check($"collapse keeps a stable swap chain {mode} {scale}", collapse.Where(frame => frame.Progress is > 0 and < 1)
                    .Select(frame => frame.ClientWidth).Distinct().Count() == 1);
                Check($"collapse preserves control size and origin {mode} {scale}", Stable(collapse, smile, pen, origin));
                Check($"collapse widths decrease {mode} {scale}", collapse.Zip(collapse.Skip(1)).All(pair => pair.Second.Width <= pair.First.Width + 1));
                Check($"collapsed native region matches painted cards {mode} {scale}", SelfTest.CheckWindowRegion(window, window.Bar.ChromeSurfaces));
                var expand = await Animate();
                Check($"expand has intermediate widths {mode} {scale}", expand.Any(frame => frame.Progress is > 0 and < 1)
                    && expand.Select(frame => frame.Width).Distinct().Count() > 2, expand);
                Check($"expand keeps a stable swap chain {mode} {scale}", expand.Where(frame => frame.Progress is > 0 and < 1)
                    .Select(frame => frame.ClientWidth).Distinct().Count() == 1);
                Check($"expand preserves control size and origin {mode} {scale}", Stable(expand, smile, pen, origin));
                Check($"expand widths increase {mode} {scale}", expand.Zip(expand.Skip(1)).All(pair => pair.Second.Width + 1 >= pair.First.Width));
                Check($"expanded geometry restored {mode} {scale}", !window.Bar.IsMainCollapsed
                    && !window.Bar.IsMainCollapseAnimating && Math.Abs(window.AppWindow.ClientSize.Width - expandedWidth) <= 1
                    && SelfTest.CheckWindowRegion(window, window.Bar.ChromeSurfaces));
            }

            Toggle(); await Task.Delay(45);
            double beforeReverse = window.Bar.MainExpansion;
            Toggle();
            Check("rapid repeat reverses from the current visible width", Math.Abs(window.Bar.MainExpansion - beforeReverse) < .001
                && window.Bar.IsMainCollapseAnimating);
            await Task.Delay(350);
            Check("rapid repeat finishes expanded", !window.Bar.IsMainCollapsed && window.Bar.MainExpansion == 1 && !window.Bar.IsMainCollapseAnimating);

            Toggle(); await Task.Delay(45);
            await SetAnimations(false);
            await Task.Delay(50);
            Check("disabling system animations completes an active collapse", window.Bar.IsMainCollapsed
                && window.Bar.MainExpansion == 0 && !window.Bar.IsMainCollapseAnimating);
            Toggle();
            Check("system animations off expands immediately", !window.Bar.IsMainCollapsed && window.Bar.MainExpansion == 1 && !window.Bar.IsMainCollapseAnimating);
            Toggle();
            Check("system animations off collapses immediately", window.Bar.IsMainCollapsed && window.Bar.MainExpansion == 0 && !window.Bar.IsMainCollapseAnimating);
            Toggle();

            await SetAnimations(true);
            Toggle(); await Task.Delay(40);
            window.SetFolded(true);
            Check("folding toolbar stops the expansion timer", !window.Bar.IsMainCollapseAnimating && window.IsFolded);
            window.SetFolded(false); await Task.Delay(80);
            Check("edge handle restore retains a usable smiley", window.Bar.ChromeSurfaces.First().ActualWidth > 0);
            Toggle(); await Task.Delay(40);
            window.Close();
            Check("closing toolbar stops the expansion timer", !window.Bar.IsMainCollapseAnimating);
        }
        catch (Exception ex) { checks.Add(new { check = "toolbar animation exception", passed = false, error = ex.ToString() }); }
        finally
        {
            if (read)
            {
                bool restored = SetSystemParameter(0x1043, 0, new(originalAnimations), 2);
                Check("original system animation preference restored", restored && GetSystemParameter(0x1042, 0, out int actual, 0) && actual == originalAnimations);
            }
            if (!closed) window.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "toolbar-animation.json"), JsonSerializer.Serialize(checks,
                new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Application.Current.Exit();
        }

        void Toggle() => window.Bar.TryHandleTap(window.Bar.ChromeSurfaces.First());
        async Task SetAnimations(bool enabled)
        {
            bool changed = SetSystemParameter(0x1043, 0, new(enabled ? 1 : 0), 2);
            for (int attempt = 0; attempt < 15 && Motion.AnimationsEnabled != enabled; attempt++) await Task.Delay(20);
            Check("system animation preference " + enabled, changed && GetSystemParameter(0x1042, 0, out int actual, 0)
                && (actual != 0) == enabled && Motion.AnimationsEnabled == enabled);
        }
        async Task<List<Frame>> Animate()
        {
            var frames = new List<Frame>();
            Toggle();
            var timeout = Stopwatch.StartNew();
            while (window.Bar.IsMainCollapseAnimating && timeout.ElapsedMilliseconds < 2000)
            {
                await Task.Delay(25);
                var smile = WindowChrome.GetScreenBounds(window, window.Bar.ChromeSurfaces.First());
                var pen = WindowChrome.GetScreenBounds(window, window.Bar.GetToolButton(InkTool.Pen));
                var surfaces = window.Bar.ChromeSurfaces.Where(surface => surface.Visibility == Visibility.Visible && surface.ActualWidth > 0)
                    .Select(surface => WindowChrome.GetScreenBounds(window, surface)).ToArray();
                int visibleWidth = surfaces.Max(rect => rect.X + rect.Width) - smile.X;
                frames.Add(new(window.Bar.MainExpansion, visibleWidth, window.AppWindow.ClientSize.Width, smile, pen));
            }
            await Task.Delay(50);
            return frames;
        }
        bool Stable(List<Frame> frames, RectInt32 smile, RectInt32 pen, PointInt32 origin) => frames.All(frame =>
            Math.Abs(frame.Smile.X - smile.X) <= 1 && Math.Abs(frame.Smile.Y - smile.Y) <= 1
            && Math.Abs(frame.Smile.Width - smile.Width) <= 1 && Math.Abs(frame.Smile.Height - smile.Height) <= 1
            && (frame.Progress == 0 || Math.Abs(frame.Pen.Width - pen.Width) <= 1))
            && WindowChrome.GetClientPosition(window).Equals(origin);
        void Check(string name, bool passed, object detail = null) => checks.Add(new { check = name, passed, detail });
    }

    private sealed record Frame(double Progress, int Width, int ClientWidth, RectInt32 Smile, RectInt32 Pen);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool GetSystemParameter(uint action, uint parameter, out int value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SetSystemParameter(uint action, uint parameter, IntPtr value, uint flags);
}
