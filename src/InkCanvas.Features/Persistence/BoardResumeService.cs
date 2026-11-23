using System.Numerics;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;

namespace InkCanvas.Features.Persistence;

public sealed class BoardResumeService : IDisposable
{
    private readonly BoardService _board;
    private readonly HashSet<InkController> _observed = new();
    private readonly Func<ResumeBehavior> _behavior;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _revision, _savedRevision = -1;
    private long _structureVersion = -1;
    private ResumeEntry _last;
    public ResumeStore Store { get; }
    public bool IsSaving { get; private set; }
    private bool _disposed;

    public BoardResumeService(BoardService board, string dataDirectory, Func<ResumeBehavior> behavior)
    {
        _board = board; _behavior = behavior; Store = new(dataDirectory, "board");
        board.PageChanged += PagesChanged; board.BackgroundChanged += Changed; board.ImagesChanged += Changed;
        board.ExtensionsChanged += Changed; board.ImagePlacementChanged += ImageChanged; board.MediaPlacementChanged += MediaChanged;
        PagesChanged();
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            if (ResumeStore.Normalize(_behavior()) == ResumeBehavior.Disabled)
            {
                await Store.ClearAsync(); _last = null; _savedRevision = -1; return;
            }
            if (_board.Pages.Any(page => page.HasActiveInteraction || page.Playback is not null)) return;
            long revision = _revision;
            if (revision == _savedRevision && _last?.PageIndex == _board.CurrentIndex) return;
            IsSaving = true;
            var entry = new ResumeEntry("board", 0, default, PageIndex: _board.CurrentIndex,
                Snapshot: revision == _savedRevision ? _last?.Snapshot : null);
            await Store.SaveAsync(entry, entry.Snapshot is null ? path => _board.SaveRecoveryAsync(path) : null);
            _last = await Store.ReadAsync(); _savedRevision = revision;
        }
        finally { IsSaving = false; _gate.Release(); }
    }

    public async Task RestoreAsync(ResumeEntry entry)
    {
        long revision = _revision;
        var apply = await _board.PrepareOpenAsync(Store.ResolveSnapshot(entry), true);
        if (_revision != revision || _board.Pages.Any(page => page.HasActiveInteraction))
            throw new InvalidOperationException("白板已有新的编辑，请保存当前内容后再恢复。");
        apply();
        _board.GoTo(Math.Clamp(entry.PageIndex, 0, _board.PageCount - 1));
        _last = entry; _savedRevision = _revision;
        await Store.SaveAsync(entry);
        _last = await Store.ReadAsync();
    }

    private void Changed() => _revision++;
    private void ViewChanged(Matrix3x2 _) => Changed();
    private void ImageChanged(BoardImageAsset _) => Changed();
    private void MediaChanged(BoardMediaAsset _) => Changed();
    private void PagesChanged()
    {
        if (_structureVersion == _board.PageStructureVersion) return;
        _structureVersion = _board.PageStructureVersion;
        foreach (var page in _observed.Where(page => !_board.Pages.Contains(page)).ToArray()) { Observe(page, false); _observed.Remove(page); }
        foreach (var page in _board.Pages) if (_observed.Add(page)) Observe(page, true);
        Changed();
    }
    private void Observe(InkController page, bool attach)
    {
        if (attach) { page.Document.Changed += Changed; page.History.Changed += Changed; page.ViewChanged += ViewChanged; }
        else { page.Document.Changed -= Changed; page.History.Changed -= Changed; page.ViewChanged -= ViewChanged; }
    }
    public void Dispose()
    {
        _disposed = true;
        _board.PageChanged -= PagesChanged; _board.BackgroundChanged -= Changed; _board.ImagesChanged -= Changed;
        _board.ExtensionsChanged -= Changed; _board.ImagePlacementChanged -= ImageChanged; _board.MediaPlacementChanged -= MediaChanged;
        foreach (var page in _observed) Observe(page, false);
    }
}
