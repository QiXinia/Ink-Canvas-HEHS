using System.Net;
using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.Features.Update;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

internal static class UpdateSourceRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var keeper = new Window { Content = new Microsoft.UI.Xaml.Controls.Grid() };
        keeper.Activate(); keeper.AppWindow.Hide();
        string package = Path.Combine(directory, "verified-package.download");
        try
        {
            ReleaseInfo latest = null;
            if (!Environment.GetCommandLineArgs().Contains("--updatesource-mirror-only"))
            {
                using var direct = new UpdateService();
                direct.StatusChanged += Log;
                var releases = await direct.GetReleasesAsync(lifetime.Token);
                latest = releases.First();
                checks.Add(new { check = "live version discovery with public-page fallback", passed = latest.IsPortable,
                    source = direct.LastSource, versions = releases.Select(release => release.Tag), latest.AssetName });
                await Download(latest, false);
            }

            using var mirror = new UpdateService(transport: new UpdateTransport(new DirectOfflineHandler()));
            mirror.StatusChanged += Log;
            var mirrored = (await mirror.GetReleasesAsync(lifetime.Token)).First();
            checks.Add(new { check = "live version discovery with GitHub direct requests disabled", passed = mirrored.IsPortable && (latest is null || mirrored.Version == latest.Version),
                source = mirror.LastSource, mirrored.Tag, mirrored.AssetName });
            await Download(mirrored, true);
        }
        catch (Exception exception)
        { checks.Add(new { check = "update source regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            if (File.Exists(package)) File.Delete(package);
            await File.WriteAllTextAsync(Path.Combine(directory, "sources.json"), JsonSerializer.Serialize(checks,
                new JsonSerializerOptions { WriteIndented = true }));
            keeper.Close(); Application.Current.Exit();
        }

        async Task Download(ReleaseInfo release, bool offline)
        {
            using var transport = new UpdateTransport(offline ? new DirectOfflineHandler() : null);
            transport.StatusChanged += Log;
            if (release.Sha256 is null) throw new InvalidDataException("Live release has no digest for source acceptance.");
            int previous = -1;
            var progress = new Progress<double>(value =>
            {
                int percent = (int)(value * 100);
                if (percent / 10 != previous) { previous = percent / 10; Log($"下载进度 {percent}%"); }
            });
            await transport.DownloadAsync(release.InstallerUrl, package, release.InstallerSize, release.Sha256, progress, lifetime.Token);
            checks.Add(new { check = offline ? "mirror package verifies GitHub SHA256" : "direct package verifies GitHub SHA256",
                passed = File.Exists(package), source = transport.LastSource, bytes = new FileInfo(package).Length, release.Sha256 });
            File.Delete(package);
        }

        void Log(string message) => File.AppendAllText(Path.Combine(directory, "network.log"), DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
    }

    private sealed class DirectOfflineHandler : DelegatingHandler
    {
        public DirectOfflineHandler() : base(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AutomaticDecompression = DecompressionMethods.All }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri.Host is "github.com" or "api.github.com")
                throw new HttpRequestException("Direct GitHub disabled for source acceptance.");
            return base.SendAsync(request, token);
        }
    }
}
