using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace InkCanvas.Features.Update;

public sealed class UpdateService : IDisposable
{
    private readonly UpdateTransport _transport;
    private FileStream _lease;
    private string _leasedDirectory;
    public UpdateService(string repository = "QiXinia/Ink-Canvas-HEHS", UpdateTransport transport = null, UpdateCache cache = null)
    {
        if (repository.Split('/').Length != 2 || repository.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '-' or '_' or '.')))
            throw new ArgumentException("Invalid GitHub repository.", nameof(repository));
        Repository = repository;
        _transport = transport ?? new UpdateTransport();
        Cache = cache ?? new UpdateCache();
        _transport.ProgressChanged += ForwardProgress;
    }

    public UpdateCache Cache { get; }
    public void ReleaseCache() { _lease?.Dispose(); _lease = null; _leasedDirectory = null; }
    public event Action<UpdateProgress> ProgressChanged;
    private void ForwardProgress(UpdateProgress progress) => ProgressChanged?.Invoke(progress);

    public string Repository { get; }
    public string LastSource => _transport.LastSource;
    public event Action<string> StatusChanged { add => _transport.StatusChanged += value; remove => _transport.StatusChanged -= value; }
    public AppVersion CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(3, 0, 0);
            return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellation = default)
    {
        string json;
        try
        {
            json = await _transport.GetTextAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", text =>
            {
                try { using var document = JsonDocument.Parse(text); return document.RootElement.ValueKind == JsonValueKind.Array; }
                catch (JsonException) { return false; }
            }, cancellation).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!cancellation.IsCancellationRequested)
        {
            Core.Diagnostics.FileLog.WriteLogToFile("Update: 版本 API 不可用，尝试公开发布页。");
            try { return await GetWebReleasesAsync(cancellation).ConfigureAwait(false); }
            catch (HttpRequestException) when (!cancellation.IsCancellationRequested)
            {
                Core.Diagnostics.FileLog.WriteLogToFile("Update: 发布页不可用，尝试最新稳定版的校验清单。");
                string sums = await _transport.GetTextAsync($"https://github.com/{Repository}/releases/latest/download/SHA256SUMS.txt",
                    text => ReleaseFeed.ParseLatestChecksums(text, Repository) is not null, cancellation).ConfigureAwait(false);
                return new[] { ReleaseFeed.ParseLatestChecksums(sums, Repository) };
            }
        }
        return ReleaseFeed.ParseStableList(json).Where(release => release.IsPortable && TrustedAsset(release.InstallerUrl)).ToArray();
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetWebReleasesAsync(CancellationToken token)
    {
        string page = await _transport.GetTextAsync($"https://github.com/{Repository}/releases",
            text => ReleaseWebFeed.ReadEntries(text, Repository).Count > 0, token).ConfigureAwait(false);
        var releases = new List<ReleaseInfo>();
        foreach (var entry in ReleaseWebFeed.ReadEntries(page, Repository))
        {
            string assets = await _transport.GetTextAsync($"https://github.com/{Repository}/releases/expanded_assets/{Uri.EscapeDataString(entry.Tag)}",
                text => ReleaseWebFeed.IsAssetsPage(text, Repository, entry), token).ConfigureAwait(false);
            var release = ReleaseWebFeed.ReadAssets(assets, Repository, entry);
            if (release?.IsPortable == true && TrustedAsset(release.InstallerUrl)) releases.Add(release);
        }
        return releases;
    }

    public async Task<ReleaseInfo> CheckAsync(CancellationToken cancellation = default)
        => (await GetReleasesAsync(cancellation).ConfigureAwait(false)).FirstOrDefault();

    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress = null, CancellationToken cancellation = default)
    {
        if (release is null || !release.IsPortable || !TrustedAsset(release.InstallerUrl))
            throw new InvalidDataException("此发行版没有可自动更新的 WinUI 3 x64 ZIP 包。");
        string hash = release.Sha256;
        if (string.IsNullOrEmpty(hash) && TrustedAsset(release.ChecksumsUrl))
        {
            string sums = await _transport.GetTextAsync(release.ChecksumsUrl, text => Checksum(text, release.AssetName) is not null, cancellation).ConfigureAwait(false);
            hash = Checksum(sums, release.AssetName);
        }
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("此安装包缺少 SHA256 校验信息。");
        string directory = Cache.PackageDirectory(hash);
        if (_leasedDirectory != directory)
        {
            _lease?.Dispose(); _lease = null; _leasedDirectory = null;
            _lease = await Cache.AcquireAsync(directory, cancellation).ConfigureAwait(false);
            _leasedDirectory = directory;
        }
        string path = Path.Combine(directory, "package.zip");
        if (File.Exists(path))
        {
            ProgressChanged?.Invoke(new(UpdateStage.Verifying, Source: "本地缓存"));
            await using var cached = File.OpenRead(path);
            string cachedHash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(cached, cancellation).ConfigureAwait(false));
            if ((release.InstallerSize <= 0 || cached.Length == release.InstallerSize) && cachedHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                ProgressChanged?.Invoke(new(UpdateStage.Verifying, Fraction: 1, Source: "本地缓存"));
                return path;
            }
        }
        await _transport.DownloadAsync(release.InstallerUrl, path, release.InstallerSize, hash, progress, cancellation).ConfigureAwait(false);
        return path;
    }

    public async Task<string> PrepareAsync(ReleaseInfo release, string archive, string target, bool restart = true, CancellationToken token = default)
    {
        string directory = Path.Combine(Path.GetDirectoryName(archive), "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var current = Process.GetCurrentProcess();
        try
        {
            ProgressChanged?.Invoke(new(UpdateStage.Preparing));
            var plan = await Task.Run(() => UpdatePackage.PrepareAsync(archive, directory, target, release.Version.ToString(),
                current.Id, current.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath, restart, token), token).ConfigureAwait(false);
            string planPath = Path.Combine(directory, "plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), token).ConfigureAwait(false);
            return planPath;
        }
        catch
        {
            try { Persistence.StorageFiles.DeleteDirectory(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static Process LaunchUpdate(string planPath, string hostDirectory = null)
    {
        string helper = hostDirectory ?? Path.Combine(AppContext.BaseDirectory, "UpdateHost");
        string copy = Path.Combine(Path.GetDirectoryName(planPath), "host");
        Directory.CreateDirectory(copy);
        foreach (string path in Directory.GetFiles(helper)) File.Copy(path, Path.Combine(copy, Path.GetFileName(path)), true);
        var start = new ProcessStartInfo(Path.Combine(copy, "InkCanvas.UpdateHost.exe"))
        {
            WorkingDirectory = copy, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(Path.GetFullPath(planPath));
        return Process.Start(start) ?? throw new IOException("无法启动更新进程。");
    }

    private bool TrustedAsset(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com" && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/" + Repository + "/releases/latest/download/", StringComparison.OrdinalIgnoreCase));

    private static string Checksum(string text, string asset)
    {
        foreach (string line in text.Split('\n'))
        {
            var parts = line.Trim().Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)
                && parts[1].TrimStart('*', ' ').Equals(asset, StringComparison.Ordinal)) return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public void Dispose() { ReleaseCache(); _transport.ProgressChanged -= ForwardProgress; _transport.Dispose(); }
}
