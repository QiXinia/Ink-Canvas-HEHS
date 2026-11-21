using System.Diagnostics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Extensions;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class ExtensionsRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "settings");
        var checks = new List<object>(); SettingsWindow window = null;
        string package = Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith("--extensionpackage="))?.Substring("--extensionpackage=".Length);
        bool noModule = Environment.GetCommandLineArgs().Contains("--expect-no-screen-module");
        if (package is not null)
        {
            string destination = Path.Combine(settings, "Plugins", "sample.guide-lines");
            foreach (string file in Directory.GetFiles(package, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(package, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
            }
            var store = new ExtensionPreferencesStore(settings); var preferences = store.Load(); preferences.EnabledPlugins.Add("sample.guide-lines"); store.Save(preferences);
        }
        using var services = new AppServices(settings);
        services.Settings.Startup.IsAutoUpdate = false;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        try
        {
            await Task.Delay(200);
            Check("optional module package state", services.Modules.HasExport<IScreenTrackingService>() != noModule
                && (noModule ? services.Modules.Status.Single(module => module.Id == "inkcanvas.screen-tracking").State == ExtensionState.NotInstalled : services.Modules.Status.Single(module => module.Id == "inkcanvas.screen-tracking").State == ExtensionState.Running), services.Modules.Status);
            var controller = services.OverlayInk;
            var pointer = new OverlayPointer(972, 200, 200, .5f, 50, 0, OverlayPointerKind.Mouse, 1);
            controller.OnPointerDown(pointer); controller.OnPointerMove(pointer with { X = 250, Y = 220 }); controller.OnPointerUp(pointer with { X = 250, Y = 220 });
            Check("core ink input runs independently of optional modules", controller.Document.Count == 1);
            controller.Undo(); Check("core undo remains available", controller.Document.Count == 0);
            if (package is not null)
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 8000 && !services.Plugins.Commands.Any()) await Task.Delay(50);
                Check("external sample runs in worker", services.Plugins.Status.Single().State == ExtensionState.Running, services.Plugins.Status);
            }
            window = new SettingsWindow(services, shell: shell); window.Activate(); await Task.Delay(200); window.SelectCategory("模块与插件"); await Task.Delay(100);
            Check("module management uses existing settings workspace", Descendants(window.Content).OfType<WindowWorkspace>().Count() == 1);
            if (package is not null && services.Plugins.Commands.FirstOrDefault() is { } command)
            {
                int before = controller.Document.Count, undo = controller.History.UndoCount;
                var button = Descendants(window.Content).OfType<Button>().Single(item => AutomationProperties.GetName(item) == command.Title);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < 4000 && controller.Document.Count == before) await Task.Delay(50);
                Check("plugin command inserts editable geometry through normal settings entry", controller.Document.Count == before + 2 && controller.History.UndoCount == undo + 1);
                controller.Undo(); Check("plugin canvas output is one undo transaction", controller.Document.Count == before);
                controller.Redo(); Check("plugin canvas transaction supports redo", controller.Document.Count == before + 2);
                var pending = shell.ExecuteExtensionCommandAsync(command);
                controller.RestoreScreenContext(Array.Empty<InkCanvas.Core.Ink.InkStroke>(), null, System.Numerics.Matrix3x2.Identity);
                await pending;
                Check("in-flight output cannot enter a replacement screen page", controller.Document.Count == 0);
                var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
                Check("plugin command is exposed by floating toolbar", Descendants(bar.Bar.CreateMorePanelContent()).OfType<TouchIconButton>().Any(item => item.Label == command.Title));
                await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "modules-and-plugins.png"));
                var toggle = Descendants(window.Content).OfType<ToggleSwitch>().Single(item => AutomationProperties.GetName(item) == "参考线示例插件 · 插件");
                ((IToggleProvider)new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                while (services.Plugins.Commands.Count > 0 && watch.ElapsedMilliseconds < 6000) await Task.Delay(50);
                Check("disabling plugin removes its commands", services.Plugins.Commands.Count == 0);
                Check("plugin preferences persist separately", !services.ExtensionStore.Load().EnabledPlugins.Contains("sample.guide-lines") && File.Exists(services.Store.SettingsPath));
            }
            else await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "modules-and-plugins.png"));
        }
        catch (Exception ex) { checks.Add(new { check = "extension integration exception", passed = false, error = ex.ToString() }); }
        finally
        {
            window?.Close();
            File.WriteAllText(Path.Combine(directory, "extensions.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose();
            foreach (var item in shell.OpenWindows.ToArray()) item.Close();
            Application.Current.Exit();
        }
        void Check(string name, bool passed, object detail = null) => checks.Add(new { check = name, passed, detail });
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
