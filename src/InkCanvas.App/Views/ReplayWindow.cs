using System.Diagnostics;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Replay;
using InkCanvas.Ink;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>Floating transport for playback in the original board/desktop ink surface.</summary>
public sealed class ReplayWindow : Window
{
    private readonly InkController _controller;
    private readonly ReplayClock _clock;
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TouchIconButton _play;
    private readonly Slider _progress;
    private readonly TextBlock _time;
    private bool _seeking, _closed;
    private double _previousMs;
    public InkController TargetController => _controller;
    public float ElapsedMs => _clock.ElapsedMs;
    private readonly Action _refreshPlacement;
    private XamlRoot _trackedRoot;
    private double _lastDpi = 1;
    public void RefreshDisplayPlacement() => _refreshPlacement();

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!_closed && Math.Abs(WindowChrome.GetDpiScale(this) - _lastDpi) > .001) RefreshDisplayPlacement();
    }

    public ReplayWindow(InkController controller, int theme, double scalePercent = 100, Window owner = null)
    {
        _controller = controller;
        Title = "墨迹回放控制";
        if (owner is not null)
            WindowChrome.MoveToMonitor(this, Platform.System.ScreenService.FromWindow(WindowChrome.GetHwnd(owner)));
        _clock = new ReplayClock(controller.Playback?.Duration ?? TimeSpan.Zero);
        var foreground = new SolidColorBrush(theme == 2 ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Padding = new Thickness(8, 5, 8, 5) };
        var grip = new TextBlock { Text = "⠿  墨迹回放", FontSize = 12, Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        row.Children.Add(grip);
        _play = Button(Glyphs.Pause, "播放 / 暂停", () =>
        {
            _clock.Toggle(); _previousMs = _elapsed.Elapsed.TotalMilliseconds;
            if (_clock.IsPlaying) _timer.Start(); else _timer.Stop();
            Sync();
        });
        Button(Glyphs.Restart, "从头播放", () => { _clock.Seek(0); _clock.Play(); _previousMs = _elapsed.Elapsed.TotalMilliseconds; _timer.Start(); Present(); });
        _progress = new Slider { Width = 220, Minimum = 0, Maximum = 1000, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_progress, "回放进度");
        _progress.ValueChanged += (_, e) => { if (!_seeking) Seek((float)(e.NewValue * _clock.DurationMs / 1000)); };
        row.Children.Add(_progress);
        _time = new TextBlock { Foreground = foreground, FontSize = 12, MinWidth = 85, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_time);
        var speed = new ComboBox { Width = 75, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        foreach (var label in new[] { "0.5×", "1×", "2×", "4×" }) speed.Items.Add(label);
        speed.SelectedIndex = 1;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(speed, "回放速度");
        speed.SelectionChanged += (_, _) => _clock.Speed = speed.SelectedIndex switch { 0 => .5f, 2 => 2, 3 => 4, _ => 1 };
        row.Children.Add(speed);
        Button(Glyphs.Close, "结束回放，恢复原稿", Close);
        var card = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(theme == 2 ? Windows.UI.Color.FromArgb(160, 255, 255, 255) : Windows.UI.Color.FromArgb(160, 0, 0, 0)),
            Background = new SolidColorBrush(theme == 2 ? Windows.UI.Color.FromArgb(235, 25, 25, 25) : Windows.UI.Color.FromArgb(235, 255, 255, 255)), Child = row };
        card.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = Math.Clamp(scalePercent, 50, 150) / 100;
        var work = WindowChrome.GetWorkArea(this);
        double dpi = WindowChrome.GetDpiScale(this);
        scale = Math.Min(scale, Math.Max(1, work.Width - 16) / (card.DesiredSize.Width * dpi));
        var host = new Viewbox { Child = card, Width = card.DesiredSize.Width * scale, Height = card.DesiredSize.Height * scale, Stretch = Stretch.Uniform };
        Content = host;
        host.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { Close(); e.Handled = true; } };
        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        FloatingBarWindow.EnableTransparency(this);
        WindowChrome.ApplyTheme(this, theme);
        WindowChrome.ClipToSurfaces(this, () => new[] { card });
        bool placed = false, fitQueued = false;
        void Place()
        {
            if (_closed) return;
            card.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double designWidth = Math.Max(1, card.DesiredSize.Width), designHeight = Math.Max(1, card.DesiredSize.Height);
            var area = WindowChrome.GetWorkArea(this);
            double currentDpi = WindowChrome.GetDpiScale(this);
            double user = Math.Clamp(scalePercent, 50, 150) / 100;
            user = Math.Min(user, Math.Min(Math.Max(1, area.Width - 16) / (designWidth * currentDpi), Math.Max(1, area.Height - 16) / (designHeight * currentDpi)));
            host.Width = designWidth * user;
            host.Height = designHeight * user;
            int width = (int)Math.Ceiling(host.Width * currentDpi), height = (int)Math.Ceiling(host.Height * currentDpi);
            var position = WindowChrome.GetClientPosition(this);
            var fit = new Platform.System.ScreenRect(placed ? position.X : area.X + (area.Width - width) / 2,
                placed ? position.Y : area.Y + area.Height - (int)(160 * currentDpi), width, height)
                .FitInside(new(area.X, area.Y, area.Width, area.Height));
            placed = true;
            _lastDpi = currentDpi;
            WindowChrome.PlaceClient(this, fit.X, fit.Y, fit.Width, fit.Height);
        }
        _refreshPlacement = Place;
        void QueueFit()
        {
            if (_closed || fitQueued) return;
            fitQueued = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { fitQueued = false; Place(); });
        }
        card.SizeChanged += (_, _) => QueueFit();
        host.Loaded += (_, _) =>
        {
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnRootChanged;
            _trackedRoot = host.XamlRoot;
            _trackedRoot.Changed += OnRootChanged;
            QueueFit();
        };
        WindowChrome.EnableDragMove(this, grip, onDragCompleted: Place);
        Place();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) =>
        {
            double now = _elapsed.Elapsed.TotalMilliseconds;
            _clock.Advance((float)(now - _previousMs)); _previousMs = now;
            Present();
            if (!_clock.IsPlaying) _timer.Stop();
        };
        controller.PlaybackChanged += OnPlaybackChanged;
        Closed += (_, _) =>
        {
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnRootChanged;
            _closed = true; _timer.Stop(); controller.PlaybackChanged -= OnPlaybackChanged; controller.EndPlayback();
        };
        _clock.Play(); _previousMs = _elapsed.Elapsed.TotalMilliseconds; _timer.Start(); Present();

        TouchIconButton Button(string glyph, string label, Action action)
        {
            var button = new TouchIconButton { Glyph = glyph, Width = 36, Height = 36, ContentForeground = foreground };
            ToolTipService.SetToolTip(button, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => action(); row.Children.Add(button); return button;
        }
    }

    public void Seek(float elapsedMs) { _clock.Seek(elapsedMs); Present(); }
    private void OnPlaybackChanged() { if (!_closed && _controller.Playback is null) Close(); }
    private void Present()
    {
        _controller.SeekPlayback(_clock.ElapsedMs);
        _seeking = true; _progress.Value = _clock.Progress * 1000; _seeking = false;
        Sync();
    }
    private void Sync()
    {
        _play.Glyph = _clock.IsPlaying ? Glyphs.Pause : Glyphs.Play;
        _time.Text = $"{TimeSpan.FromMilliseconds(_clock.ElapsedMs):mm\\:ss\\.f} / {TimeSpan.FromMilliseconds(_clock.DurationMs):mm\\:ss\\.f}";
    }
}
