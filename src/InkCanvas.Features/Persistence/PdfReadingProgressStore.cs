using System.Text.Json;

namespace InkCanvas.Features.Persistence;

/// <summary>Bounded per-document page history, containing positions and file identity only.</summary>
public sealed class PdfReadingProgressStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _bootId;
    public PdfReadingProgressStore(string settingsDirectory, long? bootId = null)
    {
        _path = Path.Combine(settingsDirectory, "Resume", "PdfReading.json"); _bootId = bootId ?? ResumeStore.CurrentBootId;
    }
    public async Task<ResumeEntry> FindAsync(string source = null, bool afterReboot = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return null;
            await using var lease = await ResumeFileLock.AcquireAsync(_path);
            var entries = await ReadAsync();
            return entries.Where(entry => (source is null || entry.SourcePath.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                && (!afterReboot || entry.PageIndex > 0 && Math.Abs(entry.BootId - _bootId) > 1)).OrderByDescending(entry => entry.SavedAtUtc).FirstOrDefault();
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(string source, int page)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        var file = new FileInfo(source);
        if (!file.Exists) return;
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            await using var lease = await ResumeFileLock.AcquireAsync(_path);
            var entries = await ReadAsync();
            var previous = entries.FirstOrDefault(entry => entry.SourcePath.Equals(file.FullName, StringComparison.OrdinalIgnoreCase));
            if (previous is not null && previous.PageIndex == page && previous.SourceLength == file.Length
                && previous.SourceWriteUtc == file.LastWriteTimeUtc && previous.BootId == _bootId) return;
            entries.RemoveAll(entry => entry.SourcePath.Equals(file.FullName, StringComparison.OrdinalIgnoreCase));
            entries.Add(new("pdf", _bootId, DateTimeOffset.UtcNow, file.FullName, file.Length, file.LastWriteTimeUtc, page));
            await WriteAsync(entries.OrderByDescending(entry => entry.SavedAtUtc).Take(100).ToList());
        }
        finally { _gate.Release(); }
    }
    public async Task RemoveAsync(ResumeEntry entry = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return;
            await using var lease = await ResumeFileLock.AcquireAsync(_path);
            var entries = await ReadAsync();
            entries.RemoveAll(current => entry is null || current.SourcePath.Equals(entry.SourcePath, StringComparison.OrdinalIgnoreCase)
                && current.SavedAtUtc == entry.SavedAtUtc);
            await WriteAsync(entries);
        }
        finally { _gate.Release(); }
    }
    private async Task<List<ResumeEntry>> ReadAsync()
    {
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("PDF 阅读进度记录过大。");
        var entries = JsonSerializer.Deserialize<List<ResumeEntry>>(await File.ReadAllBytesAsync(_path)) ?? new();
        return entries.Where(entry => entry is { Kind: "pdf", PageIndex: >= 0 } && !string.IsNullOrWhiteSpace(entry.SourcePath)).Take(100).ToList();
    }
    private async Task WriteAsync(List<ResumeEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(entries)); File.Move(temporary, _path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static bool Matches(ResumeEntry entry)
    {
        var file = new FileInfo(entry.SourcePath);
        return file.Exists && file.Length == entry.SourceLength && file.LastWriteTimeUtc == entry.SourceWriteUtc;
    }
}
