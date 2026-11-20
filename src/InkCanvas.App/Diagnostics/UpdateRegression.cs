using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.Features.Update;
using InkCanvas.App.Services;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

internal static class UpdateRegression
{
    public static async Task Run(string directory, bool mirrorOnly = false)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var keepAlive = new Window { Content = new Microsoft.UI.Xaml.Controls.Grid() }; keepAlive.Activate(); keepAlive.AppWindow.Hide();
        try
        {
            if (mirrorOnly)
            {
                using var mirror = new UpdateService(transport: new UpdateTransport(new DirectOfflineHandler()));
                mirror.StatusChanged += status => File.AppendAllText(Path.Combine(directory, "network.log"), DateTime.Now.ToString("O") + " " + status + Environment.NewLine);
                ReleaseInfo selected;
                try
                {
                    var mirrorReleases = await mirror.GetReleasesAsync(lifetime.Token);
                    selected = mirrorReleases.First();
                    Check("live mirror release list with GitHub direct connection disabled", true, new { source = mirror.LastSource, selected.Tag });
                }
                catch (HttpRequestException ex)
                {
                    Check("live mirror release list with GitHub direct connection disabled", false, ex.Message);
                    selected = ReleaseFeed.ParseNewestStable(await File.ReadAllTextAsync(Path.Combine(directory, "..", "update-github-releases.json")));
                }
                string package = await mirror.DownloadAsync(selected, cancellation: lifetime.Token);
                Check("live mirror ZIP download verifies repository SHA256", File.Exists(package), new { source = mirror.LastSource, path = package, selected.Sha256 });
                return;
            }
            using (var settings = new AppServices(Path.Combine(directory, "coordinator-settings")))
            {
                var feed = new FutureReleaseHandler();
                using var coordinator = new UpdateCoordinator(settings, new UpdateService(transport: new UpdateTransport(feed, Array.Empty<string>())));
                int confirmations = 0;
                coordinator.Confirm = (_, _) => { confirmations++; return Task.FromResult(false); };
                coordinator.CanInstallSilently = () => false;
                settings.Settings.Startup.IsAutoUpdate = true;
                settings.Settings.Startup.IsAutoUpdateWithSilence = true;
                settings.Settings.Startup.AutoUpdateWithSilenceStartTime = "00:00";
                settings.Settings.Startup.AutoUpdateWithSilenceEndTime = "00:00";
                await coordinator.CheckAsync(false);
                Check("silent coordinator defers without prompting while canvas is busy", confirmations == 0
                    && coordinator.Status.Contains("等待画布空闲") && feed.Requests == 1);
                int hour = (DateTime.Now.Hour + 2) % 24;
                settings.Settings.Startup.AutoUpdateWithSilenceStartTime = $"{hour:00}:00";
                settings.Settings.Startup.AutoUpdateWithSilenceEndTime = $"{(hour + 1) % 24:00}:00";
                await coordinator.CheckAsync(false);
                Check("silent coordinator defers outside configured hours", confirmations == 0 && coordinator.Status.Contains("等待静默更新时间段") && feed.Requests == 1);
                await coordinator.CheckAsync(true);
                Check("manual check still requests confirmation with silent mode enabled", confirmations == 1 && feed.Requests == 2);
                settings.Settings.Startup.IsAutoUpdate = false;
                await coordinator.CheckAsync(false);
                Check("disabled automatic update does not fetch releases", feed.Requests == 2);
            }
            ReleaseInfo release = null;
            string archive = null;
            using var service = new UpdateService();
            service.StatusChanged += status => File.AppendAllText(Path.Combine(directory, "network.log"), DateTime.Now.ToString("O") + " " + status + Environment.NewLine);
            try
            {
                var releases = await service.GetReleasesAsync(lifetime.Token);
                release = releases.FirstOrDefault();
                Check("live repository release list identifies portable WinUI x64 package", release?.IsPortable == true,
                    new { source = service.LastSource, versions = releases.Select(item => item.Tag), release?.AssetName, release?.Sha256 });
                archive = await service.DownloadAsync(release, cancellation: lifetime.Token);
                Check("live release package downloads and verifies SHA256", File.Exists(archive), new { source = service.LastSource, path = archive });
            }
            catch (Exception ex) { checks.Add(new { check = "live GitHub and mirror access", passed = false, error = ex.Message }); }

            // The local release artifact permits installer acceptance even during an external outage.
            if (release is null)
            {
                string feed = Path.Combine(directory, "..", "update-github-releases.json");
                if (!File.Exists(feed)) throw new IOException("Live feed unavailable and no recorded repository fixture.");
                release = ReleaseFeed.ParseNewestStable(await File.ReadAllTextAsync(feed));
                checks.Add(new { check = "repository metadata fixture fallback", tag = release.Tag });
            }
            if (archive is null)
            {
                archive = Path.GetFullPath(Path.Combine(directory, "..", "release-v3.5.0-publish", release.AssetName));
                await using var input = File.OpenRead(archive);
                Check("recorded repository archive matches GitHub digest", Convert.ToHexStringLower(await SHA256.HashDataAsync(input)) == release.Sha256);
            }
            // Exercise failover with the real mirror while only the direct GitHub request is forced offline.
            try
            {
                using var mirrorService = new UpdateService(transport: new UpdateTransport(new DirectOfflineHandler()));
                var mirrorReleases = await mirrorService.GetReleasesAsync(lifetime.Token);
                Check("live mirror fallback when GitHub is offline", mirrorReleases.Any(item => item.Tag == release.Tag), new { source = mirrorService.LastSource });
                string mirrorArchive = await mirrorService.DownloadAsync(release, cancellation: lifetime.Token);
                Check("live release package downloads through mirror when GitHub is offline", File.Exists(mirrorArchive),
                    new { source = mirrorService.LastSource, path = mirrorArchive });
            }
            catch (Exception ex) { checks.Add(new { check = "live mirror fallback when GitHub is offline", passed = false, error = ex.Message }); }

            string target = Path.Combine(directory, "installation"); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "InkCanvas.exe"), "previous installation");
            Directory.CreateDirectory(Path.Combine(target, "Modules", "inkcanvas.pdf"));
            Directory.CreateDirectory(Path.Combine(target, "Plugins", "custom"));
            File.WriteAllText(Path.Combine(target, "Plugins", "custom", "data.txt"), "keep plugin");
            File.WriteAllText(Path.Combine(target, "lesson.icink"), "keep ink");
            string work = Path.Combine(directory, "install");
            var parentStart = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"))
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            parentStart.ArgumentList.Add("-n"); parentStart.ArgumentList.Add("4"); parentStart.ArgumentList.Add("127.0.0.1");
            var plan = await Task.Run(() => UpdatePackage.PrepareAsync(archive, work, target, release.Version.ToString(), restart: false));
            Check("real release stages only installed optional modules", plan.Files.Any(file => file.Path.StartsWith("Modules/inkcanvas.pdf/"))
                && !plan.Files.Any(file => file.Path.StartsWith("Modules/inkcanvas.analytic-geometry/")));
            using var parent = Process.Start(parentStart);
            plan = plan with { ParentProcessId = parent.Id, ParentStartedUtcTicks = parent.StartTime.ToUniversalTime().Ticks,
                ParentExecutable = parent.MainModule.FileName };
            string planPath = Path.Combine(work, "plan.json"); await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));
            // This diagnostic substitutes a short-lived parent instead of exiting its own app.
            service.ReleaseCache();
            using var helper = UpdateService.LaunchUpdate(planPath);
            await Task.Delay(150);
            Check("external updater waits for the identified parent process to exit", !helper.HasExited
                && File.ReadAllText(Path.Combine(target, "InkCanvas.exe")) == "previous installation");
            await helper.WaitForExitAsync(lifetime.Token);
            var result = JsonSerializer.Deserialize<UpdateResult>(await File.ReadAllTextAsync(Path.Combine(work, "result.json")));
            Check("real release replaces isolated installation via external update process", helper.ExitCode == 0 && result.Success, result);
            Check("updater preserves existing plugins and local documents", File.ReadAllText(Path.Combine(target, "Plugins", "custom", "data.txt")) == "keep plugin"
                && File.ReadAllText(Path.Combine(target, "lesson.icink")) == "keep ink");
            Check("updated program has release version", System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(target, "InkCanvas.dll")).Version.ToString() == release.Version + ".0");
            // Start the actual downloaded application through its existing diagnostic entry point.
            string smoke = Path.Combine(directory, "updated-app-smoke");
            var start = new ProcessStartInfo(Path.Combine(target, "InkCanvas.exe")) { WorkingDirectory = target, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--extensionscheck=" + smoke);
            start.ArgumentList.Add("--expect-no-screen-module");
            using var app = Process.Start(start);
            await app.WaitForExitAsync(lifetime.Token);
            using var smokeResults = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(smoke, "extensions.json")));
            Check("downloaded release starts after replacement and passes ink and extension checks", app.ExitCode == 0
                && smokeResults.RootElement.EnumerateArray().All(item => item.GetProperty("passed").GetBoolean()),
                new { app.ExitCode, directory = smoke });
        }
        catch (Exception ex) { checks.Add(new { check = "update regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "updates.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            lifetime.Dispose(); keepAlive.Close(); Application.Current.Exit();
        }
        void Check(string name, bool passed, object detail = null) => checks.Add(new { check = name, passed, detail });
    }

    private sealed class DirectOfflineHandler : DelegatingHandler
    {
        public DirectOfflineHandler() : base(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AutomaticDecompression = DecompressionMethods.All }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri.Host is "github.com" or "api.github.com") throw new HttpRequestException("GitHub direct connection disabled for failover acceptance.");
            return base.SendAsync(request, token);
        }
    }

    private sealed class FutureReleaseHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                [{"tag_name":"v99.0.0","assets":[{"name":"InkCanvas-WinUI3-99.0.0-win-x64.zip",
                "browser_download_url":"https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v99.0.0/InkCanvas-WinUI3-99.0.0-win-x64.zip"}]}]
                """) });
        }
    }
}
