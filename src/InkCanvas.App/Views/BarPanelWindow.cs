using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>
/// The WPF 更多设置 (BorderTools) / 手势选项 (TwoFingerGestureBorder) popups. In the original these
/// are separate Border popups anchored ABOVE the floating bar; the bar window never resizes when
/// they open. Hosted here as their own borderless topmost acrylic window so expanding/collapsing
/// them (and dragging the bar) never shifts the primary button row.
/// </summary>
internal sealed class BarPanelWindow : Window
{
    private const double S = FloatingBar.WpfScale;

    private readonly FloatingBarWindow _owner;
    private readonly FloatingBar _bar;
    private readonly bool _isMore;
    private readonly BarPanelKind _kind;
    private readonly Border _host;
    private readonly Viewbox _scaledHost;
    private readonly PopupHeader _title;
    private readonly UIElement _content;
    private double _designW, _designH;
    private bool _measured;
    private XamlRoot _trackedRoot;
    private double _lastDpi;
    private int _barX, _barY, _barW;
    private bool _hasBarPosition;
    private bool _closed;
    private bool _sizeFitPending;
    private Action _detachAnchor;
    internal bool IsDetached { get; private set; }

    public BarPanelWindow(FloatingBarWindow owner, FloatingBar bar, bool isMore)
        : this(owner, bar, isMore ? BarPanelKind.More : BarPanelKind.Gesture) { }

    public BarPanelWindow(FloatingBarWindow owner, FloatingBar bar, BarPanelKind kind)
    {
        _owner = owner;
        _bar = bar;
        _kind = kind;
        _isMore = kind == BarPanelKind.More;
        Title = kind switch { BarPanelKind.More => "更多设置", BarPanelKind.Eraser => "橡皮", _ => "手势选项" };
        _content = kind switch
        {
            BarPanelKind.More => bar.CreateMorePanelContent(),
            BarPanelKind.Eraser => bar.CreateEraserPanelContent(),
            _ => bar.CreateGesturePanelContent(),
        };
        _title = new PopupHeader(Title, _bar.Theme);
        _title.CloseRequested += Close;
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(_title);
        stack.Children.Add(_content);
        _host = new Border
        {
            BorderThickness = new Thickness(S),
            CornerRadius = new CornerRadius(8),
            Child = stack,
        };
        _scaledHost = new Viewbox
        {
            Child = _host,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Content = _scaledHost;

        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        FloatingBarWindow.EnableTransparency(this);
        WindowChrome.ClipToSurfaces(this, () => new[] { _host });
        ApplyChrome();
        WindowChrome.EnableDragMove(this, _title, Detach,
            onDragCompleted: () => PlaceAboveBar(_barX, _barY, _barW));
        _detachAnchor = WindowChrome.TrackAnchor(this, WindowChrome.GetHwnd(owner), () =>
        {
            if (!_closed && !IsDetached && _hasBarPosition) PlaceAboveBar(_barX, _barY, _barW);
        });
        _bar.ThemeRefreshed += ApplyChrome;
        OptionPopupCoordinator.Attach(this, _title);

        TransientWindowDismissal.Attach(this, () => WindowChrome.GetHwnd(_owner), () => !_title.IsPinned,
            () => WindowChrome.GetScreenBounds(_owner, _bar.GetPanelButton(_kind)),
            () => WindowChrome.GetScreenBounds(_owner, _bar));
        _host.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; if (!_closed) Close(); }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _detachAnchor?.Invoke(); _detachAnchor = null;
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnXamlRootChanged;
            _bar.ThemeRefreshed -= ApplyChrome;
            // Release the shared rows so they can be re-parented into a future popup.
            if (_content is FrameworkElement fe && fe.Parent is Panel parent) parent.Children.Remove(_content);
            _host.Child = null;
            _owner.OnPanelClosed(_kind, this);
        };
        _host.Loaded += (_, _) => AttachDpiTracking();
        _host.SizeChanged += (_, _) =>
        {
            if (_closed || _sizeFitPending) return;
            _sizeFitPending = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _sizeFitPending = false;
                if (_closed || _host.ActualWidth <= 0 || _host.ActualHeight <= 0) return;
                if (Math.Abs(_designW - _host.ActualWidth) < 0.5 && Math.Abs(_designH - _host.ActualHeight) < 0.5) return;
                _designW = _host.ActualWidth;
                _designH = _host.ActualHeight;
                _measured = true;
                if (_hasBarPosition) PlaceAboveBar(_barX, _barY, _barW);
            });
        };
    }

    public bool IsMore => _isMore;
    internal void Detach()
    {
        IsDetached = true; _title.IsPinned = true;
        _detachAnchor?.Invoke(); _detachAnchor = null;
    }
    internal void DismissOnInput() { if (!_title.IsPinned && !_closed) Close(); }

    private void ApplyChrome()
    {
        int theme = _bar.Theme;
        Brush bg = _bar.ColorfulBackground ? PopupBrushes.FloatBarColorful() : PopupBrushes.FloatBarBackground(theme);
        _host.Background = bg;
        _host.BorderBrush = PopupBrushes.ToolPanelBorder(theme);
        _title.ApplyTheme(theme);
        FloatingBarWindow.UpdateAcrylicTint(this, theme);
    }

    private void AttachDpiTracking()
    {
        if (_trackedRoot is not null) _trackedRoot.Changed -= OnXamlRootChanged;
        _trackedRoot = _host.XamlRoot;
        _lastDpi = WindowChrome.GetDpiScale(this);
        if (_trackedRoot is not null) _trackedRoot.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_closed) return;
        double scale = WindowChrome.GetDpiScale(this);
        if (Math.Abs(scale - _lastDpi) < 0.001) return;
        _lastDpi = scale;
        if (_hasBarPosition) PlaceAboveBar(_barX, _barY, _barW);
    }

    /// <summary>Measure once (unrooted, so unconstrained), then anchor centred above the bar.</summary>
    public void ShowAboveBar(int barX, int barY, int barW)
    {
        if (!_measured)
        {
            _host.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _designW = _host.DesiredSize.Width;
            _designH = _host.DesiredSize.Height;
            // Fallback for a degenerate measure (text not ready): more panel = 4 x 62.5-wide
            // buttons + title band; gesture panel = icon+label+toggle rows under its title.
            if (_designW < 50 || _designH < 50)
            {
                _designW = _kind == BarPanelKind.Eraser ? 244 : _isMore ? 256 : 180;
                _designH = _kind == BarPanelKind.Eraser ? 168 : _isMore ? 236 : 165;
            }
            _measured = true;
        }
        PlaceAboveBar(barX, barY, barW);
        WindowChrome.ShowWithoutActivation(this);
        WindowChrome.EnsureAbove(this);
        FloatingBarWindow.UpdateAcrylicTint(this, _bar.Theme);
    }

    public void PlaceAboveBar(int barX, int barY, int barW)
    {
        _barX = barX; _barY = barY; _barW = barW; _hasBarPosition = true;
        var location = WindowChrome.GetClientPosition(this);
        var anchor = IsDetached ? new Windows.Graphics.RectInt32(location.X, location.Y, AppWindow.Size.Width, AppWindow.Size.Height)
            : WindowChrome.GetScreenBounds(_owner, _bar.GetPanelButton(_kind));
        var work = WindowChrome.GetWorkArea(anchor);
        double dpi = WindowChrome.GetDpiScale(anchor);
        double user = _owner.UserScale;
        // Clamp to the work area: at high user scale on a small monitor the design size can
        // exceed the screen; shrink the effective scale so nothing clips (and Math.Clamp's
        // min <= max invariant below holds).
        double fit = Math.Min(1.0, Math.Min(work.Width / (_designW * user * dpi), work.Height / (_designH * user * dpi)));
        user *= fit;
        _scaledHost.Width = _designW * user;
        _scaledHost.Height = _designH * user;
        int w = (int)Math.Ceiling(_designW * user * dpi);
        int h = (int)Math.Ceiling(_designH * user * dpi);
        int gap = (int)Math.Ceiling(4 * S * dpi);
        var position = IsDetached
            ? new Windows.Graphics.PointInt32(Math.Clamp(anchor.X, work.X, work.X + work.Width - w), Math.Clamp(anchor.Y, work.Y, work.Y + work.Height - h))
            : WindowChrome.GetPopupPosition(anchor, w, h, gap);
        WindowChrome.PlaceClient(this, position.X, position.Y, w, h);
        WindowChrome.EnsureAbove(this);
    }
}
