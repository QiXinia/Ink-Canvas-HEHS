using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Dispatching;

namespace InkCanvas.Modules.Stylus;

public sealed class StylusService : IDisposable
{
    private readonly IStylusInputHost _host;
    private readonly StylusButtonMapper _mapper = new();
    private readonly Dictionary<(InkController Controller, uint Pointer), int> _penButtons = new();
    private readonly ButtonInputMonitor _buttons;
    private readonly DigitizerButtonMonitor _digitizer;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _dispatcher;
    private Action<StylusButtonSignal> _learn;
    private long _learnUntil;
    private bool _disposed;
    private int _revision;
    private string _lastButton = "尚未接收到按钮";
    public StylusSettings Settings { get; }
    public string Status => _lastButton + " · " + _buttons.Status + " · " + _digitizer.Status;
    public bool IsLearning => _learn is not null;
    public event Action Changed;

    public StylusService(IStylusInputHost host)
    {
        _host = host;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Settings = StylusSettings.Load(host.DataDirectory);
        _buttons = new(HandleButton);
        _digitizer = new(HandleButton, () => IsLearning && _host.CanLearnShortcuts
            || Settings.Enabled && _host.CanHandleShortcuts && Settings.Bindings.Any(binding => binding.Input.StartsWith("hid:", StringComparison.Ordinal)));
        InkController.PointerInput += OnPointer;
        InkController.InputContextChanging += OnContextChanging;
        host.Canvas.TargetChanged += Reset;
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) =>
        {
            if (IsLearning && (Environment.TickCount64 >= _learnUntil || !_host.CanLearnShortcuts)) EndLearning();
            if (_mapper.HasActiveMapping && !_host.CanHandleShortcuts && !IsLearning) Reset();
        };
        _timer.Start();
    }

    public void Configure()
    {
        Reset();
        try { Settings.Save(_host.DataDirectory); }
        catch (Exception ex) { _lastButton = "配置保存失败 · " + ex.Message; }
        Changed?.Invoke();
    }

    public void BeginLearning(Action<StylusButtonSignal> learned)
    {
        Reset();
        _learn = learned;
        _learnUntil = Environment.TickCount64 + 15000;
        Changed?.Invoke();
    }
    public void EndLearning()
    {
        if (_learn is null) return;
        _learn = null;
        Changed?.Invoke();
    }

    public bool HandleButton(StylusButtonSignal signal)
    {
        if (_disposed) return false;
        if (signal.Pressed && IsLearning && _host.CanLearnShortcuts)
        {
            var learned = _learn;
            _learn = null;
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed) return;
                if (signal.Input != "key:27:0") learned(signal);
                Changed?.Invoke();
            });
            return true;
        }
        var binding = Settings.Bindings.FirstOrDefault(binding => binding.Input == signal.Input);
        if (binding is null) return false;
        if (!Settings.Enabled || !_host.CanHandleShortcuts)
        {
            if (!signal.Pressed) Reset();
            return false;
        }
        var target = _host.Canvas.CaptureTarget();
        if (target?.CanEdit != true || !_host.Canvas.IsCurrent(target)) return false;
        if (binding.Action == StylusAction.None) return false;
        _lastButton = signal.Name + (signal.Pressed ? " · 按下" : " · 松开");
        int revision = _revision;
        return _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            if (_disposed || revision != _revision || !Settings.Enabled || !_host.Canvas.IsCurrent(target)) return;
            _mapper.SetButton(target.Controller, binding, signal.Pressed);
        });
    }

    private void OnPointer(InkPointerInput input)
    {
        try { ProcessPointer(input); }
        catch (Exception ex)
        {
            Settings.Enabled = false;
            Reset();
            Core.Diagnostics.FileLog.WriteLogToFile("手写笔映射已暂停 " + ex.Message);
        }
    }

    private void ProcessPointer(InkPointerInput input)
    {
        if (_disposed || !Settings.Enabled) return;
        if (!_host.CanHandlePointer(input.Controller)) return;
        var key = (input.Controller, input.Pointer.Id);
        if (input.Phase is InkPointerPhase.Canceled or InkPointerPhase.Exit)
        {
            if (_penButtons.Remove(key, out var released))
                UpdatePenButtons(input.Controller, released, 0);
            _mapper.Flush();
            return;
        }
        if (input.Pointer.Kind != OverlayPointerKind.Pen) { if (input.Phase == InkPointerPhase.Up) _mapper.Flush(); return; }
        int current = (input.Pointer.BarrelButton ? 1 : 0) | (input.Pointer.SecondaryBarrelButton ? 2 : 0)
            | (input.Pointer.Inverted || input.Pointer.EraserButton ? 4 : 0);
        int previous = _penButtons.GetValueOrDefault(key);
        if (current != previous) UpdatePenButtons(input.Controller, previous, current);
        if (current == 0) _penButtons.Remove(key);
        else _penButtons[key] = current;
        _mapper.Flush();
    }

    private static readonly string[] PenInputs = { "pen:barrel", "pen:secondary", "pen:eraser" };
    private void UpdatePenButtons(InkController controller, int previous, int current)
    {
        for (int index = 0; index < PenInputs.Length; index++)
        {
            int flag = 1 << index;
            if (((previous ^ current) & flag) != 0) SetPenButton(controller, PenInputs[index], (current & flag) != 0);
        }
    }

    private void SetPenButton(InkController controller, string input, bool down)
    {
        var binding = Settings.Bindings.FirstOrDefault(binding => binding.Input == input);
        if (binding is null) return;
        _lastButton = binding.Name + (down ? " · 按下" : " · 松开");
        _mapper.SetButton(controller, binding, down);
    }

    private void Reset() { _revision++; _penButtons.Clear(); _mapper.Reset(); }
    private void OnContextChanging(InkController controller) { if (_mapper.OwnsController(controller)) Reset(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        InkController.PointerInput -= OnPointer;
        InkController.InputContextChanging -= OnContextChanging;
        _host.Canvas.TargetChanged -= Reset;
        _timer.Stop();
        EndLearning();
        _buttons.Dispose(); _digitizer.Dispose(); _mapper.Dispose();
        _penButtons.Clear();
    }
}
