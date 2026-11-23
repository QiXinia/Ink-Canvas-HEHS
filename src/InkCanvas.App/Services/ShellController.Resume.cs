using InkCanvas.App.Views;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Dispatching;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private DispatcherQueueTimer _resumeTimer;
    private readonly List<ResumeReminderWindow> _resumeReminders = new();
    private bool _boardResumePending;

    internal async Task InitializeResumeAsync(bool allowRestore)
    {
        if (_resumeTimer is not null) return;
        try
        {
            var mode = ResumeStore.Normalize(Services.Settings.Resume.Whiteboard);
            if (mode == ResumeBehavior.Disabled) await Services.BoardResume.Store.ClearAsync();
            else if (allowRestore)
            {
                var previous = await Services.BoardResume.Store.ReadAsync(afterReboot: true);
                if (previous is not null)
                {
                    if (mode == ResumeBehavior.Automatic) await RestoreBoardResumeAsync(previous);
                    else
                    {
                        _boardResumePending = true;
                        OfferResume("继续上次白板", "回到第 " + (previous.PageIndex + 1) + " 页。8 秒内未继续将清除这份恢复记录。",
                            () => RestoreBoardResumeAsync(previous), async () =>
                            {
                                try { await Services.BoardResume.Store.ClearAsync(previous); }
                                finally { _boardResumePending = false; }
                            });
                    }
                }
            }
            var pdf = Services.Modules.GetExport<IPdfResumeService>();
            if (pdf is not null) await pdf.InitializeResumeAsync(allowRestore);
        }
        catch (Exception error) { FileLog.WriteLogToFile("读取上次进度失败 " + error, LogType.Error); }
        if (_disposed) return;
        _resumeTimer = _dispatcher.CreateTimer(); _resumeTimer.Interval = TimeSpan.FromSeconds(15);
        _resumeTimer.Tick += async (_, _) => await SaveResumeAsync();
        _resumeTimer.Start();
    }

    private async Task RestoreBoardResumeAsync(ResumeEntry entry)
    {
        try
        {
            if (_disposed) return;
            if (Services.Board.HasContent && !await ConfirmAsync("继续上次白板", "恢复会替换当前白板，请先保存当前内容。是否继续？")) return;
            if (ResumeStore.Normalize(Services.Settings.Resume.Whiteboard) == ResumeBehavior.Disabled) return;
            await Services.BoardResume.RestoreAsync(entry);
            Services.ApplySettings();
            if (_board is null) ToggleBoard();
            _board?.Activate();
        }
        catch (Exception error) { await NotifyAsync("白板恢复失败", error.GetBaseException().Message); }
        finally { _boardResumePending = false; }
    }

    private async Task<bool> SaveResumeAsync(bool force = false)
    {
        if (_disposed) return true;
        try
        {
            if ((_board is not null || Services.Settings.Resume.Whiteboard == ResumeBehavior.Disabled)
                && !_boardResumePending && (force || !Services.BoardResume.IsSaving)) await Services.BoardResume.SaveAsync();
            var pdf = Services.Modules.GetExport<IPdfResumeService>();
            if (pdf is not null) await pdf.FlushResumeAsync();
            return true;
        }
        catch (Exception error) { FileLog.WriteLogToFile("保存阅读进度失败 " + error, LogType.Error); return false; }
    }

    internal void OfferResume(string title, string description, Func<Task> resume, Func<Task> dismiss)
    {
        var window = new ResumeReminderWindow(title, description, EffectiveTheme, () => Guard(resume), () => Guard(dismiss));
        _resumeReminders.Add(window);
        window.Closed += (_, _) => _resumeReminders.Remove(window);
        window.Show(_resumeReminders.Count - 1);

        async Task Guard(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { FileLog.WriteLogToFile("处理续接提醒失败 " + error, LogType.Error); }
        }
    }
}
