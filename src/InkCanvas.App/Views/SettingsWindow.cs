using System.Diagnostics;
using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InkCanvas.App.Views;

/// <summary>Standard settings window with on-demand category pages.</summary>
public sealed partial class SettingsWindow : Window
{
    private static readonly FontFamily IconFont = new("ms-appx:///Assets/SegoeFluentIcons.ttf#Segoe Fluent Icons");
    private static readonly string[] SilenceTimes = BuildSilenceTimes();
    private readonly AppServices _services;
    private readonly UpdateCoordinator _updater;
    private readonly IShellActions _shell;
    private StackPanel _content;
    private WindowWorkspace _workspace;
    private static readonly string[] Categories = { "启动选项", "画板选项", "手势选项", "显示器", "墨迹识别选项", "外观选项", "PPT 选项", "高级选项", "自动选项", "实验性设置", "模块与插件", "关于 Ink Canvas Artistry", "抽奖与点名", "存储与缓存" };
    private TextBlock _screenTrackingStatus;
    private TextBlock _updateStatus;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _trackingStatusTimer;
    private readonly List<Action> _refreshControls = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _notificationTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _textCommitTimer;
    private bool _synchronizingControls, _building, _closed, _committing, _rebuildPending;
    private int _theme;
    private InfoBar _notification;
    private readonly MinSizeHook _minSize;

    public SettingsWindow(AppServices services, UpdateCoordinator updater = null, IShellActions shell = null)
        : this(services, updater, shell, null) { }

    public SettingsWindow(AppServices services, UpdateCoordinator updater, IShellActions shell, string rosterDirectory)
    {
        _services = services;
        _rosterDirectory = rosterDirectory ?? LotteryWindow.DataDir;
        services.Plugins.Changed += RefreshExtensionsPage;
        services.Modules.Changed += RefreshExtensionsPage;
        _trackingStatusTimer = DispatcherQueue.CreateTimer();
        _trackingStatusTimer.Interval = TimeSpan.FromSeconds(1);
        _trackingStatusTimer.Tick += (_, _) => { if (_screenTrackingStatus is not null) _screenTrackingStatus.Text = _services.ScreenTrackingStatus; };
        _updater = updater;
        if (_updater is not null) _updater.StatusChanged += RefreshUpdateStatus;
        _shell = shell;
        Title = "Ink Canvas 设置";
        _notificationTimer = DispatcherQueue.CreateTimer();
        _notificationTimer.Interval = TimeSpan.FromSeconds(3);
        _notificationTimer.IsRepeating = false;
        _notificationTimer.Tick += (_, _) => _notification.IsOpen = false;
        _textCommitTimer = DispatcherQueue.CreateTimer();
        _textCommitTimer.Interval = TimeSpan.FromMilliseconds(300);
        _textCommitTimer.IsRepeating = false;
        _textCommitTimer.Tick += (_, _) => Commit();
        _availabilityTimer = DispatcherQueue.CreateTimer();
        _availabilityTimer.Interval = TimeSpan.FromSeconds(2);
        _availabilityTimer.Tick += (_, _) => { if (!_closed && !_building && !_committing) RefreshControls(); };
        _availabilityTimer.Start();
        InitializeSearch();
        WindowChrome.MakeStandard(this, topmost: false);
        RebuildContent();
        _minSize = new MinSizeHook(this, 640, 360);
        AppWindow.Closing += OnRosterSettingsClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _rosterEditor?.Close();
            _services.Plugins.Changed -= RefreshExtensionsPage;
            _services.Modules.Changed -= RefreshExtensionsPage;
            if (_updater is not null) _updater.StatusChanged -= RefreshUpdateStatus;
            _notificationTimer.Stop();
            _trackingStatusTimer.Stop();
            _textCommitTimer.Stop();
            _availabilityTimer.Stop();
            CloseSearch();
            _minSize.Dispose();
            _services.SaveSettings();
        };
    }

    private AppSettings S => _services.Settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _availabilityTimer;
    private int BrushTheme => _theme == 0
        ? (Application.Current.RequestedTheme == ApplicationTheme.Dark ? 2 : 1) : _theme;
    public event Action SettingsChanged;
    internal void SelectCategory(string category) => _workspace.SelectPage(category);

    private void Commit()
    {
        if (_closed || _building || _committing || _synchronizingControls) return;
        _committing = true;
        try
        {
            _textCommitTimer.Stop();
            _services.ApplySettings();
            _services.SaveSettings();
            if (S.Appearance.Theme != _theme) QueueRebuild();
            else RefreshControls();
            SettingsChanged?.Invoke();
        }
        finally { _committing = false; }
    }

    private void QueueRebuild()
    {
        if (_rebuildPending) return;
        _rebuildPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _rebuildPending = false;
            if (!_closed) RebuildContent();
        });
    }

    private void RefreshControls()
    {
        _synchronizingControls = true;
        try { foreach (var refresh in _refreshControls) refresh(); }
        finally { _synchronizingControls = false; }
    }

    private void RebuildContent()
    {
        string selected = _workspace?.SelectedPage ?? "启动选项";
        string searchText = _search?.Text ?? "";
        _theme = S.Appearance.Theme;
        _workspace = new WindowWorkspace { IsTitleBarAutoPaddingEnabled = false };
        _workspace.Resources["NavigationViewHeaderMargin"] = new Thickness(24, 24, 24, 12);
        string[] icons = { "\uE768", "\uE70F", "\uE815", "\uE7F4", "\uE8A5", "\uE790", "\uE8A1", "\uE713", "\uE77C", "\uE713", "\uE8F1", "\uE946", "\uE8D4", "\uE8B7" };
        for (int index = 0; index < Categories.Length; index++)
        {
            string category = Categories[index];
            _workspace.AddPage(category, icons[index], () => BuildPage(category));
        }
        _workspace.SearchBox = CreateSearchBox(searchText);
        _notification = new InfoBar { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Success, Margin = new Thickness(24) };
        var root = new Grid { Background = PopupBrushes.WindowSurface(BrushTheme) };
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(_workspace);
        _updateCard = new UpdateStatusCard(BrushTheme) { Margin = new Thickness(24, 8, 24, 16), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        _updateCard.CancelRequested += () => _updater?.Cancel();
        Grid.SetRow(_updateCard, 1);
        root.Children.Add(_updateCard);
        RefreshUpdateStatus();
        root.KeyDown += SearchKeyDown;
        _notification.VerticalAlignment = VerticalAlignment.Bottom;
        root.Children.Add(_notification);
        WindowFrame.Attach(this, root);
        WindowChrome.ApplyTheme(this, _theme);
        _workspace.SelectPage(selected);
    }

    private UIElement BuildPage(string category)
    {
        _building = true;
        try
        {
            _buildingCategory = category;
            if (!_indexingSearch) _storagePageGeneration++;
            if (!_indexingSearch) _updateStatus = null;
            if (!_indexingSearch) { ResetSearchHighlight(); _searchTargets.Clear(); }
            _refreshControls.Clear();
            _content = new StackPanel { Spacing = 4, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
            BuildCategory(category);
            return _content;
        }
        finally { _building = false; }
    }

    private void ShowNotification(string text, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        _notification.Severity = severity;
        _notification.Message = text;
        _notification.IsOpen = true;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    // ---- 顶部卡片：重启软件 / 重置设置 / 关闭软件 ----------------------------

    private UIElement BuildTopCard()
    {
        var text = new TextBlock
        {
            Text = "设置更改将自动保存，部分设置需要重启软件后生效",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = SecondaryText(),
        };
        var buttons = new Grid
        {
            ColumnSpacing = 8,
        };
        for (int index = 0; index < 3; index++) buttons.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        buttons.Children.Add(TopCardButton("重启软件", "\uE72C", 16, RestartApp));
        buttons.Children.Add(TopCardButton("重置设置", "\uE8F7", 18, ResetSettings));
        buttons.Children.Add(TopCardButton("关闭软件", "\uE7E8", 18, ExitApp));
        for (int index = 0; index < buttons.Children.Count; index++) Grid.SetColumn((FrameworkElement)buttons.Children[index], index);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        return Searchable(Card(panel), "重置设置 / 重启软件 / 关闭软件");
    }

    private Button TopCardButton(string label, string glyph, double glyphSize, Action onClick)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontFamily = IconFont,
            FontSize = glyphSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PopupBrushes.Foreground(BrushTheme),
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PopupBrushes.Foreground(BrushTheme),
        });
        var button = new Button
        {
            Content = row,
            MinWidth = 0,
            MinHeight = 40,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Accessible(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void RestartApp()
    {
        if (_shell is ShellController controller) { controller.Restart(); return; }
        // 原版 BtnRestart_Click：Process.Start(exe, "-m") 后退出。-m 在移植版被忽略（无互斥），保留参数以对齐原版。
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("缺少程序路径");
            Process.Start(new ProcessStartInfo(exe, "-m") { UseShellExecute = true });
        }
        catch
        {
            ShowNotification("重启失败：无法启动新的程序实例。");
            return;
        }
        ExitApp();
    }

    private void ResetSettings()
    {
        S.ResetToRecommendation();
        // 原版重置后强制开启开机自启（ToggleSwitchRunAtStartup.IsOn = true）。
        Platform.System.AutoStartService.SetEnabled(true, Environment.ProcessPath ?? "InkCanvas.exe");
        _services.ApplySettings();
        _services.SaveSettings();
        SettingsChanged?.Invoke();
        // 与主题切换一致：延迟到下一调度帧重建，避免在按钮 Click 事件内拆毁自身。
        QueueRebuild();
    }

    private void ExitApp()
    {
        // ShellController.Exit：保存、清理、Dispose 后退出；绕过退出确认（原版 CloseIsFromButton）。
        if (_shell is not null) { _shell.Exit(); return; }
        if (_updater?.ExitForInstall is not null) { _updater.ExitForInstall(); return; }
        Close();
        Application.Current.Exit();
    }

    // ---- 分区（原版 GroupBox 名称与顺序） ----------------------------------

    private void BuildCategory(string category)
    {
        switch (category)
        {
            case "模块与插件":
                BuildExtensionsCategory();
                break;
            case "实验性设置":
                _content.Children.Add(InfoRow("屏幕内容跟随优先适配 Word、PowerPoint 与 Microsoft Edge。隐藏墨迹或切回鼠标模式后仍读取页面状态，返回批注时恢复对应位置。识别失败时暂停显示该页墨迹，可手动重新绑定。"));
                _content.Children.Add(ToggleRow("启用屏幕内容跟随（实验性）", () => S.Experimental.ScreenContentTracking,
                    value => S.Experimental.ScreenContentTracking = value));
                AddSection("跟随与页面记忆");
                _content.Children.Add(ToggleRow("批注跟随正文滚动", () => S.Experimental.FollowScroll, value => S.Experimental.FollowScroll = value));
                _content.Children.Add(ToggleRow("批注跟随页面缩放", () => S.Experimental.FollowZoom, value => S.Experimental.FollowZoom = value));
                _content.Children.Add(ToggleRow("遮罩固定区域和悬浮界面", () => S.Experimental.MaskFixedRegions, value => S.Experimental.MaskFixedRegions = value,
                    note: "正文区域外的墨迹始终裁切；页面内部的固定页眉和侧栏在确认滚动后加入遮罩。"));
                _content.Children.Add(ToggleRow("自动记忆不同文档与页面的墨迹", () => S.Experimental.RememberPages, value => S.Experimental.RememberPages = value,
                    note: "本次运行期间按 Word 文档、网页地址和 PPT 幻灯片标识保存墨迹、图片及撤销记录。同一页面的工具栏、标题变化不会触发切换。"));
                _content.Children.Add(ToggleRow("保留页面中未变化区域的批注", () => S.Experimental.PreserveUnchangedRegions,
                    value => S.Experimental.PreserveUnchangedRegions = value,
                    note: "仅同步能够唯一匹配的相同段落区域。共享区域发生跨页同步时会重置目标页撤销记录，避免撤销恢复过期内容。"));
                AddSection("适配应用");
                _content.Children.Add(ToggleRow("适配 Microsoft Word", () => S.Experimental.Word, value => S.Experimental.Word = value));
                _content.Children.Add(ToggleRow("适配 Microsoft PowerPoint", () => S.Experimental.PowerPoint, value => S.Experimental.PowerPoint = value));
                _content.Children.Add(ToggleRow("适配 Microsoft Edge", () => S.Experimental.Edge, value => S.Experimental.Edge = value));
                _screenTrackingStatus = new TextBlock { Text = _services.ScreenTrackingStatus, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
                _screenTrackingStatus.Loaded += (_, _) => _trackingStatusTimer.Start();
                _screenTrackingStatus.Unloaded += (_, _) => _trackingStatusTimer.Stop();
                _content.Children.Add(Card(_screenTrackingStatus));
                _content.Children.Add(ButtonRow("重新绑定当前批注位置", "定位中断或页面排版发生大幅变化时使用", _services.ResetScreenTrackingAnchor));
                _content.Children.Add(InfoRow("需要目标应用提供可访问性滚动与缩放信息；受保护窗口、自绘画布和部分网页可能无法识别。页面记忆保存在当前进程内，不会修改文档、注入网页脚本或上传屏幕内容。白板画布不参与此实验。"));
                break;
            case "启动选项":
                AddSection("更新");
                _content.Children.Add(ToggleRow("自动检查更新", () => S.Startup.IsAutoUpdate,
                    v => S.Startup.IsAutoUpdate = v));
                _content.Children.Add(ToggleRow("静默更新", () => S.Startup.IsAutoUpdateWithSilence,
                    v => S.Startup.IsAutoUpdateWithSilence = v,
                    note: "开启后，在设定时段内等待画布空闲再自动更新。存在墨迹、文档或正在使用工具时会继续等待。"));
                _content.Children.Add(ComboRow("静默更新起始时间", SilenceTimes,
                    () => Array.IndexOf(SilenceTimes, S.Startup.AutoUpdateWithSilenceStartTime),
                    i => S.Startup.AutoUpdateWithSilenceStartTime = SilenceTimes[i]));
                _content.Children.Add(ComboRow("静默更新终止时间", SilenceTimes,
                    () => Array.IndexOf(SilenceTimes, S.Startup.AutoUpdateWithSilenceEndTime),
                    i => S.Startup.AutoUpdateWithSilenceEndTime = SilenceTimes[i]));
                _content.Children.Add(InfoRow("# 若终止时间小于起始时间，即将终止时间视为第二天的时间。若起始时间与终止时间相同，即视为全天候时间。"));
                AddSection("启动行为");
                _content.Children.Add(ToggleRow("开机时运行", () => Platform.System.AutoStartService.IsEnabled(),
                    v => Platform.System.AutoStartService.SetEnabled(v, Environment.ProcessPath ?? "InkCanvas.exe")));
                _content.Children.Add(ToggleRow("开机运行后收纳到侧边栏", () => S.Startup.IsFoldAtStartup,
                    v => S.Startup.IsFoldAtStartup = v, note: "下次启动时生效。"));
                _content.Children.Add(ToggleRow("启用笔尖模式", () => S.Startup.IsEnableNibMode,
                    v => S.Startup.IsEnableNibMode = v));
                AddSection("应用管理");
                _content.Children.Add(BuildTopCard());
                break;
            case "显示器":
                BuildDisplaySettings();
                break;
            case "画板选项":
                AddSection("画布与图片");
                _content.Children.Add(ToggleRow("上传图片时将画质压缩至 1080P 及以下", () => S.Canvas.IsCompressPicturesUploaded,
                    v => S.Canvas.IsCompressPicturesUploaded = v));
                _content.Children.Add(ToggleRow("退出画板模式后隐藏墨迹", () => S.Canvas.HideStrokeWhenSelecting,
                    v => S.Canvas.HideStrokeWhenSelecting = v,
                    note: "切换到鼠标模式时是否仍显示墨迹由此开关决定。"));
                _content.Children.Add(ToggleRow("白板背景配色", () => S.Canvas.UsingWhiteboard,
                    v =>
                    {
                        S.Canvas.UsingWhiteboard = v;
                        _services.Board.SetBackground(v ? InkCanvas.Features.Board.BoardBackground.White : InkCanvas.Features.Board.BoardBackground.Black);
                    }));
                AddSection("书写与笔尖");
                _content.Children.Add(ToggleRow("使用硬件压感", () => S.Canvas.UseHardwarePressure,
                    v => S.Canvas.UseHardwarePressure = v, note: "压感笔按实际压力控制各笔刷与荧光笔的粗细，单击描点也随压力变化。红外触摸屏使用笔速模拟压感。"));
                _content.Children.Add(ToggleRow("倾斜笔尖与水墨阴影", () => S.Canvas.UsePenTilt,
                    v => S.Canvas.UsePenTilt = v));
                _content.Children.Add(ComboRow("笔迹风格", OriginalMenuCatalog.PenStyles,
                    () => S.Canvas.InkStyle, i => S.Canvas.InkStyle = i, index => new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 12,
                        Children = { new PenStyleIcon(index) { Width = 64, Height = 26 },
                            new TextBlock { Text = OriginalMenuCatalog.PenStyles[index],
                                VerticalAlignment = VerticalAlignment.Center } },
                    }));
                _content.Children.Add(ComboRow("笔尖", OriginalMenuCatalog.PenNibs,
                    () => (int)S.Canvas.PenNib, i => S.Canvas.PenNib = (InkCanvas.Core.Ink.PenNib)i));
                _content.Children.Add(SliderRow("画笔粗细（原菜单 1–30；设置值为菜单值的一半）",
                    () => S.Canvas.InkWidth, 0.5, 15, v => S.Canvas.InkWidth = v, 0.5));
                _content.Children.Add(ToggleRow("显示画笔光标", () => S.Canvas.IsShowCursor, value => S.Canvas.IsShowCursor = value));
                AddSection("水墨笔刷");
                _content.Children.Add(SliderRow("水墨笔锋粗细（px）", () => S.Canvas.InkWashWidth,
                    1, 120, value => S.Canvas.InkWashWidth = value));
                _content.Children.Add(SliderRow("水墨枯笔飞白（%）", () => Math.Round(S.Canvas.InkWash.Dryness * 100),
                    0, 100, value => S.Canvas.InkWash = S.Canvas.InkWash with { Dryness = (float)(value / 100) }));
                _content.Children.Add(SliderRow("水墨侧锋倾角（°）", () => S.Canvas.InkWash.Angle,
                    0, 180, value => S.Canvas.InkWash = S.Canvas.InkWash with { Angle = (float)value }));
                _content.Children.Add(SliderRow("水墨扁笔侧锋度（%）", () => Math.Round(S.Canvas.InkWash.Flatness * 100),
                    0, 90, value => S.Canvas.InkWash = S.Canvas.InkWash with { Flatness = (float)(value / 100) }));
                AddSection("橡皮");
                _content.Children.Add(ComboRow("橡皮大小", OriginalMenuCatalog.EraserSizes,
                    () => S.Canvas.EraserSize, i => S.Canvas.EraserSize = i));
                _content.Children.Add(ComboRow("橡皮模式", OriginalMenuCatalog.EraserModes,
                    () => S.Canvas.EraserType, i => S.Canvas.EraserType = i));
                AddSection("荧光笔");
                _content.Children.Add(SliderRow("荧光笔透明度", () => S.Canvas.InkAlpha,
                    20, 240, v => S.Canvas.InkAlpha = (int)v, 20));
                _content.Children.Add(SliderRow("荧光笔粗细", () => (S.Canvas.HighlighterWidth ?? S.Canvas.InkWidth) * 2,
                    1, OriginalMenuCatalog.HighlighterWidthMaximum, v => S.Canvas.HighlighterWidth = v / 2));
                break;
            case "手势选项":
                AddSection("触控与变换");
                _content.Children.Add(ToggleRow("压感笔书写优先", () => S.Gesture.PenHasPriority,
                    v => S.Gesture.PenHasPriority = v, note: "笔尖落下后抑制同一屏幕上的手掌和手指输入，其他屏幕仍可同时书写。"));
                _content.Children.Add(ToggleRow("手掌与大面积接触擦除", () => S.Gesture.PalmErases,
                    v => S.Gesture.PalmErases = v, note: "未使用压感笔时，大面积接触作为板擦。阈值沿用特殊屏幕选项中的触摸大小设置。"));
                _content.Children.Add(ComboRow("缩放/旋转变换中心点", new[]
                    {
                        "画布中心点（默认）", "全部内容的中心点", "手势操作的中心点",
                    }, () => (int)S.Gesture.MatrixTransformCenterPoint,
                    i => S.Gesture.MatrixTransformCenterPoint = (MatrixTransformCenterPointOptions)i));
                _content.Children.Add(Require(ToggleRow("自动开关两指缩放功能", () => S.Gesture.AutoSwitchTwoFingerGesture,
                    value => S.Gesture.AutoSwitchTwoFingerGesture = value,
                    note: "进入白板时临时关闭多指书写并启用双指平移，离开时恢复原手势设置。"), TouchRequirement));
                _content.Children.Add(ToggleRow("允许双指旋转选中的墨迹", () => S.Gesture.IsEnableTwoFingerRotationOnSelection,
                    v => S.Gesture.IsEnableTwoFingerRotationOnSelection = v,
                    note: "支持在多指书写模式下旋转选中的墨迹、形状、图片和视频。选区捏合缩放使用两指中心。"));
                _content.Children.Add(ToggleRow("启用多点触控模式", () => S.Gesture.IsEnableMultiTouchMode,
                    v => S.Gesture.IsEnableMultiTouchMode = v));
                AddSection("双指手势");
                _content.Children.Add(ToggleRow("双指缩放", () => S.Gesture.IsEnableTwoFingerZoom,
                    v => S.Gesture.IsEnableTwoFingerZoom = v));
                _content.Children.Add(ToggleRow("双指平移", () => S.Gesture.IsEnableTwoFingerTranslate,
                    v => S.Gesture.IsEnableTwoFingerTranslate = v));
                _content.Children.Add(ToggleRow("双指旋转", () => S.Gesture.IsEnableTwoFingerRotation,
                    v => S.Gesture.IsEnableTwoFingerRotation = v));
                break;
            case "墨迹识别选项":
                AddSection("停笔成形");
                _content.Children.Add(ToggleRow("启用墨迹识别", () => S.InkToShape.IsInkToShapeEnabled,
                    v => S.InkToShape.IsInkToShapeEnabled = v));
                _content.Children.Add(SliderRow("停笔吸附等待时间 (毫秒)", () => S.InkToShape.HoldMilliseconds,
                    300, 1500, v => S.InkToShape.HoldMilliseconds = (int)v, 50));
                _content.Children.Add(ToggleRow("识别圆与椭圆", () => S.InkToShape.RecognizeRoundShapes,
                    v => S.InkToShape.RecognizeRoundShapes = v));
                _content.Children.Add(ToggleRow("识别三角形、矩形与平行四边形", () => S.InkToShape.RecognizePolygons,
                    v => S.InkToShape.RecognizePolygons = v));
                _content.Children.Add(ToggleRow("识别立方体线框", () => S.InkToShape.RecognizeCube,
                    v => S.InkToShape.RecognizeCube = v));
                _content.Children.Add(ToggleRow("识别坐标轴", () => S.InkToShape.RecognizeAxes,
                    v => S.InkToShape.RecognizeAxes = v));
                AddSection("自动对齐");
                _content.Children.Add(ToggleRow("自动对齐水平、垂直与 45° 方向", () => S.InkToShape.AutoAlign,
                    v => S.InkToShape.AutoAlign = v, note: "停笔吸附后，继续拖动可微调端点或顶点，抬笔确认。撤销可恢复原笔迹。"));
                _content.Children.Add(SliderRow("方向吸附容差 (度)", () => S.InkToShape.AlignmentAngle,
                    1, 12, v => S.InkToShape.AlignmentAngle = v, 1));
                break;
            case "外观选项":
                AddSection("主题与工具栏");
                int[] themeMap = { 1, 2, 0 };
                _content.Children.Add(ComboRow("颜色主题", OriginalMenuCatalog.ThemeOptions,
                    () => Array.IndexOf(themeMap, S.Appearance.Theme), i => S.Appearance.Theme = themeMap[i]));
                _content.Children.Add(ToggleRow("浮动工具栏显示文字注释", () => S.Appearance.IsEnableDisPlayFloatBarText,
                    v => S.Appearance.IsEnableDisPlayFloatBarText = v));
                _content.Children.Add(ToggleRow("浮动工具栏背景色", () => S.Appearance.IsColorfulViewboxFloatingBar,
                    v => S.Appearance.IsColorfulViewboxFloatingBar = v));
                AddSection("大小与位置");
                _content.Children.Add(SliderRow("浮动工具栏底部边距 (像素)", () => S.Appearance.FloatingBarBottomMargin,
                    0, 200, v => S.Appearance.FloatingBarBottomMargin = v, presets: new[] { 100d },
                    presetsLabel: "底部边距快捷值", formatPreset: v => $"重置为 {v:0}"));
                _content.Children.Add(SliderRow("浮动工具栏 UI 缩放 (%)", () => S.Appearance.FloatingBarScale,
                    50, 150, v => S.Appearance.FloatingBarScale = v, presets: new[] { 80d, 90d, 100d, 110d, 125d }, presetsLabel: "工具栏缩放快捷值"));
                _content.Children.Add(SliderRow("画板 UI 缩放 (%)", () => S.Appearance.BlackboardScale,
                    50, 150, v => S.Appearance.BlackboardScale = v, presets: new[] { 80d, 90d, 100d, 110d, 125d }, presetsLabel: "画板缩放快捷值"));
                AddSection("显示的按钮");
                _content.Children.Add(ToggleRow("按钮背景透明", () => S.Appearance.IsTransparentButtonBackground,
                    v => S.Appearance.IsTransparentButtonBackground = v));
                _content.Children.Add(ToggleRow("显示退出按钮", () => S.Appearance.IsShowExitButton,
                    v => S.Appearance.IsShowExitButton = v));
                _content.Children.Add(ToggleRow("显示橡皮按钮", () => S.Appearance.IsShowEraserButton,
                    v => S.Appearance.IsShowEraserButton = v));
                _content.Children.Add(ToggleRow("显示隐藏控制按钮", () => S.Appearance.IsShowHideControlButton,
                    v => S.Appearance.IsShowHideControlButton = v));
                _content.Children.Add(ToggleRow("显示左右切换按钮", () => S.Appearance.IsShowLRSwitchButton,
                    v => S.Appearance.IsShowLRSwitchButton = v));
                _content.Children.Add(ToggleRow("显示手指模式切换按钮", () => S.Appearance.IsShowModeFingerToggleSwitch,
                    v => S.Appearance.IsShowModeFingerToggleSwitch = v));
                break;
            case "PPT 选项":
                AddSection("翻页控件");
                _content.Children.Add(ToggleRow("显示 PPT 下方翻页箭头按钮", () => S.PowerPoint.IsShowBottomPPTNavigationPanel,
                    v => S.PowerPoint.IsShowBottomPPTNavigationPanel = v));
                _content.Children.Add(ToggleRow("显示 PPT 下方页码按钮", () => S.PowerPoint.IsShowPPTNavigationBottom,
                    v => S.PowerPoint.IsShowPPTNavigationBottom = v));
                _content.Children.Add(ToggleRow("显示 PPT 侧边翻页箭头按钮", () => S.PowerPoint.IsShowSidePPTNavigationPanel,
                    v => S.PowerPoint.IsShowSidePPTNavigationPanel = v));
                _content.Children.Add(ToggleRow("显示 PPT 侧边页码按钮", () => S.PowerPoint.IsShowPPTNavigationSides,
                    v => S.PowerPoint.IsShowPPTNavigationSides = v));
                AddSection("放映与批注");
                _content.Children.Add(ToggleRow("Microsoft PowerPoint 支持", () => S.PowerPoint.PowerPointSupport,
                    v => S.PowerPoint.PowerPointSupport = v));
                _content.Children.Add(ToggleRow("WPS 支持", () => S.PowerPoint.IsSupportWPS,
                    v => { S.PowerPoint.IsSupportWPS = v; _services.Ppt.AllowWps = v; }));
                _content.Children.Add(ToggleRow("进入 PPT 放映时自动进入批注模式", () => S.PowerPoint.IsShowCanvasAtNewSlideShow,
                    v => S.PowerPoint.IsShowCanvasAtNewSlideShow = v));
                _content.Children.Add(Require(ToggleRow("允许幻灯片模式下的双指手势", () => S.PowerPoint.IsEnableTwoFingerGestureInPresentationMode,
                    value => S.PowerPoint.IsEnableTwoFingerGestureInPresentationMode = value), () => PresentationRequirement() ?? TouchRequirement()));
                _content.Children.Add(ToggleRow("允许使用手指手势进行幻灯片翻页", () => S.PowerPoint.IsEnableFingerGestureSlideShowControl,
                    v => S.PowerPoint.IsEnableFingerGestureSlideShowControl = v));
                AddSection("保存与提示");
                _content.Children.Add(ToggleRow("自动幻灯片截屏", () => S.PowerPoint.IsAutoSaveScreenShotInPowerPoint,
                    v => S.PowerPoint.IsAutoSaveScreenShotInPowerPoint = v));
                _content.Children.Add(ToggleRow("自动保存幻灯片墨迹", () => S.PowerPoint.IsAutoSaveStrokesInPowerPoint,
                    v => S.PowerPoint.IsAutoSaveStrokesInPowerPoint = v));
                _content.Children.Add(ToggleRow("记忆并提示上次播放位置", () => S.PowerPoint.IsNotifyPreviousPage,
                    v => S.PowerPoint.IsNotifyPreviousPage = v));
                _content.Children.Add(ToggleRow("提示隐藏幻灯片", () => S.PowerPoint.IsNotifyHiddenPage,
                    v => S.PowerPoint.IsNotifyHiddenPage = v));
                _content.Children.Add(ToggleRow("提示是否已启用自动播放", () => S.PowerPoint.IsNotifyAutoPlayPresentation,
                    v => S.PowerPoint.IsNotifyAutoPlayPresentation = v));
                _content.Children.Add(Require(ToggleRow("PPT 选择时不清除墨迹", () => S.PowerPoint.IsNoClearStrokeOnSelectWhenInPowerPoint,
                    value => S.PowerPoint.IsNoClearStrokeOnSelectWhenInPowerPoint = value,
                    note: "进入鼠标模式时保留当前幻灯片的批注。"), () => PresentationRequirement()
                        ?? (S.Automation.IsAutoClearWhenExitingWritingMode ? null : "请先在“自动选项”中启用切换到鼠标模式后自动清屏。")));
                _content.Children.Add(ToggleRow("鼠标模式时在 PPT 上显示墨迹", () => S.PowerPoint.IsShowStrokeOnSelectInPowerPoint,
                    v => S.PowerPoint.IsShowStrokeOnSelectInPowerPoint = v));
                break;
            case "高级选项":
                AddSection("触控屏适配");
                _content.Children.Add(InfoRow("# 启用特殊屏幕模式以调节触控屏触摸大小倍数。# 触控屏触摸大小倍数调整为 0 即为禁用手势橡皮。"));
                _content.Children.Add(ToggleRow("特殊屏幕模式", () => S.Advanced.IsSpecialScreen,
                    v => S.Advanced.IsSpecialScreen = v));
                _content.Children.Add(SliderRow("特殊屏幕触摸大小倍数", () => S.Advanced.TouchMultiplier,
                    0, 5, v => S.Advanced.TouchMultiplier = v, 0.01));
                _content.Children.Add(Require(ButtonRow("触摸大小倍数校准", "在校准区域用触控笔点按，按接触尺寸计算触摸大小倍数。",
                    async () => await CalibrateTouchAsync(), "开始校准"), () => TouchRequirement()
                        ?? (S.Advanced.IsSpecialScreen ? null : "请先启用特殊屏幕模式。")));
                _content.Children.Add(ToggleRow("在调色盘窗口中显示 启用笔尖模式 切换按钮", () => S.Appearance.IsEnableDisPlayNibModeToggler,
                    value => S.Appearance.IsEnableDisPlayNibModeToggler = value));
                AddSection("触摸判定参数");
                _content.Children.Add(SliderRow("笔尖模式 BoundsWidth", () => S.Advanced.NibModeBoundsWidth,
                    1, 50, v => S.Advanced.NibModeBoundsWidth = (int)v));
                _content.Children.Add(SliderRow("手指模式 BoundsWidth", () => S.Advanced.FingerModeBoundsWidth,
                    1, 50, v => S.Advanced.FingerModeBoundsWidth = (int)v));
                _content.Children.Add(SliderRow("笔尖模式 ThresholdValue", () => S.Advanced.NibModeBoundsWidthThresholdValue,
                    0, 10, v => S.Advanced.NibModeBoundsWidthThresholdValue = v, 0.01));
                _content.Children.Add(SliderRow("手指模式 ThresholdValue", () => S.Advanced.FingerModeBoundsWidthThresholdValue,
                    0, 10, v => S.Advanced.FingerModeBoundsWidthThresholdValue = v, 0.01));
                _content.Children.Add(SliderRow("笔尖模式 EraserSize", () => S.Advanced.NibModeBoundsWidthEraserSize,
                    0.01, 10, v => S.Advanced.NibModeBoundsWidthEraserSize = v, 0.01));
                _content.Children.Add(SliderRow("手指模式 EraserSize", () => S.Advanced.FingerModeBoundsWidthEraserSize,
                    0.01, 10, v => S.Advanced.FingerModeBoundsWidthEraserSize = v, 0.01));
                _content.Children.Add(ToggleRow("四边红外模式", () => S.Advanced.IsQuadIR,
                    v => S.Advanced.IsQuadIR = v, note: "以宽高几何平均计算接触尺寸。"));
                AddSection("系统与诊断");
                _content.Children.Add(ToggleRow("记录日志", () => S.Advanced.IsLogEnabled,
                    v => S.Advanced.IsLogEnabled = v));
                _content.Children.Add(ToggleRow("关闭软件时二次弹窗确认", () => S.Advanced.IsSecondConfimeWhenShutdownApp,
                    value => S.Advanced.IsSecondConfimeWhenShutdownApp = value));
                _content.Children.Add(ToggleRow("阻止 Win 10/11 的边缘手势", () => S.Advanced.IsEnableEdgeGestureUtil,
                    v => S.Advanced.IsEnableEdgeGestureUtil = v));
                _content.Children.Add(InfoRow("触摸参数根据驱动报告的接触尺寸生效，可先使用校准区域测量。"));
                break;
            case "自动选项":
                AddSection("工具栏自动收纳");
                _content.Children.Add(ToggleRow("进入“希沃白板”时自动收纳", () => S.Automation.IsAutoFoldInEasiNote,
                    v => S.Automation.IsAutoFoldInEasiNote = v));
                _content.Children.Add(Require(ToggleRow("自动收纳忽略桌面EN批注窗口", () => S.Automation.IsAutoFoldInEasiNoteIgnoreDesktopAnno,
                    value => S.Automation.IsAutoFoldInEasiNoteIgnoreDesktopAnno = value,
                    note: "应用于希沃白板的空标题小型桌面批注窗口。"), () =>
                        S.Automation.IsAutoFoldInEasiNote ? null : "请先启用希沃白板自动收纳。"));
                _content.Children.Add(ToggleRow("进入“希沃视频展台”时自动收纳", () => S.Automation.IsAutoFoldInEasiCamera,
                    v => S.Automation.IsAutoFoldInEasiCamera = v));
                _content.Children.Add(ToggleRow("进入“希沃轻白板”时自动收纳", () => S.Automation.IsAutoFoldInEasiNote3C,
                    value => S.Automation.IsAutoFoldInEasiNote3C = value));
                _content.Children.Add(ToggleRow("进入“希沃品课”时自动收纳", () => S.Automation.IsAutoFoldInSeewoPincoTeacher,
                    v => S.Automation.IsAutoFoldInSeewoPincoTeacher = v));
                _content.Children.Add(ToggleRow("进入“鸿合白板”时自动收纳", () => S.Automation.IsAutoFoldInHiteTouchPro,
                    v => S.Automation.IsAutoFoldInHiteTouchPro = v));
                _content.Children.Add(ToggleRow("进入“鸿合视频展台”时自动收纳", () => S.Automation.IsAutoFoldInHiteCamera, value => S.Automation.IsAutoFoldInHiteCamera = value));
                _content.Children.Add(ToggleRow("进入“文香白板”时自动收纳", () => S.Automation.IsAutoFoldInWxBoardMain, value => S.Automation.IsAutoFoldInWxBoardMain = value));
                _content.Children.Add(ToggleRow("进入“中原旧白板”时自动收纳", () => S.Automation.IsAutoFoldInOldZyBoard, value => S.Automation.IsAutoFoldInOldZyBoard = value));
                _content.Children.Add(ToggleRow("进入“微软 Whiteboard”时自动收纳", () => S.Automation.IsAutoFoldInMSWhiteboard,
                    v => S.Automation.IsAutoFoldInMSWhiteboard = v));
                _content.Children.Add(ToggleRow("进入PPT 放映时自动收纳至侧边栏", () => S.Automation.IsAutoFoldInPPTSlideShow,
                    v => S.Automation.IsAutoFoldInPPTSlideShow = v));
                _content.Children.Add(ToggleRow("自动查杀希沃“PPT 小工具”", () => S.Automation.IsAutoKillPptService, value => S.Automation.IsAutoKillPptService = value,
                    note: "每隔 10 秒关闭当前用户的 PPTService 和 SeewoIwbAssistant 进程。"));
                _content.Children.Add(ToggleRow("自动查杀 希沃白板5", () => S.Automation.IsAutoKillEasiNote, value => S.Automation.IsAutoKillEasiNote = value,
                    note: "每隔 10 秒强制关闭当前用户的 EasiNote 进程，未保存的内容会丢失。"));
                AddSection("墨迹与截图保存");
                _content.Children.Add(ToggleRow("清屏时自动截图", () => S.Automation.IsAutoSaveStrokesAtClear,
                    v => S.Automation.IsAutoSaveStrokesAtClear = v));
                _content.Children.Add(ToggleRow("截图分日期文件夹保存", () => S.Automation.IsSaveScreenshotsInDateFolders,
                    v => S.Automation.IsSaveScreenshotsInDateFolders = v));
                _content.Children.Add(ToggleRow("截图时自动保存墨迹", () => S.Automation.IsAutoSaveStrokesAtScreenshot,
                    v => S.Automation.IsAutoSaveStrokesAtScreenshot = v));
                _content.Children.Add(ToggleRow("切换到鼠标模式后自动清屏", () => S.Automation.IsAutoClearWhenExitingWritingMode,
                    value => S.Automation.IsAutoClearWhenExitingWritingMode = value, note: "清除桌面当前页批注，可通过撤销恢复；PPT 可单独设置保留墨迹。"));
                _content.Children.Add(SliderRow("自动保存所需最少墨迹数（0 表示不限制）",
                    () => S.Automation.MinimumAutomationStrokeNumber, 0, 10,
                    v => S.Automation.MinimumAutomationStrokeNumber = (int)v));
                _content.Children.Add(TextRow("墨迹、截图自动保存位置", () => S.Automation.AutoSavedStrokesLocation,
                    v => S.Automation.AutoSavedStrokesLocation = v, PathShortcuts()));
                _content.Children.Add(InfoRow("* 请注意检查保存文件夹是否有写入权限。"));
                AddSection("历史文件清理");
                var retentionDays = OriginalMenuCatalog.AutoDeleteDays.Append(S.Automation.AutoDelSavedFilesDaysThreshold)
                    .Where(day => day > 0).Distinct().Order().ToArray();
                _content.Children.Add(Require(ToggleRow("定期自动删除超过保存时间的墨迹、截图文件", () => S.Automation.AutoDelSavedFiles,
                    value => S.Automation.AutoDelSavedFiles = value,
                    note: "每小时清理保存目录中过期的墨迹存档和程序生成的截图，跳过链接目录。"), SaveDirectoryRequirement));
                _content.Children.Add(Require(ComboRow("保存时间设置（Days）", retentionDays.Select(day => day + " 天").ToArray(),
                    () => Math.Max(0, Array.IndexOf(retentionDays, S.Automation.AutoDelSavedFilesDaysThreshold)),
                    index => S.Automation.AutoDelSavedFilesDaysThreshold = retentionDays[index]),
                    () => !S.Automation.AutoDelSavedFiles ? "请先启用定期自动删除。" : SaveDirectoryRequirement()));
                break;
            case "关于 Ink Canvas Artistry":
                AddSection("版本与更新");
                _content.Children.Add(InfoRow(_updater is null
                    ? "Ink Canvas · WinUI 3"
                    : $"当前版本：{_updater.CurrentVersion}"));
                _content.Children.Add(ButtonRow("检查更新", "从 GitHub 发行版检查新版本", async () =>
                {
                    if (_updater is not null) await _updater.CheckAsync(interactive: true);
                }));
                _content.Children.Add(ButtonRow("版本列表", "查看仓库的稳定发行版和更新说明", async () =>
                { if (_updater is not null) await _updater.ShowVersionsAsync(); }));
                _updateStatus = new TextBlock { Text = _updater?.Status ?? "更新服务未启动", TextWrapping = TextWrapping.Wrap };
                _content.Children.Add(Card(_updateStatus));
                AddSection("帮助");
                _content.Children.Add(ButtonRow("操作指南", "模式、工具、手势与快捷键", () =>
                {
                    var w = InfoWindow.OpenGuide(S.Appearance.Theme);
                    w.Activate();
                }));
                _content.Children.Add(ButtonRow("更新日志", "查看版本变更记录", () =>
                {
                    var w = InfoWindow.OpenChangeLog(S.Appearance.Theme);
                    w.Activate();
                }));

                AddSection("开源与贡献者");
                _content.Children.Add(InfoRow("* 使用和分发本软件前，请您应当且务必知晓相关开源协议，且您应当知晓本软件基于 https://github.com/WXRIW/Ink-Canvas 修改而成。开源协议: GNU General Public License v3.0"));
                _content.Children.Add(LinkRow("https://github.com/InkCanvas/Ink-Canvas-Artistry", "本项目开发者: CJ Wu (ChangSakura)"));
                _content.Children.Add(LinkRow("https://github.com/WXRIW/Ink-Canvas", "原项目开发者: XY Wang (WXRIW)"));
                _content.Children.Add(InfoRow("原项目贡献者: Kengwang、Raspberry Kan、jiajiaxd、CN-Ironegg、Clover Yan、STBBRD、ChangSakura"));

                // 移植附加分区：原版设置面板没有抽奖分区，这两项是移植版抽奖服务的真实接线。
                break;
            case "抽奖与点名":
                BuildLotterySettings();
                break;
            case "存储与缓存":
                BuildStorageSettings();
                break;
        }
    }

    private static string[] BuildSilenceTimes()
    {
        var times = new string[72];
        for (int i = 0; i < times.Length; i++)
            times[i] = $"{i * 20 / 60:00}:{i * 20 % 60:00}";
        return times;
    }

    private void RefreshUpdateStatus()
    {
        if (_closed) return;
        if (_updateStatus is not null) _updateStatus.Text = (_updater?.Status ?? "更新服务未启动")
            + (_updater?.LastCheckUtc is DateTime lastCheck ? "\n上次检查 " + lastCheck.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "");
        if (_updateCard is null) return;
        var progress = _updater?.Progress;
        bool active = _updater?.IsBusy == true;
        _updateCard.Visibility = active || progress?.Stage is Features.Update.UpdateStage.Failed or Features.Update.UpdateStage.Waiting
            ? Visibility.Visible : Visibility.Collapsed;
        string details = progress?.Source;
        if (progress?.DownloadedBytes > 0)
            details = string.Join(" · ", new[] { details, FormatBytes(progress.DownloadedBytes)
                + (progress.TotalBytes > 0 ? " / " + FormatBytes(progress.TotalBytes) : ""),
                progress.BytesPerSecond > 0 ? FormatBytes((long)progress.BytesPerSecond) + "/秒" : null }.Where(value => value is not null));
        _updateCard.Update(_updater?.Status ?? "更新服务未启动", details, progress?.Fraction,
            active && progress?.Stage is Features.Update.UpdateStage.Checking or Features.Update.UpdateStage.Downloading
                or Features.Update.UpdateStage.Verifying or Features.Update.UpdateStage.Preparing, _updater?.CanCancel == true);
        if (!_building && !_committing) RefreshControls();
    }

    private FrameworkElement PathShortcuts()
    {
        var panel = new Grid { ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var drive = new Button { Content = "D:\\Ink Canvas", MinHeight = 40, MinWidth = 0, Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        Accessible(drive, "将自动保存目录设置为 D:\\Ink Canvas");
        drive.Click += (_, _) => { S.Automation.AutoSavedStrokesLocation = @"D:\Ink Canvas"; Commit(); };
        var documents = new Button { Content = "文档目录", MinHeight = 40, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Accessible(documents, "将自动保存目录设置为文档目录");
        documents.Click += (_, _) =>
        {
            S.Automation.AutoSavedStrokesLocation = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Commit();
        };
        panel.Children.Add(drive);
        Grid.SetColumn(documents, 1);
        panel.Children.Add(documents);
        return panel;
    }

    private FrameworkElement QuickValues(string label, double[] values, Action<double> set,
        Func<double, string> format = null)
    {
        var row = new Grid { ColumnSpacing = 4 };
        foreach (double value in values)
        {
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            string text = format?.Invoke(value) ?? $"{value:0}%";
            var button = new Button { Content = text, MinHeight = 40, MinWidth = 0, FontSize = 12,
                Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch };
            Accessible(button, $"{label} {text}");
            button.Click += (_, _) => { set(value); Commit(); };
            Grid.SetColumn(button, row.Children.Count);
            row.Children.Add(button);
        }
        return row;
    }

    private UIElement LinkRow(string url, string credit)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        var link = new HyperlinkButton
        {
            Content = new TextBlock { Text = url, TextWrapping = TextWrapping.Wrap },
            NavigateUri = new Uri(url),
            Padding = new Thickness(0),
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Accessible(link, url);
        panel.Children.Add(link);
        panel.Children.Add(new TextBlock
        {
            Text = credit,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryText(),
        });
        return Card(panel);
    }

    // ---- row helpers ------------------------------------------------------

    private SolidColorBrush SecondaryText() => new(PopupBrushes.IsDark(BrushTheme)
        ? Color.FromArgb(0xA8, 255, 255, 255)
        : Color.FromArgb(0x9E, 0, 0, 0));

    private Border Card(UIElement child) => new()
    {
        // 与 Tokens.xaml 的 IcSurfaceAlt/IcStroke 同值，但按窗口主题取值，
        // 避免 Application.Current.Resources 按应用主题解析造成明暗错位。
        Background = new SolidColorBrush(PopupBrushes.IsDark(BrushTheme)
            ? Color.FromArgb(255, 43, 44, 47)
            : Color.FromArgb(255, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(PopupBrushes.IsDark(BrushTheme)
            ? Color.FromArgb(0x24, 255, 255, 255)
            : Color.FromArgb(0x1F, 0, 0, 0)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Child = child,
    };

    private UIElement ToggleRow(string header, Func<bool> get, Action<bool> set,
        bool enabled = true, string disabledReason = null, string note = null)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = get(),
            OnContent = "开",
            OffContent = "关",
            IsEnabled = enabled,
            MinWidth = 0,
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Accessible(toggle, header, enabled ? null : disabledReason);
        toggle.Toggled += (_, _) =>
        {
            if (_building || _synchronizingControls || !toggle.IsEnabled || toggle.IsOn == get()) return;
            set(toggle.IsOn);
            Commit();
        };
        _refreshControls.Add(() => toggle.IsOn = get());
        return RequireSetting(header, Searchable(Card(Row(header, toggle, note ?? (enabled ? null : disabledReason))), header, note ?? disabledReason));
    }

    private UIElement SliderRow(string header, Func<double> get, double min, double max,
        Action<double> set, double step = 1, double[] presets = null, string presetsLabel = null, Func<double, string> formatPreset = null)
    {
        var readout = new TextBlock
        {
            Text = get().ToString("0.##"),
            MinWidth = 48,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PopupBrushes.Foreground(BrushTheme),
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = Math.Clamp(get(), min, max),
            MinWidth = 100,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Accessible(slider, header, $"范围 {min:0.##} 到 {max:0.##}，步长 {step:0.##}");
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = e.NewValue.ToString("0.##");
            if (_building || _synchronizingControls || !slider.IsEnabled || Math.Abs(e.NewValue - get()) < .0001) return;
            set(e.NewValue);
            Commit();
        };
        _refreshControls.Add(() =>
        {
            slider.Value = Math.Clamp(get(), min, max);
            readout.Text = get().ToString("0.##");
        });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(slider, 0);
        Grid.SetColumn(readout, 1);
        row.Children.Add(slider);
        row.Children.Add(readout);
        var editor = new StackPanel { Spacing = 8, Children = { row } };
        if (presets is not null) editor.Children.Add(QuickValues(presetsLabel, presets, set, formatPreset));
        var card = Searchable(Card(Row(header, editor, editorWidth: 300)), header);
        if (presets is not null) Searchable(card, presetsLabel, string.Join(" ", presets.Select(value => formatPreset?.Invoke(value) ?? $"{value:0}%")));
        return RequireSetting(header, card);
    }

    private UIElement ComboRow(string header, IReadOnlyList<string> options, Func<int> get, Action<int> set, Func<int, UIElement> optionContent = null)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            MinHeight = 40,
        };
        for (int index = 0; index < options.Count; index++)
        {
            if (optionContent is null) combo.Items.Add(options[index]);
            else
            {
                var item = new ComboBoxItem { Content = optionContent(index) };
                AutomationProperties.SetName(item, options[index]);
                combo.Items.Add(item);
            }
        }
        // Set the index after items are present and before subscribing: WinUI otherwise
        // coerces it back to -1, and the initial change must not commit.
        combo.SelectedIndex = Math.Clamp(get(), 0, options.Count - 1);
        Accessible(combo, header);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building || _synchronizingControls || !combo.IsEnabled || combo.SelectedIndex < 0 || combo.SelectedIndex == get()) return;
            set(combo.SelectedIndex);
            Commit();
        };
        _refreshControls.Add(() => combo.SelectedIndex = Math.Clamp(get(), 0, options.Count - 1));
        return RequireSetting(header, Searchable(Card(Row(header, combo, editorWidth: 240)), header, string.Join(" ", options)));
    }

    private UIElement TextRow(string header, Func<string> get, Action<string> set, FrameworkElement shortcuts = null)
    {
        var box = new TextBox
        {
            Text = get() ?? string.Empty,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            MinHeight = 40,
        };
        Accessible(box, header);
        box.TextChanging += (_, _) =>
        {
            if (_building || _synchronizingControls || box.Text == get()) return;
            set(box.Text);
            _textCommitTimer.Stop();
            _textCommitTimer.Start();
        };
        box.LostFocus += (_, _) =>
        {
            if (!_synchronizingControls && _textCommitTimer.IsRunning) Commit();
        };
        _refreshControls.Add(() =>
        {
            string text = get() ?? string.Empty;
            if (box.Text != text) box.Text = text;
        });
        var editor = new StackPanel { Spacing = 8, Children = { box } };
        if (shortcuts is not null) editor.Children.Add(shortcuts);
        return Searchable(Card(Row(header, editor, editorWidth: 320)), header);
    }

    private UIElement ButtonRow(string header, string description, Action onClick, string actionLabel = null)
    {
        var button = new Button
        {
            Content = actionLabel ?? (header switch { "检查更新" => "检查更新", "版本列表" or "更新日志" or "操作指南" => "查看",
                "打开用户插件目录" => "打开文件夹", "重新绑定当前批注位置" => "重新绑定", _ => "打开" }),
            MinWidth = 100,
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Accessible(button, header, description);
        button.Click += (_, _) => { if (button.IsEnabled) onClick(); };
        return RequireSetting(header, Searchable(Card(Row(header, button, description)), header, description));
    }

    private UIElement InfoRow(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = SecondaryText(),
        FontSize = 12,
        LineHeight = 18,
        Margin = new Thickness(4, 6, 4, 10),
    };

    private SettingsRow Row(string header, FrameworkElement control, string note = null, double editorWidth = 0)
        => new(header, control, BrushTheme, note, editorWidth);

    private void AddSection(string title)
    {
        _content.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PopupBrushes.Foreground(BrushTheme), Margin = new Thickness(4, _content.Children.Count == 0 ? 0 : 20, 0, 6) });
    }

    private static void Accessible(DependencyObject control, string name, string helpText = null)
    {
        AutomationProperties.SetName(control, name);
        if (!string.IsNullOrWhiteSpace(helpText)) AutomationProperties.SetHelpText(control, helpText);
    }

    private static string OptionalText(OptionalOperation operation) => operation switch
    {
        OptionalOperation.Yes => "是",
        OptionalOperation.No => "否",
        _ => "每次询问",
    };
}
