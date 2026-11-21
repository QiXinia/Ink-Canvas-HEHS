using System.Diagnostics;
using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace InkCanvas.App.Views;

/// <summary>A full-surface classroom timer with touch-sized controls and native window corners.</summary>
public sealed class CountdownWindow : Window, IDisposable
{
    private const uint SoundFilename = 0x00020000;
    private const uint SoundAsync = 0x00000001;
    private const uint SoundNoDefault = 0x00000002;
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _elapsed = new();
    private readonly Grid _root = new();
    private Grid _expanded, _adjustLayer;
    private CountdownRing _ring;
    private TextBlock _time, _compactTime, _status, _endTime;
    private Viewbox _bigViewbox, _compactViewbox;
    private TouchIconButton _startButton, _editButton;
    private readonly List<TouchIconButton> _presets = new();
    private readonly List<NumberBox> _durationInputs = new();
    private CheckBox _sound, _repeat;
    private MinSizeHook _minSize;
    private int _configuredSeconds = 60, _totalSeconds = 60, _displaySeconds = -1, _round = 1;
    private bool _isRunning, _isPaused, _editing, _isCompact, _disposed, _updatingInputs;
    private RectInt32 _expandedBounds;
    private bool _restoreMaximized;
    internal bool SoundStarted { get; private set; }

    internal bool IsRunning => _isRunning;
    internal bool IsPaused => _isPaused;
    internal int DisplaySeconds => _displaySeconds;
    internal int Round => _round;
    internal int ConfiguredSeconds => _configuredSeconds;

    public CountdownWindow(int theme = 0)
    {
        Title = "课堂倒计时 - Ink Canvas";
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => OnTick();
        BuildUi();
        Content = _root;
        WindowChrome.MakeStandard(this, 1000, 760);
        WindowFrame.Attach(this, _root);
        WindowChrome.ApplyTheme(this, theme);
        _minSize = new MinSizeHook(this, 540, 580);
        _root.ActualThemeChanged += (_, _) => ApplyPalette();
        _root.Loaded += (_, _) => ApplyPalette();
        _root.KeyDown += OnKeyDown;
        Closed += (_, _) => Dispose();
        ApplyPalette();
        SetDigits(_configuredSeconds);
        RefreshState();
    }

    /// <summary>倒计时开始 / 继续，参数为剩余时长。</summary>
    public event Action<TimeSpan> TimerStarted;

    /// <summary>倒计时暂停 / 停止，参数为剩余时长。</summary>
    public event Action<TimeSpan> TimerStopped;

    /// <summary>倒计时归零结束。</summary>
    public event Action TimerEnded;

    private void BuildUi()
    {
        _expanded = new Grid { Margin = new Thickness(24, 12, 24, 20), RowSpacing = 12 };
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _expanded.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = new TextBlock { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        header.Children.Add(_status);
        var compact = ClassroomControls.Button("精简", ToggleCompact);
        var maximize = ClassroomControls.Button("最大化", ToggleMaximize);
        Grid.SetColumn(compact, 1); Grid.SetColumn(maximize, 2);
        header.Children.Add(compact); header.Children.Add(maximize);
        _expanded.Children.Add(header);

        var presets = new Grid { ColumnSpacing = 8 };
        foreach (int seconds in new[] { 30, 60, 180, 300, 600, 2400, 2700 })
        {
            int column = presets.ColumnDefinitions.Count;
            presets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string label = seconds < 60 ? "30秒" : $"{seconds / 60}分钟";
            var preset = ClassroomControls.Button(label, () => SetDuration(seconds));
            preset.FontSize = 16; preset.Padding = new Thickness(4);
            Grid.SetColumn(preset, column); presets.Children.Add(preset); _presets.Add(preset);

        }
        Grid.SetRow(presets, 1); _expanded.Children.Add(presets);

        _ring = new CountdownRing { Width = 360, Height = 360 };
        _time = new TextBlock { Text = "00:01:00", FontSize = 54, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false };
        _time.Tapped += (_, _) => ToggleEdit();
        AutomationProperties.SetName(_time, "剩余时间，点击调整时长");
        var face = new Grid { Width = 400, Height = 400 };
        face.Children.Add(_ring); face.Children.Add(_time);
        _bigViewbox = new Viewbox { Child = face, Margin = new Thickness(8), Stretch = Stretch.Uniform };

        _adjustLayer = new Grid { Visibility = Visibility.Collapsed, ColumnSpacing = 12, MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int unit = 0; unit < 3; unit++)
        {
            int selectedUnit = unit;
            _adjustLayer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var editor = new StackPanel { Spacing = 12, MinWidth = 112 };
            string label = new[] { "小时", "分钟", "秒" }[unit];
            editor.Children.Add(new TextBlock { Text = label, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
            editor.Children.Add(ClassroomControls.Button("＋", () => Adjust(selectedUnit, 1)));
            var input = new NumberBox { Minimum = 0, Maximum = unit == 0 ? 99 : 59, FontSize = 28,
                MinHeight = 56, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(input, label);
            input.ValueChanged += (_, args) =>
            {
                if (_updatingInputs || _isRunning || !double.IsFinite(args.NewValue)) return;
                int[] parts = Parts();
                parts[selectedUnit] = Math.Clamp((int)Math.Round(args.NewValue), 0, selectedUnit == 0 ? 99 : 59);
                SetDuration((parts[0] * 60 + parts[1]) * 60 + parts[2], keepEditor: true);
            };
            _durationInputs.Add(input); editor.Children.Add(input);
            editor.Children.Add(ClassroomControls.Button("−", () => Adjust(selectedUnit, -1)));
            Grid.SetColumn(editor, unit); _adjustLayer.Children.Add(editor);
        }
        var display = new Grid(); display.Children.Add(_bigViewbox); display.Children.Add(_adjustLayer);
        Grid.SetRow(display, 2); _expanded.Children.Add(display);

        var options = new Grid { ColumnSpacing = 16 };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sound = new CheckBox { Content = "结束提示音", IsChecked = true, MinHeight = 48, FontSize = 16 };
        _repeat = new CheckBox { Content = "循环计时", MinHeight = 48, FontSize = 16 };
        _sound.Unchecked += (_, _) => StopSound();
        _endTime = new TextBlock { FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap };
        options.Children.Add(_sound); Grid.SetColumn(_repeat, 1); options.Children.Add(_repeat);
        Grid.SetColumn(_endTime, 2); options.Children.Add(_endTime);
        Grid.SetRow(options, 3); _expanded.Children.Add(options);

        var controls = new Grid { ColumnSpacing = 10 };
        for (int column = 0; column < 4; column++) controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _editButton = ClassroomControls.Button("调整时长", ToggleEdit);
        _startButton = ClassroomControls.Button("开始", StartPause, primary: true);
        var reset = ClassroomControls.Button("复位", Reset);
        var add = ClassroomControls.Button("加 1 分钟", () => AddTime(60));
        var buttons = new[] { _editButton, _startButton, reset, add };
        for (int column = 0; column < buttons.Length; column++)
        {
            buttons[column].Height = 64; buttons[column].Padding = new Thickness(4);
            Grid.SetColumn(buttons[column], column); controls.Children.Add(buttons[column]);
        }
        Grid.SetRow(controls, 4); _expanded.Children.Add(controls);

        _compactTime = new TextBlock { FontSize = 64, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _compactViewbox = new Viewbox { Child = _compactTime, Margin = new Thickness(20, 12, 20, 80),
            Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        _compactTime.Tapped += (_, _) => ToggleCompact();
        _root.Children.Add(_expanded); _root.Children.Add(_compactViewbox);
        var restore = ClassroomControls.Button("展开", ToggleCompact);
        restore.Margin = new Thickness(16); restore.VerticalAlignment = VerticalAlignment.Bottom;
        restore.HorizontalAlignment = HorizontalAlignment.Center; restore.Visibility = Visibility.Collapsed;
        restore.Tag = "compact-restore"; _root.Children.Add(restore);
    }

    private void ApplyPalette()
    {
        int theme = _root.ActualTheme == ElementTheme.Dark ? 2 : 1;
        _root.Background = PopupBrushes.WindowSurface(theme);
        _time.Foreground = _compactTime.Foreground = _status.Foreground = _endTime.Foreground = PopupBrushes.Foreground(theme);
    }

    public TimeSpan Remaining => TimeSpan.FromSeconds(Math.Max(0, _totalSeconds - _elapsed.Elapsed.TotalSeconds));

    /// <summary>由练习闭环启动指定时长；与点击预设等价，会触发 TimerStarted。</summary>
    public void StartCountdown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || _disposed || _isRunning) return;
        SetDuration((int)Math.Ceiling(duration.TotalSeconds));
        StartPause();
    }

    public void StopCountdown()
    {
        if (!_isRunning) return;
        _timer.Stop(); _elapsed.Stop();
        _isRunning = _isPaused = false;
        _ring.IsPaused = false; _ring.Value = 0;
        StopSound();
        SetDigits(0);
        RefreshState();
        TimerStopped?.Invoke(TimeSpan.Zero);
    }


    private int[] Parts() => new[] { _configuredSeconds / 3600, _configuredSeconds / 60 % 60, _configuredSeconds % 60 };

    internal void SetDuration(int seconds, bool keepEditor = false)
    {
        if (_isRunning) return;
        _configuredSeconds = Math.Clamp(seconds, keepEditor ? 0 : 1, 359999);
        _totalSeconds = _configuredSeconds;
        _round = 1;
        _ring.Value = 0;
        StopSound();
        SetDigits(_configuredSeconds);
        if (!keepEditor) _editing = false;
        SyncEditor(); RefreshState();
    }

    private void SyncEditor()
    {
        _updatingInputs = true;
        try
        {
            int[] values = Parts();
            for (int index = 0; index < values.Length; index++) _durationInputs[index].Value = values[index];
        }
        finally { _updatingInputs = false; }
    }

    private void Adjust(int unit, int delta)
    {
        int[] values = Parts();
        int limit = unit == 0 ? 100 : 60;
        values[unit] = (values[unit] + delta + limit) % limit;
        SetDuration((values[0] * 60 + values[1]) * 60 + values[2], keepEditor: true);
    }

    internal void ToggleEdit()
    {
        if (_isRunning) return;
        _editing = !_editing;
        if (!_editing && _configuredSeconds == 0) SetDuration(1);
        SyncEditor(); RefreshState();
    }

    private void SetDigits(int seconds)
    {
        if (_displaySeconds == seconds) return;
        _displaySeconds = seconds;
        string text = $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        _time.Text = _compactTime.Text = text;
    }

    internal void OnTick()
    {
        if (!_isRunning || _isPaused || _disposed) return;
        double remaining = Math.Max(0, _totalSeconds - _elapsed.Elapsed.TotalSeconds);
        _ring.Value = remaining / _totalSeconds;
        SetDigits((int)Math.Ceiling(remaining));
        if (remaining > 0) return;
        _timer.Stop(); _elapsed.Stop(); _isRunning = false;
        TimerEnded?.Invoke();
        if (_repeat.IsChecked == true)
        {
            _round++;
            StartPause();
        }
        else RefreshState(completed: true);
        PlayFinishSound();
    }

    internal void StartPause()
    {
        if (_disposed) return;
        _editing = false;
        StopSound();
        if (!_isRunning)
        {
            _configuredSeconds = Math.Max(1, _configuredSeconds);
            _totalSeconds = _configuredSeconds;
            _elapsed.Restart(); _isRunning = true; _isPaused = false;
            _timer.Start(); SetDigits(_totalSeconds); _ring.Value = 1;
            TimerStarted?.Invoke(Remaining);
        }
        else if (_isPaused)
        {
            _elapsed.Start(); _isPaused = false; _timer.Start();
            TimerStarted?.Invoke(Remaining);
        }
        else
        {
            _elapsed.Stop(); _isPaused = true; _timer.Stop();
            TimerStopped?.Invoke(Remaining);
        }
        _ring.IsPaused = _isPaused;
        RefreshState();
    }

    internal void Reset()
    {
        bool wasActive = _isRunning;
        _timer.Stop(); _elapsed.Reset(); StopSound();
        _isRunning = _isPaused = _editing = false; _round = 1;
        _ring.Value = 0; _ring.IsPaused = false;
        SetDigits(_configuredSeconds); RefreshState();
        if (wasActive) TimerStopped?.Invoke(TimeSpan.Zero);
    }

    internal void AddTime(int seconds)
    {
        if (_disposed || seconds <= 0) return;
        if (!_isRunning) { SetDuration(Math.Min(359999, _configuredSeconds + seconds)); return; }
        _totalSeconds = Math.Min(359999, _totalSeconds + seconds);
        double remaining = Math.Max(0, _totalSeconds - _elapsed.Elapsed.TotalSeconds);
        SetDigits((int)Math.Ceiling(remaining));
        _ring.Value = remaining / _totalSeconds;
        RefreshState();
    }

    private void RefreshState(bool completed = false)
    {
        _status.Text = completed ? "时间到" : _isPaused ? "已暂停" : _isRunning
            ? (_round > 1 ? $"第 {_round} 轮计时中" : "计时中") : _editing ? "调整时长" : "准备开始";
        _startButton.Content = _isRunning ? (_isPaused ? "继续" : "暂停") : completed ? "再来一次" : "开始";
        AutomationProperties.SetName(_startButton, _startButton.Content.ToString());
        _editButton.Content = _editing ? "完成" : "调整时长";
        _editButton.IsEnabled = !_isRunning;
        foreach (var preset in _presets) preset.IsEnabled = !_isRunning;
        _adjustLayer.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        _bigViewbox.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        _endTime.Text = _isRunning && !_isPaused
            ? $"预计 {DateTime.Now.AddSeconds(Math.Max(0, _totalSeconds - _elapsed.Elapsed.TotalSeconds)):HH:mm} 结束"
            : completed ? "本轮计时已结束" : "";
    }

    private void PlayFinishSound()
    {
        if (_sound.IsChecked != true || _disposed) return;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "TimerDownNotice.wav");
            if (!File.Exists(path)) return;
            // The bundled PCM WAV can use the low-overhead, asynchronous Windows sound API.
            SoundStarted = PlaySoundW(path, IntPtr.Zero, SoundFilename | SoundAsync | SoundNoDefault);
        }
        catch { SoundStarted = false; }
    }

    private void StopSound()
    {
        if (!SoundStarted) return;
        PlaySoundW(null, IntPtr.Zero, 0);
        SoundStarted = false;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(string sound, IntPtr module, uint flags);

    internal void ToggleCompact()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        _minSize.Dispose();
        if (!_isCompact)
        {
            _restoreMaximized = presenter.State == OverlappedPresenterState.Maximized;
            if (_restoreMaximized) presenter.Restore();
            _expandedBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            _minSize = new MinSizeHook(this, 300, 220);
            double scale = WindowChrome.GetDpiScale(this);
            var work = WindowChrome.GetWorkArea(this);
            int width = Math.Min((int)Math.Ceiling(400 * scale), work.Width);
            int height = Math.Min((int)Math.Ceiling(250 * scale), work.Height);
            WindowChrome.Place(this, Math.Clamp(_expandedBounds.X, work.X, work.X + work.Width - width),
                Math.Clamp(_expandedBounds.Y, work.Y, work.Y + work.Height - height), width, height);
        }
        else
        {
            _minSize = new MinSizeHook(this, 540, 580);
            var work = WindowChrome.GetWorkArea(this);
            int width = Math.Min(_expandedBounds.Width, work.Width), height = Math.Min(_expandedBounds.Height, work.Height);
            WindowChrome.Place(this, Math.Clamp(_expandedBounds.X, work.X, work.X + work.Width - width),
                Math.Clamp(_expandedBounds.Y, work.Y, work.Y + work.Height - height), width, height);
            if (_restoreMaximized) presenter.Maximize();
        }
        _isCompact = !_isCompact;
        _expanded.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        _compactViewbox.Visibility = _isCompact ? Visibility.Visible : Visibility.Collapsed;
        _root.Children.OfType<TouchIconButton>().Single(button => (string)button.Tag == "compact-restore").Visibility
            = _isCompact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleMaximize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore(); else presenter.Maximize();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.OriginalSource is TextBox or NumberBox or Button or CheckBox) return;
        if (args.Key == VirtualKey.Space) { StartPause(); args.Handled = true; }
        else if (args.Key == VirtualKey.Escape && _editing) { ToggleEdit(); args.Handled = true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _elapsed.Stop(); _minSize?.Dispose();
        StopSound();
    }
}
