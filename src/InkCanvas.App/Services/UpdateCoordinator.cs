using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using InkCanvas.Features.Update;
using Microsoft.UI.Dispatching;

namespace InkCanvas.App.Services;

/// <summary>Check, stage and install on the configured schedule, while the canvas is idle.</summary>
public sealed class UpdateCoordinator : IDisposable
{
    private readonly AppServices _services;
    private readonly UpdateService _update;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _timer;
    private bool _running, _disposed;
    private DateTime _lastCheck = DateTime.MinValue;
    private DateTime _retryAfter = DateTime.MinValue;
    private ReleaseInfo _pending;
    private string _archive, _plan;
    private string _cachedTag;
    private CancellationTokenSource _operation;
    private int _operationId;
    private bool _maintenanceRunning;

    public UpdateCoordinator(AppServices services, UpdateService update = null)
    {
        _services = services;
        _update = update ?? new UpdateService(cache: services.UpdateCache);
        _update.StatusChanged += OnTransportStatus;
        _update.ProgressChanged += OnProgress;
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += async (_, _) =>
        {
            MaintainCache();
            if (_disposed || !services.Settings.Startup.IsAutoUpdate || DateTime.UtcNow < _retryAfter) return;
            if (_pending is not null || DateTime.UtcNow - _lastCheck > TimeSpan.FromHours(6)) await CheckAsync(false);
        };
        _timer.Start();
        MaintainCache();
    }

    public Func<string, string, Task<bool>> Confirm { get; set; }
    public Func<ReleaseInfo, Task<bool>> ConfirmRelease { get; set; }
    public Func<string, string, Task> Notify { get; set; }
    public Func<IReadOnlyList<ReleaseInfo>, Task<ReleaseInfo>> ChooseRelease { get; set; }
    public Func<bool> CanInstallSilently { get; set; }
    public Func<bool, Task<bool>> PrepareForInstall { get; set; }
    public Action ExitForInstall { get; set; }
    public AppVersion CurrentVersion => _update.CurrentVersion;
    public string Status { get; private set; } = "尚未检查更新";
    public UpdateProgress Progress { get; private set; } = new(UpdateStage.Idle);
    public bool IsBusy => _running;
    public bool CanCancel => _running && Progress.Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.Preparing;
    public UpdateCache Cache => _update.Cache;
    public DateTime? LastCheckUtc { get; private set; }
    public string TargetVersion => _pending?.Tag;
    public void Cancel() { if (CanCancel) _operation?.Cancel(); }
    private CancellationToken Token => _operation?.Token ?? _lifetime.Token;
    public event Action StatusChanged;

    public async Task CheckAsync(bool interactive)
    {
        if (_running || _disposed || !interactive && !_services.Settings.Startup.IsAutoUpdate) return;
        _running = true;
        StartOperation();
        try
        {
            if (_pending is null || interactive || DateTime.UtcNow - _lastCheck > TimeSpan.FromHours(6))
            {
                SetStatus("正在获取 GitHub 版本列表…", UpdateStage.Checking);
                var releases = await _update.GetReleasesAsync(Token);
                _lastCheck = DateTime.UtcNow;
                LastCheckUtc = _lastCheck;
                var release = releases.FirstOrDefault();
                if (release is null) { _pending = null; SetStatus("仓库尚无适用的稳定发行版", UpdateStage.Idle); if (interactive) await Say("检查更新", Status); return; }
                if (!release.IsNewerThan(CurrentVersion))
                { _pending = null; SetStatus($"已是最新版本 {CurrentVersion} · {_update.LastSource}", UpdateStage.Idle); if (interactive) await Say("检查更新", Status); return; }
                if (_cachedTag != release.Tag) { _archive = null; _plan = null; _cachedTag = release.Tag; }
                _pending = release;
            }
            await InstallPendingAsync(interactive);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { if (!_disposed) { _pending = null; SetStatus("已取消更新", UpdateStage.Canceled); } }
        catch (Exception exception) { await FailedAsync(exception, interactive); }
        finally { FinishOperation(); }
    }

    public async Task ShowVersionsAsync()
    {
        if (_running || _disposed) return;
        _running = true;
        StartOperation();
        try
        {
            SetStatus("正在获取 GitHub 版本列表…", UpdateStage.Checking);
            var releases = await _update.GetReleasesAsync(Token);
            _lastCheck = DateTime.UtcNow;
            LastCheckUtc = _lastCheck;
            SetStatus($"已获取 {releases.Count} 个稳定版本 · {_update.LastSource}", UpdateStage.Idle);
            var selected = ChooseRelease is null ? null : await ChooseRelease(releases);
            if (selected is null) return;
            if (!selected.IsNewerThan(CurrentVersion)) { await Say("版本列表", "所选版本不高于当前版本。"); return; }
            if (_cachedTag != selected.Tag) { _archive = null; _plan = null; _cachedTag = selected.Tag; }
            _pending = selected;
            await InstallPendingAsync(true);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { if (!_disposed) { _pending = null; SetStatus("已取消更新", UpdateStage.Canceled); } }
        catch (Exception exception) { await FailedAsync(exception, true); }
        finally { FinishOperation(); }
    }

    private async Task InstallPendingAsync(bool interactive)
    {
        var release = _pending;
        bool silentEnabled = _services.Settings.Startup.IsAutoUpdateWithSilence;
        bool silent = !interactive && UpdatePolicy.MayInstallSilently(_services.Settings.Startup, DateTime.Now);
        if (!interactive && silentEnabled && !silent)
        { SetStatus($"发现 {release.Tag}，等待静默更新时间段", UpdateStage.Waiting); return; }
        if (silent && CanInstallSilently?.Invoke() != true)
        { SetStatus($"发现 {release.Tag}，等待画布空闲", UpdateStage.Waiting); return; }
        if (!silent)
        {
            SetStatus($"发现新版本 {release.Tag}", UpdateStage.Waiting);
            string notes = release.Notes ?? "";
            if (notes.Length > 600) notes = notes[..600] + "…";
            bool accepted = ConfirmRelease is not null ? await ConfirmRelease(release)
                : Confirm is not null && await Confirm("发现更新", $"新版本 {release.Tag}，当前 {CurrentVersion}。\n\n{notes}\n\n下载校验后会保存当前画布、退出并更新，完成后重新打开。");
            if (!accepted)
            { _pending = null; SetStatus("已取消本次更新", UpdateStage.Canceled); return; }
        }
        string previousArchive = _archive;
        if (_archive is null || !File.Exists(_archive)) _plan = null;
        SetStatus($"正在获取并校验 {release.Tag}", UpdateStage.Downloading);
        _archive = await _update.DownloadAsync(release, cancellation: Token);
        if (_archive != previousArchive) _plan = null;
        if (silent && (!UpdatePolicy.MayInstallSilently(_services.Settings.Startup, DateTime.Now) || CanInstallSilently?.Invoke() != true))
        { SetStatus("安装包已校验，等待静默时段和画布空闲", UpdateStage.Waiting); return; }
        SetStatus("正在准备更新文件…", UpdateStage.Preparing);
        if (_plan is null || !File.Exists(_plan))
            _plan = await _update.PrepareAsync(release, _archive, AppContext.BaseDirectory, token: Token);
        Token.ThrowIfCancellationRequested();
        SetStatus("安装包已准备，正在保存当前画布…", UpdateStage.Waiting);
        if (PrepareForInstall is null || !await PrepareForInstall(silent))
        {
            if (interactive) _pending = null;
            SetStatus("安装包已准备，等待下次安装", UpdateStage.Waiting); return;
        }
        if (silent && !UpdatePolicy.MayInstallSilently(_services.Settings.Startup, DateTime.Now)) return;
        using var helper = UpdateService.LaunchUpdate(_plan);
        SetStatus("正在退出并安装更新…", UpdateStage.Installing);
        ExitForInstall?.Invoke();
    }

    private void StartOperation()
    {
        _operationId++;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        StatusChanged?.Invoke();
    }
    private void FinishOperation()
    {
        _running = false; _operation?.Dispose(); _operation = null;
        if (_archive is null) _update.ReleaseCache();
        if (!_disposed) StatusChanged?.Invoke();
    }
    private void OnTransportStatus(string status)
    {
        int operation = _operationId;
        _dispatcher.TryEnqueue(() => { if (!_disposed && _running && operation == _operationId) SetStatus(status); });
    }
    private void OnProgress(UpdateProgress progress)
    {
        int operation = _operationId;
        void Apply()
        {
            if (_disposed || !_running || operation != _operationId) return;
            Progress = progress;
            Status = progress.Stage switch
            {
                UpdateStage.Downloading => $"正在下载 {_pending?.Tag}" + (progress.Fraction is double value ? $" · {value:P0}" : ""),
                UpdateStage.Verifying => "正在校验更新包 SHA256…",
                UpdateStage.Preparing => "正在准备更新文件…",
                _ => Status,
            };
            StatusChanged?.Invoke();
        }
        if (_dispatcher.HasThreadAccess) Apply();
        else _dispatcher.TryEnqueue(Apply);
    }
    private void SetStatus(string status, UpdateStage? stage = null)
    {
        Status = status;
        if (stage.HasValue) Progress = new(stage.Value, Source: Progress.Source);
        StatusChanged?.Invoke();
    }
    private void MaintainCache()
    {
        if (_maintenanceRunning || !_services.Settings.Storage.AutomaticallyCleanCache) return;
        _maintenanceRunning = true;
        int days = _services.Settings.Storage.UpdateCacheDays;
        _ = Task.Run(() =>
        {
            try
            {
                if (Cache.LastCleanupUtc is DateTime last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
                Cache.Cleanup(days);
                new Features.Persistence.AppStorage(_services.Store, Cache).CleanupTemporaryFiles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Core.Diagnostics.FileLog.WriteLogToFile("缓存维护失败 · " + ex.Message); }
            finally { _maintenanceRunning = false; }
        });
    }
    private async Task FailedAsync(Exception exception, bool interactive)
    {
        _retryAfter = DateTime.UtcNow.AddMinutes(15);
        _lastCheck = DateTime.UtcNow - TimeSpan.FromHours(6);
        SetStatus("更新失败 · " + exception.Message, UpdateStage.Failed);
        Core.Diagnostics.FileLog.WriteLogToFile(Status, Core.Diagnostics.LogType.Error);
        if (interactive) await Say("更新失败", exception.Message);
    }
    private Task Say(string title, string message) => Notify?.Invoke(title, message) ?? Task.CompletedTask;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _timer.Stop(); _lifetime.Cancel(); _update.StatusChanged -= OnTransportStatus; _update.ProgressChanged -= OnProgress; _update.Dispose();
    }
}
