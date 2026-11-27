using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Input;
using InkCanvas.Platform.System;
using InkCanvas.Platform.Windows;

namespace InkCanvas.App.Services;

/// <summary>
/// Owns the transparent screen-annotation surface: the layered window, its presenter and the
/// global hotkeys registered against it.
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private Core.Settings.DisplaySettings _displaySettings = new();
    private long _idleSince;
    private bool _idleResourcesReleased;
    public void ConfigureDisplays(Core.Settings.DisplaySettings settings)
    {
        _displaySettings = settings ?? new();
        RefreshDisplayBounds();
    }
    public OverlayHost(InkController controller)
    {
        var screens = ScreenService.All();
        var screen = screens.Count == 0 ? ScreenService.Virtual : ScreenService.GetVirtualBounds(screens);
        Window = new LayeredInkWindow(screen.X, screen.Y, screen.Width, screen.Height);
        Window.SetScreenLayout(screens);
        Presenter = new OverlayInkPresenter(Window, controller);
        Hotkeys = new HotkeyService(Window.Handle);
        Controller = controller;
        controller.ViewCenter = new System.Numerics.Vector2(
            screen.Width / Window.DpiScale / 2f, screen.Height / Window.DpiScale / 2f);

        Window.HotkeyPressed += id => Hotkeys.Dispatch(id);
        Presenter.Attach();
    }

    public LayeredInkWindow Window { get; }

    public OverlayInkPresenter Presenter { get; }

    public HotkeyService Hotkeys { get; }

    public InkController Controller { get; }
    public event Action InteractionChanged;

    /// <summary>Keep saved ink at the same desktop pixel when the virtual desktop origin changes.</summary>
    public bool RefreshDisplayBounds() => RefreshDisplayBounds(ScreenService.All());

    internal bool RefreshDisplayBounds(IReadOnlyList<ScreenInfo> screens)
    {
        // Enumeration may be empty briefly while Windows applies a projection mode.
        var screen = ScreenService.GetVirtualBounds(screens);
        if (screen.Width <= 0 || screen.Height <= 0) return false;
        Window.SetScreenLayout(screens);
        bool resized = SetDisplayBounds(screen);
        // A monitor split, DPI or enabled area can change without changing the outer bounds.
        Window.SetInputAreas(_displaySettings.AllDisplays ? null : screens
            .Where(item => (_displaySettings.EnabledDevices ?? new()).Contains(item.DeviceName, StringComparer.OrdinalIgnoreCase))
            .Select(item => item.Bounds));
        return resized;
    }

    internal bool SetDisplayBounds(ScreenRect screen)
    {
        if (screen == new ScreenRect(Window.X, Window.Y, Window.Width, Window.Height)) return false;
        bool visible = Window.IsVisible;
        Window.CancelInput();
        Presenter.Suspend();
        int oldX = Window.X, oldY = Window.Y;
        try
        {
            Window.SetBounds(screen.X, screen.Y, screen.Width, screen.Height);
            Controller.ViewTransform *= System.Numerics.Matrix3x2.CreateTranslation(
                (oldX - screen.X) / Window.DpiScale, (oldY - screen.Y) / Window.DpiScale);
            Controller.ViewCenter = new(screen.Width / Window.DpiScale / 2, screen.Height / Window.DpiScale / 2);
            Presenter.SetScreenTrackingClip(null, false);
        }
        finally { if (visible) Presenter.Attach(); }
        return true;
    }

    public bool ClickThrough
    {
        get => Window.ClickThrough;
        set => SetInteractionMode(value, inkVisible: !value);
    }

    public void SetInteractionMode(bool clickThrough, bool inkVisible)
    {
        if (inkVisible && !clickThrough) { _idleResourcesReleased = false; _idleSince = 0; }
        if (clickThrough || !inkVisible) Controller.CancelLassoErase();
        if (clickThrough || !inkVisible) Controller.HideEraserPreview();
        Window.ClickThrough = clickThrough;
        Presenter.InkVisible = inkVisible;
        InteractionChanged?.Invoke();
    }

    public void Show()
    {
        _idleResourcesReleased = false;
        _idleSince = 0;
        Window.Show();
        Presenter.Attach();
        InteractionChanged?.Invoke();
    }

    public void Hide()
    {
        Controller.CancelLassoErase();
        Presenter.Suspend();
        Window.Hide();
        InteractionChanged?.Invoke();
    }

    internal bool TrimIdleResources(bool force = false)
    {
        if (Window.IsVisible && Presenter.InkVisible)
        {
            _idleSince = 0;
            _idleResourcesReleased = false;
            return false;
        }
        if (_idleResourcesReleased || Controller.HasActiveInteraction) return false;
        if (_idleSince == 0) _idleSince = Environment.TickCount64;
        if (!force && Environment.TickCount64 - _idleSince < 15_000) return false;
        Presenter.ReleaseIdleResources();
        Window.ReleasePixelBuffer();
        _idleResourcesReleased = true;
        return true;
    }

    /// <summary>
    /// Register the WPF guide's hotkey set (OperatingGuideWindow rows): Ctrl+Z/Y undo/redo,
    /// Ctrl+E clear, Alt+C screenshot, Alt+V fold, Alt+D draw/palette, Alt+Q mouse mode,
    /// Alt+B board, Alt+S select, Alt+E eraser, Alt+L line, Shift+Esc exit slideshow.
    /// PageUp/PageDown remain as slideshow conveniences (documented in ppt-porting-notes).
    /// Failures are non-fatal (key may be taken).
    /// </summary>
    public void RegisterHotkeys(Action undo, Action redo, Action toggleEraser, Action clear,
        Action togglePassThrough = null, Action toggleBoard = null,
        Action pptNext = null, Action pptPrevious = null, Action pptExit = null,
        Action screenshot = null, Action fold = null, Action select = null, Action line = null, Action draw = null,
        Func<bool> editingEnabled = null, Func<bool> overlayEditingEnabled = null, Func<bool> presentationEnabled = null)
    {
        Hotkeys.Register(HotkeyModifiers.Control, 0x5A /* Z */, undo, overlayEditingEnabled);
        Hotkeys.Register(HotkeyModifiers.Control, 0x59 /* Y */, redo, overlayEditingEnabled);
        Hotkeys.Register(HotkeyModifiers.Control, 0x45 /* E */, clear, overlayEditingEnabled);
        Hotkeys.Register(HotkeyModifiers.Alt, 0x45 /* E */, toggleEraser, editingEnabled);
        if (select is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x53 /* S */, select, editingEnabled);
        if (line is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x4C /* L */, line, editingEnabled);
        if (toggleBoard is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x42 /* B */, toggleBoard);
        if (fold is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x56 /* V */, fold);
        if (screenshot is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x43 /* C */, screenshot);
        if (draw is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x44 /* D */, draw);
        // Escape hatch: the overlay captures the whole screen while writing, so mouse mode must
        // work even if the bar is unreachable.
        if (togglePassThrough is not null) Hotkeys.Register(HotkeyModifiers.Alt, 0x51 /* Q */, togglePassThrough);
        if (pptNext is not null) Hotkeys.Register(HotkeyModifiers.None, 0x22 /* PageDown */, pptNext, presentationEnabled);
        if (pptPrevious is not null) Hotkeys.Register(HotkeyModifiers.None, 0x21 /* PageUp */, pptPrevious, presentationEnabled);
        if (pptExit is not null) Hotkeys.Register(HotkeyModifiers.Shift, 0x1B /* Escape */, pptExit, presentationEnabled);
    }

    public void Dispose()
    {
        Hotkeys.Dispose();
        Presenter.Dispose();
        Window.Dispose();
    }
}
