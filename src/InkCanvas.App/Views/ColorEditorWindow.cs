using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace InkCanvas.App.Views;

/// <summary>Activatable, light-dismiss colour panel anchored beside the brush options.</summary>
internal sealed class ColorEditorWindow : Window
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly ColorPicker _picker;
    private readonly Action<InkColor> _changed;
    private bool _pending;
    public Task Completion => _completion.Task;

    public ColorEditorWindow(Window owner, RectInt32 anchor, InkColor color, int theme, Action<InkColor> changed)
    {
        Title = "自定义颜色 - Ink Canvas";
        _changed = changed;
        _picker = new ColorPicker
        {
            Color = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B),
            IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false,
            IsHexInputVisible = true, IsColorChannelTextInputVisible = true, IsMoreButtonVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, Width = 288,
        };
        AutomationProperties.SetName(_picker, "自定义颜色取色器");
        var header = new PopupHeader("自定义颜色", theme, canPin: false);
        header.CloseRequested += Close;
        var scroll = new ScrollViewer
        {
            Content = _picker, Padding = new Thickness(12, 0, 12, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(header);
        Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
        var surface = new Border
        {
            Background = PopupBrushes.Surface(theme), BorderBrush = PopupBrushes.ToolPanelBorder(theme),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel,
        };
        Content = surface;
        WindowChrome.MakeBorderless(this, topmost: true);
        SystemBackdrop = new MicaBackdrop();
        WindowChrome.ApplyTheme(this, theme);
        WindowChrome.ClipToSurfaces(this, () => new[] { surface });
        _picker.ColorChanged += (_, _) => QueueChange();
        _picker.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => QueueChange()), true);
        _applyTimer.Tick += (_, _) => Flush();
        panel.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Escape) return;
            args.Handled = true;
            Close();
        };

        int offsetY = anchor.Y - owner.AppWindow.Position.Y;
        void PlaceBeside()
        {
            var bounds = owner.AppWindow.Position;
            var size = owner.AppWindow.Size;
            var ownerBounds = new RectInt32(bounds.X, bounds.Y, size.Width, size.Height);
            var work = WindowChrome.GetWorkArea(ownerBounds);
            double dpi = WindowChrome.GetDpiScale(ownerBounds);
            int width = Math.Min(work.Width, (int)Math.Ceiling(316 * dpi));
            int height = Math.Min(work.Height, (int)Math.Ceiling(560 * dpi));
            int gap = (int)Math.Ceiling(8 * dpi);
            int right = bounds.X + size.Width + gap;
            int x = right + width <= work.X + work.Width ? right : bounds.X - width - gap;
            WindowChrome.PlaceClient(this, Math.Clamp(x, work.X, work.X + work.Width - width),
                Math.Clamp(bounds.Y + offsetY, work.Y, work.Y + work.Height - height), width, height);
        }
        PlaceBeside();
        var detach = WindowChrome.TrackAnchor(this, WindowChrome.GetHwnd(owner), PlaceBeside);
        surface.Loaded += (_, _) => PlaceBeside();
        TransientWindowDismissal.Attach(this, () => WindowChrome.GetHwnd(owner));
        void OwnerClosed(object sender, WindowEventArgs args) => Close();
        owner.Closed += OwnerClosed;
        Closed += (_, _) =>
        {
            Flush();
            detach();
            owner.Closed -= OwnerClosed;
            _completion.TrySetResult();
        };
    }

    private void Flush()
    {
        _applyTimer.Stop();
        if (!_pending) return;
        _pending = false;
        var chosen = _picker.Color;
        _changed(InkColor.Opaque(chosen.R, chosen.G, chosen.B));
    }

    private void QueueChange()
    {
        _pending = true;
        if (!_applyTimer.IsEnabled) _applyTimer.Start();
    }
}
