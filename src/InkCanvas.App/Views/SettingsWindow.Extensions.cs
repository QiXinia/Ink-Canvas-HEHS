using InkCanvas.Extensions;
using InkCanvas.Extensibility;
using InkCanvas.Core.Update;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private void RefreshExtensionsPage() => DispatcherQueue.TryEnqueue(() =>
    {
        _searchIndexed = false;
        if (!_closed && _workspace?.SelectedPage == "模块与插件") QueueRebuild();
        else if (!_closed) RefreshControls();
    });

    private void BuildExtensionsCategory()
    {
        BuildRepositoryExtensions();
        AddSection("原生功能插件");
        _content.Children.Add(InfoRow("安装、更新和开关设置在重启后生效。"));
        if (_services.Modules.Status.Count == 0) _content.Children.Add(InfoRow("模块状态在主工具栏启动后显示。"));
        var commands = (_shell?.ExtensionCommands ?? _services.Plugins.Commands).ToArray();
        var displayed = new HashSet<(RepositoryExtensionKind Kind, string Id)>();
        var nativeModules = ExtensionGrid();
        foreach (var module in _services.Modules.Status.OrderBy(module => module.Name, StringComparer.CurrentCulture))
        {
            nativeModules.Children.Add(ExtensionCard(module.Id, module.Name, RepositoryExtensionKind.Module, module, commands));
            displayed.Add((RepositoryExtensionKind.Module, module.Id));
        }
        AddSection("外部插件");
        _content.Children.Add(ButtonRow("打开用户插件目录", "配置目录中的插件可与主体程序独立更新", () =>
        {
            try
            {
                string path = Path.Combine(_services.Store.Directory, "Plugins"); Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowNotification(ex.Message); }
        }));
        if (_services.Plugins.Status.Count == 0) _content.Children.Add(InfoRow("尚未安装外部插件。"));
        var externalPlugins = ExtensionGrid();
        foreach (var plugin in _services.Plugins.Status.OrderBy(plugin => plugin.Name, StringComparer.CurrentCulture))
        {
            externalPlugins.Children.Add(ExtensionCard(plugin.Id, plugin.Name, RepositoryExtensionKind.Plugin, plugin, commands));
            displayed.Add((RepositoryExtensionKind.Plugin, plugin.Id));
        }
        var available = _services.ExtensionUpdates.Catalog.Where(release => !displayed.Contains((release.Kind, release.Id))).ToArray();
        if (available.Length == 0) return;
        AddSection("可安装插件");
        var availablePlugins = ExtensionGrid();
        foreach (var release in available)
            availablePlugins.Children.Add(ExtensionCard(release.Id, release.Name, release.Kind, null, commands));
    }

    private Grid ExtensionGrid()
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(0) });
        void ArrangeCards()
        {
            int columns = grid.ActualWidth >= 800 && grid.Children.Count > 1 ? 2 : 1;
            grid.ColumnDefinitions[1].Width = columns == 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            int rows = (grid.Children.Count + columns - 1) / columns;
            if (grid.RowDefinitions.Count == rows) return;
            grid.RowDefinitions.Clear();
            for (int row = 0; row < rows; row++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int index = 0; index < grid.Children.Count; index++)
            {
                var card = (FrameworkElement)grid.Children[index];
                Grid.SetColumn(card, index % columns);
                Grid.SetRow(card, index / columns);
            }
        }
        grid.Loading += (_, _) => ArrangeCards();
        grid.SizeChanged += (_, _) => ArrangeCards();
        _content.Children.Add(grid);
        return grid;
    }

    private void SetModuleEnabled(string id, bool enabled)
    {
        bool previous = !_services.ExtensionPreferences.DisabledModules.Contains(id);
        if (enabled) _services.ExtensionPreferences.DisabledModules.Remove(id);
        else _services.ExtensionPreferences.DisabledModules.Add(id);
        try { _services.ExtensionStore.Save(_services.ExtensionPreferences); ShowNotification("模块开关将在下次启动时生效"); }
        catch (Exception ex)
        {
            if (previous) _services.ExtensionPreferences.DisabledModules.Remove(id);
            else _services.ExtensionPreferences.DisabledModules.Add(id);
            ShowNotification("保存失败 " + ex.Message); QueueRebuild();
        }
    }

    private Border ExtensionCard(string id, string name, RepositoryExtensionKind kind, ExtensionStatus status,
        IReadOnlyList<ExtensionCommand> commands)
    {
        var updates = _services.ExtensionUpdates;
        var release = updates.Catalog.FirstOrDefault(item => item.Id == id && item.Kind == kind);
        var actions = new WrapPanel();
        var update = ExtensionAction("检查更新", name + "下载与更新");
        actions.Children.Add(update);
        update.Click += async (_, _) =>
        {
            if (release is not null) await updates.InstallAsync(release);
            else await updates.CheckAsync();
        };
        if (release is not null) Accessible(update, release.Name + "下载与更新");

        FrameworkElement toggle = new Border { Visibility = Visibility.Collapsed };
        if (kind is RepositoryExtensionKind.Module or RepositoryExtensionKind.Plugin)
        {
            bool installed = status is not null && status.State != ExtensionState.NotInstalled;
            bool Enabled() => installed && (kind == RepositoryExtensionKind.Module
                ? !_services.ExtensionPreferences.DisabledModules.Contains(id)
                : _services.ExtensionPreferences.EnabledPlugins.Contains(id));
            var control = new ToggleSwitch { IsOn = Enabled(), OnContent = "开", OffContent = "关", MinWidth = 0, MinHeight = 40, IsEnabled = installed };
            Accessible(control, name + (kind == RepositoryExtensionKind.Module ? " · 原生插件" : " · 插件"),
                installed ? null : "安装并重启后可启用此插件。");
            control.Toggled += (_, _) =>
            {
                if (_building || _synchronizingControls || !control.IsEnabled || control.IsOn == Enabled()) return;
                if (kind == RepositoryExtensionKind.Module) SetModuleEnabled(id, control.IsOn);
                else _ = SetPluginEnabledAsync(id, control.IsOn);
                Commit();
            };
            _refreshControls.Add(() => control.IsOn = Enabled());
            toggle = control;
        }
        var row = Row(name, toggle);
        var panel = new StackPanel { Spacing = 12, Children = { row, actions } };
        var card = Searchable(Card(panel), name, "插件 开关 下载 安装 更新 设置 " + id + " " + release?.Name);
        AutomationProperties.SetAutomationId(card, "extension-card-" + id);
        foreach (var command in commands.Where(command => command.PluginId == id))
        {
            var button = ExtensionAction(kind == RepositoryExtensionKind.Module && command.CommandId == "open" ? "打开窗口" : command.Title,
                command.Title, command.Description);
            button.IsEnabled = _shell is not null;
            if (_shell is null) ToolTipService.SetToolTip(button, "画布命令宿主未启动，请从主程序打开设置。");
            button.Click += async (_, _) => { if (_shell is not null) await _shell.ExecuteExtensionCommandAsync(command); };
            actions.Children.Add(button);
            Searchable(card, command.Title, command.Description);
        }
        var pages = _moduleSettingsPages.Where(page => page.Id == id || page.Id.StartsWith(id + ".", StringComparison.Ordinal)).ToArray();
        foreach (var page in pages)
        {
            var button = ExtensionAction(pages.Length == 1 ? "设置" : page.Name, name + "设置 · " + page.Name);
            button.Click += (_, _) => _workspace.SelectPage(page.Name);
            actions.Children.Add(button);
            Searchable(card, name + "设置", page.Name + " " + page.SearchTerms);
        }
        void Refresh()
        {
            var pending = updates.Pending.FirstOrDefault(item => item.Id == id);
            var details = new List<string>();
            string installed = status is null || status.State == ExtensionState.NotInstalled ? "未安装" : "已安装 " + status.Version + " · " + StateText(status.State);
            details.Add(installed + (release is null ? "" : " · 仓库 " + release.Version));
            string description = release?.Description ?? ExtensionFeed.Known.GetValueOrDefault(id).Description;
            if (!string.IsNullOrWhiteSpace(description)) details.Add(description);
            if (pending is not null) details.Add(pending.Version + " 待重启生效");
            if (!string.IsNullOrWhiteSpace(status?.Error)) details.Add(status.Error);
            string reason = release is null ? null : updates.UnavailableReason(release);
            if (reason is not null) details.Add(reason);
            row.Description = string.Join("\n", details);
            if (release is null)
            {
                update.Content = updates.HasChecked ? "无仓库版本" : "检查更新";
                update.IsEnabled = !updates.IsBusy && !updates.HasChecked;
                ToolTipService.SetToolTip(update, updates.HasChecked ? "当前仓库未提供此插件的更新包。" : "获取仓库中可用的插件版本。");
            }
            else
            {
                update.Content = updates.ActiveId == id ? "正在准备" : updates.FailedInstall(id) ? "重新下载" : pending is not null ? "待重启"
                    : updates.InstalledVersion(release) is null ? "安装" : updates.HasUpdate(release) ? "更新" : "已是最新";
                update.IsEnabled = updates.CanInstall(release);
                ToolTipService.SetToolTip(update, reason ?? release.Asset.Name);
            }
        }
        Refresh();
        if (!_indexingSearch) _repositoryRefreshers.Add(Refresh);
        return card;
    }

    private static Button ExtensionAction(string label, string accessibleName, string description = null)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            MinHeight = 40, MinWidth = 0, MaxWidth = 320, Padding = new Thickness(12, 6, 12, 6),
        };
        Accessible(button, accessibleName, description);
        ToolTipService.SetToolTip(button, description ?? accessibleName);
        return button;
    }

    private async Task SetPluginEnabledAsync(string id, bool enabled)
    {
        bool previous = _services.ExtensionPreferences.EnabledPlugins.Contains(id);
        if (enabled) _services.ExtensionPreferences.EnabledPlugins.Add(id); else _services.ExtensionPreferences.EnabledPlugins.Remove(id);
        try
        {
            _services.ExtensionStore.Save(_services.ExtensionPreferences);
            await _services.Plugins.SetEnabledAsync(id, enabled);
        }
        catch (Exception ex)
        {
            if (previous) _services.ExtensionPreferences.EnabledPlugins.Add(id); else _services.ExtensionPreferences.EnabledPlugins.Remove(id);
            try { _services.ExtensionStore.Save(_services.ExtensionPreferences); } catch { }
            if (!_closed) ShowNotification("插件状态更新失败 " + ex.Message);
        }
        if (!_closed) QueueRebuild();
    }
    private static string StateText(ExtensionState state) => state switch
    {
        ExtensionState.Running => "运行中", ExtensionState.Disabled => "已停用", ExtensionState.Starting => "正在启动",
        ExtensionState.Failed => "加载或运行失败", ExtensionState.Blocked => "依赖不可用", ExtensionState.Stopped => "已停止", _ => "已安装",
    };
}
