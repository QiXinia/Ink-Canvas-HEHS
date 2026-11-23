using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace InkCanvas.App.Views;

internal sealed class ToolOptionsWindow : Window
{
    private readonly FrameworkElement _content;
    private readonly PopupHeader _header;
    private readonly Viewbox _host;
    private readonly Func<RectInt32> _anchor;
    private readonly Func<double> _scale;
    private Action _detachAnchor;
    private Windows.Foundation.Size _designSize;
    private bool _measureDirty = true;
    private bool _closed, _fitPending;
    private XamlRoot _trackedRoot;
    private double _lastDpi;
    internal bool IsDetached { get; private set; }
    internal bool IsPinned => _header.IsPinned;

    internal ToolOptionsWindow(Window owner, FrameworkElement content, PopupHeader header,
        Func<RectInt32> anchor, Func<double> scale, int theme, Func<RectInt32> interactionRegion = null, Func<bool> isEditing = null)
    {
        _content = content;
        _header = header;
        _anchor = anchor;
        _scale = scale;
        _host = new Viewbox { Child = content, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var surface = new Border { Child = _host, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Content = surface;
        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        WindowChrome.ApplyTheme(this, theme);
        FloatingBarWindow.EnableTransparency(this);
        WindowChrome.ClipToSurfaces(this, () => new[] { content as Border ?? (content as UserControl)?.Content as Border ?? surface });
        header.CloseRequested += Close;
        OptionPopupCoordinator.Attach(this, header);
        WindowChrome.EnableDragMove(this, header, Detach,
            onDragCompleted: Fit);
        TransientWindowDismissal.Attach(this, () => WindowChrome.GetHwnd(owner), () => !header.IsPinned && isEditing?.Invoke() != true, anchor, interactionRegion);
        _detachAnchor = WindowChrome.TrackAnchor(this, WindowChrome.GetHwnd(owner), () => { if (!IsDetached) Fit(); });
        content.Loaded += OnLoaded;
        content.SizeChanged += OnSizeChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            if (content is BoardPenPalette palette) palette.FlushSliderChanges();
            _detachAnchor?.Invoke(); _detachAnchor = null;
            header.CloseRequested -= Close;
            content.Loaded -= OnLoaded;
            content.SizeChanged -= OnSizeChanged;
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnDpiChanged;
            _host.Child = null;
        };
        Fit();
        WindowChrome.ShowWithoutActivation(this);
        WindowChrome.EnsureAbove(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _measureDirty = true;
        if (_trackedRoot is not null) _trackedRoot.Changed -= OnDpiChanged;
        _trackedRoot = _content.XamlRoot;
        _lastDpi = WindowChrome.GetDpiScale(this);
        if (_trackedRoot is not null) _trackedRoot.Changed += OnDpiChanged;
        Fit();
    }
    internal void Detach()
    {
        IsDetached = true; _header.IsPinned = true;
        _detachAnchor?.Invoke(); _detachAnchor = null;
    }
    private void OnDpiChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double dpi = WindowChrome.GetDpiScale(this);
        if (_closed || Math.Abs(dpi - _lastDpi) < .001) return;
        _lastDpi = dpi;
        Fit();
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        _measureDirty = true;
        if (_closed || _fitPending) return;
        _fitPending = true;
        DispatcherQueue.TryEnqueue(() => { _fitPending = false; if (!_closed) Fit(); });
    }

    internal void Fit()
    {
        if (_closed) return;
        var anchor = IsDetached ? default : _anchor();
        var work = IsDetached ? WindowChrome.GetWorkArea(this) : WindowChrome.GetWorkArea(anchor);
        double dpi = IsDetached ? WindowChrome.GetDpiScale(this) : WindowChrome.GetDpiScale(anchor);
        if (_measureDirty)
        {
            _content.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _designSize = _content.DesiredSize;
            _measureDirty = false;
        }
        var size = _designSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        double scale = Math.Clamp(_scale(), .5, 1.5);
        scale = Math.Min(scale, Math.Min(work.Width / (size.Width * dpi), work.Height / (size.Height * dpi)));
        int width = Math.Min(work.Width, (int)Math.Ceiling(size.Width * scale * dpi));
        int height = Math.Min(work.Height, (int)Math.Ceiling(size.Height * scale * dpi));
        _host.Width = width / dpi;
        _host.Height = height / dpi;
        var current = WindowChrome.GetClientPosition(this);
        var position = IsDetached
            ? new PointInt32(Math.Clamp(current.X, work.X, work.X + work.Width - width), Math.Clamp(current.Y, work.Y, work.Y + work.Height - height))
            : WindowChrome.GetPopupPosition(anchor, width, height, (int)Math.Ceiling(8 * dpi));
        WindowChrome.PlaceClient(this, position.X, position.Y, width, height);
    }
}
