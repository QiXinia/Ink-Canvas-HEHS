using InkCanvas.App.Services;
using InkCanvas.Features.Classroom;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>
/// 分组积分窗口：与点名/抽奖共用同一份名单，随机分组后支持加减分、手动调整与本轮重置。
/// 所有数据只保存在本地设置中，不联网。
/// </summary>
public sealed class GroupScoreWindow : Window
{
    private readonly GroupScoreService _service;
    private readonly Action<string> _pasteToBoard;
    private readonly StackPanel _groupsPanel = new() { Spacing = 8 };
    private readonly TextBlock _roundText = new() { FontSize = 12, Opacity = 0.75 };
    private readonly ComboBox _modeBox = new() { MinWidth = 118, SelectedIndex = 1 };
    private readonly NumberBox _valueBox = new()
    {
        Minimum = 1,
        Maximum = 99,
        Value = 4,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = 92,
    };

    public GroupScoreWindow(
        GroupScoreService service,
        IReadOnlyList<string> rosterNames,
        GroupSplitMode mode,
        int value,
        int theme,
        Action<string> pasteToBoard)
    {
        _service = service ?? new GroupScoreService();
        _pasteToBoard = pasteToBoard;
        Title = "分组积分";
        _modeBox.Items.Add("按组数");
        _modeBox.Items.Add("按人数");
        _modeBox.SelectedIndex = mode == GroupSplitMode.BySize ? 1 : 0;
        _valueBox.Value = Math.Clamp(value <= 0 ? 4 : value, 1, 99);

        if (_service.Groups.Count == 0)
            _service.ResetGroups(rosterNames, mode, value, Environment.TickCount);

        var split = new Button { Content = "随机分组", MinHeight = 34, MinWidth = 88 };
        split.Click += (_, _) => SplitGroups(rosterNames);

        var splitRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        splitRow.Children.Add(_modeBox);
        splitRow.Children.Add(_valueBox);
        splitRow.Children.Add(split);

        var reset = new Button { Content = "本轮重置", MinHeight = 34, MinWidth = 88 };
        reset.Click += (_, _) =>
        {
            _service.ResetRound();
            Refresh();
            RaiseChanged();
        };

        var paste = new Button { Content = "贴到白板", MinHeight = 34, MinWidth = 96 };
        paste.Click += (_, _) => _pasteToBoard?.Invoke(_service.BuildResultText());

        var close = new Button { Content = "关闭", MinHeight = 34, MinWidth = 72 };
        close.Click += (_, _) => Close();

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actionRow.Children.Add(reset);
        actionRow.Children.Add(paste);
        actionRow.Children.Add(close);

        var scroll = new ScrollViewer
        {
            Content = _groupsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 300,
        };

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "小组积分",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(_roundText);
        panel.Children.Add(splitRow);
        panel.Children.Add(scroll);
        panel.Children.Add(actionRow);

        var root = new Border
        {
            Background = (Brush)Application.Current.Resources["IcSurfaceBrush"],
            Child = panel,
        };

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { Close(); e.Handled = true; };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.CenterOnPrimary(this, 560, 540);
        WindowChrome.ApplyTheme(this, theme);
        Refresh();
    }

    public GroupScoreService Service => _service;

    public GroupSplitMode Mode => _modeBox.SelectedIndex == 1 ? GroupSplitMode.BySize : GroupSplitMode.ByGroupCount;

    public int SplitValue => (int)Math.Clamp(_valueBox.Value, 1, 99);

    /// <summary>每次分组或积分变化触发，由外层持久化到本地设置。</summary>
    public event Action<GroupScoreState> StateChanged;

    private void SplitGroups(IReadOnlyList<string> rosterNames)
    {
        _service.ResetGroups(rosterNames, Mode, SplitValue, Environment.TickCount);
        Refresh();
        RaiseChanged();
    }

    private void Refresh()
    {
        _roundText.Text = "第 " + _service.State.Round + " 轮 · 共 "
            + _service.Groups.Count + " 组 · 总分 " + _service.State.TotalScore;
        _groupsPanel.Children.Clear();

        if (_service.Groups.Count == 0)
        {
            _groupsPanel.Children.Add(new TextBlock
            {
                Text = "还没有名单。先打开「点名」窗口导入名单，再回到这里随机分组。",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            });
            return;
        }

        foreach (ScoreGroup group in _service.Groups)
            _groupsPanel.Children.Add(BuildGroupCard(group));
    }

    private FrameworkElement BuildGroupCard(ScoreGroup group)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = group.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = group.Score + " 分",
            FontSize = 14,
            Foreground = (Brush)Application.Current.Resources["IcAccentBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        var plus1 = ScoreButton("+1", 1);
        var plus5 = ScoreButton("+5", 5);
        var minus1 = ScoreButton("-1", -1);
        header.Children.Add(plus1);
        header.Children.Add(plus5);
        header.Children.Add(minus1);

        var members = new TextBlock
        {
            Text = group.Members.Count == 0 ? "（未分组）" : string.Join("、", group.Members),
            FontSize = 12,
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(header);
        stack.Children.Add(members);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["IcSurfaceAltBrush"],
            BorderBrush = (Brush)Application.Current.Resources["IcStrokeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = stack,
        };

        Button ScoreButton(string text, int delta)
        {
            var button = new Button
            {
                Content = text,
                MinHeight = 28,
                MinWidth = 44,
                Padding = new Thickness(6, 0, 6, 0),
            };
            button.Click += (_, _) =>
            {
                _service.AddScore(group.Index, delta);
                Refresh();
                RaiseChanged();
            };
            return button;
        }
    }

    private void RaiseChanged() => StateChanged?.Invoke(_service.State);
}