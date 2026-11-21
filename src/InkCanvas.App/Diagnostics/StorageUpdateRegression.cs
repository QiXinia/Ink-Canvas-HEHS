using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class StorageUpdateRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var keeper = new Window { Content = new Grid() }; keeper.Activate(); keeper.AppWindow.Hide();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Storage.AutomaticallyCleanCache = false;
        services.SaveSettings();
        var handler = new DownloadHandler();
        using var coordinator = new UpdateCoordinator(services, new UpdateService(transport: new UpdateTransport(handler), cache: services.UpdateCache));
        coordinator.Confirm = (_, _) => Task.FromResult(true);
        coordinator.Notify = (_, _) => Task.CompletedTask;
        SettingsWindow window = null;
        try
        {
            window = new SettingsWindow(services, coordinator); window.Activate();
            await Task.Delay(200);
            window.SelectCategory("存储与缓存");
            await Task.Delay(200);
            Check("storage page exposes cache and user configuration", Texts(window).Contains("更新缓存") && Texts(window).Contains("用户配置"));
            Check("cache settings are searchable", window.SearchSettings("缓存").Any(entry => entry.Title == "更新缓存"));
            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "storage.png"));
            var update = coordinator.CheckAsync(true);
            await Wait(() => coordinator.Progress.DownloadedBytes > 0);
            Check("download progress includes bytes speed and mirror", coordinator.Progress.Fraction > 0 && coordinator.Progress.TotalBytes > 0
                && coordinator.Progress.BytesPerSecond > 0 && coordinator.Progress.Source == "ghfile.geekertao.top");
            Check("cache cleanup disabled while updating", !Descendants(window.Content).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "清理未使用的缓存").IsEnabled);
            window.SelectCategory("画板选项");
            await Task.Delay(60);
            Check("progress persists across settings pages", Descendants(window.Content).OfType<ProgressBar>()
                .Any(bar => AutomationProperties.GetName(bar) == "更新进度" && bar.Visibility == Visibility.Visible && bar.Value > 0));
            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "download.png"));
            window.Close(); window = new SettingsWindow(services, coordinator); window.Activate();
            await Task.Delay(100);
            Check("reopening settings retains ongoing download", coordinator.IsBusy && Descendants(window.Content).OfType<UpdateStatusCard>().Single().Visibility == Visibility.Visible);
            coordinator.Cancel(); await update;
            Check("cancel stops download and clears partial files", !coordinator.IsBusy && coordinator.Progress.Stage == UpdateStage.Canceled
                && Directory.GetFiles(services.UpdateCache.Root, "*.partial", SearchOption.AllDirectories).Length == 0);
            window.SelectCategory("存储与缓存");
            await Task.Delay(150);
            Check("storage cleanup becomes available after cancellation", Descendants(window.Content).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "清理未使用的缓存").IsEnabled);
            string disposable = services.UpdateCache.PackageDirectory(new string('e', 64));
            Directory.CreateDirectory(disposable);
            File.WriteAllBytes(Path.Combine(disposable, "package.zip"), new byte[4096]);
            var clean = Descendants(window.Content).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "清理未使用的缓存");
            ((IInvokeProvider)new ButtonAutomationPeer(clean).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => !Directory.Exists(disposable));
            Check("normal cleanup action removes unused packages and preserves settings", File.Exists(services.Store.SettingsPath));
            string temporary = Path.Combine(Path.GetTempPath(), "inkcache-install-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                string target = Path.Combine(temporary, "target"), work = Path.Combine(temporary, "cache", "install"), payload = Path.Combine(work, "payload");
                Directory.CreateDirectory(target); Directory.CreateDirectory(payload);
                File.WriteAllText(Path.Combine(target, "InkCanvas.exe"), "isolated installation fixture");
                File.WriteAllText(Path.Combine(payload, "InkCanvas.Core.dll"), "new payload");
                string hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("new payload")));
                var plan = new UpdatePlan(target, payload, "99.0.0", new[] { new UpdateFile("InkCanvas.Core.dll", hash) }, 0, 0, null, false);
                string planPath = Path.Combine(work, "plan.json");
                await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));
                using var helper = UpdateService.LaunchUpdate(planPath);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await helper.WaitForExitAsync(timeout.Token);
                var result = JsonSerializer.Deserialize<UpdateResult>(await File.ReadAllTextAsync(Path.Combine(work, "result.json")));
                Check("external installer acquires cache lease and applies isolated payload", helper.ExitCode == 0 && result.Success
                    && File.ReadAllText(Path.Combine(target, "InkCanvas.Core.dll")) == "new payload");
            }
            finally { Directory.Delete(temporary, recursive: true); }
        }
        catch (Exception ex) { checks.Add(new { check = "storage and progress regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            window?.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "storage-update.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            coordinator.Dispose(); services.Dispose(); keeper.Close(); Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    private static string Texts(Window window) => string.Join(" ", Descendants(window.Content).OfType<TextBlock>().Select(block => block.Text));
    private static async Task Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("Update progress did not arrive.");
            await Task.Delay(10);
        }
    }
    private sealed class DownloadHandler : HttpMessageHandler
    {
        private readonly byte[] _package = new byte[4 * 1024 * 1024];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri.AbsolutePath.EndsWith(".zip"))
            {
                if (request.RequestUri.Host == "github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                var content = new StreamContent(new SlowStream(_package));
                content.Headers.ContentLength = _package.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(_package));
            string feed = JsonSerializer.Serialize(new[] { new { tag_name = "v99.0.0", assets = new[] {
                new { name = "InkCanvas-WinUI3-99.0.0-win-x64.zip", size = _package.Length, digest,
                    browser_download_url = "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v99.0.0/InkCanvas-WinUI3-99.0.0-win-x64.zip" } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) });
        }
    }
    private sealed class SlowStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken);
        }
    }
}
