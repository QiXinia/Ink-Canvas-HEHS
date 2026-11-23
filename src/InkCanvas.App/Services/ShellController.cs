using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Ppt;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Tools;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.System;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using InkCanvas.Host.Contracts;
using InkCanvas.Extensibility;

using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Classroom;
using InkCanvas.Platform.Input;
using InkCanvas.Platform.Windows;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;


namespace InkCanvas.App.Services;

/// <summary>
/// Owns the windows and implements the shell actions the floating bar invokes. Keeps App.xaml.cs
/// down to startup wiring.
/// </summary>
public sealed partial class ShellController : IShellActions, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly PptInkFileStore _pptStore;
    private readonly PptInkSession _pptSession;
    private SpotlightWindow _spotlight;
    private MagnifierWindow _magnifier;
    private readonly NavigationGuard _pptGuard = new();
    private readonly SlideSwitchGate _pptGate = new();
    private readonly Dictionary<PptNavigationPlacement, PptNavigationWindow> _pptNavigations = new();
    private SlideNavigatorWindow _slideNav;
    private SlideContentIndex _slideIndex;
    private DateTime _lastSlideIndexRefreshUtc = DateTime.MinValue;
    private bool _pptNavCollapsed;
    private bool _manualPptNav;
    private PptSnapshot _pptShowSnapshot;
    private int _pptCaptureSession;
    private int _pptCaptureGeneration;
    private string _pptScreenshotPath;
    private int _pptScreenshotSlideIndex;
    private int _pptScreenshotSlideId;
    private readonly PresentationSourceSelector _pptSources = new();
    private readonly ComPresentationSource _pptComSource;
    private readonly WindowPresentationSource _pptWindowSource = new();
    private readonly KeyPresentationSource _pptKeySource = new();
    private BlackScreenWindow _blackScreen;
    private DateTime _lastPptRpcBusyLogUtc = DateTime.MinValue;
    private FocusRegionStore _focusRegions;

    private FloatingBarWindow _bar;
    private SelectionToolbarWindow _selectionToolbar;
    private bool _selectionToolbarCollapsed;
    private BoardWindow _board;
    private BoardWindow _cachedBoard;
    private bool _boardSwitching;
    private SettingsWindow _settings;
    private bool _settingsOpening;
    private IntPtr _settingsDrawingSurface;
    private bool _settingsSurfaceWasTopmost;
    private LotteryWindow _settingsRosterSource;
    private string _requestedSettingsCategory;
    internal string RosterDirectory { get; set; } = LotteryWindow.DataDir;
    private bool _imagePicking;
    private CancellationTokenSource _imageImportCancellation;
    private CountdownWindow _countdown;
    private LotteryWindow _lottery;
    private ReplayWindow _replay;
    private readonly List<NoticeWindow> _notices = new();
    private PenPaletteWindow _palette;
    private ColorEditorWindow _colorEditor;
    private readonly List<PenPaletteWindow> _detachedPalettes = new();
    private ShapePickerWindow _shapePicker;
    private bool _lastSlideShowState;
    private bool _lastAutoFoldRequested;
    private bool? _manualFoldOverride;
    private bool _exiting;
    private bool _exitPromptOpen;
    private bool _paletteHighlighter;
    private bool _disposed;
    private InkController _boundBoardController;
    private IReadOnlyList<ScreenInfo> _screens = Array.Empty<ScreenInfo>();
    private bool _displayRefreshPending;
    private long _lastDisplayCheck;


    public ShellController(AppServices services)
    {
        Services = services;
        services.SettingsApplied += ApplyDocumentSettings;
        services.SettingsApplied += RefreshIconFonts;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Overlay = new OverlayHost(services.OverlayInk);
        Overlay.InteractionChanged += RefreshShortcutRegistration;
        Overlay.ConfigureDisplays(services.Settings.Display);
        services.SettingsApplied += ApplyDisplaySettings;
        services.SettingsApplied += RefreshPaletteNibModes;
        Overlay.Window.DisplaySettingsChanged += QueueDisplayRefresh;
        _screens = ScreenService.All();
        _documentAi = new DocumentAiHost(this);
        RegisterAssistantTools();
        services.StartModules(new ScreenTrackingHost(services, Overlay, () => _board is not null || DocumentActive), new DocumentHost(this), new CanvasToolsHost(this), new AssistantHost(this), new LibraryHost(this), _documentAi, new StylusInputHost(this));
        _pptStore = new PptInkFileStore(services.Settings.Automation.AutoSavedStrokesLocation);
        services.SettingsApplied += ApplyArchivePaths;
        _pptSession = new PptInkSession(services.Ppt, _pptStore)
        {
            InkManagedExternally = () => Services.Modules.HasExport<IScreenTrackingService>() && Services.Settings.Experimental.ScreenContentTracking && Services.Settings.Experimental.PowerPoint,
            CaptureCurrentInk = () => Services.OverlayInk.Document.Strokes,
            ApplyCurrentInk = strokes =>
            {
                var controller = Services.OverlayInk;
                Overlay.Presenter.BeginSceneUpdate();
                try
                {
                    Overlay.Window.CancelInput();
                    controller.NotifyInputContextChanging();
                    controller.EndPlayback();
                    controller.FinishDotAnimations();
                    controller.ClearSelection();
                    controller.Document.ReplaceAll(strokes);
                    controller.History.Clear();
                    _bar?.Bar.RefreshSelection();
                }
                finally { Overlay.Presenter.EndSceneUpdate(); }
            },
            AutoSavePresentationInk = () => Services.Settings.PowerPoint.IsAutoSaveStrokesInPowerPoint,
            AutoSavePptScreenshots = () => Services.Settings.PowerPoint.IsAutoSaveScreenShotInPowerPoint,
            NotifyPreviousPage = () => Services.Settings.PowerPoint.IsNotifyPreviousPage,
            NotifyHiddenSlides = () => Services.Settings.PowerPoint.IsNotifyHiddenPage,
            NotifySlideTimings = () => Services.Settings.PowerPoint.IsNotifyAutoPlayPresentation,
            AutoSaveRoot = () => Services.Settings.Automation.AutoSavedStrokesLocation,
            UseScreenshotDateFolders = () => Services.Settings.Automation.IsSaveScreenshotsInDateFolders,
            SaveInkWithScreenshot = () => Services.Settings.Automation.IsAutoSaveStrokesAtScreenshot,
            MinimumScreenshotStrokeCount = () => Services.Settings.Automation.MinimumAutomationStrokeNumber,
        };
        _pptSession.PresentationNotice += OnPptPresentationNotice;
        _pptSession.ScreenshotRequested += OnPptScreenshotRequested;
        _pptSession.InkTransition += OnPptInkTransition;
        _pptSession.Failed += LogPptFailure;
        _pptComSource = new ComPresentationSource(
            () => _pptSession.Snapshot.IsAttached, () => _pptSession.Next(), () => _pptSession.Previous());
        _focusRegions = FocusRegionStore.Deserialize(Services.Settings.Classroom.SpotlightRegions);
        Updater = new UpdateCoordinator(services)
        {
            Confirm = ConfirmAsync,
            ConfirmRelease = ConfirmUpdateReleaseAsync,
            Notify = ShowUpdateNoticeAsync,
            ExitForInstall = () => { _installingUpdate = true; Exit(); },
            CanInstallSilently = CanInstallUpdateSilently,
            PrepareForInstall = PrepareForUpdateAsync,
            ChooseRelease = ChooseUpdateReleaseAsync,
        };

        _pollTimer = _dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(900);
        _pollTimer.Tick += (_, _) => Poll();
        services.Ppt.RefreshRequested += QueuePresentationRefresh;
    }

    /// <summary>Raised whenever a chrome window is created (for the --shot self-check hook).</summary>
    public event Action<Window> WindowOpened;

    private void NotifyWindowOpened(Window w)
    {
        w.Activated += (_, _) => RefreshShortcutRegistration();
        if (w.Content is FrameworkElement root)
            root.GotFocus += (_, _) => RefreshShortcutRegistration();
        if (w is SettingsWindow or CountdownWindow or LotteryWindow or InfoWindow)
        {
            var owner = _documentWorkspace?.Window ?? (Window)_board ?? _bar;
            if (owner is not null) WindowChrome.CenterOnOwner(w, owner);
        }
        WindowOpened?.Invoke(w);
    }

    private void RefreshIconFonts()
    {
        foreach (var window in OpenWindows)
        {
            IconFontStyle.Refresh(window.Content);
            if (window.Content is FrameworkElement { XamlRoot: { } root })
                foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                    IconFontStyle.Refresh(popup.Child);
        }
    }

    private void QueueDisplayRefresh()
    {
        if (_displayRefreshPending || _disposed) return;
        _displayRefreshPending = true;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _displayRefreshPending = false;
            if (!_disposed) RefreshDisplays();
        });
    }

    private void RefreshDisplays() => RefreshDisplays(ScreenService.All());

    internal void RefreshDisplays(IReadOnlyList<ScreenInfo> screens)
    {
        _lastDisplayCheck = Environment.TickCount64;
        if (screens.Count == 0) return;
        if (_screens.SequenceEqual(screens)) return;
        try
        {
            Overlay.RefreshDisplayBounds(screens);
            _bar?.RefreshDisplayPlacement();
            _palette?.RefreshDisplayPlacement();
            foreach (var palette in _detachedPalettes) palette.RefreshDisplayPlacement();
            _shapePicker?.RefreshDisplayPlacement();
            if (_board is not null) foreach (var panel in _board.ToolPanels) panel.Fit();
            _replay?.RefreshDisplayPlacement();
            foreach (var window in OpenWindows)
                if (window is SettingsWindow or CountdownWindow or LotteryWindow or InfoWindow or ReplayWindow or NoticeWindow or UpdateWindow
                    || _documentWindows.Contains(window))
                    WindowChrome.KeepVisible(window);
            QueueDesktopSelection();
            Services.Modules.Invoke<IScreenTrackingService>(tracking => tracking.Refresh());
            _screens = screens.ToArray();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"Display layout: {ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>All live chrome windows (for the --shot headless visual self-check).</summary>
    public IEnumerable<Window> OpenWindows
    {
        get
        {
            if (_bar is not null) yield return _bar;
            foreach (var navigation in _pptNavigations.Values) yield return navigation;
            if (_selectionToolbar is not null) yield return _selectionToolbar;
            if (_board is not null) yield return _board;
            if (_documentWorkspace is not null) yield return _documentWorkspace.Window;
            foreach (var window in _documentWindows) if (!ReferenceEquals(window, _documentWorkspace?.Window)) yield return window;
            if (_settings is not null) yield return _settings;
            if (_colorEditor is not null) yield return _colorEditor;
            foreach (var reminder in _resumeReminders) yield return reminder;
            if (_countdown is not null) yield return _countdown;
            if (_lottery is not null) yield return _lottery;
            if (_replay is not null) yield return _replay;
            if (_updateWindow is not null) yield return _updateWindow;
            foreach (var notice in _notices) yield return notice;
            if (_palette is not null) yield return _palette;
            foreach (var palette in _detachedPalettes) yield return palette;
            if (_shapePicker is not null) yield return _shapePicker;
        }
    }

    public AppServices Services { get; }

    public OverlayHost Overlay { get; }

    public UpdateCoordinator Updater { get; }

    /// <summary>Diagnostics for the floating bar's window-vs-content sizing.</summary>
    public string BarDiagnostics
    {
        get
        {
            if (_bar is null) return "no bar";
            var size = _bar.AppWindow.Size;
            var client = _bar.AppWindow.ClientSize;
            var desired = _bar.Bar.DesiredSize;
            return $"window={size.Width}x{size.Height}; client={client.Width}x{client.Height}; "
                 + $"dpi={WindowChrome.GetDpiScale(_bar):0.##}; desired={desired.Width:0.#}x{desired.Height:0.#}; "
                 + $"backdrop={(_bar.SystemBackdrop as ChromeBackdrop)?.State}; "
                 + $"primaryChildren={_bar.Bar.PrimaryChildCount}; computedRow={_bar.Bar.ComputedPrimaryWidth:0.#}";
        }
    }

    public void Start()
    {
        _bar = new FloatingBarWindow();
        NotifyWindowOpened(_bar);
        _bar.Bar.Settings = Services.Settings;
        _bar.Configure(Services.OverlayInk, this);
        _displayedExtensionCommands = ExtensionCommands.ToArray();
        Services.Plugins.Changed += OnExtensionCommandsChanged;
        Services.Modules.Changed += OnExtensionCommandsChanged;
        _ = StartPluginsAsync();
        _bar.Bar.LayoutChanged += OnBarLayoutChanged;
        _bar.Bar.SettingsEdited += () => { Services.ApplySettings(); Services.SaveSettings(); };
        _bar.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                        Services.Settings.Appearance.FloatingBarScale);
        WindowChrome.ApplyTheme(_bar, Services.Settings.Appearance.Theme);
        WindowChrome.ShowWithoutActivation(_bar);

        Services.OverlayInk.Invalidated += QueueDesktopSelection;
        Services.OverlayInk.Invalidated += QueueDesktopMedia;
        Services.ScreenImages.ImagesChanged += QueueDesktopMedia;
        Services.ScreenImages.PageChanged += QueueDesktopMedia;
        Services.ScreenImages.MediaPlacementChanged += OnDesktopMediaPlacement;
        Services.OverlayInk.SelectionChanged += QueueDesktopSelection;
        _bar.AppWindow.Changed += OnBarWindowChanged;

        Overlay.Show();
        // A click on the overlay raises it to the top of the topmost Z-band, above every chrome
        // window. Re-assert the chrome immediately so close buttons and title bars stay clickable.
        Overlay.Window.InputBegan += () =>
        {
            RaiseChromeAboveOverlay();
            _bar?.DismissPanelsOnInput();
            // WPF HideSubPanels: laying ink dismisses the pen/shape popups (pin only survives picks).
            _palette?.DismissOnOverlayInput();
            _shapePicker?.DismissOnOverlayInput();
        };
        if (Services.Settings.Startup.IsFoldAtStartup) _bar.SetFolded(true);
        Overlay.RegisterHotkeys(
            undo: () => ActiveController.Undo(),
            redo: () => ActiveController.Redo(),
            toggleEraser: () => { ActiveController.Tool = InkTool.Eraser; _bar.Bar.RefreshSelection(); },
            clear: () =>
            {
                if (ActiveController.HasSelection) ActiveController.DeleteSelection();
                else ClearCanvas();
            },
            togglePassThrough: ToggleClickThrough,
            toggleBoard: ToggleBoard,
            pptNext: () => HandlePresentationKey(PresentationKeyMap.VkPageDown),
            pptPrevious: () => HandlePresentationKey(PresentationKeyMap.VkPageUp),
            pptExit: () => { if (_lastSlideShowState) PptExit(); },
            screenshot: () => _ = ScreenshotAsync(),
            fold: ToggleFold,
            select: () => { if (_board is not null) _board.SelectOrSelectAll(); else ActiveController.Tool = InkTool.Select; _bar.Bar.RefreshSelection(); },
            line: () =>
            {
                ActiveController.SelectShape(Core.Shapes.ShapeKind.Line);
                _bar?.Bar.RefreshSelection();
            },
            draw: () =>
            {
                bool active = ActiveController.Tool == InkTool.Pen && (DocumentActive || _board is not null || !Overlay.ClickThrough);
                if (!DocumentActive && _board is null && Overlay.ClickThrough) ToggleClickThrough();
                ActiveController.Tool = InkTool.Pen;
                if (active) OpenPenPalette(false);
            },
            editingEnabled: () => CanvasShortcutsEnabled,
            overlayEditingEnabled: () => OverlayShortcutsEnabled,
            presentationEnabled: () => PresentationShortcutsEnabled);

        Overlay.Hotkeys.Register(HotkeyModifiers.None, 0x78 /* F9 */, ToggleBlackScreen);
        Overlay.Hotkeys.Register(HotkeyModifiers.None, 0x42 /* B */, ToggleBlackScreen, () => PresentationShortcutsEnabled);
        Overlay.Hotkeys.Register(HotkeyModifiers.None, 0x1B /* Escape */, CloseBlackScreen, () => _blackScreen is not null);
        StartShortcutTracking();
        _pollTimer.Start();

        // Delete-selection changes the button count; FloatingBar raises LayoutChanged for this.
        Services.OverlayInk.SlideFlipRequested += next => { if (next) PptNext(); else PptPrevious(); };
        Services.OverlayInk.ToolChanged += OnControllerToolChanged;

        Core.Diagnostics.FileLog.NewLog($"Ink Canvas 启动，版本 {Updater.CurrentVersion}");

        ApplyEdgeGestureSetting();

        if (Services.Settings.Startup.IsAutoUpdate)
            _ = Updater.CheckAsync(interactive: false);
    }

    /// <summary>
    /// The default hotkey set. Extracted so it can be replayed after the Win32 hotkey service
    /// drops a temporary key (the service only supports unregistering everything).
    /// </summary>
    private void OnBarLayoutChanged()
    {
        _bar?.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                         Services.Settings.Appearance.FloatingBarScale, recenter: false);
        QueueDesktopSelection();
    }

    private void OnBarWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange) QueueDesktopSelection();
    }

    private void QueueDesktopSelection()
    {
        if (_disposed || _exiting) return;
        var ink = Services.OverlayInk;
        if (_selectionToolbar is null && DesktopSelectionAvailable && ink.HasSelection && ink.Playback is null
            && !ink.IsRegionSelecting && !ink.IsRotatingSelection)
        {
            var toolbar = new SelectionToolbarWindow(this);
            _selectionToolbar = toolbar;
            if (_selectionToolbarCollapsed) toolbar.Toolbar.ToggleCollapsed();
            toolbar.Closed += (_, _) =>
            {
                _selectionToolbarCollapsed = toolbar.Toolbar.IsCollapsed;
                if (ReferenceEquals(_selectionToolbar, toolbar)) _selectionToolbar = null;
            };
            NotifyWindowOpened(toolbar);
        }
        if (ink.HasSelection || _selectionToolbar?.IsShown == true) _selectionToolbar?.QueueUpdate();
    }

    internal bool DesktopSelectionAvailable => _board is null && !DocumentActive && !_exiting && !Overlay.ClickThrough
        && Services.OverlayInk.Tool == InkTool.Select;

    internal Core.Ink.Rect DesktopToolbarBounds
    {
        get
        {
            if (_bar is null || _bar.IsFolded) return Core.Ink.Rect.Empty;
            var pos = _bar.AppWindow.Position;
            var size = _bar.AppWindow.Size;
            return new Core.Ink.Rect(pos.X, pos.Y, pos.X + size.Width, pos.Y + size.Height);
        }
    }

    private InkController ActiveController => _board is not null
        ? Services.Board.Current
        : DocumentActive ? _documentWorkspace.Controller : Services.OverlayInk;

    /// <summary>
    /// WPF GetMainWindowTheme: in board mode the aux windows follow the black/white board
    /// choice (light iff whiteboard), otherwise the application theme.
    /// </summary>
    /// <summary>Auto-revert to pen changes Tool outside bar clicks; keep the bar selection in sync.</summary>
    private void OnControllerToolChanged()
    {
        RefreshShortcutRegistration();
        if (DocumentActive && ActiveController.Tool == InkTool.Pan) OptionPopupCoordinator.DismissUnpinned();
        if (_bar is not null) _bar.Bar.ClickThroughActive = DocumentActive ? ActiveController.Tool == InkTool.Pan : Overlay.ClickThrough;
        _bar?.Bar.RefreshSelection();
        QueueDesktopSelection();
    }

    private void BindBoardController(InkController controller)
    {
        if (!ReferenceEquals(controller, _boundBoardController)) CloseDetachedPalettes();
        if (_boundBoardController is not null)
        {
            _boundBoardController.ToolChanged -= OnControllerToolChanged;
        }
        _boundBoardController = controller;
        if (controller is not null)
        {
            controller.ToolChanged += OnControllerToolChanged;
        }
    }

    private int EffectiveTheme => _board is not null
        ? (Services.Settings.Canvas.UsingWhiteboard ? 1 : 2)
        : Services.Settings.Appearance.Theme;

    private void ApplyEdgeGestureSetting()
        => Platform.System.EdgeGestureService.SetEdgeGesturesDisabled(
            Overlay.Window.Handle, Services.Settings.Advanced.IsEnableEdgeGestureUtil);

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = DocumentActive ? _documentWorkspace.Window : (Window)_board ?? _bar;
        return await YesOrNoWindow.AskAsync(owner, message, title);
    }

    private Task NotifyAsync(string title, string message)
    {
        if (_disposed || _exiting) return Task.CompletedTask;
        _bar?.ClosePanels();
        var notice = _notices.FirstOrDefault(window => window.Title == title && window.Message == message);
        if (notice is not null) { notice.Activate(); return notice.Completion; }
        notice = new NoticeWindow(title, message, EffectiveTheme);
        _notices.Add(notice);
        notice.Closed += (_, _) => _notices.Remove(notice);
        var owner = DocumentActive ? _documentWorkspace.Window : (Window)_board ?? _bar;
        if (owner is not null) WindowChrome.CenterOnOwner(notice, owner);
        NotifyWindowOpened(notice);
        notice.Activate();
        WindowChrome.EnsureAbove(notice, Overlay.Window.Handle);
        return notice.Completion;
    }

    private void OnPptPresentationNotice(PptPresentationNotice notice)
    {
        _dispatcher.TryEnqueue(async () => await ShowPptPresentationNoticeAsync(notice));
    }

    private async Task ShowPptPresentationNoticeAsync(PptPresentationNotice notice)
    {
        // WPF YesOrNoNotificationWindow parity: exact strings, topmost 450x160 window that also
        // works while the floating bar is folded (the old ContentDialog hung on a hidden XamlRoot).
        var owner = (Window)_board ?? _bar;
        string resumeHint = notice.ResumeIndexDegraded ? "（按旧页码恢复，页序可能已变化）" : "";
        if (notice.PreviousSlideIndex is > 0
            && await YesOrNoWindow.AskAsync(owner, $"上次播放到了第 {notice.PreviousSlideIndex} 页，是否立即跳转？{resumeHint}"))
            _pptSession.GoToSlide(notice.PreviousSlideIndex.Value);

        if (notice.OfferRestoreHiddenSlides
            && await YesOrNoWindow.AskAsync(owner, "检测到此演示文档中包含隐藏的幻灯片，是否取消隐藏？"))
            _pptSession.RestoreHiddenSlides();

        if (notice.OfferDisableTimings
            && await YesOrNoWindow.AskAsync(owner, "检测到此演示文档中自动播放或排练计时已经启用，可能导致幻灯片自动翻页，是否取消？"))
            _pptSession.DisableSlideTimings();

        if (notice.LegacyInkFilesDetected || notice.ResumeIndexDegraded)
            await ShowInkRecoveryWizardAsync(notice);
    }

    private async Task ShowInkRecoveryWizardAsync(PptPresentationNotice notice)
    {
        try
        {
            if (_bar?.Content is not FrameworkElement root || root.XamlRoot is null) return;
            string summary = notice.InkMappingSummary ?? "课件页序与墨迹存档不一致。";
            string backupHint = string.IsNullOrWhiteSpace(notice.BackupDirectory)
                ? "选择清空前会先备份整个墨迹目录。"
                : $"迁移备份：{notice.BackupDirectory}";
            var dialog = new ContentDialog
            {
                Title = "课件批注与当前页面不一致",
                Content = new TextBlock
                {
                    Text = $"{summary}\n{backupHint}\n\n保留现有批注不会修改任何数据。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "保留现有批注",
                SecondaryButtonText = "备份并导出",
                CloseButtonText = "稍后处理",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root.XamlRoot,
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    "PPT ink recovery: user kept existing ink after mapping mismatch", Core.Diagnostics.LogType.Info);
                return;
            }
            if (result != ContentDialogResult.Secondary)
            {
                Core.Diagnostics.FileLog.WriteLogToFile(
                    "PPT ink recovery: user postponed the mapping decision", Core.Diagnostics.LogType.Info);
                return;
            }

            string directory = _pptStore.GetPresentationDirectory(notice.Details.Presentation);
            string backup = SlideInkMigrator.Backup(directory);
            if (string.IsNullOrWhiteSpace(backup))
            {
                await NotifyAsync("备份失败", "无法备份该课件的墨迹目录，已取消操作，未删除任何数据。");
                Core.Diagnostics.FileLog.WriteLogToFile(
                    "PPT ink recovery: backup failed, nothing cleared", Core.Diagnostics.LogType.Error);
                return;
            }
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"PPT ink recovery: backup exported to {backup}", Core.Diagnostics.LogType.Info);
            if (!await AskAsync("清空该课件的批注？",
                    $"批注已备份到：\n{backup}\n\n仅在确认后清空此课件的批注，其他数据不受影响。", "清空批注"))
            {
                await NotifyAsync("已导出备份", $"未清空任何批注。备份位置：{backup}");
                return;
            }
            _pptSession.ClearPresentationInk();
            await NotifyAsync("批注已清空", $"已备份到 {backup}，随后清空了该课件的批注。");
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"PPT ink recovery: user cleared ink after backup {backup}", Core.Diagnostics.LogType.Info);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"PPT ink recovery wizard: {ex}", Core.Diagnostics.LogType.Error);
        }
    }

    private async Task<bool> AskAsync(string title, string message, string primary)
    {
        if (_bar?.Content is not FrameworkElement root || root.XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnPptScreenshotRequested(PptScreenshotRequest request)
    {
        if (!_pptGate.IsCurrent(_pptCaptureSession, _pptCaptureGeneration)) return;
        // Capture screen pixels synchronously before the COM navigation mutates the slideshow.
        try
        {
            var area = ShowWindowTracker.TryGet(request.Snapshot.WindowHandle, out var measured)
                ? new ScreenRect(measured.X, measured.Y, measured.Width, measured.Height)
                : request.Snapshot.WindowHandle != IntPtr.Zero
                    ? ScreenService.FromWindow(request.Snapshot.WindowHandle).Bounds : GetScreenshotBounds();
            var (pixels, width, height) = ScreenshotService.Capture(area.X, area.Y, area.Width, area.Height);
            if (width <= 0 || height <= 0) return;
            string path = request.SuggestedPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                string root = Services.Settings.Automation.AutoSavedStrokesLocation;
                path = Path.Combine(root, "Auto Saved - PPT Screenshots", DateTime.Now.ToString("yyyy-MM-dd"),
                    $"{request.Snapshot.ShowPosition}_{DateTime.Now:HH-mm-ss}.png");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RememberPptScreenshot(path, request.Snapshot.Slide?.Index ?? request.InkSlide?.Index ?? 0,
                request.Snapshot.Slide?.SlideId ?? request.InkSlide?.SlideId ?? 0);
            _ = ScreenshotService.SavePngAsync(path, pixels, width, height);
            SaveSlideNavigatorThumbnail(request.Snapshot.Presentation, _pptScreenshotSlideId, pixels, width, height);
            if (request.SaveInkAlongside)
            {
                string ink = Path.ChangeExtension(path, ".icink");
                InkFileStore.Save(ink, request.Strokes);
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"PPT screenshot: {ex}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>Periodic mode maintenance: slideshow detection, auto-fold, layering.</summary>
    private void Poll()
    {
        if (_exiting) return;
        RefreshDocumentTarget();
        MaintainAutomation();
        Overlay.TrimIdleResources();
        if (_selectionToolbar is { IsShown: false } toolbar && Environment.TickCount64 - toolbar.HiddenSince >= 15_000)
            toolbar.Close();
        if (Environment.TickCount64 - _lastDisplayCheck >= 1000) RefreshDisplays();
        RefreshPresentation();

        // Always evaluate auto-fold so disabling its settings also unfolds a bar that was
        // previously folded automatically.
        if (_documentWorkspace is null) ApplyAutoFold();
        RaiseChromeAboveOverlay();
        RefreshShortcutRegistration();
    }

    private bool _presentationRefreshQueued, _refreshingPresentation;
    private void QueuePresentationRefresh()
    {
        if (_exiting || _disposed || _presentationRefreshQueued) return;
        _presentationRefreshQueued = true;
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _presentationRefreshQueued = false;
            if (_exiting || _disposed) return;
            RefreshPresentation();
            RaiseChromeAboveOverlay();
        })) _presentationRefreshQueued = false;
    }

    private void RefreshPresentation()
    {
        if (_refreshingPresentation) return;
        _refreshingPresentation = true;
        try
        {
            if (Services.Settings.PowerPoint.PowerPointSupport && _documentWorkspace is null)
            {
                _pptCaptureGeneration = _pptGate.NextGeneration();
                _pptCaptureSession = _pptGate.Session;
                var current = _pptSession.Poll();

                // COM unavailable and no show visible through automation: WPS/PowerPoint may still
                // be running a full-screen viewer. Treat its window as a degraded slideshow so the
                // pager stays usable (page numbers stay 0/0, the snapshot has no slide data).
                bool windowAvailable = false;
                if (!current.IsAttached && !current.IsSlideShowRunning)
                {
                    FallbackShowWindow fallback = PresentationFallbackDetector.Detect();
                    if (fallback is not null)
                    {
                        windowAvailable = true;
                        current = new PptSnapshot
                        {
                            State = PptConnectionState.SlideShow,
                            WindowHandle = fallback.Handle,
                        };
                    }
                }
                _pptSources.Select(current.IsAttached, windowAvailable);
                bool isShow = current.IsSlideShowRunning;
                if (isShow != _lastSlideShowState)
                {
                    if (isShow) _bar?.SetPresentationMode(true, current.WindowHandle, current.IsWindowedShow);
                    _lastSlideShowState = isShow;
                    Services.PresentationActive = isShow;
                    Services.ApplySettings();
                    _bar?.Bar.SetPptModeVisible(isShow);
                    if (isShow)
                    {
                        _pptGate.BeginSession();
                        _pptCaptureSession = _pptGate.Session;
                        _pptCaptureGeneration = _pptGate.Generation;
                    }
                    else
                    {
                        _pptSources.Reset();
                        CloseBlackScreen();
                    }
                    if (isShow)
                    {
                        // MW_PPT.cs:283-286: slideshow begin leaves board mode.
                        if (_board is not null) ToggleBoard();
                        if (Services.Settings.PowerPoint.IsShowCanvasAtNewSlideShow)
                        {
                            Overlay.Show();
                            // MW_PPT.cs:302,397-400: lastDesktopInkColor=1 -> red pen on show begin.
                            Services.OverlayInk.CurrentAttributes.Color = Core.Ink.InkColor.Red;
                            _bar?.Bar.RefreshSelection();
                        }
                    }
                    else
                    {
                        // MW_PPT.cs:505-508: slideshow end leaves board mode; canvas hide state
                        // only re-applies when a non-transparent board cover was active (exited above).
                        if (_board is not null) ToggleBoard();
                    }
                }
                _bar?.SetPresentationMode(isShow, current.WindowHandle, current.IsWindowedShow);
                if (isShow && _board is null) Overlay.Window.EnsureAbove(current.WindowHandle);
                UpdatePptNavigation(current);
            }
            else
            {
                if (_lastSlideShowState)
                {
                    _lastSlideShowState = false;
                    Services.PresentationActive = false;
                    Services.ApplySettings();
                    _bar?.Bar.SetPptModeVisible(false);
                    _pptSources.Reset();
                    CloseBlackScreen();
                }
                _bar?.SetPresentationMode(false);
                UpdatePptNavigation(PptSnapshot.Detached);
            }

            // 原条件：放映中 + 尚无墨迹 + 允许手指手势控制放映。未验证。
            Services.OverlayInk.Palm.PptGestureEligible =
                _lastSlideShowState
                && Services.OverlayInk.Document.Count == 0
                && Services.Settings.PowerPoint.IsEnableFingerGestureSlideShowControl;

            _pollTimer.Interval = Services.Ppt.LastFailure is null ? _pptSession.RecommendedPollInterval : TimeSpan.FromMilliseconds(80);
        }
        catch (Exception ex) { LogPptFailure(new PptFailure("Poll", ex.Message, Marshal.GetHRForException(ex))); }
        finally { _refreshingPresentation = false; }
    }

    /// <summary>
    /// Keep floating tools above the annotation surface after overlay input and placement.
    /// </summary>
    private void RaiseChromeAboveOverlay()
    {
        ImageFilePicker.EnsureAbove(_board is not null ? WindowChrome.GetHwnd(_board) : Overlay.Window.Handle);
        if (_bar is not null && _board is null) WindowChrome.EnsureAbove(_bar, Overlay.Window.Handle);
        if (_selectionToolbar?.IsShown == true) WindowChrome.EnsureAbove(_selectionToolbar, Overlay.Window.Handle);
        _bar?.EnsureHandlesAbove(Overlay.Window.Handle);
        if (_board is not null && _settingsDrawingSurface != WindowChrome.GetHwnd(_board)) WindowChrome.EnsureAbove(_board, Overlay.Window.Handle);
        if (_countdown is not null) WindowChrome.EnsureAbove(_countdown, Overlay.Window.Handle);
        if (_lottery is not null) WindowChrome.EnsureAbove(_lottery, Overlay.Window.Handle);
        if (_replay is not null) WindowChrome.EnsureAbove(_replay, Overlay.Window.Handle);
        if (_updateWindow is not null) WindowChrome.EnsureAbove(_updateWindow, Overlay.Window.Handle);
        foreach (var notice in _notices) WindowChrome.EnsureAbove(notice, Overlay.Window.Handle);
        foreach (var navigation in _pptNavigations.Values)
            if (navigation.AppWindow.IsVisible) WindowChrome.EnsureAbove(navigation, Overlay.Window.Handle);

        if (_palette is not null) WindowChrome.EnsureAbove(_palette, Overlay.Window.Handle);
        foreach (var palette in _detachedPalettes) WindowChrome.EnsureAbove(palette, Overlay.Window.Handle);
        if (_shapePicker is not null) WindowChrome.EnsureAbove(_shapePicker, Overlay.Window.Handle);
        if (_board is not null)
            foreach (var panel in _board.ToolPanels) WindowChrome.EnsureAbove(panel, Overlay.Window.Handle);
        foreach (var window in _documentWindows) if (!ReferenceEquals(window, _documentWorkspace?.Window) && window.AppWindow.IsVisible) WindowChrome.EnsureAbove(window, Overlay.Window.Handle);
        UpdateSettingsDrawingOrder();
        RefreshDesktopMedia();
    }

    private const int RpcCallRejected = unchecked((int)0x80010001);
    private const int RpcServerCallRetryLater = unchecked((int)0x8001010A);
    private static readonly TimeSpan PptRpcBusyLogInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// PowerPoint 忙（模态对话框、正在启动/退出放映）时会拒绝 RPC 调用。这类错误是预期内的
    /// 瞬时状态，降级为 Trace 并最多每 10 秒记录一次；其它 PPT 失败仍记为 Error。
    /// </summary>
    private void LogPptFailure(PptFailure failure)
    {
        if (failure is null) return;
        if (IsPptRpcBusy(failure.ErrorCode, failure.Message))
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastPptRpcBusyLogUtc < PptRpcBusyLogInterval) return;
            _lastPptRpcBusyLogUtc = now;
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"PPT 忙，已忽略 RPC 拒绝呼叫：{failure.Operation}: {failure.Message} (0x{failure.ErrorCode:X8})",
                Core.Diagnostics.LogType.Trace);
            return;
        }

        Core.Diagnostics.FileLog.WriteLogToFile(
            $"PPT/{failure.Operation}: {failure.Message} (0x{failure.ErrorCode:X8})", Core.Diagnostics.LogType.Error);
    }

    private static bool IsPptRpcBusy(int errorCode, string message)
    {
        if (errorCode == RpcCallRejected || errorCode == RpcServerCallRetryLater) return true;
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("被呼叫方拒绝接收呼叫", StringComparison.Ordinal)
            || message.Contains("Could not get dispatch ID", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdatePptNavigation(PptSnapshot snapshot)
    {
        NotifyAiSlideChange(snapshot);

        bool inShow = snapshot?.IsSlideShowRunning == true;
        _pptShowSnapshot = inShow ? snapshot : null;
        if (inShow) RefreshSlideIndex(snapshot);
        if (!inShow && !_manualPptNav)
        {
            ClosePptNavigationWindows();
            _pptNavCollapsed = false;
            return;
        }
        bool visible = _board is null && _bar?.IsFolded != true && !_pptNavCollapsed;
        var settings = Services.Settings.PowerPoint;
        foreach (var placement in Enum.GetValues<PptNavigationPlacement>())
        {
            bool side = placement is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight;
            bool enabled = side ? settings.IsShowSidePPTNavigationPanel : settings.IsShowBottomPPTNavigationPanel;
            if (!_pptNavigations.TryGetValue(placement, out var window) && enabled && visible)
            {
                window = CreatePptNavigation(placement);
                _pptNavigations.Add(placement, window);
            }
            window?.Update(snapshot, visible && enabled,
                inShow && (side ? settings.IsShowPPTNavigationSides : settings.IsShowPPTNavigationBottom));
        }
    }

    /// <summary>
    /// 翻页后只提示「题目来自旧页」，不自动清空或多选区；保留课堂上的题目上下文。
    /// </summary>
    private void NotifyAiSlideChange(PptSnapshot snapshot)
    {
        if (Assistant is null) return;
        Assistant.NotifySlideChanged(snapshot?.Slide?.SlideId ?? 0, snapshot?.Slide?.Index ?? 0,
            snapshot?.IsSlideShowRunning == true ? Services.Ppt.GetAnimationStep() : 0);
    }

    private PptNavigationWindow CreatePptNavigation(PptNavigationPlacement placement)
    {
        var window = new PptNavigationWindow(placement, this, Services.Settings.Appearance.Theme, _bar, CreatePptPreviewContext());
        return window;
    }

    private void ClosePptNavigationWindows()
    {
        foreach (var window in _pptNavigations.Values) window.Close();
        _pptNavigations.Clear();
        _pptPreviewSource?.Dispose();
        _pptPreviewSource = null;
    }

    private void ApplyAutoFold()
    {
        if (_bar is null) return;
        var foreground = ForegroundWindowService.DescribeForeground();
        var a = Services.Settings.Automation;
        bool fold = Core.Settings.AutomationPolicy.ShouldFold(a, foreground.Process, foreground.Title, foreground.Height, _lastSlideShowState);
        if (!fold && a.IsAutoFoldInOldZyBoard)
            fold = WindowEnumerator.WindowExists("WhiteBoard - DrawingWindow") || WindowEnumerator.WindowExists("InstantAnnotationWindow");

        if (fold != _lastAutoFoldRequested)
        {
            _lastAutoFoldRequested = fold;
            _manualFoldOverride = null;
        }
        bool desiredFold = _manualFoldOverride ?? fold;

        if (desiredFold && !_bar.IsFolded)
        {
            // WPF FoldFloatingBar_Click always invokes CursorIcon_Click before hiding.
            if (!Overlay.ClickThrough) ToggleClickThrough();
            _bar.SetFolded(true);
        }
        else if (!desiredFold && _bar.IsFolded)
        {
            // WPF unfold restores only the toolbar; the overlay remains in mouse mode until
            // the user selects an ink tool.
            _bar.SetFolded(false);
        }
        if (desiredFold) Overlay.ClickThrough = true;
    }

    // ---- IShellActions ----------------------------------------------------

    public void ToggleBoard()
    {
        if (_boardSwitching) return;
        CloseDetachedPalettes();
        _imageImportCancellation?.Cancel();
        _videoImportCancellation?.Cancel();
        _selectionToolbar?.Hide();
        _replay?.Close();
        if (_board is null)
        {
            Services.BoardModeActive = true;
            Services.ConfigureController(Services.Board.Current);
            _documentWorkspace?.Window.AppWindow.Hide();
            _palette?.Close(); _shapePicker?.Close();
            bool restoring = _cachedBoard is not null;
            _board = _cachedBoard ?? new BoardWindow(Services, this, _bar);
            _cachedBoard = null;
            if (!restoring)
            {
                NotifyWindowOpened(_board);
                var boardWindow = _board;
                _board.Closed += (_, _) => OnBoardClosed(boardWindow);
            }
            _board.CanvasInputBegan += OnBoardInputBegan;
            _board.CurrentPageChanged += OnBoardPageChanged;
            BindBoardController(_board.CurrentController);
            BindToolbar(_board.CurrentController);
            _board.RestoreFromAnnotation();
            _board.Activate();
            // The board is opaque and full-screen; the screen overlay would draw over it.
            Overlay.Hide();
            // WPF slides the floating bar off-edge in board mode; hide it here (handles stay).
            if (_bar is not null && !_bar.IsFolded) _bar.AppWindow.Hide();
        }
        else
        {
            _ = ReturnToAnnotationAsync(_board);
        }
    }

    private async Task ReturnToAnnotationAsync(BoardWindow board)
    {
        _boardSwitching = true;
        try
        {
            if (!await board.HideForAnnotationAsync() || !ReferenceEquals(_board, board)) return;
            _cachedBoard = board;
            OnBoardClosed(board);
        }
        finally { _boardSwitching = false; }
    }

    private void OnBoardPageChanged()
    {
        _replay?.Close();
        if (_board is null || _bar is null) return;
        BindToolbar(_board.CurrentController);
        BindBoardController(_board.CurrentController);
        _palette?.Close();
        _palette = null;
        _shapePicker?.Close(); _shapePicker = null;
        Assistant?.NotifyBoardChanged();
    }

    private void OnBoardInputBegan()
    {
        if (_board is not null) ImageFilePicker.EnsureAbove(WindowChrome.GetHwnd(_board));
        _palette?.DismissOnOverlayInput();
        _shapePicker?.DismissOnOverlayInput();
    }

    private void OnBoardClosed(BoardWindow board)
    {
        if (board.IsClosed && ReferenceEquals(_cachedBoard, board)) _cachedBoard = null;
        if (!ReferenceEquals(_board, board)) return;
        _board = null;
        Services.BoardModeActive = false;
        Services.ConfigureController(Services.OverlayInk);
        board.CurrentPageChanged -= OnBoardPageChanged;
        board.CanvasInputBegan -= OnBoardInputBegan;
        if (_disposed || _exiting) return;
        _replay?.Close();
        _palette?.Close(); _shapePicker?.Close();
        BindBoardController(null);
        BindToolbar(ActiveController);
        Services.Modules.Invoke<IScreenTrackingService>(tracking => tracking.Refresh());
        if (_documentWorkspace is not null) { _documentWorkspace.Window.Activate(); OnDocumentPageChanged(); }
        else Overlay.Show();
        if (_bar is not null && !_bar.IsFolded)
        {
            WindowChrome.ShowWithoutActivation(_bar);
            _bar.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                            Services.Settings.Appearance.FloatingBarScale, recenter: false);
        }
        QueueDesktopSelection();
    }

    public void OpenSettings() => OpenSettingsPage(null);

    private void OpenSettingsPage(string category)
    {
        if (category is not null) _requestedSettingsCategory = category;
        if (_settings is not null)
        {
            ShowRequestedSettingsPage(_settings);
            ActivateSettingsWindow(_settings); return;
        }
        if (_settingsOpening || _exiting) return;
        _settingsOpening = true;
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (_exiting) return;
                _bar?.ClosePanels();
                _palette?.DismissOnOverlayInput();
                _shapePicker?.DismissOnOverlayInput();
                var window = new SettingsWindow(Services, Updater, this, RosterDirectory);
                _settings = window;
                ShowRequestedSettingsPage(window);
                NotifyWindowOpened(window);
                window.SettingsChanged += OnSettingsChanged;
                window.Activated += (_, _) => UpdateSettingsDrawingOrder();
                window.Closed += (_, _) =>
                {
                    window.SettingsChanged -= OnSettingsChanged;
                    _settings = null;
                    UpdateSettingsDrawingOrder();
                    _settingsRosterSource?.AppWindow.Show(activateWindow: false);
                    _settingsRosterSource = null;
                };
                ActivateSettingsWindow(window);
            }
            finally { _settingsOpening = false; }
        });
    }

    private void ActivateSettingsWindow(SettingsWindow window)
    {
        _bar?.ClosePanels();
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        window.Activate();
        // Settings is opened by an explicit user action from nonactivating tool chrome.
        // Request native foreground focus as well as XAML activation, then reorder ink.
        NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(window));
        UpdateSettingsDrawingOrder();
    }

    private void ShowRequestedSettingsPage(SettingsWindow window)
    {
        if (_requestedSettingsCategory is null) return;
        window.SelectCategory(_requestedSettingsCategory);
        if (_requestedSettingsCategory == "抽奖与点名") window.RevealRosterEditor();
        _requestedSettingsCategory = null;
    }

    private void UpdateSettingsDrawingOrder()
    {
        if (_disposed) return;
        var settings = _settings is null ? IntPtr.Zero : WindowChrome.GetHwnd(_settings);
        var surface = IntPtr.Zero;
        if (settings != IntPtr.Zero && NativeMethods.GetForegroundWindow() == settings)
        {
            if (_board is not null) surface = WindowChrome.GetHwnd(_board);
            else if (!DocumentActive) surface = Overlay.Window.Handle;
        }
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        if (surface != _settingsDrawingSurface)
        {
            if (_settingsDrawingSurface != IntPtr.Zero)
                NativeMethods.SetWindowPos(_settingsDrawingSurface,
                    _settingsSurfaceWasTopmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            _settingsDrawingSurface = surface;
            _settingsSurfaceWasTopmost = surface != IntPtr.Zero && NativeMethods.HasExStyle(surface, NativeMethods.WS_EX_TOPMOST);
        }
        // A normal settings window must receive input while the drawing surface stays visible.
        // Inserting the drawing HWND after it also removes that surface from the topmost band.
        if (surface != IntPtr.Zero) NativeMethods.SetWindowPos(surface, settings, 0, 0, 0, 0, flags);
    }

    private void OpenRosterSettings(LotteryWindow source)
    {
        if (!ReferenceEquals(_settingsRosterSource, source))
        {
            _settingsRosterSource?.AppWindow.Show(activateWindow: false);
            _settingsRosterSource = source;
            source.Closed += (_, _) => { if (ReferenceEquals(_settingsRosterSource, source)) _settingsRosterSource = null; };
        }
        source.AppWindow.Hide();
        OpenSettingsPage("抽奖与点名");
    }

    private void OnSettingsChanged()
    {
        QueueDesktopSelection();
        _bar?.Reposition(Services.Settings.Appearance.FloatingBarBottomMargin,
                         Services.Settings.Appearance.FloatingBarScale);
        if (_bar is not null) WindowChrome.ApplyTheme(_bar, Services.Settings.Appearance.Theme);
        // WPF ApplyScaling: menus live inside the scaled bar/board Viewbox, so scale them live.
        double menuScale = _board is not null
            ? Services.Settings.Appearance.BlackboardScale
            : Services.Settings.Appearance.FloatingBarScale;
        _palette?.ApplyScale(menuScale);
        foreach (var palette in _detachedPalettes) palette.ApplyScale(menuScale);
        _shapePicker?.ApplyScale(menuScale);
        _board?.RefreshUiScale();
        _bar?.Bar.RefreshSettingsVisuals();
        if (_lastSlideShowState) UpdatePptNavigation(_pptSession.Snapshot);
        ApplyEdgeGestureSetting();
    }

    private void ApplyDisplaySettings() => Overlay.ConfigureDisplays(Services.Settings.Display);

    private void RefreshPaletteNibModes()
    {
        _board?.RefreshToolSettings();
        _bar?.Bar.RefreshSelection();
        if (_palette is not null) { ConfigurePaletteNibMode(_palette); _palette.RefreshFromController(Services.Settings.Canvas); }
        foreach (var palette in _detachedPalettes) { ConfigurePaletteNibMode(palette); palette.RefreshFromController(Services.Settings.Canvas); }
    }

    private void ConfigurePaletteNibMode(PenPaletteWindow palette) => palette.ConfigureNibMode(
        Services.Settings.Appearance.IsEnableDisPlayNibModeToggler, Services.Settings.Startup.IsEnableNibMode,
        value => { Services.Settings.Startup.IsEnableNibMode = value; Services.ApplySettings(); Services.SaveSettings(); });

    public void OpenTimer()
    {
        if (_countdown is not null) { _countdown.Activate(); return; }
        var window = new CountdownWindow(EffectiveTheme, Services.Settings.Classroom.CountdownMinutes);
        NotifyWindowOpened(window);
        _countdown = window;
        WindowChrome.ApplyTheme(window, Services.Settings.Appearance.Theme);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_countdown, window)) _countdown = null;
        };
        window.Activate();
    }

    public void OpenLottery()
    {
        if (_lottery is not null) { _lottery.Activate(); return; }
        _lottery = new LotteryWindow(Services.Lottery, autoClose: false, EffectiveTheme, RosterDirectory, OpenRosterSettings);
        _lottery.OptionsChanged += () =>
        {
            Services.Settings.Rand.PeopleCount = Services.Lottery.PeopleCount;
            Services.Settings.Rand.SamplingMode = Services.Lottery.SamplingMode;
            Services.Settings.Rand.CooldownHalfLifeDays = Services.Lottery.CooldownHalfLifeDays;
            Services.Settings.Rand.CooldownPenaltyStrength = Services.Lottery.CooldownPenaltyStrength;
            Services.SaveSettings();
        };
        NotifyWindowOpened(_lottery);
        WindowChrome.ApplyTheme(_lottery, EffectiveTheme);
        _lottery.Closed += (_, _) => _lottery = null;
        _lottery.Activate();
        WindowChrome.EnsureAbove(_lottery);
    }

    /// <summary>WPF 单次抽选: quick-pick lottery that auto-rolls and auto-closes after results.</summary>
    public void OpenLotteryQuick()
    {
        var quick = new LotteryWindow(Services.Lottery, autoClose: true, EffectiveTheme, RosterDirectory, OpenRosterSettings);
        WindowChrome.ApplyTheme(quick, EffectiveTheme);
        quick.Activate();
        WindowChrome.EnsureAbove(quick);
    }

    public void OpenSpotlight()
    {
        if (_spotlight is not null) { _spotlight.Activate(); return; }
        var window = new SpotlightWindow(AiAssistantAvailable);
        window.CaptureRequested += askAi => _ = CaptureFocusAsync(askAi);
        window.SaveRegionRequested += OnSaveFocusRegion;
        window.CycleRegionRequested += OnCycleFocusRegion;
        _spotlight = window;
        _spotlight.Closed += (_, _) => _spotlight = null;
        _spotlight.Activate();
    }

    public void OpenMagnifier()
    {
        if (_magnifier is not null) { _magnifier.Activate(); return; }
        var window = new MagnifierWindow(AiAssistantAvailable);
        window.CaptureRequested += askAi => _ = CaptureFocusAsync(askAi);
        RestoreWindowPosition(
            window.MoveTo, Services.Settings.Classroom.MagnifierX, Services.Settings.Classroom.MagnifierY);
        _magnifier = window;
        _magnifier.Closed += (_, _) =>
        {
            try
            {
                Services.Settings.Classroom.MagnifierX = window.PositionX;
                Services.Settings.Classroom.MagnifierY = window.PositionY;
                Services.SaveSettings();
            }
            catch (Exception) { }

            if (ReferenceEquals(_magnifier, window)) _magnifier = null;
        };
        _magnifier.Activate();
    }

    private static void RestoreWindowPosition(Action<int, int> move, int x, int y)
    {
        if (move is null || x == int.MinValue || y == int.MinValue) return;
        var work = ScreenService.WorkArea;
        int clampedX = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - 120));
        int clampedY = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - 80));
        move(clampedX, clampedY);
    }

    public void PasteSpotlightToBoard() => _ = CaptureFocusAsync(askAi: false);

    public void AskAiFromSpotlight() => _ = CaptureFocusAsync(askAi: true);

    private async Task CaptureFocusAsync(bool askAi)
    {
        ScreenRect rect;
        bool fromSpotlight = _spotlight is not null;

        if (_spotlight is not null)
        {
            rect = _spotlight.CurrentScreenRect;
            _spotlight.SetMaskVisible(false);
        }
        else if (_magnifier is not null)
        {
            rect = _magnifier.CurrentScreenRect;
            _magnifier.HideForCapture();
        }
        else
        {
            await NotifyAsync("聚焦贴图", "请先打开聚光灯或放大镜，调整好聚焦区域后再贴图。");
            return;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            RestoreFocusWindow(fromSpotlight);
            await NotifyAsync("聚焦贴图", "聚焦区域尚未确定，请先拖动聚光灯或移动放大镜取景。");
            return;
        }

        try
        {
            await Task.Delay(90);

            if (askAi)
            {
                await StartAssistantFromRegionAsync(rect);
                return;
            }

            string base64 = await AssistantCapture.CapturePngBase64Async(
                rect.X, rect.Y, rect.Width, rect.Height);
            byte[] png = string.IsNullOrEmpty(base64)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(base64);
            if (png.Length == 0)
            {
                await NotifyAsync("聚焦贴图", "截图失败，请重试。");
                return;
            }

            await PasteImageCardAsync("聚焦区域", png, 320, 220, "聚光灯/放大镜截图");
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"聚焦贴图失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
        finally
        {
            RestoreFocusWindow(fromSpotlight);
        }
    }

    private void RestoreFocusWindow(bool fromSpotlight)
    {
        if (fromSpotlight) _spotlight?.SetMaskVisible(true);
        else _magnifier?.ShowAfterCapture();
    }

    private void OnSaveFocusRegion(ScreenRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            _ = NotifyAsync("聚焦区域", "当前聚焦区无效，请先拖动聚光灯。");
            return;
        }

        FocusRegionItem saved = _focusRegions.Add(new FocusRegionItem
        {
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
            SlideId = _pptSession.Snapshot?.Slide?.SlideId ?? 0,
            SlideIndex = _pptSession.Snapshot?.Slide?.Index ?? 0,
            AnimationStep = _pptSession.Snapshot?.IsSlideShowRunning == true
                ? Services.Ppt.GetAnimationStep()
                : 0,
            BoardPageIndex = _board is null ? -1 : Services.Board.CurrentIndex,
        }, Environment.TickCount64);

        if (saved is null)
        {
            _ = NotifyAsync("聚焦区域", "保存失败（最多保存 " + FocusRegionStore.MaximumRegions + " 个区域）。");
            return;
        }

        PersistFocusRegions();
        _ = NotifyAsync("聚焦区域", "已保存「" + saved.Name + "」，共 " + _focusRegions.Count + " 个。");
    }

    private void OnCycleFocusRegion(int delta)
    {
        if (_focusRegions.Count == 0)
        {
            _ = NotifyAsync("聚焦区域", "还没有保存的聚焦区域；点「保存区」先保存当前区域。");
            return;
        }

        ScreenRect currentRect = _spotlight?.CurrentScreenRect ?? default;
        FocusRegionItem current = currentRect.Width > 0
            ? _focusRegions.FindMatching(
                currentRect.X, currentRect.Y, currentRect.Width, currentRect.Height)
            : null;

        FocusRegionItem next = _focusRegions.Next(current, delta);
        if (next is null) return;

        _spotlight?.SetRegion(new ScreenRect(next.X, next.Y, next.Width, next.Height));
        _ = NotifyAsync("聚焦区域", "切换到「" + next.Name + "」（" + next.Order + "/" + _focusRegions.Count + "）。");
    }

    private void PersistFocusRegions()
    {
        Services.Settings.Classroom.SpotlightRegions = _focusRegions.Serialize();
        Services.SaveSettings();
    }

    private Task StartAssistantFromRegionAsync(ScreenRect rect) => Assistant?.OpenRegionAsync(rect) ?? Task.CompletedTask;

    private async Task PasteImageCardAsync(
        string title, byte[] png, int width, int height, string metadataText)
    {
        string source = "";
        if (_pptSession.Snapshot?.Presentation is not null)
            source = "课件第 " + (_pptSession.Snapshot?.Slide?.Index ?? 0) + " 页";
        else if (_board is not null)
            source = "画板第 " + (Services.Board.CurrentIndex + 1) + " 页";

        if (!string.IsNullOrEmpty(source))
            metadataText = (metadataText ?? "") + "\n来源：" + source;

        var work = ScreenService.WorkArea;
        double dpi = CurrentDpi() > 0 ? CurrentDpi() : 1.0;
        double viewWidth = work.Width / dpi;
        double viewHeight = work.Height / dpi;
        double left = Math.Clamp(viewWidth / 2.0 - width / 2.0, 8, Math.Max(8, viewWidth - width - 8));
        double top = Math.Clamp(viewHeight / 2.0 - height / 2.0, 8, Math.Max(8, viewHeight - height - 8));

        string metadata = new AiPasteMetadata
        {
            Kind = AiPasteKind.Steps,
            Text = metadataText ?? "",
            Lines = (metadataText ?? "").Split('\n').Where(line => line.Trim().Length > 0).ToList(),
        }.Serialize();

        bool added = Services.Board.AddImage(new BoardImageAsset
        {
            Name = title,
            Data = png,
            ContentType = "image/png",
            Left = (float)left,
            Top = (float)top,
            Width = width,
            Height = height,
            Metadata = metadata,
        });

        if (added && _board is null) ToggleBoard();
    }

    /// <summary>WPF 保存墨迹: save the active surface (board page or overlay) as .icink.</summary>
    public void SaveStrokes() => _ = SaveStrokesAsync();

    public void InsertImage() => _ = InsertImageAsync();

    internal async Task InsertImageAsync()
    {
        if (DocumentActive) { await NotifyAsync("PDF 批注", "PDF 文档中可使用笔、荧光笔和形状批注。图片可插入画板页。"); return; }
        if (_imagePicking) return;
        _imagePicking = true;
        var cancellation = new CancellationTokenSource();
        _imageImportCancellation = cancellation;
        try
        {
            if (_board is not null) { await _board.InsertImageAsync(); return; }
            var controller = Services.OverlayInk;
            bool IsCurrent() => !_exiting && !_disposed && _board is null && !DocumentActive;
            var viewport = new System.Numerics.Vector2(Overlay.Window.Width / Overlay.Window.DpiScale,
                Overlay.Window.Height / Overlay.Window.DpiScale);
            var asset = await ImageImport.PickAsync(_bar, Services.Settings.Canvas.IsCompressPicturesUploaded,
                viewport, controller.ViewTransform, cancellation.Token);
            if (asset is not null && await ImageImport.WaitForCanvasAsync(controller, IsCurrent, cancellation.Token))
            {
                viewport = new(Overlay.Window.Width / Overlay.Window.DpiScale, Overlay.Window.Height / Overlay.Window.DpiScale);
                ImageImport.PlaceInView(asset, viewport, controller.ViewTransform);
                InsertDesktopImage(asset);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_exiting && !_disposed) await NotifyAsync("插入图片", $"图片插入失败：{ex.Message}"); }
        finally
        {
            _imagePicking = false;
            if (ReferenceEquals(_imageImportCancellation, cancellation)) _imageImportCancellation = null;
            cancellation.Dispose();
        }
    }

    internal void InsertDesktopImage(BoardImageAsset asset)
    {
        if (!Services.ScreenImages.AddImage(asset)) return;
        Overlay.SetInteractionMode(clickThrough: false, inkVisible: true);
        Services.OverlayInk.SelectElement(Services.ScreenImages.GetSelectionElements().Single(element => element.Id == asset.Id));
        if (_bar is not null) { _bar.Bar.ClickThroughActive = false; _bar.Bar.RefreshSelection(); }
    }

    internal void CloneDesktopSelectionToBoard()
    {
        var strokes = Core.Ink.InkStroke.CloneObjects(Services.OverlayInk.Selection);
        var images = Services.OverlayInk.SelectedElements.OfType<BoardSelectionElement>()
            .Where(element => element.Image is not null).Select(element => element.Image.Copy(preserveId: false, copyData: false)).ToList();
        if (strokes.Count == 0 && images.Count == 0) return;
        var center = Services.OverlayInk.SelectionBounds.Center;
        if (Services.Board.Current.Document.Count > 0 || Services.Board.CurrentImages.Count > 0 || Services.Board.CurrentMedia.Count > 0)
        {
            if (!Services.Board.InsertPageAfterCurrent())
            {
                _ = NotifyAsync("衍至画板", "已达到画板页数上限。");
                return;
            }
        }
        ToggleBoard();
        var target = Services.Board.Current;
        var delta = System.Numerics.Matrix3x2.CreateTranslation(target.ViewCenter - center);
        foreach (var stroke in strokes) stroke.Transform(delta, scaleWidth: false);
        foreach (var image in images) image.Transform = BoardElementMath.Append(image.Transform, delta);
        target.Document.AddRange(strokes);
        foreach (var image in images) Services.Board.SetImagePresent(target, image, true);
        target.History.Push(new Core.History.DelegateAction("clone to board",
            () => { target.Document.AddRange(strokes); foreach (var image in images) Services.Board.SetImagePresent(target, image, true); },
            () => { target.Document.RemoveAll(strokes); foreach (var image in images) Services.Board.SetImagePresent(target, image, false); }));
        target.Tool = InkTool.Select;
        target.SelectAll();
    }

    internal async Task ExportDesktopSelectionAsync()
    {
        var strokes = Services.OverlayInk.Selection.Select(stroke => stroke.Clone()).ToList();
        var images = Services.OverlayInk.SelectedElements.OfType<BoardSelectionElement>()
            .Where(element => element.Image is not null).Select(element => element.Image.Copy(copyData: false)).ToList();
        var bounds = Services.OverlayInk.SelectionBounds;
        if (strokes.Count == 0 && images.Count == 0 || bounds.IsEmpty) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"选中墨迹 {DateTime.Now:yyyy-MM-dd HH-mm-ss}" };
            picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd((Window)_selectionToolbar ?? _bar));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            using var controller = new InkController();
            controller.Document.AddRange(strokes);
            var imageStore = new BoardService(controller);
            foreach (var image in images) imageStore.SetImagePresent(controller, image, true);
            controller.GetSelectableElements = imageStore.GetSelectionElements;
            controller.ViewTransform = System.Numerics.Matrix3x2.CreateTranslation(-bounds.Left + 5, -bounds.Top + 5);
            using var renderer = new Ink.Rendering.InkSceneRenderer { ShowSelectionOutline = false };
            await renderer.Images.PrepareAsync(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), controller);
            using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(),
                Math.Clamp(MathF.Ceiling(bounds.Width + 10), 1, 8192), Math.Clamp(MathF.Ceiling(bounds.Height + 10), 1, 8192), 96);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                renderer.DrawCommitted(drawing, controller);
            }
            await target.SaveAsync(file.Path, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"export selection: {ex}", Core.Diagnostics.LogType.Error);
            await NotifyAsync("存为图片", $"存为图片失败：{ex.Message}");
        }
    }

    private async Task SaveStrokesAsync()
    {
        if (DocumentActive) { await _documentWorkspace.SaveAsync(); return; }
        try
        {
            var target = _board is not null ? Services.Board : Services.ScreenImages;
            var owner = (Window)_board ?? _bar;
            bool entireBoard = _board is not null;
            string name = Core.Settings.ArchivePolicy.FileName(Services.Settings.Archive, DateTimeOffset.Now, entireBoard ? target.PageCount : 1, entireBoard ? "整板" : "批注");
            var library = Services.Modules.GetExport<IBoardLibraryService>();
            string path = library is not null ? await library.PickSaveAsync(name) : await ImageFilePicker.SaveArchiveAsync(WindowChrome.GetWorkArea(owner),
                Core.Settings.ArchivePolicy.ResolveDirectory(Services.Settings.Archive.DefaultDirectory), name);
            if (path is null || _disposed) return;
            await Services.Archives.SaveManualAsync(target, path, entireBoard);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"save strokes: {ex}", Core.Diagnostics.LogType.Error);
            if (!_disposed) await NotifyAsync("存档保存失败", ex.GetBaseException().Message);
        }
    }

    /// <summary>Opens the available document browser or the archive file picker.</summary>
    public void OpenStrokes() => _ = OpenStrokesAsync();

    private async Task OpenStrokesAsync()
    {
        try
        {
            if (Services.Modules.GetExport<IBoardLibraryService>() is { } library)
            {
                library.Open();
                return;
            }
            var owner = (Window)_board ?? _bar;
            var store = _board is not null ? Services.Board : Services.ScreenImages;
            var extensions = new[] { ".icink" }.Concat(Services.Plugins.ImportExtensions).ToList();
            if (Services.Modules.HasExport<IPdfWorkspaceService>()) extensions.Add(".pdf");
            string path = await ImageFilePicker.OpenArchiveAsync(WindowChrome.GetWorkArea(owner),
                Core.Settings.ArchivePolicy.ResolveDirectory(Services.Settings.Archive.DefaultDirectory), extensions.Distinct().ToArray());
            if (path is null || _disposed) return;
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) { await OpenPdfAsync(path); return; }
            var page = store.Current;
            bool entireBoard = ReferenceEquals(store, Services.Board);
            var apply = await Services.PrepareInkOpenAsync(path, store, entireBoard);
            if (_disposed || owner != ((Window)_board ?? _bar) || page != store.Current) return;
            if (entireBoard ? store.HasContent : store.Current.Document.Count > 0 || store.CurrentImages.Count > 0 || store.CurrentMedia.Count > 0)
            {
                if (!await YesOrNoWindow.AskAsync(owner, "打开将替换当前墨迹、图片和视频，是否继续？")) return;
            }
            if (_disposed || owner != ((Window)_board ?? _bar) || page != store.Current) return;
            apply();
            _bar?.Bar.RefreshSelection();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"open strokes: {ex}", Core.Diagnostics.LogType.Error);
            if (!_disposed) await NotifyAsync("文件打开失败", ex.GetBaseException().Message);
        }
    }

    public void OpenReplay()
    {
        if (_replay is not null) { WindowChrome.ShowWithoutActivation(_replay); return; }
        var controller = ActiveController;
        if (!controller.BeginPlayback()) return;
        bool restoreOverlay = _board is null && !DocumentActive && !Overlay.Presenter.InkVisible;
        if (_board is null && !DocumentActive) Overlay.Presenter.InkVisible = true;
        _replay = new ReplayWindow(controller, EffectiveTheme,
            _board is not null ? Services.Settings.Appearance.BlackboardScale : Services.Settings.Appearance.FloatingBarScale,
            _documentWorkspace?.Window ?? (Window)_board ?? _bar);
        NotifyWindowOpened(_replay);
        _replay.Closed += (_, _) => { _replay = null; if (restoreOverlay) Overlay.Presenter.InkVisible = false; };
        WindowChrome.ShowWithoutActivation(_replay);
        WindowChrome.EnsureAbove(_replay);
    }

    public void OpenGuide() => OpenSettingsPage("操作指南");

    public void OpenPenPalette(bool highlighter)
    {
        ActiveController.Tool = highlighter ? InkTool.Highlighter : InkTool.Pen;
        if (_board is not null)
        {
            _board.ShowPenPalette(); return;
        }
        // WPF MW_FloatBarIcons.cs:721-731: clicking the same pen button toggles the palette closed.
        var detachedPalette = _detachedPalettes.FirstOrDefault(window => window.IsHighlighter == highlighter);
        if (detachedPalette is not null) { WindowChrome.EnsureAbove(detachedPalette); return; }
        if (_palette?.IsPinned == true)
        {
            if (_paletteHighlighter == highlighter) { WindowChrome.EnsureAbove(_palette); return; }
            _detachedPalettes.Add(_palette);
            _palette = null;
        }
        if (_palette is not null && _paletteHighlighter == highlighter) { _palette.Close(); _palette = null; return; }
        if (_palette is not null) { _palette.Close(); _palette = null; }
        _paletteHighlighter = highlighter;
        var controller = ActiveController;
        _palette = new PenPaletteWindow(controller, highlighter, EffectiveTheme,
            _board is not null ? Services.Settings.Appearance.BlackboardScale : Services.Settings.Appearance.FloatingBarScale,
            Services.Settings.Canvas)
        {
            HighlighterAlpha = Services.Settings.Canvas.InkAlpha,
            LightColorScheme = Services.Settings.Appearance.Theme == 1,
            InkWidthChanged = width =>
            {
                if (highlighter) Services.Settings.Canvas.HighlighterWidth = width;
                else if (controller.CurrentAttributes.TaperKind == Core.Ink.TaperKind.InkWash) Services.Settings.Canvas.InkWashWidth = width;
                else Services.Settings.Canvas.InkWidth = width;
                controller.ConfigurePenWidths((float)Services.Settings.Canvas.InkWidth,
                    (float)(Services.Settings.Canvas.HighlighterWidth ?? Services.Settings.Canvas.InkWidth));
            },
            InkStyleChanged = style =>
            {
                Services.Settings.Canvas.InkStyle = style;
                Services.ApplySettings();
                Services.SaveSettings();
            },
            PenNibChanged = nib =>
            {
                Services.Settings.Canvas.PenNib = nib;
                Services.ApplySettings();
                Services.SaveSettings();
            },
            InkAlphaChanged = alpha =>
            {
                Services.Settings.Canvas.InkAlpha = alpha;
            },
            AttributesChanged = () => { Services.RememberToolOptions(controller); _bar?.Bar.RefreshSelection(); },
            ColorPaletteChanged = () => { Services.ApplySettings(); Services.SaveSettings(); },
            EditColorAsync = EditCustomColorAsync,
        };
        NotifyWindowOpened(_palette);
        ConfigurePaletteNibMode(_palette);
        var openedPalette = _palette;
        _palette.Closed += (_, _) =>
        {
            if (ReferenceEquals(_palette, openedPalette)) _palette = null;
            _detachedPalettes.Remove(openedPalette);
        };
        if (_board is not null || _bar is not null)
        {
            var tool = highlighter ? InkTool.Highlighter : InkTool.Pen;
            _palette.PlaceAboveBar(() => _board is not null ? _board.GetToolAnchor(tool) : _bar.GetToolAnchor(tool),
                WindowChrome.GetHwnd((Window)_board ?? _bar),
                () => _board is not null ? _board.GetToolbarBounds() : WindowChrome.GetScreenBounds(_bar, _bar.Bar));
        }
        WindowChrome.ShowWithoutActivation(_palette);
        WindowChrome.EnsureAbove(_palette);
    }

    public async Task EditCustomColorAsync(FrameworkElement anchor, InkColor color, Action<InkColor> changed)
    {
        if (_colorEditor is not null) { _colorEditor.Activate(); return; }
        var candidates = OpenWindows.Concat(_board?.ToolPanels.Cast<Window>() ?? Enumerable.Empty<Window>());
        var owner = candidates.FirstOrDefault(window => window.Content is FrameworkElement root && ReferenceEquals(root.XamlRoot, anchor.XamlRoot));
        if (owner is null) return;
        var window = new ColorEditorWindow(owner, WindowChrome.GetScreenBounds(owner, anchor), color, EffectiveTheme, changed);
        _colorEditor = window;
        try
        {
            NotifyWindowOpened(window);
            window.Activate();
            WindowChrome.EnsureAbove(window);
            await window.Completion;
        }
        finally { if (ReferenceEquals(_colorEditor, window)) _colorEditor = null; }
    }

    private void CloseDetachedPalettes()
    {
        foreach (var palette in _detachedPalettes.ToArray()) palette.Close();
    }

    public void OpenShapePicker()
    {
        if (_shapePicker is not null)
        {
            if (!_shapePicker.AutoHide) WindowChrome.EnsureAbove(_shapePicker);
            else _shapePicker.Close();
            return;
        }
        _shapePicker = new ShapePickerWindow(ActiveController,
            () => { Services.RememberToolOptions(ActiveController); _bar?.Bar.RefreshSelection(); }, EffectiveTheme,
            _board is not null ? Services.Settings.Appearance.BlackboardScale : Services.Settings.Appearance.FloatingBarScale,
            boardMode: _board is not null);
        NotifyWindowOpened(_shapePicker);
        var openedPicker = _shapePicker;
        _shapePicker.Closed += (_, _) => { if (ReferenceEquals(_shapePicker, openedPicker)) _shapePicker = null; };
        if (_board is not null || _bar is not null)
        {
            _shapePicker.PlaceAboveBar(() => _board is not null ? _board.GetToolAnchor(InkTool.Shape) : _bar.GetToolAnchor(InkTool.Shape),
                WindowChrome.GetHwnd((Window)_board ?? _bar),
                () => _board is not null ? _board.GetToolbarBounds() : WindowChrome.GetScreenBounds(_bar, _bar.Bar));
        }
        WindowChrome.ShowWithoutActivation(_shapePicker);
        WindowChrome.EnsureAbove(_shapePicker);
    }

    internal ScreenRect GetScreenshotBounds()
    {
        var workspace = _documentWorkspace?.Window ?? (Window)_board;
        return workspace is not null ? ScreenService.FromWindow(WindowChrome.GetHwnd(workspace)).Bounds : ScreenService.Virtual;
    }

    public async Task ScreenshotAsync() => await CaptureScreenshotToFileAsync();

    private async Task<string> CaptureScreenshotToFileAsync()
    {
        try
        {
            var screen = GetScreenshotBounds();
            string dir = Path.Combine(Services.Settings.Automation.AutoSavedStrokesLocation, "Screenshots");
            string path = await ScreenshotService.CaptureToFileAsync(dir, screen.X, screen.Y, screen.Width, screen.Height,
                Services.Settings.Automation.IsSaveScreenshotsInDateFolders);

            if (Services.Settings.Automation.IsAutoSaveStrokesAtScreenshot)
                await Services.AutoSave.SaveSnapshotAsync(ActiveController.Document.Strokes, "screenshot");
            return path;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"Screenshot: {ex.Message}", Core.Diagnostics.LogType.Error);
            return null;
        }
    }

    public bool ClearCanvas()
    {
        var controller = ActiveController;
        if (Services.Settings.Archive.ScreenCapture.SaveOnClear) _ = SaveScreenArchiveAsync("清屏前存档");
        var automation = Services.Settings.Automation;
        if (automation.IsAutoSaveStrokesAtClear && controller.Document.Count > automation.MinimumAutomationStrokeNumber)
        {
            _ = Services.AutoSave.SaveSnapshotAsync(controller.Document.Strokes, "clear");
            _ = ScreenshotAsync();
        }
        if (DocumentActive) controller.Clear();
        else (_board is not null ? Services.Board : Services.ScreenImages).ClearCurrentPage();
        return true;
    }

    public void ToggleFold()
    {
        if (_bar is null) return;
        bool fold = !_bar.IsFolded;
        _manualFoldOverride = fold;
        if (fold)
        {
            // WPF FoldFloatingBar_Click invokes CursorIcon_Click before hiding, in desktop and PPT modes.
            if (!Overlay.ClickThrough) ToggleClickThrough();
            _bar.SetFolded(true);
        }
        else
        {
            _bar.SetFolded(false);
        }
    }

    public void ToggleClickThrough()
    {
        if (DocumentActive) { ActiveController.Tool = ActiveController.Tool == InkTool.Pan ? InkTool.Pen : InkTool.Pan; return; }
        if (_board is not null)
        {
            _ = CloseBoardToMouseModeAsync();
            return;
        }
        bool enableMouseMode = !Overlay.ClickThrough;
        if (enableMouseMode)
        {
            OptionPopupCoordinator.DismissUnpinned();
            SaveBeforeMouseMode();
            bool showInk = _lastSlideShowState
                ? !Services.Settings.PowerPoint.HideStrokesAfterLeavingAnnotation
                : !Services.Settings.Canvas.HideStrokeWhenSelecting;
            Overlay.SetInteractionMode(clickThrough: true, inkVisible: showInk);
            if (Core.Settings.AutomationPolicy.ShouldClearOnMouseMode(Services.Settings.Automation, Services.Settings.PowerPoint, _lastSlideShowState))
                Services.ScreenImages.ClearCurrentPage();
        }
        else
        {
            // CursorIcon_Click in the WPF source restores drawing without clearing strokes.
            Services.Modules.Invoke<IScreenTrackingService>(tracking => tracking.Refresh());
            Overlay.SetInteractionMode(clickThrough: false, inkVisible: true);
        }

        if (_bar is not null)
        {
            _bar.Bar.ClickThroughActive = Overlay.ClickThrough;
            _bar.Bar.RefreshSelection();
        }
        QueueDesktopSelection();
    }

    private void SaveBeforeMouseMode()
    {
        if (Services.Settings.Archive.ScreenCapture.SaveOnMouseMode) _ = SaveScreenArchiveAsync("切换鼠标模式");
        var strokes = ActiveController.Document.Strokes;
        if (strokes.Count <= Services.Settings.Automation.MinimumAutomationStrokeNumber) return;
        if (_lastSlideShowState) _pptSession.RequestScreenshot();
        else _ = ScreenshotAsync();
        _ = Services.AutoSave.SaveSnapshotAsync(strokes, _lastSlideShowState ? "ppt-mouse-mode" : "mouse-mode");
    }

    /// <summary>Launch in pass-through mode so the overlay does not capture the desktop's input.</summary>
    public void StartInClickThrough()
    {
        if (Overlay.ClickThrough) return;
        ToggleClickThrough();
    }

    public void PptNext() => NavigatePpt(next: true);

    public void PptPrevious() => NavigatePpt(next: false);

    private void NavigatePpt(bool next)
    {
        long now = Environment.TickCount64;
        if (!_pptGuard.TryEnter(now)) return;
        try
        {
            // The generation guards the screenshot request raised synchronously by the session.
            int generation = _pptGate.NextGeneration();
            _pptCaptureSession = _pptGate.Session;
            _pptCaptureGeneration = generation;

            if (_board is not null) ToggleBoard();
            if (_lastSlideShowState) _ = SaveScreenArchiveAsync("PPT 翻页前存档");
            bool moved = NavigateThroughSources(next);
            QueuePresentationRefresh();

            // The host receives its own simulated navigation back as a slide change; remember
            // that it was ours so the poll loop does not treat it as an external event.
            if (moved) _pptGuard.SuppressExternal(now);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"PPT navigation: {ex}", Core.Diagnostics.LogType.Error);
        }
        finally
        {
            _pptGuard.Exit(now);
        }
    }

    /// <summary>
    /// COM → full-screen window → raw keys. Outside a slide show only COM is attempted, which
    /// preserves the previous behaviour and avoids injecting Page Down into unrelated apps.
    /// Each failure is recorded once per show via <see cref="PresentationSourceSelector"/>.
    /// </summary>
    private bool NavigateThroughSources(bool next)
    {
        PresentationSourceLevel level = _pptSources.Active;
        while (level != PresentationSourceLevel.None)
        {
            bool moved;
            switch (level)
            {
                case PresentationSourceLevel.Com:
                    moved = next ? _pptComSource.Next() : _pptComSource.Previous();
                    if (moved) return true;
                    if (!_lastSlideShowState) return false;
                    DegradePptSource(level);
                    break;
                case PresentationSourceLevel.Window:
                    if (!_pptWindowSource.IsAvailable)
                    {
                        DegradePptSource(level);
                        break;
                    }
                    moved = next ? _pptWindowSource.Next() : _pptWindowSource.Previous();
                    if (moved) return true;
                    DegradePptSource(level);
                    break;
                case PresentationSourceLevel.Keys:
                    moved = next ? _pptKeySource.Next() : _pptKeySource.Previous();
                    if (moved) return true;
                    DegradePptSource(level);
                    return false;
                default:
                    return false;
            }
            level = _pptSources.Active;
        }
        return false;
    }

    private void DegradePptSource(PresentationSourceLevel failed)
    {
        PresentationSourceLevel previous = _pptSources.Active;
        _pptSources.ReportFailure(failed);
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"PPT 翻页降级：{previous} → {_pptSources.Active}（{_pptSources.LastReason}）",
            Core.Diagnostics.LogType.Info);
    }

    private void HandlePresentationKey(int virtualKey)
    {
        if (!_lastSlideShowState) return;
        switch (PresentationKeyMap.Map(virtualKey))
        {
            case PresentationKeyAction.Next:
                PptNext();
                break;
            case PresentationKeyAction.Previous:
                PptPrevious();
                break;
            case PresentationKeyAction.ToggleBlackScreen:
                ToggleBlackScreen();
                break;
        }
    }

    public void ToggleBlackScreen()
    {
        if (_blackScreen is not null)
        {
            CloseBlackScreen();
            return;
        }
        _blackScreen = new BlackScreenWindow();
        _blackScreen.Closed += (_, _) =>
        {
            _blackScreen = null;
            RefreshShortcutRegistration();
        };
        _blackScreen.Activate();
        // The window can show without owning focus, so cover that case with a global Esc that
        // only exists while the black screen is up. The window also handles Esc itself.
        RefreshShortcutRegistration();
        Core.Diagnostics.FileLog.WriteLogToFile("黑屏已开启（F9/B 或点击/Esc 恢复）", Core.Diagnostics.LogType.Info);
    }

    private void CloseBlackScreen()
    {
        var window = _blackScreen;
        _blackScreen = null;
        window?.Close();
        RefreshShortcutRegistration();
    }

    public void RotatePptNavigationCorner()
    {
        // Kept for shell action compatibility. All enabled corners are displayed together.
        UpdatePptNavigation(_pptShowSnapshot ?? PptSnapshot.Detached);
    }

    public void CollapsePptNavigation()
    {
        _pptNavCollapsed = !_pptNavCollapsed;
        var snapshot = _pptShowSnapshot;
        UpdatePptNavigation(snapshot ?? PptSnapshot.Detached);
    }

    public void TogglePptNavigationWindow()
    {
        if (_manualPptNav)
        {
            _manualPptNav = false;
            ClosePptNavigationWindows();
            _pptNavCollapsed = false;
            return;
        }

        _manualPptNav = true;
        _pptNavCollapsed = false;
        UpdatePptNavigation(_pptSession.Snapshot ?? PptSnapshot.Detached);
    }

    // ---- 课件页导航（批注/收藏索引） ------------------------------------

    public void ToggleSlideNavigator()
    {
        _slideNav ??= CreateSlideNavigator();
        _slideNav.Toggle();
        if (_slideNav.IsVisible) RefreshSlideIndex(_pptSession.Snapshot);
    }

    public void PptGotoSlide(int oneBasedIndex)
    {
        if (oneBasedIndex <= 0) return;
        if (_pptSession.GoToSlide(oneBasedIndex)) return;
        _ = NotifyAsync("无法跳页", "当前放映源不支持直接跳页，请用上一页/下一页；PowerPoint 放映时可正常跳转。");
    }

    private SlideNavigatorWindow CreateSlideNavigator()
    {
        var window = new SlideNavigatorWindow(Services.Settings.Appearance.Theme);
        window.SlideActivated += index => PptGotoSlide(index);
        window.FavoriteToggled += OnSlideFavoriteToggled;
        window.AiReviewRequested += OnSlideAiReviewRequested;
        return window;
    }

    private void OnSlideFavoriteToggled(int slideId)
    {
        if (_slideIndex is null || slideId <= 0) return;
        if (!_slideIndex.ToggleFavorite(slideId)) return;

        SaveSlideIndex();
        _slideNav?.Update(_slideIndex, _pptSession.Snapshot?.Slide?.SlideId ?? 0);
    }

    /// <summary>翻到有 AI 讲解的页时，从页索引取出已保存讲解并重新显示在浮窗里。</summary>
    private void OnSlideAiReviewRequested(int slideId)
    {
        var entry = _slideIndex?.Find(slideId);
        if (entry is not null) Assistant?.Review(slideId, entry.Index, entry.AnimationStep, entry.Text);
    }

    /// <summary>Keep the current slide current, reading a full inventory only while the legacy index is visible.</summary>
    private void RefreshSlideIndex(PptSnapshot snapshot)
    {
        if (snapshot?.IsSlideShowRunning != true || snapshot.Presentation is null) return;

        if (_slideIndex?.Identity != snapshot.Presentation.Identity)
        {
            _slideIndex = LoadSlideIndexSidecar(snapshot.Presentation) ?? new SlideContentIndex();
            _slideIndex.Identity = snapshot.Presentation.Identity;
            _slideIndex.Name = snapshot.Presentation.Name;
        }
        int currentSlideId = snapshot.Slide?.SlideId ?? 0;
        int animationStep = Services.Ppt.GetAnimationStep();
        if (snapshot.Slide is { } activeSlide)
        {
            var active = _slideIndex.Find(activeSlide.SlideId);
            if (active is null)
            {
                active = new SlideContentEntry { SlideId = activeSlide.SlideId, Index = activeSlide.Index, Name = activeSlide.Name };
                _slideIndex.Slides.Add(active);
            }
            active.Index = activeSlide.Index;
            active.HasInk = Services.OverlayInk.Document.Count > 0;
            active.InkStrokeCount = Services.OverlayInk.Document.Count;
            active.AnimationStep = animationStep;
        }
        if (_slideNav?.IsVisible != true) return;

        DateTime now = DateTime.UtcNow;
        if (now - _lastSlideIndexRefreshUtc < TimeSpan.FromSeconds(2)) return;
        _lastSlideIndexRefreshUtc = now;

        try
        {
            PptPresentationDetails details = Services.Ppt.GetPresentationDetails();
            if (details?.Presentation is null) return;

            SlideContentIndex sidecar = LoadSlideIndexSidecar(details.Presentation);
            SlideContentIndex previous = sidecar ?? _slideIndex;
            _slideIndex = SlideContentIndex.Build(
                details, _pptSession.CreateIndexArchive(), previous,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string directory = _pptStore.GetPresentationDirectory(details.Presentation);
            foreach (SlideContentEntry entry in _slideIndex.Slides)
            {
                if (entry.SlideId <= 0 || !string.IsNullOrEmpty(entry.ThumbnailPath)) continue;
                string thumb = Path.Combine(directory, "thumb-" + entry.SlideId + ".png");
                if (File.Exists(thumb)) entry.ThumbnailPath = thumb;
            }

            SlideContentEntry current = _slideIndex.Find(currentSlideId);
            if (current is not null && string.IsNullOrEmpty(current.Text))
            {
                string text = SafeSlideText(currentSlideId);
                if (!string.IsNullOrWhiteSpace(text)) current.Text = text;
            }

            if (current is not null) current.AnimationStep = animationStep;

            _slideNav?.Update(_slideIndex, currentSlideId);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课件页索引刷新失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    private string SlideIndexPath(PptPresentationInfo presentation)
        => Path.Combine(_pptStore.GetPresentationDirectory(presentation), "SlideIndex.json");

    private SlideContentIndex LoadSlideIndexSidecar(PptPresentationInfo presentation)
    {
        try
        {
            string path = SlideIndexPath(presentation);
            return File.Exists(path) ? SlideContentIndex.Deserialize(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>无放映时读取最近一次课件的 SlideIndex.json，并把缺失缩略图按同目录 thumb-{slideId}.png 回填。</summary>
    private void SaveSlideIndex()
    {
        PptPresentationInfo presentation = _pptSession.Snapshot?.Presentation;
        if (_slideIndex is null || presentation is null) return;

        try
        {
            string path = SlideIndexPath(presentation);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _slideIndex.Serialize());
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课件页索引保存失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>把当页截图另存为固定缩略图（thumb-{slideId}.png），供课件页导航复用。</summary>
    private void SaveSlideNavigatorThumbnail(
        PptPresentationInfo presentation, int slideId, byte[] pixels, int width, int height)
    {
        if (presentation is null || slideId <= 0) return;

        try
        {
            string directory = _pptStore.GetPresentationDirectory(presentation);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "thumb-" + slideId + ".png");
            if (!File.Exists(path)) _ = ScreenshotService.SavePngAsync(path, pixels, width, height);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课件页缩略图保存失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }

    private void OnPptInkTransition(PptInkTransition transition)
    {
        if (transition is null) return;

        // A slide change invalidates any selection made on the previous slide.
        if (transition.Kind != PptInkTransitionKind.Slide) return;
        Services.OverlayInk.Selection.Clear();
        _bar?.Bar.RefreshSelection();
    }

    private void RememberPptScreenshot(string path, int slideIndex, int slideId)
    {
        _pptScreenshotPath = path;
        _pptScreenshotSlideIndex = slideIndex;
        _pptScreenshotSlideId = slideId;
    }

    public void PptExit()
    {
        _pptSession.ExitSlideShow();
        QueuePresentationRefresh();
        // MW_PPT.cs:685 HideSubPanels("cursor") after View.Exit.
        _bar?.ClosePanels();
        _palette?.Close();
        _shapePicker?.Close();
    }

    public void PptOpenNavigation()
    {
        if (!_lastSlideShowState) return;
        var window = _pptNavigations.GetValueOrDefault(PptNavigationPlacement.BottomLeft)
            ?? _pptNavigations.Values.FirstOrDefault();
        window?.SetPreviewOpen(!window.PreviewOpen);
    }

    private string SafeSlideText(int slideId)
    {
        if (slideId <= 0) return "";
        try
        {
            return Services.Ppt.GetSlideText(slideId) ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }


    private string CurrentPresentationName()
    {
        try
        {
            return _pptSession.Snapshot?.Presentation?.Name ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }


    private double CurrentDpi()
    {
        double dpi = Overlay.Window.DpiScale;
        return dpi > 0 ? dpi : 1.0;
    }


    public void Exit() => _ = ExitAsync(restart: false);
    public void Restart() => _ = ExitAsync(restart: true);

    private async Task ExitAsync(bool restart)
    {
        if (_exiting || _exitPromptOpen) return;
        _exitPromptOpen = true;
        try
        {
            if (Services.Settings.Advanced.IsSecondConfimeWhenShutdownApp && !_installingUpdate
                && !await ConfirmAsync(restart ? "重启 Ink Canvas" : "退出 Ink Canvas", restart ? "确认保存设置并重启 Ink Canvas？" : "确认关闭 Ink Canvas？")) return;
            if (Services.Settings.Archive.ScreenCapture.SaveOnExit)
            {
                await SaveScreenArchiveAsync(restart ? "重启前屏幕存档" : "退出前屏幕存档");
                await WaitForScreenArchivesAsync();
                if (Services.ScreenArchives.LastError is { } captureError && Services.ScreenArchives.IsEnabled(_lastSlideShowState)
                    && Services.OverlayInk.Document.Count > 0 && _board is null)
                { await NotifyAsync("截图白板存档失败", captureError); return; }
            }
            if (_documentWorkspace is not null && !await _documentWorkspace.CloseAsync()) return;
            if (!await SaveResumeAsync(force: true))
            {
                await NotifyAsync("续接进度保存失败", "请检查用户数据目录的可用空间和写入权限后重试。");
                return;
            }
            if (Services.Settings.Archive.SaveOnExit)
            {
                try
                {
                    if (_board is not null) await _board.SaveArchiveBeforeCloseAsync(restart ? "重启程序" : "退出程序");
                    else await Services.Archives.SaveAutomaticAsync(restart ? "重启程序" : "退出程序");
                }
                catch (Exception ex)
                {
                    await NotifyAsync("白板存档失败", "白板内容已保留，请检查保存设置后再退出。\n" + ex.GetBaseException().Message);
                    return;
                }
            }
            if (restart)
            {
                Services.SaveSettings();
                try
                {
                    if (Services.ExtensionInstalls.Pending.Count > 0) { using var helper = Features.Update.UpdateService.LaunchRestart(); }
                    else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath, "-m") { UseShellExecute = true });
                }
                catch (Exception ex) { await NotifyAsync("重启失败", ex.Message); return; }
            }
            _exiting = true;
            _pollTimer.Stop();

            if (Services.Settings.Automation.IsAutoSaveStrokesAtClear)
                await Services.AutoSave.SaveSnapshotAsync(Services.OverlayInk.Document.Strokes, "exit");
            await Services.AutoSave.WaitForPendingAsync();
            await Services.Archives.WaitForPendingAsync();
            await WaitForScreenArchivesAsync();
            Services.SaveSettings();

            Dispose();
            Application.Current.Exit();
        }
        finally { _exitPromptOpen = false; }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _palette?.FlushSliderChanges();
        foreach (var palette in _detachedPalettes.ToArray()) palette.FlushSliderChanges();
        _resumeTimer?.Stop();
        foreach (var reminder in _resumeReminders.ToArray()) reminder.Close();
        _pptPreviewSource?.Dispose();
        _board?.CloseForShutdown();
        _cachedBoard?.CloseForShutdown();
        _cachedBoard = null;
        _documentAi?.Dispose();
        _colorEditor?.Close();
        _spotlight?.Close();
        _magnifier?.Close();
        _slideNav?.Close();
        CloseBlackScreen();
        _pptSession.InkTransition -= OnPptInkTransition;
        StopShortcutTracking();
        Overlay.InteractionChanged -= RefreshShortcutRegistration;
        StopDocumentForegroundTracking();
        foreach (var notice in _notices.ToArray()) notice.Close();
        _updateWindow?.Close();
        foreach (var palette in _detachedPalettes.ToArray()) palette.Close();
        _imageImportCancellation?.Cancel();
        _videoImportCancellation?.Cancel();
        Services.Plugins.Changed -= OnExtensionCommandsChanged;
        Services.Modules.Changed -= OnExtensionCommandsChanged;
        Services.SettingsApplied -= ApplyDocumentSettings;
        Services.SettingsApplied -= RefreshIconFonts;
        Services.SettingsApplied -= ApplyArchivePaths;
        Services.SettingsApplied -= ApplyDisplaySettings;
        Services.SettingsApplied -= RefreshPaletteNibModes;
        Services.Plugins.Dispose();
        Services.Modules.Dispose();
        Services.OverlayInk.Invalidated -= QueueDesktopSelection;
        Services.OverlayInk.Invalidated -= QueueDesktopMedia;
        Services.ScreenImages.ImagesChanged -= QueueDesktopMedia;
        Services.ScreenImages.PageChanged -= QueueDesktopMedia;
        Services.ScreenImages.MediaPlacementChanged -= OnDesktopMediaPlacement;
        foreach (var video in _desktopVideos.Values) video.Close();
        _desktopVideos.Clear();
        Services.OverlayInk.SelectionChanged -= QueueDesktopSelection;
        if (_bar is not null) _bar.AppWindow.Changed -= OnBarWindowChanged;
        if (_bar is not null) _bar.Bar.LayoutChanged -= OnBarLayoutChanged;
        _selectionToolbar?.Close();
        _selectionToolbar = null;
        _replay?.Close();
        _pollTimer.Stop();
        _countdown?.Dispose();
        ClosePptNavigationWindows();
        _pptSession.Dispose();
        Services.Ppt.RefreshRequested -= QueuePresentationRefresh;
        Services.Ppt.Dispose();
        Updater.Dispose();
        Overlay.Window.DisplaySettingsChanged -= QueueDisplayRefresh;
        Overlay.Dispose();
        Services.Dispose();
    }
}
