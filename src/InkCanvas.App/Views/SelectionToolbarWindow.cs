using System.Numerics;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Selection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rect = InkCanvas.Core.Ink.Rect;

namespace InkCanvas.App.Views;

/// <summary>Nonactivating selection actions, positioned using screen pixels.</summary>
public sealed class SelectionToolbarWindow : Window
{
    private readonly ShellController _shell;
    private readonly Border _surface;
    private readonly Viewbox _scaled;
    private bool _pending, _closed, _shown;
    private Rect _lastPlacement;
    private double _lastScale;
    private int _lastTheme = -1;
    private bool _measureDirty = true;
    private Windows.Foundation.Size _desired;
    private XamlRoot _trackedRoot;

    public SelectionToolbarWindow(ShellController shell)
    {
        _shell = shell;
        Title = "选择工具";
        Toolbar = new SelectionToolbar(() => shell.Services.OverlayInk,
            async () => await shell.ExportDesktopSelectionAsync(), shell.CloneDesktopSelectionToBoard, "衍至画板");
        _surface = new Border { Padding = new Thickness(4), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Child = Toolbar };
        _scaled = new Viewbox { Child = _surface, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Content = _scaled;
        WindowChrome.MakeBorderless(this, noActivate: true);
        FloatingBarWindow.EnableTransparency(this);
        WindowChrome.ClipToSurfaces(this, () => new[] { _surface });
        Toolbar.LayoutChanged += () => { _measureDirty = true; QueueUpdate(); };
        _surface.Loaded += (_, _) =>
        {
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnXamlRootChanged;
            _trackedRoot = _surface.XamlRoot;
            if (_trackedRoot is not null) _trackedRoot.Changed += OnXamlRootChanged;
            QueueUpdate();
        };
        _surface.SizeChanged += (_, _) => QueueUpdate();
        Closed += (_, _) =>
        {
            _closed = true;
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnXamlRootChanged;
        };
    }

    public SelectionToolbar Toolbar { get; }
    public bool IsShown => _shown;
    internal long HiddenSince { get; private set; } = Environment.TickCount64;

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueUpdate();

    public void QueueUpdate()
    {
        if (_pending || _closed) return;
        _pending = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pending = false;
            if (!_closed) Update();
        });
    }

    private void Update()
    {
        var controller = _shell.Services.OverlayInk;
        if (!_shell.DesktopSelectionAvailable || !controller.HasSelection || controller.Playback is not null
            || controller.IsRegionSelecting || controller.IsRotatingSelection)
        {
            Hide();
            return;
        }
        int theme = _shell.Services.Settings.Appearance.Theme;
        if (_lastTheme != theme)
        {
            _lastTheme = theme;
            WindowChrome.ApplyTheme(this, theme);
            _surface.Background = PopupBrushes.FloatBarBackground(theme);
            _surface.BorderBrush = PopupBrushes.FloatBarBorder(theme);
            FloatingBarWindow.UpdateAcrylicTint(this, theme);
            Toolbar.ApplyTheme(theme);
        }
        Toolbar.Refresh();
        var overlay = _shell.Overlay.Window;
        var view = controller.SelectionVisualBounds;
        var selection = new Rect(overlay.X + view.Left * overlay.DpiScale, overlay.Y + view.Top * overlay.DpiScale,
            overlay.X + view.Right * overlay.DpiScale, overlay.Y + view.Bottom * overlay.DpiScale);
        // Query the monitor containing the selected object, even before this toolbar is shown.
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
            new Windows.Graphics.PointInt32((int)selection.Center.X, (int)selection.Center.Y),
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var area = display.WorkArea;
        var work = new Rect(area.X + 8, area.Y + 8, area.X + area.Width - 8, area.Y + area.Height - 8);
        if (_measureDirty)
        {
            _surface.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _desired = _surface.DesiredSize;
            _measureDirty = false;
        }
        var desired = _desired;
        double dpi = InkCanvas.Platform.System.ScreenService.FromRect(new(area.X, area.Y, area.Width, area.Height)).DpiScale;
        double scale = Math.Clamp(_shell.Services.Settings.Appearance.FloatingBarScale, 50, 200) / 100d;
        scale = Math.Min(scale, Math.Min(work.Width / (desired.Width * dpi), work.Height / (desired.Height * dpi)));
        int width = Math.Max(1, (int)Math.Ceiling(desired.Width * scale * dpi));
        int height = Math.Max(1, (int)Math.Ceiling(desired.Height * scale * dpi));
        var place = FloatingToolbarLayout.Place(work, selection, _shell.DesktopToolbarBounds,
            new Vector2(width, height), (float)(10 * dpi));
        // Input can arrive several times per frame. Skip HWND sizing unless geometry changed.
        var rounded = new Rect(MathF.Round(place.Left), MathF.Round(place.Top), MathF.Round(place.Left) + width, MathF.Round(place.Top) + height);
        if (rounded != _lastPlacement || Math.Abs(scale - _lastScale) > .001)
        {
            _lastPlacement = rounded;
            _lastScale = scale;
            _scaled.Width = desired.Width * scale;
            _scaled.Height = desired.Height * scale;
            WindowChrome.PlaceClient(this, (int)rounded.Left, (int)rounded.Top, width, height);
        }
        if (!_shown)
        {
            _shown = true;
            WindowChrome.ShowWithoutActivation(this);
        }
    }

    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        HiddenSince = Environment.TickCount64;
        Toolbar.ClosePanels();
        AppWindow.Hide();
    }
}
