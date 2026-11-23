using System.Text.Json;
using InkCanvas.Core.Settings;

namespace InkCanvas.Features.Persistence;

public sealed record ResumeEntry(string Kind, long BootId, DateTimeOffset SavedAtUtc, string SourcePath = null,
    long SourceLength = 0, DateTime SourceWriteUtc = default, int PageIndex = 0, string Snapshot = null);

/// <summary>Small, atomic resume records. Content snapshots are committed before their pointer.</summary>
public sealed class ResumeStore
{
    private readonly string _root;
    private readonly string _kind;
    private readonly long _bootId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string PathName => Path.Combine(_root, _kind + ".json");
    public static long CurrentBootId => (long)Math.Round((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64) / 60000d);

    public ResumeStore(string settingsDirectory, string kind, long? bootId = null)
    {
        if (kind is not ("board" or "pdf")) throw new ArgumentOutOfRangeException(nameof(kind));
        _root = Path.Combine(settingsDirectory, "Resume"); _kind = kind; _bootId = bootId ?? CurrentBootId;
    }

    private string NewSnapshotPath() => Path.Combine(_root, _kind + "-" + Guid.NewGuid().ToString("N") + ".icink");

    public async Task<ResumeEntry> ReadAsync(bool afterReboot = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(PathName)) return null;
            await using var lease = await ResumeFileLock.AcquireAsync(PathName);
            if (new FileInfo(PathName).Length > 65536) throw new InvalidDataException("恢复记录无效。");
            var entry = JsonSerializer.Deserialize<ResumeEntry>(await File.ReadAllBytesAsync(PathName));
            if (entry is null || entry.Kind != _kind || entry.PageIndex < 0) throw new InvalidDataException("恢复记录无效。");
            if (entry.Snapshot is not null) ResolveSnapshot(entry);
            return afterReboot && Math.Abs(entry.BootId - _bootId) <= 1 ? null : entry;
        }
        finally { _gate.Release(); }
    }

    public string ResolveSnapshot(ResumeEntry entry)
    {
        if (entry.Snapshot is null || Path.GetFileName(entry.Snapshot) != entry.Snapshot || !entry.Snapshot.StartsWith(_kind + "-", StringComparison.Ordinal)
            || !entry.Snapshot.EndsWith(".icink", StringComparison.Ordinal)) throw new InvalidDataException("恢复快照路径无效。");
        return Path.Combine(_root, entry.Snapshot);
    }

    public async Task SaveAsync(ResumeEntry entry, Func<string, Task> snapshot = null)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root);
            await using var lease = await ResumeFileLock.AcquireAsync(PathName);
            entry = entry with { Kind = _kind, BootId = _bootId, SavedAtUtc = DateTimeOffset.UtcNow };
            if (snapshot is not null)
            {
                string path = NewSnapshotPath();
                await snapshot(path);
                entry = entry with { Snapshot = Path.GetFileName(path) };
            }
            string temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(entry)); File.Move(temporary, PathName, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Prune(entry.Snapshot);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(ResumeEntry expected = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (!Directory.Exists(_root)) return;
            await using var lease = await ResumeFileLock.AcquireAsync(PathName);
            if (expected is not null && File.Exists(PathName))
            {
                var current = JsonSerializer.Deserialize<ResumeEntry>(await File.ReadAllBytesAsync(PathName));
                if (current?.SavedAtUtc != expected.SavedAtUtc) return;
            }
            if (File.Exists(PathName)) File.Delete(PathName);
            Prune(null);
        }
        finally { _gate.Release(); }
    }

    private void Prune(string retained)
    {
        if (!Directory.Exists(_root)) return;
        foreach (string path in Directory.EnumerateFiles(_root, _kind + "-*.icink"))
            if (Path.GetFileName(path) != retained)
                try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static ResumeBehavior Normalize(ResumeBehavior behavior) => Enum.IsDefined(behavior) ? behavior : ResumeBehavior.Reminder;
}

internal static class ResumeFileLock
{
    internal static async Task<FileStream> AcquireAsync(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 40) { await Task.Delay(50); }
        }
    }
}
