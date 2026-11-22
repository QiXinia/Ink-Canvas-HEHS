using System.Reflection;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Update;
using InkCanvas.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class PluginManagementRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.DefaultDirectory = Path.Combine(directory, "documents");
        services.Settings.Archive.SaveOnExit = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        SettingsWindow settings = null;
        try
        {
            shell.Start(); shell.StartInClickThrough();
            var updates = services.ExtensionUpdates;
            var releases = services.Modules.Status.Concat(services.Plugins.Status)
                .Where(status => ExtensionFeed.Known.ContainsKey(status.Id))
                .Select(status => Release(status.Id, status.Id == "inkcanvas.pdf" ? status.Version : "99.0.0")).ToList();
            releases.Add(Release("sample.guide-lines", "1.0.0"));
            SetUpdate("Catalog", releases);
            SetUpdate("HasChecked", true);
            settings = new SettingsWindow(services, shell: shell);
            settings.Activate(); settings.SelectCategory("模块与插件");
            await Task.Delay(180);
            foreach (int theme in new[] { 1, 2 })
            {
                services.Settings.Appearance.Theme = theme;
                typeof(SettingsWindow).GetMethod("RebuildContent", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(settings, null);
                settings.SelectCategory("模块与插件");
                WindowChrome.ApplyTheme(settings, theme);
                foreach (int width in new[] { 640, 1000, 1400 })
                {
                    settings.AppWindow.Resize(new(width, 820));
                    await Task.Delay(100);
                    var cards = Cards().ToArray();
                    Check($"each installed or available plugin has one card {theme}-{width}", cards.Select(AutomationProperties.GetAutomationId).Distinct().Count() == cards.Length
                        && cards.Length == services.Modules.Status.Count + services.Plugins.Status.Count + 1);
                    Check($"AI update toggle window and settings share a card {theme}-{width}", Descendants(Card("inkcanvas.ai-assistant")).OfType<ToggleSwitch>().Count() == 1
                        && Buttons(Card("inkcanvas.ai-assistant")).Count() == 3);
                    Check($"all plugin actions fit their cards {theme}-{width}", cards.All(card => Descendants(card).OfType<Button>().All(button => Fits(button, card))));
                    Check($"global update actions remain available {theme}-{width}", Buttons(settings.Content).Count(button => AutomationProperties.GetName(button)
                        is "检查插件更新" or "一键更新全部插件") == 2);
                    var frame = (WindowFrame)settings.Content;
                    var background = frame.Background;
                    frame.Background = new SolidColorBrush(theme == 1 ? Microsoft.UI.Colors.WhiteSmoke : Windows.UI.Color.FromArgb(255, 32, 32, 32));
                    try { await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, $"plugin-management-{theme}-{width}.png")); }
                    finally { frame.Background = background; }
                }
            }
            Check("current and available versions retain distinct actions", Label(Update("inkcanvas.pdf")) == "已是最新" && !Update("inkcanvas.pdf").IsEnabled
                && Label(Update("inkcanvas.library")) == "更新" && Update("inkcanvas.library").IsEnabled);
            Check("uninstalled plugin combines installation with an unavailable toggle", Label(Update("sample.guide-lines")) == "安装"
                && !Descendants(Card("sample.guide-lines")).OfType<ToggleSwitch>().Single().IsEnabled);

            var ai = services.Modules.Status.Single(status => status.Id == "inkcanvas.ai-assistant");
            var toggle = Descendants(Card(ai.Id)).OfType<ToggleSwitch>().Single();
            toggle.IsOn = false;
            Check("native toggle keeps restart persistence", services.ExtensionPreferences.DisabledModules.Contains(ai.Id)
                && services.ExtensionStore.Load().DisabledModules.Contains(ai.Id));
            toggle.IsOn = true;
            var settingsAction = Buttons(Card(ai.Id)).Single(button => AutomationProperties.GetName(button) == ai.Name + "设置 · AI 助手");
            Invoke(settingsAction); await Task.Delay(80);
            var workspace = Descendants(settings.Content).OfType<WindowWorkspace>().Single();
            Check("card settings opens its plugin page and supports back", workspace.SelectedPage == "AI 助手"
                && workspace.GoBack() && workspace.SelectedPage == "模块与插件");
            Invoke(Buttons(Card("inkcanvas.library")).Single(button => AutomationProperties.GetName(button) == "文稿库"));
            for (int attempt = 0; attempt < 100 && !shell.OpenWindows.Any(window => window.GetType().Name == "LibraryWindow"); attempt++) await Task.Delay(30);
            var library = shell.OpenWindows.FirstOrDefault(window => window.GetType().Name == "LibraryWindow");
            Check("card window action opens the matching plugin", library is not null);
            library?.Close();
            SetUpdate("IsBusy", true); SetUpdate("ActiveId", ai.Id); Refresh(); await Task.Delay(80);
            Check("download state updates the matching card and disables installation", Label(Update(ai.Id)) == "正在准备" && !Update(ai.Id).IsEnabled
                && Cards().SelectMany(Buttons).Where(button => AutomationProperties.GetName(button).EndsWith("下载与更新", StringComparison.Ordinal)).All(button => !button.IsEnabled));
            SetUpdate("IsBusy", false); SetUpdate("ActiveId", null);
            services.ExtensionInstalls.Results.Add(new("inkcanvas.library", false, "diagnostic download failure"));
            Refresh(); await Task.Delay(80);
            Check("retry stays on the failed plugin card", Label(Update("inkcanvas.library")) == "重新下载" && Update("inkcanvas.library").IsEnabled);
            services.ExtensionInstalls.Results.Clear();
            typeof(ExtensionInstallStore).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(services.ExtensionInstalls,
                new[] { new PendingExtension("inkcanvas.library", "文稿库", "99.0.0", RepositoryExtensionKind.Module, "diagnostic", Array.Empty<UpdateFile>()) });
            Refresh(); await Task.Delay(120);
            Check("pending restart state appears on the same card", Label(Update("inkcanvas.library")) == "待重启" && !Update("inkcanvas.library").IsEnabled
                && Descendants(Card("inkcanvas.library")).OfType<TextBlock>().Any(text => text.Text.Contains("99.0.0 待重启生效")));
            Check("plugin commands remain searchable after regrouping", settings.SearchSettings("文稿库").Any(entry => entry.Category == "模块与插件"));
        }
        catch (Exception ex) { checks.Add(new { check = "plugin management regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            settings?.Close();
            foreach (var window in shell.OpenWindows.Where(window => window.GetType().Name == "LibraryWindow").ToArray()) window.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "plugin-management.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }

        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        ExtensionRelease Release(string id, string version)
        {
            var metadata = ExtensionFeed.Known[id];
            return new(id, metadata.Name, metadata.Description, version, metadata.Kind, services.ExtensionUpdates.HostVersion, "diagnostic",
                new(id + "-" + version + ".zip", "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/diagnostic/" + id + ".zip", 100, new string('0', 64), null));
        }
        void SetUpdate(string property, object value) => typeof(ExtensionUpdateCoordinator).GetProperty(property).SetValue(services.ExtensionUpdates, value);
        void Refresh() => typeof(SettingsWindow).GetMethod("RefreshRepositoryUpdates", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(settings, null);
        IEnumerable<Border> Cards() => Descendants(settings.Content).OfType<Border>().Where(card => AutomationProperties.GetAutomationId(card).StartsWith("extension-card-", StringComparison.Ordinal));
        Border Card(string id) => Cards().Single(card => AutomationProperties.GetAutomationId(card) == "extension-card-" + id);
        Button Update(string id) => Buttons(Card(id)).Single(button => AutomationProperties.GetName(button).EndsWith("下载与更新", StringComparison.Ordinal));
    }

    private static string Label(Button button) => button.Content is TextBlock text ? text.Text : button.Content?.ToString();
    private static IEnumerable<Button> Buttons(DependencyObject root) => Descendants(root).OfType<Button>();
    private static bool Fits(FrameworkElement element, FrameworkElement parent)
    {
        var bounds = element.TransformToVisual(parent).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        return element.ActualWidth > 0 && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= parent.ActualWidth + 1 && bounds.Bottom <= parent.ActualHeight + 1;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
