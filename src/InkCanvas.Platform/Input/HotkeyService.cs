using InkCanvas.Platform.Interop;

namespace InkCanvas.Platform.Input;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
    NoRepeat = 0x4000,
}

/// <summary>
/// Global hotkeys registered against a window that pumps WM_HOTKEY
/// (see <see cref="Windows.LayeredInkWindow.HotkeyPressed"/>).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private sealed class Binding(HotkeyModifiers modifiers, uint key, Action action, Func<bool> enabled)
    {
        public readonly HotkeyModifiers Modifiers = modifiers;
        public readonly uint Key = key;
        public readonly Action Action = action;
        public readonly Func<bool> Enabled = enabled;
        public int? RegisteredId;
    }
    private readonly List<Binding> _bindings = new();
    private readonly Dictionary<int, Binding> _registered = new();
    private readonly Func<IntPtr, int, uint, uint, bool> _register;
    private readonly Func<IntPtr, int, bool> _unregister;
    private int _nextId = 0x1000;
    private bool _disposed;

    public HotkeyService(IntPtr hwnd) : this(hwnd, NativeMethods.RegisterHotKey, NativeMethods.UnregisterHotKey) { }

    public HotkeyService(IntPtr hwnd, Func<IntPtr, int, uint, uint, bool> register, Func<IntPtr, int, bool> unregister)
    {
        _hwnd = hwnd;
        _register = register ?? throw new ArgumentNullException(nameof(register));
        _unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    }

    public bool Register(HotkeyModifiers modifiers, uint virtualKey, Action action, Func<bool> enabled = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(action);
        var binding = new Binding(modifiers, virtualKey, action, enabled);
        _bindings.Add(binding);
        return enabled?.Invoke() == false || TryRegister(binding);
    }

    private bool TryRegister(Binding binding)
    {
        // Fresh IDs prevent queued WM_HOTKEY messages from an earlier mode firing after re-entry.
        if (_nextId > 0xBFFF) _nextId = 0x1000;
        while (_registered.ContainsKey(_nextId))
            if (++_nextId > 0xBFFF) _nextId = 0x1000;
        int id = _nextId++;
        if (!_register(_hwnd, id, (uint)binding.Modifiers | (uint)HotkeyModifiers.NoRepeat, binding.Key)) return false;
        binding.RegisteredId = id;
        _registered.Add(id, binding);
        return true;
    }

    /// <summary>Release inactive shortcuts so the foreground application receives the original keys.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        foreach (var binding in _bindings)
        {
            bool enabled = binding.Enabled?.Invoke() != false;
            if (enabled && !binding.RegisteredId.HasValue) TryRegister(binding);
            else if (!enabled && binding.RegisteredId is { } id)
            {
                _unregister(_hwnd, id);
                _registered.Remove(id);
                binding.RegisteredId = null;
            }
        }
    }

    /// <summary>Dispatch a WM_HOTKEY id. Returns true when it was ours.</summary>
    public bool Dispatch(int id)
    {
        if (_disposed || !_registered.TryGetValue(id, out var binding)) return false;
        if (binding.Enabled?.Invoke() == false) { Refresh(); return false; }
        binding.Action();
        Refresh();
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered.Keys) _unregister(_hwnd, id);
        _registered.Clear();
        _bindings.Clear();
    }

    internal int? GetRegisteredId(HotkeyModifiers modifiers, uint key) => _registered
        .Where(item => item.Value.Modifiers == modifiers && item.Value.Key == key).Select(item => (int?)item.Key).FirstOrDefault();

    public void Dispose()
    {
        if (_disposed) return;
        UnregisterAll(); _disposed = true;
    }
}
