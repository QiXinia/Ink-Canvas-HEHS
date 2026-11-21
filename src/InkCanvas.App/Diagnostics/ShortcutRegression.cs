using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Platform.Input;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace InkCanvas.App.Diagnostics;

internal static class ShortcutRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Automation.IsAutoClearWhenExitingWritingMode = false;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        using var probe = new LayeredInkWindow(0, 0, 1, 1);
        const int probeId = 700;
        const uint key = 0x86; // Ctrl+Shift+F23 isolates the OS registration check from other running copies.
        const HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift;
        nint foreground = 0;
        bool realForegroundAvailable = NativeMethods.GetForegroundWindow() != 0;
        try
        {
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            foreground = WindowChrome.GetHwnd(bar);
            NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(bar)); await Task.Delay(80);
            bool free = CanReserve();
            Check("native test key starts available", free);
            if (!free) throw new InvalidOperationException("The isolated test key is already registered.");
            shell.Overlay.Hotkeys.Register(modifiers, key, services.OverlayInk.Undo, () => shell.IsOverlayShortcutContext(foreground));
            Check("mouse mode does not register editing keys", Id() is null && CanReserve() && !shell.IsOverlayShortcutContext(foreground));
            Seed(services.OverlayInk);
            shell.ToggleClickThrough();
            NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(bar)); await Task.Delay(80);
            shell.Overlay.Hotkeys.Refresh();
            var first = Id();
            Check("writing mode registers the native editing key", first.HasValue && !CanReserve() && shell.IsOverlayShortcutContext(foreground));
            if (first is not { } firstId) throw new InvalidOperationException($"Writing shortcut inactive. Foreground={NativeMethods.GetForegroundWindow()}, bar={WindowChrome.GetHwnd(bar)}");
            shell.Overlay.Hotkeys.Dispatch(firstId);
            Check("writing undo acts once on screen ink", services.OverlayInk.Document.Count == 0 && services.OverlayInk.History.CanRedo);
            shell.ToggleClickThrough();
            Check("return to mouse releases the key to other applications", Id() is null && CanReserve());
            Check("queued shortcut from writing is rejected in mouse mode", !shell.Overlay.Hotkeys.Dispatch(firstId) && services.OverlayInk.History.CanRedo);
            shell.ToggleClickThrough(); await Task.Delay(50);
            Check("writing re-entry gets a fresh registration", Id() is { } next && next != firstId);
            services.OverlayInk.Tool = InkTool.Pan;
            Check("pan tool releases drawing shortcuts", Id() is null && CanReserve());
            services.OverlayInk.Tool = InkTool.Pen;
            shell.OpenSettings(); await Task.Delay(150);
            foreground = WindowChrome.GetHwnd(shell.OpenWindows.OfType<SettingsWindow>().Single()); shell.Overlay.Hotkeys.Refresh();
            Check("settings window suspends canvas shortcuts", !shell.IsCanvasShortcutContext(foreground) && Id() is null && CanReserve());
            shell.OpenWindows.OfType<SettingsWindow>().Single().Close();
            foreground = WindowChrome.GetHwnd(bar);
            NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(bar)); await Task.Delay(100);
            shell.Overlay.Hotkeys.Refresh();
            Check("return from settings restores writing shortcuts", shell.IsOverlayShortcutContext(foreground) && Id().HasValue);
            shell.ToggleClickThrough(); shell.ToggleBoard(); await Task.Delay(120);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            var c = board.CurrentController; Seed(c);
            Check("whiteboard uses local undo instead of a global registration", !shell.OverlayShortcutsEnabled
                && shell.Overlay.Hotkeys.GetRegisteredId(HotkeyModifiers.Control, 0x5A) is null);
            Check("whiteboard local undo works exactly once", board.TryHandleBoardShortcut(VirtualKey.Z, true, false, false, null)
                && c.Document.Count == 0 && c.History.CanRedo);
            Check("whiteboard local redo restores ink", board.TryHandleBoardShortcut(VirtualKey.Y, true, false, false, null) && c.Document.Count == 1);
            c.Tool = InkTool.Pan;
            foreach (var shortcut in new[] { VirtualKey.Z, VirtualKey.Y, VirtualKey.E, VirtualKey.A, VirtualKey.Delete })
                Check("whiteboard mouse/pan ignores " + shortcut, !board.TryHandleBoardShortcut(shortcut, shortcut != VirtualKey.Delete, false, false, null)
                    && c.Document.Count == 1 && !c.HasSelection);
            c.Tool = InkTool.Select;
            Check("text entry owns undo in the whiteboard", !board.TryHandleBoardShortcut(VirtualKey.Z, true, false, false, new TextBox()) && c.Document.Count == 1);
            Check("number editor owns undo", !board.TryHandleBoardShortcut(VirtualKey.Z, true, false, false, new NumberBox()) && c.Document.Count == 1);
            Check("other modifier combinations do not run canvas undo", !board.TryHandleBoardShortcut(VirtualKey.Z, true, true, false, null)
                && !board.TryHandleBoardShortcut(VirtualKey.Z, true, false, true, null) && c.Document.Count == 1);
            board.Close(); await Task.Delay(60);
            Check("leaving board returns to mouse without claiming edit keys", shell.Overlay.ClickThrough && Id() is null && CanReserve());
        }
        catch (Exception ex) { checks.Add(new { check = "shortcut regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "shortcuts.json"), JsonSerializer.Serialize(new { realForegroundAvailable,
                foregroundContextSuppliedByTest = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        int? Id() => shell.Overlay.Hotkeys.GetRegisteredId(modifiers, key);
        bool CanReserve()
        {
            bool registered = NativeMethods.RegisterHotKey(probe.Handle, probeId, (uint)(modifiers | HotkeyModifiers.NoRepeat), key);
            if (registered) NativeMethods.UnregisterHotKey(probe.Handle, probeId);
            return registered;
        }
    }

    private static void Seed(InkController controller)
    {
        controller.Tool = InkTool.Pen;
        var point = new OverlayPointer(994, 50, 50, .5f, 0, 0, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(point); controller.OnPointerUp(point with { X = 100, Y = 100 });
    }
}
