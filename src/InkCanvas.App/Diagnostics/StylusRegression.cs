using System.Reflection;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Ink;
using InkCanvas.Host.Contracts;
using InkCanvas.Platform.Windows;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class StylusRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.SaveOnExit = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        SettingsWindow window = null;
        Window learningWindow = null;
        IDisposable learningService = null;
        try
        {
            shell.Start(); shell.StartInClickThrough();
            Check("stylus module loads with a searchable settings page", services.Modules.Status.Any(module => module.Id == "inkcanvas.stylus" && module.State == Extensions.ExtensionState.Running)
                && services.ModuleSettings.Pages.Any(page => page.Name == "手写笔与数位板"));
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "InkCanvas.Modules.Stylus");
            var type = assembly.GetType("InkCanvas.Modules.Stylus.StylusService");
            var service = services.Modules.GetType().GetMethod("GetExport").MakeGenericMethod(type).Invoke(services.Modules, null);
            shell.ToggleClickThrough();
            var screen = services.OverlayInk;
            var pen = new OverlayPointer(741, 120, 120, .6f, 0, 0, OverlayPointerKind.Pen, 1) { HasPressure = true };
            screen.OnPointerHover(pen with { BarrelButton = true });
            Check("Windows Ink barrel switches the annotation tool", screen.Tool == InkTool.Eraser);
            screen.OnPointerHover(pen with { BarrelButton = true, SecondaryBarrelButton = true });
            Check("independent second barrel chooses selection", screen.Tool == InkTool.Select);
            screen.OnPointerHover(pen with { BarrelButton = true });
            Check("releasing second barrel restores first barrel tool", screen.Tool == InkTool.Eraser);
            screen.OnPointerHover(pen);
            Check("releasing the barrel restores the pen", screen.Tool == InkTool.Pen);
            screen.OnPointerHover(pen with { Inverted = true, EraserButton = true });
            Check("inverted eraser end has its own mapping", screen.Tool == InkTool.Eraser);
            screen.OnPointerExited(pen.Id);
            Check("leaving the digitizer releases temporary tools", screen.Tool == InkTool.Pen);
            screen.OnPointerDown(pen);
            screen.OnPointerMove(pen with { X = 150, BarrelButton = true });
            Check("barrel changes wait for the live stroke", screen.Tool == InkTool.Pen);
            screen.OnPointerUp(pen with { X = 180, BarrelButton = true });
            Check("contact end commits ink then applies the held button", screen.Tool == InkTool.Eraser && screen.Document.Count == 1);
            screen.OnPointerHover(pen);
            screen.Undo();
            Check("button changes preserve ink undo", screen.Document.Count == 0);

            shell.ToggleBoard();
            await Task.Delay(150);
            var first = services.Board.Current;
            first.OnPointerHover(pen with { BarrelButton = true });
            Check("board receives the same barrel mapping", first.Tool == InkTool.Eraser);
            services.Board.NextPage();
            Check("page switch releases temporary tools before copying drawing state", first.Tool == InkTool.Pen && services.Board.Current.Tool == InkTool.Pen);

            shell.OpenSettings();
            await Task.Delay(180);
            window = shell.OpenWindows.OfType<SettingsWindow>().Single();
            window.SelectCategory("手写笔与数位板");
            await Task.Delay(180);
            Check("settings expose separate actions and modes for three pen inputs", Descendants(window.Content).OfType<ComboBox>().Count() == 6);
            Check("plugin page participates in settings search", window.SearchSettings("数位板").Any(entry => entry.Category == "手写笔与数位板"));
            foreach (int theme in new[] { 1, 2 })
            {
                WindowChrome.ApplyTheme(window, theme);
                foreach (int width in new[] { 640, 1000 })
                {
                    window.AppWindow.Resize(new(width, 780));
                    await Task.Delay(100);
                    await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, $"stylus-settings-{theme}-{width}.png"));
                    var root = (FrameworkElement)window.Content;
                    Check($"button editors fit the settings viewport {theme}-{width}", Descendants(window.Content).OfType<ComboBox>().All(combo =>
                    {
                        var bounds = combo.TransformToVisual(root).TransformBounds(new(0, 0, combo.ActualWidth, combo.ActualHeight));
                        return combo.ActualWidth > 0 && bounds.Left >= 0 && bounds.Right <= root.ActualWidth + 1;
                    }));
                }
            }
            var barrelAction = Descendants(window.Content).OfType<ComboBox>().Single(combo => AutomationProperties.GetName(combo) == "手写笔主侧键操作");
            barrelAction.SelectedIndex = 2;
            string path = Path.Combine(services.Store.Directory, "PluginData", "inkcanvas.stylus", "buttons.json");
            Check("mapping edits save immediately in plugin data", File.Exists(path) && File.ReadAllText(path).Contains("Highlighter"));
            checks.Add(new { check = "native foreground access", available = NativeMethods.GetForegroundWindow() != 0 });
            window.Close(); window = null;
            var settings = type.GetProperty("Settings").GetValue(service);
            settings.GetType().GetProperty("Enabled").SetValue(settings, false);
            type.GetMethod("Configure").Invoke(service, null);
            // Exercise learning and queued driver input with an explicit test host when no interactive desktop owns foreground focus.
            var inputHost = new InputTestHost(Path.GetDirectoryName(path), shell.CanvasToolsTestHost);
            learningService = (IDisposable)Activator.CreateInstance(type, inputHost);
            var learningSettings = type.GetProperty("Settings").GetValue(learningService);
            learningSettings.GetType().GetProperty("Enabled").SetValue(learningSettings, true);
            type.GetMethod("Configure").Invoke(learningService, null);
            var view = (UserControl)Activator.CreateInstance(assembly.GetType("InkCanvas.Modules.Stylus.StylusSettingsView"), learningService);
            learningWindow = new Window { Content = view };
            WindowChrome.MakeStandard(learningWindow, 800, 700);
            learningWindow.Activate();
            await Task.Delay(100);
            var learn = Descendants(view).OfType<Button>().Single(button => button.Content?.ToString() == "学习数位板 / 驱动按钮");
            ((IInvokeProvider)new ButtonAutomationPeer(learn).GetPattern(PatternInterface.Invoke)).Invoke();
            var signalType = assembly.GetType("InkCanvas.Modules.Stylus.StylusButtonSignal");
            object Signal(bool down) => Activator.CreateInstance(signalType, "key:124:0", "F13 数位板按钮", down);
            bool learned = (bool)type.GetMethod("HandleButton").Invoke(learningService, new[] { Signal(true) });
            await Task.Delay(120);
            Check("learning with the test host records a driver button and creates editors", learned && File.ReadAllText(path).Contains("key:124:0")
                && Descendants(view).OfType<ComboBox>().Count() == 8);
            inputHost.CanLearnShortcuts = false;
            var current = services.Board.Current;
            current.OnPointerDown(pen); current.OnPointerUp(pen with { X = 180 });
            type.GetMethod("HandleButton").Invoke(learningService, new[] { Signal(true) });
            type.GetMethod("HandleButton").Invoke(learningService, new[] { Signal(false) });
            await Task.Delay(120);
            Check("learned driver button executes undo through the test host", current.Document.Count == 0);
            learningWindow.Close(); learningWindow = null;
            learningService.Dispose(); learningService = null;
            settings.GetType().GetProperty("Enabled").SetValue(settings, true);
            type.GetMethod("Configure").Invoke(service, null);
            services.Board.Current.OnPointerHover(pen with { BarrelButton = true });
            Check("edited barrel action works on the active page", services.Board.Current.Tool == InkTool.Highlighter);

            string status = (string)type.GetProperty("Status").GetValue(service);
            checks.Add(new { check = "native button monitor status", status });
            settings.GetType().GetProperty("Enabled").SetValue(settings, false);
            type.GetMethod("Configure").Invoke(service, null);
            Check("disabling mappings restores the previous tool", services.Board.Current.Tool == InkTool.Pen);
            services.Board.Current.OnPointerHover(pen with { BarrelButton = true });
            Check("disabled plugin leaves ordinary ink routing active", services.Board.Current.Tool == InkTool.Pen);
            ((IDisposable)service).Dispose();
            services.Board.Current.OnPointerHover(pen with { BarrelButton = true });
            Check("module disposal detaches its pointer listeners", services.Board.Current.Tool == InkTool.Pen);
        }
        catch (Exception ex) { checks.Add(new { check = "stylus regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            window?.Close();
            learningWindow?.Close(); learningService?.Dispose();
            await File.WriteAllTextAsync(Path.Combine(directory, "stylus-check.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }
    private sealed class InputTestHost(string directory, ICanvasToolsHost canvas) : IStylusInputHost
    {
        public string DataDirectory => directory;
        public ICanvasToolsHost Canvas => canvas;
        public bool CanHandleShortcuts => true;
        public bool CanLearnShortcuts { get; set; } = true;
        public bool CanHandlePointer(InkController controller) => ReferenceEquals(Canvas.CaptureTarget().Controller, controller);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
