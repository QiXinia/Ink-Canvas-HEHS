using System.Diagnostics;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private UpdateStatusCard _updateCard;
    private int _storagePageGeneration;
    private bool _storageCleaning;

    private void BuildStorageSettings()
    {
        S.Storage ??= new();
        var storage = new AppStorage(_services.Store, _updater?.Cache ?? _services.UpdateCache);
        var cache = _updater?.Cache ?? _services.UpdateCache;
        int generation = _storagePageGeneration;
        AddSection("缓存管理");
        var updateBytes = new TextBlock { Text = "正在统计…", TextWrapping = TextWrapping.Wrap };
        var temporaryBytes = new TextBlock { Text = "正在统计…", TextWrapping = TextWrapping.Wrap };
        var configurationBytes = new TextBlock { Text = "正在统计…", TextWrapping = TextWrapping.Wrap };
        var userBytes = new TextBlock { Text = "正在统计…", TextWrapping = TextWrapping.Wrap };
        var saveState = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _content.Children.Add(Searchable(Card(Row("更新缓存", updateBytes, "下载包、解压文件和安装回滚文件。使用中或待安装的文件会自动保留。", 240)), "更新缓存", "下载 安装 回滚 空间"));
        _content.Children.Add(Searchable(Card(Row("配置临时文件", temporaryBytes, "清理超过一天的配置写入残留文件。", 240)), "配置临时文件", "缓存 清理 空间"));
        _content.Children.Add(ToggleRow("自动清理过期缓存", () => S.Storage.AutomaticallyCleanCache, value => S.Storage.AutomaticallyCleanCache = value,
            note: "每天检查一次更新缓存和配置临时文件。"));
        int[] days = { 1, 3, 7, 14, 30 };
        _content.Children.Add(ComboRow("更新缓存保留时间", days.Select(value => value + " 天").ToArray(),
            () => Math.Max(0, Array.IndexOf(days, S.Storage.UpdateCacheDays)), index => S.Storage.UpdateCacheDays = days[index]));
        var clean = ButtonRow("清理未使用的缓存", "立即释放可清理的更新文件和过期配置临时文件。", async () =>
        {
            if (_updater?.IsBusy == true || _storageCleaning) return;
            _storageCleaning = true; RefreshControls();
            try
            {
                var result = await Task.Run(() =>
                {
                    var updates = cache.Cleanup(S.Storage.UpdateCacheDays, allUnused: true);
                    var temporary = storage.CleanupTemporaryFiles();
                    return new CacheCleanupResult(updates.FreedBytes + temporary.FreedBytes,
                        updates.RemovedItems + temporary.RemovedItems, updates.SkippedItems + temporary.SkippedItems);
                });
                if (!_closed) ShowNotification($"已释放 {FormatBytes(result.FreedBytes)}，清理 {result.RemovedItems} 项，保留 {result.SkippedItems} 项正在使用或受保护的文件。");
                await Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { if (!_closed) ShowNotification("清理未完成 · " + ex.Message, InfoBarSeverity.Error); }
            finally { _storageCleaning = false; if (!_closed) RefreshControls(); }
        }, "清理");
        _content.Children.Add(Require(clean, () => _storageCleaning ? "正在清理缓存。" : _updater?.IsBusy == true ? "更新正在进行，请完成或取消后再清理缓存。" : null));
        _content.Children.Add(ButtonRow("刷新存储占用", "重新统计本地文件大小。", async () => await Refresh(), "刷新"));
        _content.Children.Add(ButtonRow("打开更新缓存目录", cache.Root, () => OpenStorageFolder(cache.Root), "打开文件夹"));
        AddSection("用户配置与数据");
        _content.Children.Add(Searchable(Card(Row("用户配置", configurationBytes, "主配置、上一份配置备份、插件开关和点名冷却记录。", 240)), "用户配置", "Settings.json 备份 恢复 保存"));
        _content.Children.Add(Card(saveState));
        _content.Children.Add(Searchable(Card(Row("其他用户数据", userBytes, "包含用户目录中的插件数据、日志和更新前的画布备份。", 240)), "其他用户数据", "配置 日志 插件 备份"));
        _content.Children.Add(ButtonRow("打开用户配置目录", _services.Store.Directory, () => OpenStorageFolder(_services.Store.Directory), "打开文件夹"));
        _content.Children.Add(InfoRow("设置内容未变化时跳过磁盘写入。保存时保留上一份有效配置，主配置损坏后自动尝试备份。"));
        if (!_indexingSearch) _ = Refresh();

        async Task Refresh()
        {
            try
            {
                var info = await Task.Run(storage.Inspect);
                if (_closed || generation != _storagePageGeneration) return;
                updateBytes.Text = $"{FormatBytes(info.Updates.Bytes)} · {info.Updates.Packages} 个缓存包"
                    + (info.Updates.LastCleanupUtc is DateTime last ? "\n上次清理 " + last.ToLocalTime().ToString("MM-dd HH:mm") : "");
                temporaryBytes.Text = FormatBytes(info.TemporaryBytes);
                configurationBytes.Text = FormatBytes(info.ConfigurationBytes);
                userBytes.Text = FormatBytes(info.UserDataBytes);
                saveState.Text = info.SaveError is not null ? "配置保存异常 · " + info.SaveError
                    : (info.RecoveredFromBackup ? "本次启动已从备份恢复配置。\n" : "")
                        + (info.LastSavedUtc is DateTime saved ? "最近保存 " + saved.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未写入配置文件")
                        + (info.HasBackup ? " · 已保留上一份配置" : "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { if (!_closed && generation == _storagePageGeneration) updateBytes.Text = "统计未完成 · " + ex.Message; }
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = Math.Max(0, bytes); int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private void OpenStorageFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowNotification("无法打开目录 · " + ex.Message, InfoBarSeverity.Error); }
    }
}
