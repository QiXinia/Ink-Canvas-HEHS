using System.Runtime.InteropServices;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using InkCanvas.Platform.System;
using Windows.Graphics;

namespace InkCanvas.Modules.AiAssistant;

public sealed partial class AssistantWindow
{
    private Button _pinButton;
    private WindowFrame _frame;
    private Window _edgeHandle;
    private bool _collapsed, _pinned = true, _dockedRight = true, _positioned;
    private bool _sizeConstraintQueued;
    private RectInt32 _expandedBounds;
    private Func<ScreenRect?> _placementBounds;
    public event Action<Window, bool> ConfigureEdgeHandle;
    public void SetPlacementBounds(Func<ScreenRect?> placementBounds) => _placementBounds = placementBounds;
    private double Dpi => Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d);

    public void UseNativeFrame()
    {
        _frame = WindowFrame.Attach(this, _root).UseMica();
        _frame.Header.IconSource = null;
        Activated += (_, args) =>
        {
            if (_positioned || args.WindowActivationState == WindowActivationState.Deactivated) return;
            _positioned = true;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) DockPanel(true); });
        };
        AppWindow.Changed += (_, args) =>
        {
            if (_closed || _collapsed || !args.DidSizeChange || _sizeConstraintQueued) return;
            _sizeConstraintQueued = true;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                _sizeConstraintQueued = false;
                if (_closed || _collapsed || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
                // Read the current size when applying the constraint. Resize events can
                // otherwise enqueue stale sizes that undo a later drag or maximize.
                var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                int width = Math.Min(work.Width, Math.Max((int)(360 * Dpi), AppWindow.Size.Width));
                int height = Math.Min(work.Height, Math.Max((int)(480 * Dpi), AppWindow.Size.Height));
                if (width != AppWindow.Size.Width || height != AppWindow.Size.Height) AppWindow.Resize(new(width, height));
            })) _sizeConstraintQueued = false;
        };
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = _pinned;
        _pinButton.Opacity = _pinned ? 1 : .55;
        AutomationProperties.SetName(_pinButton, _pinned ? "取消窗口置顶" : "窗口置顶");
        ToolTipService.SetToolTip(_pinButton, _pinned ? "取消窗口置顶" : "窗口置顶");
    }

    public void DockPanel(bool right)
    {
        _dockedRight = right;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var area = _placementBounds?.Invoke();
        int left = work.X, top = work.Y, rightEdge = work.X + work.Width, bottomEdge = work.Y + work.Height;
        if (area is { Width: > 0, Height: > 0 } bounds)
        {
            left = Math.Max(left, bounds.X); top = Math.Max(top, bounds.Y);
            rightEdge = Math.Min(rightEdge, bounds.X + bounds.Width);
            bottomEdge = Math.Min(bottomEdge, bounds.Y + bounds.Height);
            if (rightEdge <= left || bottomEdge <= top)
            { left = work.X; top = work.Y; rightEdge = work.X + work.Width; bottomEdge = work.Y + work.Height; }
        }
        int margin = (int)(12 * Dpi);
        int areaWidth = rightEdge - left, areaHeight = bottomEdge - top;
        margin = Math.Min(margin, Math.Min(areaWidth, areaHeight) / 20);
        int width = Math.Min(Math.Max(1, areaWidth - margin * 2), (int)(380 * Dpi));
        int height = Math.Max(1, areaHeight - margin * 2);
        AppWindow.MoveAndResize(new(right ? rightEdge - width - margin : left + margin,
            top + Math.Max(0, (areaHeight - height) / 2), width, height));
    }

    public void CollapseToEdge()
    {
        if (_collapsed || _closed) return;
        _expandedBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _collapsed = true;
        var handle = new Window { Title = "展开 AI 助手", SystemBackdrop = new DesktopAcrylicBackdrop() };
        _edgeHandle = handle;
        var button = new TouchIconButton { Width = 44, Height = 64, Content = AiGlyph.CreateFramed(24, 24),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(0), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(button, "展开 AI 助手"); ToolTipService.SetToolTip(button, "展开 AI 助手");
        button.Click += (_, _) => ShowPanel();
        handle.Content = new Grid { RequestedTheme = _frame.RequestedTheme, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { button } };
        var presenter = (OverlappedPresenter)handle.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = _pinned; presenter.IsResizable = false;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var area = _placementBounds?.Invoke();
        int left = work.X, right = work.X + work.Width, top = work.Y, bottom = work.Y + work.Height;
        if (area is { Width: > 0, Height: > 0 } bounds)
        {
            left = Math.Max(left, bounds.X); right = Math.Min(right, bounds.X + bounds.Width);
            top = Math.Max(top, bounds.Y); bottom = Math.Min(bottom, bounds.Y + bounds.Height);
            if (right <= left || bottom <= top)
            { left = work.X; right = work.X + work.Width; top = work.Y; bottom = work.Y + work.Height; }
        }
        int width = Math.Min((int)(52 * Dpi), right - left), height = Math.Min((int)(80 * Dpi), bottom - top);
        int x = _dockedRight ? right - width : left;
        int y = Math.Clamp(_expandedBounds.Y + _expandedBounds.Height / 2 - height / 2, top, Math.Max(top, bottom - height));
        AppWindow.MoveAndResize(new(x, y, width, height));
        ConfigureEdgeHandle?.Invoke(handle, _pinned);
        AppWindow.Hide();
    }

    public void ShowPanel()
    {
        if (_closed) return;
        if (_collapsed)
        {
            _collapsed = false; CloseEdgeHandle();
            var work = DisplayArea.GetFromRect(_expandedBounds, DisplayAreaFallback.Nearest).WorkArea;
            int width = Math.Min(_expandedBounds.Width, work.Width), height = Math.Min(_expandedBounds.Height, work.Height);
            AppWindow.MoveAndResize(new(Math.Clamp(_expandedBounds.X, work.X, work.X + work.Width - width),
                Math.Clamp(_expandedBounds.Y, work.Y, work.Y + work.Height - height), width, height));
        }
        Activate();
        foreach (var turn in _turns) turn.Refresh();
        Motion.PlayEntrance(_root, 0);
    }

    internal void SetCanvasVisible(bool visible)
    {
        var window = _collapsed ? _edgeHandle : this;
        if (window is null || _closed) return;
        if (visible) window.AppWindow.Show(activateWindow: false);
        else window.AppWindow.Hide();
    }

    private void CloseEdgeHandle()
    { var handle = _edgeHandle; _edgeHandle = null; handle?.Close(); }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
