using InkCanvas.Features.Tools;
using InkCanvas.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private readonly string _rosterDirectory;
    private ClassroomRosterEditor _rosterEditor;
    private Border _rosterCard;
    private bool _allowRosterClose, _confirmingRosterClose;
    private bool _revealRoster;

    private void BuildLotterySettings()
    {
        _content.Children.Add(SliderRow("数字点名总人数", () => S.Rand.PeopleCount, 1, 9999,
            value => S.Rand.PeopleCount = (int)value));
        _content.Children.Add(ComboRow("点名方式", new[] { "软冷却点名（默认）", "均匀随机", "本轮不重复" },
            () => (int)S.Rand.SamplingMode, value => S.Rand.SamplingMode = (LotterySamplingMode)value));
        _content.Children.Add(CooldownNumberRow("冷却半衰期（天）", () => S.Rand.CooldownHalfLifeDays,
            value => S.Rand.CooldownHalfLifeDays = value, .25, 3650, 1,
            "默认 10 天。每经过一个半衰期，近期抽选惩罚记忆减半，概率逐渐恢复。"));
        _content.Children.Add(CooldownNumberRow("软冷却惩罚强度", () => S.Rand.CooldownPenaltyStrength,
            value => S.Rand.CooldownPenaltyStrength = value, .1, 10, .1,
            "默认 1，数值越大，近期被抽中的人员概率降低越明显。始终保留再次抽中的机会。"));
        _content.Children.Add(ButtonRow("清除此名单的冷却记录", "清除当前名单的近期抽选记忆，软冷却权重重新从相同起点开始。", async () =>
        {
            var dialog = new ContentDialog { Title = "清除冷却记录", Content = "清除当前名单的近期抽选记忆？",
                PrimaryButtonText = "清除", CloseButtonText = "取消", XamlRoot = ((FrameworkElement)Content).XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    if (File.Exists(Path.Combine(_rosterDirectory, "Names.txt")))
                        _services.Lottery.LoadNames(ClassroomRoster.Load(_rosterDirectory));
                    _services.Lottery.ClearCurrentCooldownMemory();
                    ShowNotification("已清除当前名单的冷却记录。");
                }
                catch (Exception exception) { ShowNotification($"无法读取当前名单。{exception.Message}", InfoBarSeverity.Error); }
            }
        }, "清除记录"));
        if (_indexingSearch)
        {
            _content.Children.Add(Searchable(Card(new TextBlock { Text = "名单管理" }), "名单管理", "姓名 班级 导入 TXT CSV TSV 粘贴 保存"));
            return;
        }
        if (_rosterEditor is null)
        {
            _rosterEditor = new ClassroomRosterEditor(this, _rosterDirectory);
            _rosterEditor.Saved += names =>
            {
                _services.Lottery.LoadNames(names);
                ShowNotification($"已保存 {names.Count} 人的名单，抽选窗口已同步更新。");
            };
        }
        if (_rosterCard is not null) _rosterCard.Child = null;
        var card = _rosterCard = Card(_rosterEditor);
        card.Margin = new Thickness(0, 12, 0, 0);
        _content.Children.Add(new TextBlock { Text = "名单管理", FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
        _content.Children.Add(Searchable(card, "名单管理", "姓名 班级 导入 TXT CSV TSV 粘贴 保存"));
        if (_revealRoster) QueueRosterReveal();
    }

    internal void RevealRosterEditor()
    {
        _revealRoster = true;
        _workspace.RevealContent();
        QueueRosterReveal();
    }

    private void QueueRosterReveal()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || !_revealRoster || _rosterCard is null || _workspace.SelectedPage != "抽奖与点名") return;
            _workspace.ScrollToElement(_rosterCard);
            _revealRoster = false;
        });
    }

    private UIElement CooldownNumberRow(string title, Func<double> get, Action<double> set,
        double minimum, double maximum, double step, string description)
    {
        var number = new NumberBox { Value = get(), Minimum = minimum, Maximum = maximum, SmallChange = step,
            LargeChange = step * 5, MinHeight = 40, MinWidth = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        Accessible(number, title, description);
        number.ValueChanged += (_, args) =>
        {
            if (_building || _synchronizingControls || !number.IsEnabled || !double.IsFinite(args.NewValue) || args.NewValue == get()) return;
            set(args.NewValue); Commit();
        };
        _refreshControls.Add(() =>
        {
            number.Value = get(); number.IsEnabled = S.Rand.SamplingMode == LotterySamplingMode.SoftCooldown;
            ToolTipService.SetToolTip(number, number.IsEnabled ? description : "选择软冷却点名后可调整。" );
        });
        number.IsEnabled = S.Rand.SamplingMode == LotterySamplingMode.SoftCooldown;
        return Searchable(Card(Row(title, number, description, 180)), title, description);
    }

    private async void OnRosterSettingsClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowRosterClose || _rosterEditor is null || !_rosterEditor.HasChanges && !_rosterEditor.IsBusy) return;
        args.Cancel = true;
        if (_confirmingRosterClose || _rosterEditor.IsBusy) return;
        _confirmingRosterClose = true;
        try
        {
            _workspace.SelectPage("抽奖与点名");
            if (await _rosterEditor.ConfirmCloseAsync()) { _allowRosterClose = true; Close(); }
        }
        finally { _confirmingRosterClose = false; }
    }
}
