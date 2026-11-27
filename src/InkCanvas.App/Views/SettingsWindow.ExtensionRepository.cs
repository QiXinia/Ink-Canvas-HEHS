using InkCanvas.Core.Update;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private readonly List<Action> _repositoryRefreshers = new();
    private IReadOnlyList<ExtensionRelease> _displayedCatalog;
    private int _displayedPending;
    private TextBlock _repositoryStatus;

    private void RefreshRepositoryUpdates()
    {
        if (_closed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            var updates = _services.ExtensionUpdates;
            if (_repositoryStatus is not null) _repositoryStatus.Text = updates.Status;
            foreach (var refresh in _repositoryRefreshers) refresh();
            RefreshUpdateStatus();
            if (_workspace?.SelectedPage == "模块与插件" && (!ReferenceEquals(_displayedCatalog, updates.Catalog) || _displayedPending != updates.Pending.Count))
                QueueRebuild();
        });
    }

    private void BuildRepositoryExtensions()
    {
        var updates = _services.ExtensionUpdates;
        if (!_indexingSearch)
        { _repositoryRefreshers.Clear(); _displayedCatalog = updates.Catalog; _displayedPending = updates.Pending.Count; }
        AddSection("插件更新");
        _content.Children.Add(InfoRow("从项目 GitHub Release 下载并校验插件。安装和更新在后台准备，重启后生效，插件设置与数据继续保留。"));
        var controls = new WrapPanel();
        var check = new Button { Content = "检查插件更新", MinHeight = 40 };
        var all = new Button { Content = "一键更新全部", MinHeight = 40 };
        var restart = new Button { Content = "重启并应用", MinHeight = 40 };
        Accessible(check, "检查插件更新"); Accessible(all, "一键更新全部插件"); Accessible(restart, "重启并应用插件");
        check.Click += async (_, _) => await updates.CheckAsync();
        all.Click += async (_, _) => await updates.UpdateAllAsync();
        restart.Click += (_, _) => RestartApp();
        controls.Children.Add(check); controls.Children.Add(all); controls.Children.Add(restart);
        _content.Children.Add(Card(controls));
        void RefreshButtons()
        {
            check.IsEnabled = !updates.IsBusy;
            all.IsEnabled = !updates.IsBusy && updates.Catalog.Any(item => updates.HasUpdate(item) && updates.CanInstall(item));
            restart.Visibility = updates.Pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            restart.IsEnabled = !updates.IsBusy;
        }
        RefreshButtons(); if (!_indexingSearch) _repositoryRefreshers.Add(RefreshButtons);
        var status = new TextBlock { Text = updates.Status, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 8) };
        _content.Children.Add(status); if (!_indexingSearch) _repositoryStatus = status;
        foreach (var result in _services.ExtensionInstalls.Results.Where(result => !result.Success))
            _content.Children.Add(InfoRow("上次安装未完成 · " + result.Message));
        if (!_indexingSearch && !updates.HasChecked && !updates.IsBusy) DispatcherQueue.TryEnqueue(async () =>
        { if (!_closed) await updates.CheckAsync(); });
    }
}
