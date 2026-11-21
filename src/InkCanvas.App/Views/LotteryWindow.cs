using System.ComponentModel;
using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Features.Tools;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace InkCanvas.App.Views;

/// <summary>Responsive result cards and large classroom touch controls.</summary>
public sealed class LotteryWindow : Window
{
    internal static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkCanvas");
    private readonly LotteryService _lottery;
    private readonly bool _autoClose;
    private readonly string _dataDirectory;
    private readonly Action<LotteryWindow> _openRosterSettings;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _rollCancellation;
    private readonly MinSizeHook _minSize;
    private readonly Grid _root = new();
    private Grid _page, _workspace, _controls, _quickRow, _secondaryActions;
    private Border _results;
    private GridView _resultView;
    private TextBlock _placeholder, _resultTitle, _countLabel, _summary, _history, _notice;
    private ProgressRing _progress;
    private TextBlock _countTitle;
    private TouchIconButton _minusButton, _plusButton, _rollButton, _importButton, _resetRound, _copy, _optionsButton;
    private ComboBox _samplingMode;
    private NumberBox _poolInput, _cooldownPeriod;
    private readonly List<(TouchIconButton Button, int Count)> _quickCounts = new();
    private List<ResultItem> _items = new();
    private int _totalCount = 1;
    private bool _rolling, _closed, _syncing;
    private NamesInputWindow _namesInput;
    private IReadOnlyList<string> _lastResult = Array.Empty<string>();
    internal IReadOnlyList<string> LastResult => _lastResult;
    internal event Action OptionsChanged;
    private int DrawCount => Math.Min(_totalCount, _lottery.RemainingCount);

    public LotteryWindow(LotteryService lottery, bool autoClose = false, int theme = 0, string dataDirectory = null)
        : this(lottery, autoClose, theme, dataDirectory, null) { }

    public LotteryWindow(LotteryService lottery, bool autoClose, int theme, string dataDirectory,
        Action<LotteryWindow> openRosterSettings)
    {
        _lottery = lottery; _autoClose = autoClose;
        _dataDirectory = dataDirectory ?? DataDir; _openRosterSettings = openRosterSettings;
        Title = "抽奖 / 课堂点名 - Ink Canvas";
        BuildUi();
        Content = _root;
        WindowChrome.MakeStandard(this, 1100, 720);
        WindowFrame.Attach(this, _root);
        WindowChrome.ApplyTheme(this, theme);
        _minSize = new MinSizeHook(this, 600, 540);
        _lottery.NamesChanged += OnNamesChanged;
        _lottery.ConfigurationChanged += OnConfigurationChanged;
        _root.ActualThemeChanged += (_, _) => ApplyPalette();
        _root.Loaded += async (_, _) =>
        {
            ApplyPalette(); ReloadNames();
            if (_autoClose)
            {
                try { await Task.Delay(100, _lifetime.Token); await RollAsync(); }
                catch (OperationCanceledException) { }
            }
        };
        Activated += (_, _) => { if (!_closed) ReloadNames(); };
        Closed += (_, _) =>
        {
            _closed = true; _lottery.NamesChanged -= OnNamesChanged;
            _lottery.ConfigurationChanged -= OnConfigurationChanged;
            _lifetime.Cancel(); _lifetime.Dispose();
            _minSize.Dispose(); _namesInput?.CloseWithoutSaving();
        };
        ApplyPalette(); RefreshSummary();
    }

    private void BuildUi()
    {
        var page = _page = new Grid { Margin = new Thickness(24, 16, 24, 20), RowSpacing = 20 };
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _summary = new TextBlock { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _resetRound = ClassroomControls.Button("重置本轮", () =>
        {
            if (_rolling) return;
            _lottery.Reset(); ResetResults();
            _notice.Text = "已开始新一轮，全部人员重新参与抽选。"; RefreshSummary();
        }, glyph: "\uE72C");
        header.Children.Add(_summary); Grid.SetColumn(_resetRound, 1); header.Children.Add(_resetRound);
        page.Children.Add(header);

        _resultTitle = new TextBlock { Text = "抽选结果", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center };
        _progress = new ProgressRing { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        var resultHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            Margin = new Thickness(20, 18, 20, 10), Children = { _progress, _resultTitle } };
        _placeholder = new TextBlock { Text = "准备抽选", FontSize = 44, Opacity = .6,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _resultView = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false,
            Padding = new Thickness(12, 0, 12, 12), Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_resultView, "抽选结果");
        ScrollViewer.SetHorizontalScrollBarVisibility(_resultView, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(_resultView, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_resultView, ScrollBarVisibility.Auto);
        _resultView.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ItemsWrapGrid Orientation="Horizontal" />
            </ItemsPanelTemplate>
            """);
        _resultView.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border CornerRadius="12" BorderThickness="1" Padding="12"
                    Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
                    <TextBlock Text="{Binding Name}" FontSize="{Binding FontSize}" MaxWidth="{Binding TextWidth}" FontWeight="SemiBold"
                        TextAlignment="Center" TextWrapping="Wrap" MaxLines="2" TextTrimming="CharacterEllipsis"
                        VerticalAlignment="Center" HorizontalAlignment="Stretch"
                        AutomationProperties.Name="{Binding Name}" ToolTipService.ToolTip="{Binding Name}" />
                </Border>
            </DataTemplate>
            """);
        var itemStyle = new Style { TargetType = typeof(GridViewItem) };
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4)));
        itemStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        _resultView.ItemContainerStyle = itemStyle;
        _resultView.SizeChanged += (_, _) => UpdateResultLayout();
        _resultView.Loaded += (_, _) => UpdateResultLayout();
        var resultBody = new Grid();
        resultBody.Children.Add(_placeholder); resultBody.Children.Add(_resultView);
        var resultPanel = new Grid();
        resultPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        resultPanel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        resultPanel.Children.Add(resultHeader); Grid.SetRow(resultBody, 1); resultPanel.Children.Add(resultBody);
        _results = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = resultPanel };

        _controls = new Grid { RowSpacing = 16, ColumnSpacing = 16, VerticalAlignment = VerticalAlignment.Center };
        _controls.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _controls.ColumnDefinitions.Add(new() { Width = new GridLength(0) });
        for (int row = 0; row < 4; row++) _controls.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _countTitle = new TextBlock { Text = "本次抽选人数", FontSize = 18 };
        var countSection = new StackPanel { Spacing = 8, Children = { _countTitle } };
        var count = new Grid { ColumnSpacing = 12 };
        count.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
        count.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        count.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
        _minusButton = ClassroomControls.Button("−", () => ChangeCount(DrawCount - 1));
        _plusButton = ClassroomControls.Button("＋", () => ChangeCount(DrawCount + 1));
        AutomationProperties.SetName(_minusButton, "减少人数"); AutomationProperties.SetName(_plusButton, "增加人数");
        _minusButton.Height = _plusButton.Height = 70; _minusButton.FontSize = _plusButton.FontSize = 32;
        _countLabel = new TextBlock { FontSize = 42, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        count.Children.Add(_minusButton); Grid.SetColumn(_countLabel, 1); count.Children.Add(_countLabel);
        Grid.SetColumn(_plusButton, 2); count.Children.Add(_plusButton); countSection.Children.Add(count); _controls.Children.Add(countSection);
        _quickRow = new Grid { ColumnSpacing = 6 };
        foreach (int value in new[] { 1, 2, 3, 5, 10 })
        {
            int column = _quickRow.ColumnDefinitions.Count;
            _quickRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var button = ClassroomControls.Button($"{value}", () => ChangeCount(value));
            AutomationProperties.SetName(button, $"抽选 {value} 人");
            button.FontSize = 18; button.MinWidth = 0; button.Padding = new Thickness(2);
            Grid.SetColumn(button, column); _quickRow.Children.Add(button); _quickCounts.Add((button, value));
        }
        Grid.SetRow(_quickRow, 1); _controls.Children.Add(_quickRow);
        _rollButton = ClassroomControls.Button("开始抽选", () => _ = RollAsync(), primary: true, glyph: Glyphs.Rand);
        _rollButton.Height = 76; _rollButton.FontSize = 24;
        AutomationProperties.SetName(_rollButton, "抽奖"); Grid.SetRow(_rollButton, 2); _controls.Children.Add(_rollButton);
        _samplingMode = new ComboBox { Header = "点名方式", MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (string label in new[] { "软冷却点名（默认）", "均匀随机", "本轮不重复" }) _samplingMode.Items.Add(label);
        AutomationProperties.SetName(_samplingMode, "点名方式");
        _samplingMode.SelectedIndex = (int)_lottery.SamplingMode;
        _samplingMode.SelectionChanged += (_, _) =>
        {
            if (_syncing || _rolling || _samplingMode.SelectedIndex < 0) return;
            _lottery.SamplingMode = (LotterySamplingMode)_samplingMode.SelectedIndex;
            OptionsChanged?.Invoke();
        };
        _cooldownPeriod = new NumberBox { Header = "冷却半衰期（天）", Value = _lottery.CooldownHalfLifeDays,
            Minimum = .25, Maximum = 3650, SmallChange = 1, MinHeight = 56,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetName(_cooldownPeriod, "冷却半衰期（天）");
        _cooldownPeriod.ValueChanged += (_, args) =>
        {
            if (_syncing || _rolling || !double.IsFinite(args.NewValue)) return;
            _lottery.CooldownHalfLifeDays = args.NewValue;
            OptionsChanged?.Invoke();
        };
        _poolInput = new NumberBox { Header = "数字点名总人数", Value = _lottery.PeopleCount, Minimum = 1, Maximum = 9999,
            SmallChange = 1, LargeChange = 10, MinHeight = 56, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _poolInput.ValueChanged += (_, args) =>
        {
            if (_syncing || _rolling || !double.IsFinite(args.NewValue)) return;
            _lottery.PeopleCount = Math.Clamp((int)Math.Round(args.NewValue), 1, 9999);
            OptionsChanged?.Invoke(); RefreshSummary();
        };
        _optionsButton = ClassroomControls.Button("选项", glyph: "\uE713");
        _optionsButton.Flyout = new Flyout { Content = new StackPanel { Spacing = 12, Width = 300, Children =
        {
            _samplingMode, _cooldownPeriod,
            new TextBlock { Text = "近期抽中越多，概率越低。随时间逐渐恢复，仍可再次抽中。", TextWrapping = TextWrapping.Wrap, FontSize = 13 },
            _poolInput,
        } } };
        _importButton = ClassroomControls.Button("名单管理", OpenNamesInput, glyph: "\uE77B");
        AutomationProperties.SetName(_importButton, "导入名单");
        _secondaryActions = new Grid { ColumnSpacing = 8 };
        _secondaryActions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _secondaryActions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _secondaryActions.Children.Add(_importButton); Grid.SetColumn(_optionsButton, 1); _secondaryActions.Children.Add(_optionsButton);
        Grid.SetRow(_secondaryActions, 3); _controls.Children.Add(_secondaryActions);
        _workspace = new Grid { ColumnSpacing = 24, RowSpacing = 16 };
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(320) });
        _workspace.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _workspace.Children.Add(_results); _workspace.Children.Add(_controls);
        _workspace.SizeChanged += (_, _) => UpdateLayout();
        Grid.SetRow(_workspace, 1); page.Children.Add(_workspace);

        var footer = new Grid { ColumnSpacing = 16 };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _notice = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        _history = new TextBlock { FontSize = 13, MaxLines = 2, Opacity = .65, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var information = new StackPanel { Spacing = 6, Children = { _notice, _history } };
        footer.Children.Add(information);
        _copy = ClassroomControls.Button("复制结果", CopyResult, glyph: "\uE8C8");
        Grid.SetColumn(_copy, 1); footer.Children.Add(_copy);
        Grid.SetRow(footer, 2); page.Children.Add(footer);
        _root.Children.Add(page);
    }

    private void UpdateLayout()
    {
        bool stacked = _workspace.ActualWidth < 780;
        _page.Margin = stacked ? new Thickness(16) : new Thickness(24, 16, 24, 20);
        _page.RowSpacing = stacked ? 12 : 20;
        _workspace.RowSpacing = stacked ? 12 : 16;
        _controls.RowSpacing = stacked ? 8 : 16;
        _workspace.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 320);
        Grid.SetRow(_controls, stacked ? 1 : 0); Grid.SetColumn(_controls, stacked ? 0 : 1);
        _controls.MaxWidth = stacked ? double.PositiveInfinity : 320;
        _controls.ColumnDefinitions[1].Width = new GridLength(stacked ? 1 : 0, GridUnitType.Star);
        _countTitle.Visibility = _quickRow.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(_rollButton, stacked ? 0 : 2); Grid.SetColumn(_rollButton, stacked ? 1 : 0);
        Grid.SetRow(_secondaryActions, stacked ? 1 : 3); Grid.SetColumnSpan(_secondaryActions, stacked ? 2 : 1);
        UpdateResultLayout();
    }

    private void UpdateResultLayout()
    {
        if (_resultView.ItemsPanelRoot is not ItemsWrapGrid panel || _resultView.ActualWidth <= 0) return;
        double width = Math.Max(1, _resultView.ActualWidth - 40);
        int maximumColumns = _items.Count <= 6 ? 2 : 4;
        int columns = Math.Max(1, Math.Min(_items.Count, Math.Min(maximumColumns, (int)(width / 160))));
        panel.MaximumRowsOrColumns = columns;
        panel.ItemWidth = width / columns;
        panel.ItemHeight = _items.Count == 1 ? Math.Max(150, _resultView.ActualHeight - 16) : _items.Count <= 6 ? 130 : 94;
        double fontSize = _items.Count == 1 ? 72 : _items.Count <= 6 ? 36 : 28;
        foreach (var item in _items)
        {
            double textWidth = item.Name.Sum(character => character > 255 ? 1 : .6);
            item.TextWidth = Math.Max(40, panel.ItemWidth - 40);
            item.FontSize = Math.Max(16, Math.Min(fontSize, (panel.ItemWidth - 40) / Math.Max(1, textWidth)));
        }
    }

    private void ApplyPalette()
    {
        int theme = _root.ActualTheme == ElementTheme.Dark ? 2 : 1;
        _root.Background = PopupBrushes.WindowSurface(theme);
        _results.Background = new SolidColorBrush(theme == 2 ? Windows.UI.Color.FromArgb(255, 36, 36, 36)
            : Windows.UI.Color.FromArgb(255, 247, 249, 252));
        _results.BorderBrush = PopupBrushes.ToolPanelBorder(theme);
        _resultView.Foreground = PopupBrushes.Foreground(theme);
        foreach (var text in new[] { _placeholder, _resultTitle, _countLabel, _summary, _history, _notice })
            text.Foreground = PopupBrushes.Foreground(theme);
    }

    internal void ChangeCount(int value)
    {
        if (_rolling) return;
        _totalCount = Math.Clamp(value, 1, _lottery.PoolSize); RefreshSummary();
    }

    private void RefreshSummary()
    {
        _syncing = true;
        try
        {
            int remaining = _lottery.RemainingCount;
            _totalCount = Math.Clamp(_totalCount, 1, _lottery.PoolSize);
            _summary.Text = _lottery.NoRepeat ? $"共 {_lottery.PoolSize} 人 · 本轮剩余 {remaining} 人"
                : $"共 {_lottery.PoolSize} 人 · " + (_lottery.SamplingMode == LotterySamplingMode.SoftCooldown ? "软冷却点名" : "均匀随机");
            _countLabel.Text = DrawCount.ToString();
            _poolInput.Value = _lottery.PeopleCount; _poolInput.IsEnabled = !_rolling && _lottery.Names.Count == 0;
            _samplingMode.SelectedIndex = (int)_lottery.SamplingMode;
            _cooldownPeriod.Value = _lottery.CooldownHalfLifeDays;
            _cooldownPeriod.IsEnabled = !_rolling && _lottery.SamplingMode == LotterySamplingMode.SoftCooldown;
            ToolTipService.SetToolTip(_cooldownPeriod, _lottery.SamplingMode == LotterySamplingMode.SoftCooldown
                ? "每经过设定天数，已有惩罚记忆减半。" : "软冷却点名模式下可调整。" );
            _rollButton.IsEnabled = !_rolling && remaining > 0;
            ClassroomControls.SetButtonContent(_rollButton, _rolling ? "正在抽选" : remaining == 0 ? "本轮已抽完" : "开始抽选", Glyphs.Rand);
            _minusButton.IsEnabled = !_rolling && DrawCount > 1;
            _plusButton.IsEnabled = !_rolling && DrawCount < remaining;
            _importButton.IsEnabled = _resetRound.IsEnabled = _samplingMode.IsEnabled = _optionsButton.IsEnabled = !_rolling;
            _resetRound.Visibility = _lottery.NoRepeat ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (button, count) in _quickCounts)
            {
                button.IsEnabled = !_rolling && count <= remaining;
                button.IsSelected = count == DrawCount;
                button.SelectionBackground = PopupBrushes.BadgeBackground(_root.ActualTheme == ElementTheme.Dark ? 2 : 1);
                button.Background = count == DrawCount ? button.SelectionBackground
                    : PopupBrushes.Surface(_root.ActualTheme == ElementTheme.Dark ? 2 : 1);
                button.Foreground = count == DrawCount ? PopupBrushes.BadgeForeground(_root.ActualTheme == ElementTheme.Dark ? 2 : 1)
                    : PopupBrushes.Foreground(_root.ActualTheme == ElementTheme.Dark ? 2 : 1);
                button.RefreshVisual();
            }
            _copy.IsEnabled = !_rolling && _lastResult.Count > 0;
            _progress.IsActive = _rolling; _progress.Visibility = _rolling ? Visibility.Visible : Visibility.Collapsed;
            _history.Text = string.Join("\n", _lottery.History.TakeLast(2).Reverse().Select(draw =>
                $"{draw.Time.ToLocalTime():HH:mm}　{string.Join("、", draw.Names.Take(6))}" + (draw.Names.Count > 6 ? $" 等 {draw.Names.Count} 人" : "")));
        }
        finally { _syncing = false; }
    }

    private void ResetResults()
    {
        _lastResult = Array.Empty<string>(); _items.Clear(); _resultView.ItemsSource = null;
        _resultView.Visibility = Visibility.Collapsed; _placeholder.Visibility = Visibility.Visible;
        _resultTitle.Text = "抽选结果";
        _placeholder.Text = "准备抽选";
    }

    private void OnNamesChanged()
    {
        _rollCancellation?.Cancel(); ResetResults();
        _notice.Text = "名单已更新，新一轮抽选已准备好。"; RefreshSummary();
    }

    private void OnConfigurationChanged()
    {
        if (_rolling) { _rollCancellation?.Cancel(); ResetResults(); }
        _notice.Text = "";
        RefreshSummary();
    }

    private void ReloadNames()
    {
        try
        {
            if (File.Exists(Path.Combine(_dataDirectory, "Names.txt"))) _lottery.LoadNames(ClassroomRoster.Load(_dataDirectory));
        }
        catch { _notice.Text = "名单读取失败，已保留当前名单。"; }
        RefreshSummary();
    }

    private void OpenNamesInput()
    {
        if (_rolling || _closed) return;
        if (_openRosterSettings is not null) { _openRosterSettings(this); return; }
        if (_namesInput is not null) { _namesInput.Activate(); return; }
        var dialog = new NamesInputWindow(_root.ActualTheme == ElementTheme.Dark ? 2 : 1, _dataDirectory);
        _namesInput = dialog;
        dialog.Closed += (_, _) => { _namesInput = null; if (!_closed) ReloadNames(); };
        dialog.Activate(); WindowChrome.EnsureAbove(dialog);
    }

    private void ShowNames(IReadOnlyList<string> names)
    {
        if (_items.Count != names.Count)
        {
            _items = names.Select(name => new ResultItem { Name = name }).ToList();
            _resultView.ItemsSource = _items;
        }
        else for (int index = 0; index < names.Count; index++) _items[index].Name = names[index];
        _placeholder.Visibility = Visibility.Collapsed; _resultView.Visibility = Visibility.Visible;
        UpdateResultLayout();
    }

    public event Action<string> Picked;

    internal async Task RollAsync()
    {
        if (_rolling || _closed || DrawCount == 0) return;
        _rolling = true; RefreshSummary();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _rollCancellation = cancellation;
        var token = cancellation.Token;
        int take = DrawCount;
        var used = _lottery.Used.ToHashSet();
        var candidates = Enumerable.Range(0, _lottery.PoolSize).Where(index => !_lottery.NoRepeat || !used.Contains(index))
            .Select(index => _lottery.Names.Count > 0 ? _lottery.Names[index] : (index + 1).ToString()).ToArray();
        var preview = new string[Math.Min(take, 24)];
        _resultTitle.Text = $"正在抽选 {take} 人"; _notice.Text = "";
        try
        {
            int previewFrames = Motion.AnimationsEnabled ? 14 : 0;
            for (int frame = 0; frame < previewFrames; frame++)
            {
                for (int index = 0; index < preview.Length; index++)
                {
                    int next = Random.Shared.Next(index, candidates.Length);
                    (candidates[index], candidates[next]) = (candidates[next], candidates[index]);
                    preview[index] = candidates[index];
                }
                ShowNames(preview);
                await Task.Delay(35 + frame * frame / 2, token);
            }
            token.ThrowIfCancellationRequested();
            _lastResult = _lottery.Draw(take); ShowNames(_lastResult);
            if (_lastResult.Count > 0) Picked?.Invoke(string.Join("、", _lastResult));
            _resultTitle.Text = $"本次抽选 {_lastResult.Count} 人";
            _resultView.UpdateLayout();
            UpdateResultLayout();
            for (int index = 0; index < Math.Min(_lastResult.Count, 12); index++)
                if (_resultView.ContainerFromIndex(index) is GridViewItem card) Motion.PlayEntrance(card, 10, index * 24);
            if (Motion.AnimationsEnabled) await Task.Delay(520, token);
            _notice.Text = _lottery.RemainingCount == 0 ? "本轮已全部抽完，重置本轮后可继续。" : "";
            if (_autoClose) { await Task.Delay(1800, token); if (!_closed) Close(); }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _rollCancellation = null; _rolling = false;
            if (!_closed) RefreshSummary();
        }
    }

    private void CopyResult()
    {
        if (_lastResult.Count == 0) return;
        try
        {
            var package = new DataPackage(); package.SetText(string.Join(Environment.NewLine, _lastResult));
            Clipboard.SetContent(package); _notice.Text = "已复制本次抽选结果。";
        }
        catch { _notice.Text = "暂时无法访问剪贴板，请重试。"; }
    }

    public sealed class ResultItem : INotifyPropertyChanged
    {
        private string _name;
        private double _fontSize = 36;
        private double _textWidth = 160;
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new(nameof(Name)));
            }
        }
        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (_fontSize == value) return;
                _fontSize = value;
                PropertyChanged?.Invoke(this, new(nameof(FontSize)));
            }
        }
        public double TextWidth
        {
            get => _textWidth;
            set
            {
                if (_textWidth == value) return;
                _textWidth = value;
                PropertyChanged?.Invoke(this, new(nameof(TextWidth)));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
