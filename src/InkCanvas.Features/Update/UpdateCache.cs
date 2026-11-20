using System.Text.Json;
using System.Diagnostics;
using InkCanvas.Core.Update;
using InkCanvas.Features.Persistence;

namespace InkCanvas.Features.Update;

public sealed record CacheCleanupResult(long FreedBytes, int RemovedItems, int SkippedItems);
public sealed record UpdateCacheInfo(long Bytes, int Packages, DateTime? LastCleanupUtc);

/// <summary>Owns downloaded packages and staging directories, with cross-process leases.</summary>
public sealed class UpdateCache
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkCanvas", "Updates");
    public UpdateCache(string root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);
    public string Root { get; }
    private string CleanupStamp => Path.Combine(Root, "cleanup.json");
    public DateTime? LastCleanupUtc
    {
        get
        {
            try { return File.Exists(CleanupStamp) ? JsonSerializer.Deserialize<DateTime>(File.ReadAllText(CleanupStamp)) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    public string PackageDirectory(string hash) => Path.Combine(Root, "package-" + hash.ToLowerInvariant());

    internal async Task<FileStream> AcquireAsync(string directory, CancellationToken token)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("无效的更新缓存路径。");
        Directory.CreateDirectory(directory);
        if (!StorageFiles.IsRegular(directory)) throw new IOException("更新缓存目录包含链接。");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(150, token).ConfigureAwait(false); }
        }
    }

    public UpdateCacheInfo Inspect()
    {
        return new(StorageFiles.Size(Root), Directories().Count(), LastCleanupUtc);
    }

    public CacheCleanupResult Cleanup(int retentionDays, bool allUnused = false)
    {
        long freed = 0; int removed = 0, skipped = 0;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 30));
        foreach (string directory in Directories())
        {
            FileStream lease = null;
            try
            {
                if (!StorageFiles.IsRegular(directory)) { skipped++; continue; }
                lease = new FileStream(Path.Combine(directory, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var files = StorageFiles.Enumerate(directory).ToArray();
                // A rollback journal or uncompleted install plan must survive cache cleanup.
                bool protectedInstall = files.Any(file => file.Name == "journal.json") || files.Any(file =>
                    file.Name == "plan.json" && PendingInstall(file));
                var content = files.Where(file => file.Name != ".lease").ToArray();
                DateTime newest = content.Length > 0 ? content.Max(file => file.LastWriteTimeUtc) : new DirectoryInfo(directory).LastWriteTimeUtc;
                if (protectedInstall || !allUnused && newest > cutoff) { skipped++; continue; }
                long bytes = content.Sum(file => file.Length);
                // Keep the lease exclusively open until removal. Windows allows unlinking it
                // after closing, while all other content has already been removed under lock.
                foreach (var child in Directory.GetDirectories(directory))
                    if (!StorageFiles.DeleteDirectory(child)) throw new IOException("缓存子目录包含链接或已无法访问。");
                foreach (var file in new DirectoryInfo(directory).GetFiles())
                    if (file.Name != ".lease" && (file.Attributes & FileAttributes.ReparsePoint) == 0) file.Delete();
                lease.Dispose(); lease = null;
                File.Delete(Path.Combine(directory, ".lease"));
                Directory.Delete(directory);
                freed += bytes; removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            finally { lease?.Dispose(); }
        }
        try
        {
            if (Directory.Exists(Root) && StorageFiles.IsRegular(Root))
                File.WriteAllText(CleanupStamp, JsonSerializer.Serialize(DateTime.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(freed, removed, skipped);
    }

    private IEnumerable<string> Directories()
    {
        if (!Directory.Exists(Root) || !StorageFiles.IsRegular(Root)) return Array.Empty<string>();
        return Directory.GetDirectories(Root).Where(directory =>
        {
            string name = Path.GetFileName(directory);
            return Guid.TryParseExact(name, "N", out _) || name.Length == 72 && name.StartsWith("package-", StringComparison.Ordinal)
                && name[8..].All(Uri.IsHexDigit);
        }).ToArray();
    }

    private static bool PendingInstall(FileInfo planFile)
    {
        try
        {
            if (File.Exists(Path.Combine(planFile.DirectoryName, "result.json"))) return false;
            if (planFile.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-10)) return true;
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planFile.FullName));
            if (plan is null || plan.ParentProcessId <= 0) return false;
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception) { return true; }
    }
}
