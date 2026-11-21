using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private readonly List<nint> _shortcutHooks = new();
    private ForegroundCallback _shortcutCallback;
    private bool _shortcutRefreshQueued;

    internal bool CanvasShortcutsEnabled => IsCanvasShortcutContext(NativeMethods.GetForegroundWindow());

    internal bool IsCanvasShortcutContext(nint foreground)
    {
        if (_disposed || _exiting || _imagePicking || _settingsOpening || _exitPromptOpen || ActiveController.Tool == InkTool.Pan) return false;
        Window canvas = (Window)_board ?? (DocumentActive ? _documentWorkspace.Window : null);
        if (canvas is not null)
        {
            if (!canvas.AppWindow.IsVisible || canvas.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized }) return false;
        }
        else if (Overlay.ClickThrough || !Overlay.Window.IsVisible || !Overlay.Presenter.InkVisible || _bar?.IsFolded == true) return false;
        if (foreground == 0) return false;
        if (foreground == Overlay.Window.Handle) return canvas is null;
        var chrome = OpenWindows.Concat(_board?.ToolPanels ?? Enumerable.Empty<Window>())
            .FirstOrDefault(window => WindowChrome.GetHwnd(window) == foreground);
        if (chrome is not null)
            return (ReferenceEquals(chrome, canvas) || chrome is FloatingBarWindow or SelectionToolbarWindow or PenPaletteWindow or ShapePickerWindow or ToolOptionsWindow)
                && !KeyboardShortcutScope.HasTextInputFocus(chrome.Content as FrameworkElement);
        NativeMethods.GetWindowThreadProcessId(foreground, out uint process);
        return canvas is null && process != Environment.ProcessId;
    }

    // WinUI workspaces route editing keys locally. Desktop annotation keeps its no-activation input.
    internal bool OverlayShortcutsEnabled => IsOverlayShortcutContext(NativeMethods.GetForegroundWindow());
    internal bool IsOverlayShortcutContext(nint foreground) => _board is null && !DocumentActive && IsCanvasShortcutContext(foreground);
    private bool PresentationShortcutsEnabled => !_disposed && !_exiting && _lastSlideShowState && _board is null && !DocumentActive
        && (OverlayShortcutsEnabled || _pptSession.Snapshot.WindowHandle != 0 && NativeMethods.GetForegroundWindow() == _pptSession.Snapshot.WindowHandle);

    private void RefreshShortcutRegistration()
    {
        if (!_disposed) Overlay.Hotkeys.Refresh();
    }

    private void StartShortcutTracking()
    {
        _shortcutCallback = (_, _, _, _, _, _, _) =>
        {
            if (_disposed || _shortcutRefreshQueued) return;
            _shortcutRefreshQueued = true;
            if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
            {
                _shortcutRefreshQueued = false;
                RefreshShortcutRegistration();
            })) _shortcutRefreshQueued = false;
        };
        foreach (uint eventId in new uint[] { 3, 0x8005 }) // foreground and keyboard focus
        {
            var hook = SetWinEventHook(eventId, eventId, 0, _shortcutCallback, 0, 0, 0);
            if (hook != 0) _shortcutHooks.Add(hook);
        }
        RefreshShortcutRegistration();
    }

    private void StopShortcutTracking()
    {
        foreach (var hook in _shortcutHooks) UnhookWinEvent(hook);
        _shortcutHooks.Clear(); _shortcutCallback = null;
    }
}
