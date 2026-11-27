using InkCanvas.Ink;

namespace InkCanvas.Modules.Stylus;

/// <summary>Tracks physical button edges and restores temporary tools only while it still owns the tool choice.</summary>
public sealed class StylusButtonMapper : IDisposable
{
    private readonly record struct ToolState(InkTool Tool, InkEraserMode EraserMode);
    private sealed record Held(string Input, StylusBinding Binding);
    private readonly List<Held> _temporary = new();
    private readonly HashSet<string> _pressed = new();
    private InkController _controller;
    private ToolState? _temporaryBase, _togglePrevious, _toggleState, _applied;
    private StylusBinding _pending;
    private bool _changing;
    public bool HasPressedButtons => _pressed.Count > 0;
    public bool HasActiveMapping => _temporaryBase is not null || _togglePrevious is not null || _pending is not null || _pressed.Count > 0;
    public bool OwnsController(InkController controller) => ReferenceEquals(_controller, controller);

    public bool SetButton(InkController controller, StylusBinding binding, bool down)
    {
        if (controller is null || binding is null || binding.Action == StylusAction.None) return false;
        if (!ReferenceEquals(_controller, controller)) { Reset(); _controller = controller; controller.ToolChanged += OnToolChanged; }
        if (down)
        {
            if (!_pressed.Add(binding.Input)) return true;
            if (binding.Mode == StylusSwitchMode.Temporary && IsTool(binding.Action))
            {
                _temporaryBase ??= Capture(controller);
                _temporary.Add(new(binding.Input, binding));
            }
            else _pending = binding;
        }
        else
        {
            _pressed.Remove(binding.Input);
            _temporary.RemoveAll(item => item.Input == binding.Input);
        }
        Flush();
        return true;
    }

    public void Flush()
    {
        if (_controller is null || _controller.HasActiveInteraction || _controller.Playback is not null) return;
        _changing = true;
        try
        {
            if (_pending is { } pending)
            {
                _pending = null;
                if (IsTool(pending.Action))
                {
                    var next = State(pending.Action, _controller);
                    _temporary.Clear();
                    _temporaryBase = null;
                    if (pending.Mode == StylusSwitchMode.Toggle && _toggleState == next && Capture(_controller) == next)
                    { Apply(_togglePrevious.Value); _togglePrevious = null; _toggleState = null; }
                    else
                    {
                        _togglePrevious = pending.Mode == StylusSwitchMode.Toggle ? Capture(_controller) : null;
                        _toggleState = pending.Mode == StylusSwitchMode.Toggle ? next : null;
                        Apply(next);
                    }
                }
                else ApplyAction(pending.Action);
            }
            if (_temporary.LastOrDefault() is { } held) Apply(State(held.Binding.Action, _controller));
            else if (_temporaryBase is { } original)
            {
                Apply(original); _temporaryBase = null;
            }
        }
        finally { _changing = false; }
    }

    private void Apply(ToolState state)
    {
        if (_controller.EraserMode != state.EraserMode) _controller.EraserMode = state.EraserMode;
        if (_controller.Tool != state.Tool) _controller.Tool = state.Tool;
        _applied = Capture(_controller);
    }
    private void ApplyAction(StylusAction action)
    {
        switch (action)
        {
            case StylusAction.Undo: _controller.Undo(); break;
            case StylusAction.Redo: _controller.Redo(); break;
            case StylusAction.Larger: _controller.CurrentAttributes.BaseWidth = Math.Clamp(_controller.CurrentAttributes.BaseWidth * 1.2f, .5f, 120); break;
            case StylusAction.Smaller: _controller.CurrentAttributes.BaseWidth = Math.Clamp(_controller.CurrentAttributes.BaseWidth / 1.2f, .5f, 120); break;
        }
    }
    private static bool IsTool(StylusAction action) => action is >= StylusAction.Pen and <= StylusAction.Pan;
    private static ToolState Capture(InkController controller) => new(controller.Tool, controller.EraserMode);
    private static ToolState State(StylusAction action, InkController controller) => action switch
    {
        StylusAction.Pen => new(InkTool.Pen, controller.EraserMode),
        StylusAction.Highlighter => new(InkTool.Highlighter, controller.EraserMode),
        StylusAction.AreaEraser => new(InkTool.Eraser, InkEraserMode.Area),
        StylusAction.StrokeEraser => new(InkTool.Eraser, InkEraserMode.WholeStroke),
        StylusAction.Select => new(InkTool.Select, controller.EraserMode),
        StylusAction.Shape => new(InkTool.Shape, controller.EraserMode),
        StylusAction.Pan => new(InkTool.Pan, controller.EraserMode),
        _ => Capture(controller),
    };
    private void OnToolChanged()
    {
        if (_changing) return;
        _temporary.Clear(); _temporaryBase = null; _togglePrevious = null; _toggleState = null; _applied = null; _pending = null;
    }
    public void Reset()
    {
        if (_controller is null) return;
        _controller.ToolChanged -= OnToolChanged;
        var original = _temporaryBase ?? _togglePrevious;
        if (original is { } restore && _applied == Capture(_controller))
        {
            Apply(restore);
        }
        _controller = null; _temporaryBase = null; _togglePrevious = null; _toggleState = null; _applied = null; _pending = null;
        _temporary.Clear(); _pressed.Clear();
    }
    public void Dispose() => Reset();
}
