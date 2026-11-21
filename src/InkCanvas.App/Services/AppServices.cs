using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Ppt;
using InkCanvas.Features.Tools;
using InkCanvas.Ink;
using InkCanvas.Extensibility;
using InkCanvas.Extensions;
using InkCanvas.Host.Contracts;
using InkCanvas.Features.Update;

namespace InkCanvas.App.Services;

/// <summary>
/// Composition root. Owns the long-lived services and is the only place that translates
/// persisted settings into live behaviour.
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices(string settingsDirectory = null)
    {
        Store = new SettingsStore(settingsDirectory);
        Settings = Store.Load();
        Settings.Storage ??= new();
        UpdateCache = new UpdateCache(settingsDirectory is null ? null : Path.Combine(settingsDirectory, "UpdateCache"));
        ExtensionStore = new ExtensionPreferencesStore(Store.Directory);
        ExtensionPreferences = ExtensionStore.Load();
        Plugins = new PluginManager(Path.Combine(AppContext.BaseDirectory, "PluginHost", "InkCanvas.PluginHost.exe"), Path.Combine(Store.Directory, "PluginData"));
        Plugins.Discover(Path.Combine(AppContext.BaseDirectory, "Plugins"), Path.Combine(Store.Directory, "Plugins"));
        Board = new BoardService();
        ScreenImages = new BoardService(OverlayInk);
        OverlayInk.GetSelectableElements = ScreenImages.GetSelectionElements;
        ScreenImages.ImagePlacementChanged += OnScreenImagePlacementChanged;
        // 1:1 SetBoardTheme：白板模式决定画板明暗。未验证。
        Board.Background = Core.Theming.ThemeResolver.GetBoardTheme(Settings.Canvas.UsingWhiteboard)
            == Core.Theming.AppTheme.Light ? BoardBackground.White : BoardBackground.Black;
        Ppt = new PptInteropService();
        AutoSave = new AutoSaveService(Settings.Automation);
        Lottery = new LotteryService();
        LotteryMemory = new LotteryMemoryStore(Lottery, Store.Directory);
        ApplySettings();
    }

    public SettingsStore Store { get; }
    public UpdateCache UpdateCache { get; }

    public AppSettings Settings { get; private set; }
    public ModuleRuntime Modules { get; } = new();
    public PluginManager Plugins { get; }
    internal async Task<Action> PrepareInkOpenAsync(string path, BoardService target, bool entireBoard = false)
    {
        if (Path.GetExtension(path).Equals(".icink", StringComparison.OrdinalIgnoreCase))
            return await target.PrepareOpenAsync(path, entireBoard);

        string directory = Directory.CreateTempSubdirectory("InkCanvas-import-").FullName;
        string output = Path.Combine(directory, "page.icink");
        try
        {
            await Plugins.ConvertFileAsync(path, output);
            if (new FileInfo(output).Length > 192L * 1024 * 1024)
                throw new InvalidDataException("插件返回的画板超过 192 MiB。");
            return await target.PrepareImportedPageAsync(output, entireBoard);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }


    public ExtensionPreferencesStore ExtensionStore { get; }
    public ExtensionPreferences ExtensionPreferences { get; }
    private bool _modulesStarted;
    public void StartModules(IScreenTrackingHost host, IDocumentWorkspaceHost documents = null, ICanvasToolsHost canvasTools = null)
    {
        if (_modulesStarted) return; _modulesStarted = true;
        Modules.AddService(host);
        if (documents is not null) Modules.AddService(documents);
        if (canvasTools is not null) Modules.AddService(canvasTools);
        Modules.RegisterPackage("inkcanvas.screen-tracking", "屏幕内容跟随", Path.Combine(AppContext.BaseDirectory, "Modules", "inkcanvas.screen-tracking", "module.json"));
        if (documents is not null) Modules.RegisterPackage("inkcanvas.pdf", "PDF 阅读与批注", Path.Combine(AppContext.BaseDirectory, "Modules", "inkcanvas.pdf", "module.json"));
        if (canvasTools is not null) Modules.RegisterPackage("inkcanvas.analytic-geometry", "解析几何", Path.Combine(AppContext.BaseDirectory, "Modules", "inkcanvas.analytic-geometry", "module.json"));
        Modules.Start(ExtensionPreferences.DisabledModules);
        Modules.Changed += UpdateModuleStatus;
        UpdateModuleStatus();
    }
    private void UpdateModuleStatus()
    {
        var status = Modules.Status.FirstOrDefault(module => module.Id == "inkcanvas.screen-tracking");
        if (status is not null && status.State != ExtensionState.Running)
            ScreenTrackingStatus = $"屏幕跟随模块未运行 · {status.State} · {status.Error}";
    }

    /// <summary>Ink on the transparent screen overlay.</summary>
    public InkController OverlayInk { get; } = new();

    public BoardService Board { get; }
    public BoardService ScreenImages { get; }

    private void OnScreenImagePlacementChanged(BoardImageAsset image) => OverlayInk.NotifyElementsChanged();

    public PptInteropService Ppt { get; }

    public AutoSaveService AutoSave { get; }

    public LotteryService Lottery { get; }
    public LotteryMemoryStore LotteryMemory { get; }
    public string ScreenTrackingStatus { get; internal set; } = "未启用屏幕内容跟随";
    public event Action SettingsApplied;
    public event Action ScreenTrackingResetRequested;
    public void ResetScreenTrackingAnchor() => ScreenTrackingResetRequested?.Invoke();

    /// <summary>Push the current settings into every controller.</summary>
    public void ApplySettings()
    {
        Settings.Canvas.InkWash = Settings.Canvas.InkWash.Normalize();
        Settings.Canvas.InkWashWidth = double.IsFinite(Settings.Canvas.InkWashWidth) ? Math.Clamp(Settings.Canvas.InkWashWidth, 1, 120) : 30;
        Settings.Storage ??= new();
        Settings.Storage.UpdateCacheDays = Math.Clamp(Settings.Storage.UpdateCacheDays, 1, 30);
        Settings.Experimental ??= new ExperimentalSettings();
        foreach (var controller in AllControllers())
        {
            ConfigureController(controller);
        }

        Ppt.AllowWps = Settings.PowerPoint.IsSupportWPS;
        Lottery.PeopleCount = Settings.Rand.PeopleCount;
        Lottery.CooldownHalfLifeDays = Settings.Rand.CooldownHalfLifeDays;
        Lottery.CooldownPenaltyStrength = Settings.Rand.CooldownPenaltyStrength;
        Lottery.SamplingMode = Settings.Rand.SamplingMode;
        Core.Diagnostics.FileLog.Enabled = Settings.Advanced.IsLogEnabled;
        SettingsApplied?.Invoke();
    }

    public void ConfigureController(InkController controller)
    {
        var taper = OriginalMenuCatalog.ToTaperKind(Settings.Canvas.InkStyle);
        controller.ConfigurePenWidths((float)Math.Clamp(Settings.Canvas.InkWidth, .5, 60),
            (float)Math.Clamp(Settings.Canvas.HighlighterWidth ?? Settings.Canvas.InkWidth, .5, 60));
        controller.ConfigurePenColors(Settings.Canvas.PenColor is uint pen ? InkColor.FromPackedArgb(pen) : null,
            Settings.Canvas.HighlighterColor is uint highlighter ? InkColor.FromPackedArgb(highlighter) : null);
        if (Settings.Canvas.ShapeKind is { } shape && Enum.IsDefined(shape) && controller.ShapeKind != shape)
            controller.ShapeKind = shape;
        controller.CurrentAttributes.TaperKind = taper;
        controller.InkWashWidth = (float)(double.IsFinite(Settings.Canvas.InkWashWidth) ? Math.Clamp(Settings.Canvas.InkWashWidth, 1, 120) : 30);
        controller.CurrentAttributes.InkWash = Settings.Canvas.InkWash.Normalize();
        controller.CurrentAttributes.Nib = Enum.IsDefined(Settings.Canvas.PenNib) ? Settings.Canvas.PenNib : PenNib.Round;
        controller.CurrentAttributes.HighlighterAlpha = (byte)Math.Clamp(Settings.Canvas.InkAlpha, 20, 240);
        controller.ShowPenCursor = Settings.Canvas.IsShowCursor;
        controller.EraserRadius = OriginalMenuCatalog.ToEraserRadius(Settings.Canvas.EraserSize);
        if (Settings.Canvas.EraserType == 1) controller.EraserMode = InkEraserMode.Area;
        else if (Settings.Canvas.EraserType == 2) controller.EraserMode = InkEraserMode.WholeStroke;
        else if (Settings.Canvas.EraserType == 3) controller.EraserMode = InkEraserMode.Lasso;
        controller.RecognizeShapes = Settings.InkToShape.IsInkToShapeEnabled;
        controller.RecognitionOptions = Settings.InkToShape;
        controller.UseHardwarePressure = Settings.Canvas.UseHardwarePressure;
        controller.UsePenTilt = Settings.Canvas.UsePenTilt;
        controller.Palm.PenHasPriority = Settings.Gesture.PenHasPriority;
        controller.PalmErases = Settings.Gesture.PalmErases;
        controller.MultiTouchEnabled = Settings.Gesture.IsEnableMultiTouchMode;
        controller.PanZoomEnabled = Settings.Gesture.IsEnableTwoFingerGesture;
        controller.ConfigureGestures(
            Settings.Gesture.IsEnableTwoFingerZoom,
            Settings.Gesture.IsEnableTwoFingerTranslate,
            Settings.Gesture.IsEnableTwoFingerRotation);
        if (BoardModeActive && Board.Pages.Contains(controller) && Settings.Gesture.AutoSwitchTwoFingerGesture)
        {
            controller.MultiTouchEnabled = false;
            controller.ConfigureGestures(Settings.Gesture.IsEnableTwoFingerZoom, true, Settings.Gesture.IsEnableTwoFingerRotation);
        }
        controller.TouchGesturesEnabled = !ReferenceEquals(controller, OverlayInk) || !PresentationActive
            || Settings.PowerPoint.IsEnableTwoFingerGestureInPresentationMode;
        if (!controller.TouchGesturesEnabled)
            controller.ConfigureGestures(false, false, false);
        controller.Palm.PalmContactThreshold = Math.Max(8, Settings.Advanced.FingerModeBoundsWidth);
        controller.GesturesTargetSelection = Settings.Gesture.IsEnableTwoFingerRotationOnSelection;

        // 1:1 特殊屏移植接线（QuadIR / TouchMultiplier / 笔尖-手指分档）。未验证。
        controller.Palm.Advanced = Settings.Advanced;
        controller.Palm.NibMode = Settings.Startup.IsEnableNibMode;
        controller.TransformCenterOption = Settings.Gesture.MatrixTransformCenterPoint;
    }

    public IEnumerable<InkController> AllControllers()
    {
        yield return OverlayInk;
        foreach (var page in Board.Pages) yield return page;
    }

    public void SaveSettings() => Store.Save(Settings);

    public void RememberToolOptions(InkController controller)
    {
        if (controller.Tool == InkTool.Highlighter) Settings.Canvas.HighlighterColor = controller.CurrentAttributes.Color.PackedArgb;
        else if (controller.Tool == InkTool.Pen) Settings.Canvas.PenColor = controller.CurrentAttributes.Color.PackedArgb;
        if (controller.Tool == InkTool.Shape) Settings.Canvas.ShapeKind = controller.ShapeKind;
        ApplySettings();
        SaveSettings();
    }
    public bool BoardModeActive { get; set; }
    public bool PresentationActive { get; set; }

    /// <summary>Re-read settings from disk (used after the settings window edits them).</summary>
    public void Reload()
    {
        Settings = Store.Load();
        ApplySettings();
    }

    public void Dispose()
    {
        LotteryMemory.Dispose();
        Plugins.Dispose();
        Modules.Changed -= UpdateModuleStatus;
        Modules.Dispose();
        ScreenImages.ImagePlacementChanged -= OnScreenImagePlacementChanged;
        OverlayInk.Dispose();
        foreach (var page in Board.Pages) page.Dispose();
    }
}
