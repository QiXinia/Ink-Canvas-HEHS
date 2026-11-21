using InkCanvas.Features.Update;

namespace InkCanvas.Features.Persistence;

public sealed record AppStorageInfo(UpdateCacheInfo Updates, long ConfigurationBytes, long TemporaryBytes,
    long UserDataBytes, DateTime? LastSavedUtc, bool HasBackup, string SaveError, bool RecoveredFromBackup);

public sealed class AppStorage(SettingsStore settings, UpdateCache updates)
{
    public AppStorageInfo Inspect()
    {
        var files = StorageFiles.Enumerate(settings.Directory).Where(file =>
            !file.FullName.StartsWith(updates.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
        long configuration = files.Where(file => IsConfiguration(file.Name) && file.DirectoryName == Path.GetFullPath(settings.Directory))
            .Sum(file => file.Length);
        var temporary = TemporaryFiles().ToArray();
        return new(updates.Inspect(), configuration, temporary.Sum(file => file.Length),
            Math.Max(0, files.Sum(file => file.Length) - configuration - temporary.Sum(file => file.Length)),
            settings.LastSavedUtc, File.Exists(settings.BackupPath), settings.LastError, settings.RecoveredFromBackup);
    }

    public CacheCleanupResult CleanupTemporaryFiles()
    {
        long freed = 0; int removed = 0, skipped = 0;
        foreach (var file in TemporaryFiles())
        {
            // Recent temporary files may belong to an ongoing write in another instance.
            if (file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) { skipped++; continue; }
            try { long size = file.Length; file.Delete(); freed += size; removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(freed, removed, skipped);
    }

    private IEnumerable<FileInfo> TemporaryFiles() => StorageFiles.Enumerate(settings.Directory)
        .Where(file => file.DirectoryName == Path.GetFullPath(settings.Directory)
            && new[] { "Settings.json", "Extensions.json", "LotteryCooldown.json" }.Any(name =>
                file.Name == name + ".tmp" || file.Name.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)
                    && file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParseExact(file.Name.Substring(name.Length + 1, file.Name.Length - name.Length - 5), "N", out _)));

    private static bool IsConfiguration(string name) => name is "Settings.json" or "Settings.json.bak" or "Extensions.json" or "LotteryCooldown.json";
}
