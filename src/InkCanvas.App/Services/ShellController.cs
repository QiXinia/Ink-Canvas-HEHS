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

namespace InkCanvas.App.Services;

/// <summary>
/// Owns the windows and implements the shell actions the floating bar invokes. Keeps App.xaml.cs
/// down to startup wiring.
/// </summary>
public sealed partial class ShellController : IShellActions, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly PptInkSession _pptSession;
    private FloatingBarWindow _bar;
    private SelectionToolbarWindow _selectionToolbar;
    private bool _selectionToolbarCollapsed;
    private BoardWindow _board;
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
    private InfoWindow _guide;
    private readonly List<NoticeWindow> _notices = new();
    private PenPaletteWindow _palette;
    private readonly List<PenPaletteWindow> _detachedPalettes = new();
    private ShapePickerWindow _shapePicker;
    private PptNavigationWindow _pptBottomLeft, _pptBottomRight, _pptSideLeft, _pptSideRight;
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
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Overlay = new OverlayHost(services.OverlayInk);
        Overlay.ConfigureDisplays(services.Settings.Display);
        services.SettingsApplied += ApplyDisplaySettings;
        Overlay.Window.DisplaySettingsChanged += QueueDisplayRefresh;
        _screens = ScreenService.All();
        services.StartModules(new ScreenTrackingHost(services, Overlay, () => _board is not null || DocumentActive), new DocumentHost(this), new CanvasToolsHost(this));
        _pptSession = new PptInkSession(services.Ppt,
            new PptInkFileStore(services.Settings.Automation.AutoSavedStrokesLocation))
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
        _pptSession.Failed += failure => Core.Diagnostics.FileLog.WriteLogToFile(
            $"PPT/{failure.Operation}: {failure.Message} (0x{failure.ErrorCode:X8})", Core.Diagnostics.LogType.Error);
        if (services.Settings.PowerPoint.PowerPointSupport) _pptSession.Poll();
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
        if (w is SettingsWindow or CountdownWindow or LotteryWindow or InfoWindow)
        {
            var owner = _documentWorkspace?.Window ?? (Window)_board ?? _bar;
            if (owner is not null) WindowChrome.CenterOnOwner(w, owner);
        }
        WindowOpened?.Invoke(w);
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

    private void RefreshDisplays()
    {
        _lastDisplayCheck = Environment.TickCount64;
        var screens = ScreenService.All();
        if (_screens.SequenceEqual(screens)) return;
        try
        {
            Overlay.RefreshDisplayBounds();
            Overlay.ConfigureDisplays(Services.Settings.Display);
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
            _screens = screens;
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
            if (_selectionToolbar is not null) yield return _selectionToolbar;
            if (_board is not null) yield return _board;
            if (_documentWorkspace is not null) yield return _documentWorkspace.Window;
            foreach (var window in _documentWindows) if (!ReferenceEquals(window, _documentWorkspace?.Window)) yield return window;
            if (_settings is not null) yield return _settings;
            if (_countdown is not null) yield return _countdown;
            if (_lottery is not null) yield return _lottery;
            if (_replay is not null) yield return _replay;
            if (_guide is not null) yield return _guide;
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
            pptNext: () => { if (_lastSlideShowState) PptNext(); },
            pptPrevious: () => { if (_lastSlideShowState) PptPrevious(); },
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
            });

        _pollTimer.Start();

        // Delete-selection changes the button count; FloatingBar raises LayoutChanged for this.
        Services.OverlayInk.SlideFlipRequested += next => { if (next) PptNext(); else PptPrevious(); };
        Services.OverlayInk.ToolChanged += OnControllerToolChanged;

        Core.Diagnostics.FileLog.NewLog($"Ink Canvas 启动，版本 {Updater.CurrentVersion}");

        ApplyEdgeGestureSetting();

        if (Services.Settings.Startup.IsAutoUpdate)
            _ = Updater.CheckAsync(interactive: false);
    }

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
            && !ink.IsMarqueeSelecting && !ink.IsRotatingSelection)
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
        if (notice.PreviousSlideIndex is > 0
            && await YesOrNoWindow.AskAsync(owner, $"上次播放到了第 {notice.PreviousSlideIndex} 页, 是否立即跳转"))
            _pptSession.GoToSlide(notice.PreviousSlideIndex.Value);

        if (notice.OfferRestoreHiddenSlides
            && await YesOrNoWindow.AskAsync(owner, "检测到此演示文档中包含隐藏的幻灯片，是否取消隐藏？"))
            _pptSession.RestoreHiddenSlides();

        if (notice.OfferDisableTimings
            && await YesOrNoWindow.AskAsync(owner, "检测到此演示文档中自动播放或排练计时已经启用，可能导致幻灯片自动翻页，是否取消？"))
            _pptSession.DisableSlideTimings();

        if (notice.LegacyInkFilesDetected)
            await NotifyAsync("发现旧版墨迹存档", "发现 .icstk/.icart 存档。该 WPF ISF 格式无法在 WinUI 3 直接读取；当前仅保留文件，不会覆盖或删除。");
    }

    private void OnPptScreenshotRequested(PptScreenshotRequest request)
    {
        // Capture screen pixels synchronously before the COM navigation mutates the slideshow.
        try
        {
            var area = ScreenService.FromWindow(request.Snapshot.WindowHandle).Bounds;
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
            _ = ScreenshotService.SavePngAsync(path, pixels, width, height);
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
                var current = _pptSession.Poll();
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
        if (_guide is not null) WindowChrome.EnsureAbove(_guide, Overlay.Window.Handle);
        if (_updateWindow is not null) WindowChrome.EnsureAbove(_updateWindow, Overlay.Window.Handle);
        foreach (var notice in _notices) WindowChrome.EnsureAbove(notice, Overlay.Window.Handle);
        if (_pptBottomLeft is not null) WindowChrome.EnsureAbove(_pptBottomLeft, Overlay.Window.Handle);
        if (_pptBottomRight is not null) WindowChrome.EnsureAbove(_pptBottomRight, Overlay.Window.Handle);
        if (_pptSideLeft is not null) WindowChrome.EnsureAbove(_pptSideLeft, Overlay.Window.Handle);
        if (_pptSideRight is not null) WindowChrome.EnsureAbove(_pptSideRight, Overlay.Window.Handle);
        if (_palette is not null) WindowChrome.EnsureAbove(_palette, Overlay.Window.Handle);
        foreach (var palette in _detachedPalettes) WindowChrome.EnsureAbove(palette, Overlay.Window.Handle);
        if (_shapePicker is not null) WindowChrome.EnsureAbove(_shapePicker, Overlay.Window.Handle);
        if (_board is not null)
            foreach (var panel in _board.ToolPanels) WindowChrome.EnsureAbove(panel, Overlay.Window.Handle);
        foreach (var window in _documentWindows) if (!ReferenceEquals(window, _documentWorkspace?.Window) && window.AppWindow.IsVisible) WindowChrome.EnsureAbove(window, Overlay.Window.Handle);
        UpdateSettingsDrawingOrder();
    }

    private void UpdatePptNavigation(PptSnapshot snapshot)
    {
        bool inShow = snapshot?.IsSlideShowRunning == true;
        var s = Services.Settings.PowerPoint;
        // WPF hides all four panels on fold and on board entry; per-poll re-evaluation
        // re-shows them on unfold/board-exit during a show.
        if (!inShow || (_bar is not null && _bar.IsFolded) || _board is not null)
        {
            ClosePptNavigationWindows();
            return;
        }

        if (s.IsShowBottomPPTNavigationPanel)
        {
            _pptBottomLeft ??= CreatePptNavigation(PptNavigationPlacement.BottomLeft);
            _pptBottomRight ??= CreatePptNavigation(PptNavigationPlacement.BottomRight);
        }
        if (s.IsShowSidePPTNavigationPanel)
        {
            _pptSideLeft ??= CreatePptNavigation(PptNavigationPlacement.SideLeft);
            _pptSideRight ??= CreatePptNavigation(PptNavigationPlacement.SideRight);
        }

        _pptBottomLeft?.Update(snapshot, s.IsShowBottomPPTNavigationPanel, s.IsShowPPTNavigationBottom);
        _pptBottomRight?.Update(snapshot, s.IsShowBottomPPTNavigationPanel, s.IsShowPPTNavigationBottom);
        _pptSideLeft?.Update(snapshot, s.IsShowSidePPTNavigationPanel, s.IsShowPPTNavigationSides);
        _pptSideRight?.Update(snapshot, s.IsShowSidePPTNavigationPanel, s.IsShowPPTNavigationSides);
    }

    private PptNavigationWindow CreatePptNavigation(PptNavigationPlacement placement)
    {
        var window = new PptNavigationWindow(placement, this, Services.Settings.Appearance.Theme);
        return window;
    }

    private void ClosePptNavigationWindows()
    {
        _pptBottomLeft?.Close(); _pptBottomLeft = null;
        _pptBottomRight?.Close(); _pptBottomRight = null;
        _pptSideLeft?.Close(); _pptSideLeft = null;
        _pptSideRight?.Close(); _pptSideRight = null;
    }

    private void ApplyAutoFold()
    {
        if (_bar is null) return;
        var foreground = ForegroundWindowService.DescribeForeground();
        var a = Services.Settings.Automation;
        bool fold = Core.Settings.AutomationPolicy.ShouldFold(a, foreground.Process, foreground.Title, foreground.Height, _lastSlideShowState);

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
        CloseDetachedPalettes();
        _imageImportCancellation?.Cancel();
        _selectionToolbar?.Hide();
        _replay?.Close();
        if (_board is null)
        {
            Services.BoardModeActive = true;
            Services.ApplySettings();
            _documentWorkspace?.Window.AppWindow.Hide();
            _palette?.Close(); _shapePicker?.Close();
            _board = new BoardWindow(Services, this, _bar);
            NotifyWindowOpened(_board);
            _board.CanvasInputBegan += OnBoardInputBegan;
            _board.CurrentPageChanged += OnBoardPageChanged;
            BindBoardController(_board.CurrentController);
            BindToolbar(_board.CurrentController);
            var boardWindow = _board;
            _board.Closed += (_, _) => OnBoardClosed(boardWindow);
            _board.Activate();
            // The board is opaque and full-screen; the screen overlay would draw over it.
            Overlay.Hide();
            // WPF slides the floating bar off-edge in board mode; hide it here (handles stay).
            if (_bar is not null && !_bar.IsFolded) _bar.AppWindow.Hide();
        }
        else
        {
            _board.Close();
        }
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
    }

    private void OnBoardInputBegan()
    {
        if (_board is not null) ImageFilePicker.EnsureAbove(WindowChrome.GetHwnd(_board));
        _palette?.DismissOnOverlayInput();
        _shapePicker?.DismissOnOverlayInput();
    }

    private void OnBoardClosed(BoardWindow board)
    {
        if (!ReferenceEquals(_board, board)) return;
        _board = null;
        Services.BoardModeActive = false;
        Services.ApplySettings();
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
            _settings.Activate(); return;
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
                window.Activate();
            }
            finally { _settingsOpening = false; }
        });
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

    public void OpenTimer()
    {
        if (_countdown is not null) { _countdown.Activate(); return; }
        _countdown = new CountdownWindow(EffectiveTheme);
        NotifyWindowOpened(_countdown);
        WindowChrome.ApplyTheme(_countdown, EffectiveTheme);
        _countdown.Closed += (_, _) => _countdown = null;
        _countdown.Activate();
        WindowChrome.EnsureAbove(_countdown);
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
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"墨迹 {DateTime.Now:yyyy-MM-dd HH-mm-ss}",
            };
            picker.FileTypeChoices.Add("Ink Canvas 墨迹", new List<string> { ".icink" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd((Window)_board ?? _bar));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            (_board is not null ? Services.Board : Services.ScreenImages).SavePage(file.Path);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"save strokes: {ex}", Core.Diagnostics.LogType.Error);
        }
    }

    /// <summary>WPF 打开墨迹: replace the active surface's strokes from a .icink file (confirmed).</summary>
    public void OpenStrokes() => _ = OpenStrokesAsync();

    private async Task OpenStrokesAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".icink");
            if (Services.Modules.HasExport<IPdfWorkspaceService>()) picker.FileTypeFilter.Add(".pdf");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd((Window)_board ?? _bar));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            if (Path.GetExtension(file.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) { await OpenPdfAsync(file.Path); return; }
            var owner = (Window)_board ?? _bar;
            var store = _board is not null ? Services.Board : Services.ScreenImages;
            if (store.Current.Document.Count > 0 || store.CurrentImages.Count > 0 || store.CurrentMedia.Count > 0)
            {
                if (!await YesOrNoWindow.AskAsync(owner, "打开将替换当前墨迹和图片，是否继续？")) return;
            }
            store.LoadPage(file.Path);
            _bar?.Bar.RefreshSelection();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile($"open strokes: {ex}", Core.Diagnostics.LogType.Error);
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

    public void OpenGuide()
    {
        if (_guide is not null) { _guide.Activate(); return; }
        _guide = InfoWindow.OpenGuide(EffectiveTheme);
        NotifyWindowOpened(_guide);
        _guide.Closed += (_, _) => _guide = null;
        _guide.Activate();
        WindowChrome.EnsureAbove(_guide);
    }

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
        if (_palette?.IsDetached == true)
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
            _board is not null ? Services.Settings.Appearance.BlackboardScale : Services.Settings.Appearance.FloatingBarScale)
        {
            HighlighterAlpha = Services.Settings.Canvas.InkAlpha,
            LightColorScheme = Services.Settings.Appearance.Theme == 1,
            InkWidthChanged = width =>
            {
                if (highlighter) Services.Settings.Canvas.HighlighterWidth = width;
                else Services.Settings.Canvas.InkWidth = width;
                Services.ApplySettings();
                Services.SaveSettings();
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
                Services.ApplySettings();
                Services.SaveSettings();
            },
            AttributesChanged = () => { Services.RememberToolOptions(controller); _bar?.Bar.RefreshSelection(); },
        };
        NotifyWindowOpened(_palette);
        _palette.ConfigureNibMode(Services.Settings.Appearance.IsEnableDisPlayNibModeToggler, Services.Settings.Startup.IsEnableNibMode,
            value => { Services.Settings.Startup.IsEnableNibMode = value; Services.ApplySettings(); Services.SaveSettings(); });
        var openedPalette = _palette;
        _palette.Closed += (_, _) =>
        {
            if (ReferenceEquals(_palette, openedPalette)) _palette = null;
            _detachedPalettes.Remove(openedPalette);
        };
        WindowChrome.ShowWithoutActivation(_palette);
        if (_board is not null || _bar is not null)
        {
            var tool = highlighter ? InkTool.Highlighter : InkTool.Pen;
            _palette.PlaceAboveBar(() => _board is not null ? _board.GetToolAnchor(tool) : _bar.GetToolAnchor(tool),
                WindowChrome.GetHwnd((Window)_board ?? _bar));
        }
        WindowChrome.EnsureAbove(_palette);
    }

    private void CloseDetachedPalettes()
    {
        foreach (var palette in _detachedPalettes.ToArray()) palette.Close();
    }

    public void OpenShapePicker()
    {
        if (_shapePicker is not null)
        {
            if (_shapePicker.IsDetached) WindowChrome.EnsureAbove(_shapePicker);
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
        WindowChrome.ShowWithoutActivation(_shapePicker);
        if (_board is not null || _bar is not null)
        {
            _shapePicker.PlaceAboveBar(() => _board is not null ? _board.GetToolAnchor(InkTool.Shape) : _bar.GetToolAnchor(InkTool.Shape),
                WindowChrome.GetHwnd((Window)_board ?? _bar));
        }
        WindowChrome.EnsureAbove(_shapePicker);
    }

    public async Task ScreenshotAsync()
    {
        try
        {
            var screen = ScreenService.FromWindow(WindowChrome.GetHwnd(_documentWorkspace?.Window ?? (Window)_board ?? _bar)).Bounds;
            string dir = Path.Combine(Services.Settings.Automation.AutoSavedStrokesLocation, "Screenshots");
            await ScreenshotService.CaptureToFileAsync(dir, screen.X, screen.Y, screen.Width, screen.Height,
                Services.Settings.Automation.IsSaveScreenshotsInDateFolders);

            if (Services.Settings.Automation.IsAutoSaveStrokesAtScreenshot)
                Services.AutoSave.SaveSnapshot(ActiveController.Document.Strokes, "screenshot");
        }
        catch
        {
            // Capture can fail on a locked-down desktop; never take the app down for it.
        }
    }

    public bool ClearCanvas()
    {
        var controller = ActiveController;
        var automation = Services.Settings.Automation;
        if (automation.IsAutoSaveStrokesAtClear && controller.Document.Count > automation.MinimumAutomationStrokeNumber)
        {
            Services.AutoSave.SaveSnapshot(controller.Document.Strokes, "clear");
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
            _board.Close();
            if (!Overlay.ClickThrough) ToggleClickThrough();
            return;
        }
        bool enableMouseMode = !Overlay.ClickThrough;
        if (enableMouseMode)
        {
            SaveBeforeMouseMode();
            bool showInk = _lastSlideShowState
                ? Services.Settings.PowerPoint.IsShowStrokeOnSelectInPowerPoint
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
        var strokes = ActiveController.Document.Strokes;
        if (strokes.Count <= Services.Settings.Automation.MinimumAutomationStrokeNumber) return;
        if (_lastSlideShowState) _pptSession.RequestScreenshot();
        else _ = ScreenshotAsync();
        Services.AutoSave.SaveSnapshot(strokes, _lastSlideShowState ? "ppt-mouse-mode" : "mouse-mode");
    }

    /// <summary>Launch in pass-through mode so the overlay does not capture the desktop's input.</summary>
    public void StartInClickThrough()
    {
        if (Overlay.ClickThrough) return;
        ToggleClickThrough();
    }

    public void PptNext()
    {
        if (_board is not null) ToggleBoard(); // MW_PPT.cs:574-581
        _pptSession.Next();
        QueuePresentationRefresh();
    }

    public void PptPrevious()
    {
        if (_board is not null) ToggleBoard(); // MW_PPT.cs:610-617
        _pptSession.Previous();
        QueuePresentationRefresh();
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
        if (!Overlay.ClickThrough) ToggleClickThrough();
        _pptSession.ShowSlideNavigationPane();
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
            if (_documentWorkspace is not null && !await _documentWorkspace.CloseAsync()) return;
            if (restart)
            {
                Services.SaveSettings();
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath, "-m") { UseShellExecute = true }); }
                catch (Exception ex) { await NotifyAsync("重启失败", ex.Message); return; }
            }
            _exiting = true;
            _pollTimer.Stop();

            if (Services.Settings.Automation.IsAutoSaveStrokesAtClear)
                Services.AutoSave.SaveSnapshot(Services.OverlayInk.Document.Strokes, "exit");
            Services.SaveSettings();

            Dispose();
            Application.Current.Exit();
        }
        finally { _exitPromptOpen = false; }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        StopDocumentForegroundTracking();
        foreach (var notice in _notices.ToArray()) notice.Close();
        _updateWindow?.Close();
        foreach (var palette in _detachedPalettes.ToArray()) palette.Close();
        _imageImportCancellation?.Cancel();
        Services.Plugins.Changed -= OnExtensionCommandsChanged;
        Services.Modules.Changed -= OnExtensionCommandsChanged;
        Services.SettingsApplied -= ApplyDocumentSettings;
        Services.SettingsApplied -= ApplyDisplaySettings;
        Services.Plugins.Dispose();
        Services.Modules.Dispose();
        Services.OverlayInk.Invalidated -= QueueDesktopSelection;
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
