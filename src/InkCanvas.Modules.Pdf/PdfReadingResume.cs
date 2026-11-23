using InkCanvas.Core.Settings;
using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.Pdf;

internal sealed partial class PdfWorkspaceService : IPdfResumeService
{
    private PdfReadingProgressStore _reading;
    private ResumeEntry _pendingProgress;
    private PdfWindow _pendingWindow;
    private PdfWindow _ignoredWindow;
    private bool _restoringPage, _startupResumePending;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _readingTimer;
    private ResumeBehavior ReadingBehavior => ResumeStore.Normalize(host.Settings.Resume?.Pdf ?? ResumeBehavior.Reminder);

    internal void ConfigureReadingProgress()
    {
        _reading = new PdfReadingProgressStore(host.DataDirectory);
    }

    public async Task InitializeResumeAsync(bool allowRestore)
    {
        if (ReadingBehavior == ResumeBehavior.Disabled) { await _reading.RemoveAsync(); return; }
        if (!allowRestore || _window is not null) return;
        var previous = await _reading.FindAsync(afterReboot: true);
        if (previous is null) return;
        if (!PdfReadingProgressStore.Matches(previous)) { await _reading.RemoveAsync(previous); return; }
        if (ReadingBehavior == ResumeBehavior.Automatic)
        {
            await OpenAsync(previous.SourcePath);
            return;
        }
        _startupResumePending = true;
        host.OfferResume("继续上次 PDF", Path.GetFileName(previous.SourcePath) + " · 第 " + (previous.PageIndex + 1)
            + " 页。8 秒内未继续将清除此文件的阅读进度。", async () =>
        {
            _startupResumePending = false;
            if (ReadingBehavior == ResumeBehavior.Disabled) return;
            _restoringPage = true;
            try
            {
                await OpenAsync(previous.SourcePath);
                if (_window is not null && _window.FilePath.Equals(previous.SourcePath, StringComparison.OrdinalIgnoreCase))
                    _window.GoToPage(Math.Clamp(previous.PageIndex, 0, _window.PageCountForResume - 1));
            }
            finally { _restoringPage = false; }
            await FlushResumeAsync();
        }, async () => { await _reading.RemoveAsync(previous); _startupResumePending = false; });
    }

    internal async Task ApplyReadingProgressAsync(PdfWindow window)
    {
        if (_restoringPage) return;
        if (ReadingBehavior == ResumeBehavior.Disabled) { await _reading.RemoveAsync(); return; }
        var previous = await _reading.FindAsync(window.FilePath);
        if (previous is null || previous.PageIndex == 0) return;
        if (!PdfReadingProgressStore.Matches(previous)) { await _reading.RemoveAsync(previous); return; }
        if (ReadingBehavior == ResumeBehavior.Automatic)
            window.GoToPage(Math.Clamp(previous.PageIndex, 0, window.PageCountForResume - 1));
        else
        {
            _pendingProgress = previous; _pendingWindow = window;
            host.OfferResume("继续阅读 PDF", "回到“" + Path.GetFileName(window.FilePath) + "”第 " + (previous.PageIndex + 1)
                + " 页。8 秒内未继续将清除这份阅读进度。", async () =>
            {
                if (_disposed || !ReferenceEquals(_window, window) || !ReferenceEquals(_pendingProgress, previous)) return;
                _pendingProgress = null; _pendingWindow = null;
                if (ReadingBehavior != ResumeBehavior.Disabled) window.GoToPage(Math.Clamp(previous.PageIndex, 0, window.PageCountForResume - 1));
                await FlushResumeAsync();
            }, async () =>
            {
                if (ReferenceEquals(_pendingProgress, previous))
                {
                    _ignoredWindow = window;
                    _pendingProgress = null; _pendingWindow = null;
                }
                await _reading.RemoveAsync(previous);
            });
        }
    }

    public async Task FlushResumeAsync()
    {
        if (_disposed) return;
        if (ReadingBehavior == ResumeBehavior.Disabled) { await _reading.RemoveAsync(); return; }
        if (_window is null || _restoringPage || _startupResumePending || ReferenceEquals(_pendingWindow, _window)
            || ReferenceEquals(_ignoredWindow, _window)) return;
        await _reading.SaveAsync(_window.FilePath, _window.PageIndex);
    }

    private async void OnReadingProgressChanged()
    {
        try
        {
            if (_disposed || _window is null) return;
            _ignoredWindow = null;
            if (_pendingProgress is { } previous)
            {
                _pendingProgress = null; _pendingWindow = null;
                await _reading.RemoveAsync(previous);
            }
            if (_readingTimer is null)
            {
                _readingTimer = _window.DispatcherQueue.CreateTimer();
                _readingTimer.Interval = TimeSpan.FromMilliseconds(500); _readingTimer.IsRepeating = false;
                _readingTimer.Tick += async (_, _) => await SaveReadingProgressSafelyAsync();
            }
            _readingTimer.Stop(); _readingTimer.Start();
        }
        catch (Exception error) { Core.Diagnostics.FileLog.WriteLogToFile("记录 PDF 阅读进度失败 " + error.Message); }
    }

    internal async Task SaveReadingProgressSafelyAsync()
    {
        try { await FlushResumeAsync(); }
        catch (Exception error) { Core.Diagnostics.FileLog.WriteLogToFile("保存 PDF 阅读进度失败 " + error.Message); }
    }
}

public sealed partial class PdfWindow
{
    internal int PageCountForResume => _file.Pages.Count;
    internal event Action ReadingProgressChanged;
    internal Func<Task> ClosingForResume { get; set; }
}
