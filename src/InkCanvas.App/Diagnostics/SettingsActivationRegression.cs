using System.Diagnostics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class SettingsActivationRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        string statusFile = Path.Combine(directory, "foreground.json");
        var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--activationhost=" + statusFile);
        start.ArgumentList.Add("--foreground-client=" + Environment.ProcessId);
        using var foregroundHost = Process.Start(start);
        bool granted = false;
        try
        {
            for (int attempt = 0; attempt < 50 && !granted; attempt++)
            {
                await Task.Delay(100);
                try
                {
                    using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statusFile));
                    granted = state.RootElement.GetProperty("foregroundGranted").GetBoolean();
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            checks.Add(new { check = "native foreground activation available", available = granted });
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            Invoke(Descendants(bar.Content).OfType<TouchIconButton>().Single(button => button.Label == "工具"));
            await Task.Delay(80);
            var panel = bar.GetOpenPanel(BarPanelKind.More);
            Invoke(Descendants(panel.Content).OfType<TouchIconButton>().Single(button => button.Label == "设置"));
            await Task.Delay(250);
            var settings = shell.OpenWindows.OfType<SettingsWindow>().Single();
            CheckActive("tool menu activates settings", settings);
            Check("settings remains a normal activatable window", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(settings), NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE)
                && settings.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: false });
            Check("tool menu closes before settings takes focus", bar.GetOpenPanel(BarPanelKind.More) is null);
            var workspace = Descendants(settings.Content).OfType<WindowWorkspace>().Single();
            workspace.FocusSearch(); await Task.Delay(80);
            var search = Descendants(settings.Content).OfType<AutoSuggestBox>().Single();
            search.Text = "墨迹";
            var focused = FocusManager.GetFocusedElement(((FrameworkElement)settings.Content).XamlRoot);
            if (granted) Check("settings search receives keyboard focus", Descendants(search).Contains(focused));
            for (int repeat = 0; repeat < 3; repeat++) shell.OpenSettings();
            await Task.Delay(80);
            Check("repeated entry reuses settings and preserves its editor", shell.OpenWindows.OfType<SettingsWindow>().Count() == 1
                && search.Text == "墨迹" && ReferenceEquals(focused, FocusManager.GetFocusedElement(((FrameworkElement)settings.Content).XamlRoot)));
            var presenter = (OverlappedPresenter)settings.AppWindow.Presenter;
            presenter.Minimize(); await Task.Delay(80);
            shell.OpenSettings(); await Task.Delay(150);
            Check("opening settings restores a minimized window", presenter.State != OverlappedPresenterState.Minimized);
            CheckActive("restored settings has foreground focus", settings);
            settings.AppWindow.Hide(); await Task.Delay(50);
            shell.OpenSettings(); await Task.Delay(150);
            Check("opening settings reveals its hidden window", settings.AppWindow.IsVisible);
            CheckActive("revealed settings has foreground focus", settings);
            settings.Close(); await Task.Delay(80);
            shell.ToggleFold();
            shell.OpenSettings(); await Task.Delay(200);
            settings = shell.OpenWindows.OfType<SettingsWindow>().Single();
            CheckActive("settings activates while toolbar is folded", settings);
            Check("folded toolbar retains its state", bar.IsFolded);
            settings.Close(); await Task.Delay(80);
            shell.ToggleFold();
            shell.ToggleBoard(); await Task.Delay(150);
            shell.OpenSettings(); await Task.Delay(200);
            settings = shell.OpenWindows.OfType<SettingsWindow>().Single();
            CheckActive("board settings activates without a second click", settings);
            await Task.Delay(1000);
            CheckActive("drawing maintenance preserves settings focus", settings);
            Check("settings stays non-topmost after maintenance", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(settings), NativeMethods.WS_EX_TOPMOST));
            Check("main toolbar keeps its nonactivating input", NativeMethods.HasExStyle(WindowChrome.GetHwnd(bar), NativeMethods.WS_EX_NOACTIVATE));
        }
        catch (Exception ex) { checks.Add(new { check = "settings activation regression exception", passed = false, detail = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings-activation.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            if (!foregroundHost.HasExited) { foregroundHost.CloseMainWindow(); await Task.Delay(100); }
            var windows = shell.OpenWindows.ToArray();
            shell.Dispose();
            foreach (var window in windows) window.Close();
            Application.Current.Exit();
        }
        void Check(string check, bool passed) => checks.Add(new { check, passed });
        void CheckActive(string check, SettingsWindow window) => checks.Add(new { check, available = granted,
            passed = granted ? (bool?)(NativeMethods.GetForegroundWindow() == WindowChrome.GetHwnd(window)) : null,
            foreground = NativeMethods.GetForegroundWindow().ToInt64(), expected = WindowChrome.GetHwnd(window).ToInt64() });
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
