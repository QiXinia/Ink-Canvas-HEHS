using System.Runtime.InteropServices;
using InkCanvas.Platform.Interop;

namespace InkCanvas.Modules.Stylus;

public readonly record struct StylusButtonSignal(string Input, string Name, bool Pressed);

internal sealed class ButtonInputMonitor : IDisposable
{
    private delegate nint Hook(int code, nint message, nint data);
    private readonly Hook _keyboardCallback, _mouseCallback;
    private readonly Func<StylusButtonSignal, bool> _handle;
    private readonly Dictionary<uint, StylusButtonSignal> _keys = new();
    private readonly HashSet<int> _mouseButtons = new();
    private nint _keyboard, _mouse;
    public string Status => _keyboard != 0 && _mouse != 0 ? "键盘与鼠标按钮接收正常" : "部分按钮监听不可用";

    public ButtonInputMonitor(Func<StylusButtonSignal, bool> handle)
    {
        _handle = handle;
        _keyboardCallback = Keyboard;
        _mouseCallback = Mouse;
        _keyboard = SetWindowsHookExW(13, _keyboardCallback, 0, 0);
        _mouse = SetWindowsHookExW(14, _mouseCallback, 0, 0);
    }

    private nint Keyboard(int code, nint message, nint data)
    {
        try
        {
            if (code < 0) return CallNextHookEx(0, code, message, data);
            var key = Marshal.PtrToStructure<KeyboardData>(data);
            // Tablet drivers commonly emit their configured shortcuts through synthesized keyboard input.
            if (key.Key is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C)
                return CallNextHookEx(0, code, message, data);
            bool down = message is 0x100 or 0x104;
            if (down && _keys.ContainsKey(key.Key)) return 1;
            if (!down)
            {
                if (!_keys.Remove(key.Key, out var previous)) return CallNextHookEx(0, code, message, data);
                _handle(previous with { Pressed = false });
                return 1;
            }
            int modifiers = (Down(0x11) ? 1 : 0) | (Down(0x12) ? 2 : 0) | (Down(0x10) ? 4 : 0) | (Down(0x5B) || Down(0x5C) ? 8 : 0);
            string name = string.Concat((modifiers & 1) != 0 ? "Ctrl+" : "", (modifiers & 2) != 0 ? "Alt+" : "",
                (modifiers & 4) != 0 ? "Shift+" : "", (modifiers & 8) != 0 ? "Win+" : "", (Windows.System.VirtualKey)key.Key);
            var signal = new StylusButtonSignal($"key:{key.Key}:{modifiers}", name, true);
            if (_handle(signal)) { _keys[key.Key] = signal; return 1; }
        }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("手写笔按键接收 " + ex.Message); }
        return CallNextHookEx(0, code, message, data);
    }

    private nint Mouse(int code, nint message, nint data)
    {
        try
        {
            if (code < 0) return CallNextHookEx(0, code, message, data);
            var mouse = Marshal.PtrToStructure<MouseData>(data);
            if ((mouse.Extra.ToInt64() & 0xFFFFFF00L) == 0xFF515700L)
                return CallNextHookEx(0, code, message, data);
            int button = message switch { 0x204 or 0x205 => 2, 0x207 or 0x208 => 3, 0x20B or 0x20C => (int)(mouse.Data >> 16) + 3, _ => 0 };
            if (button == 0) return CallNextHookEx(0, code, message, data);
            bool down = message is 0x204 or 0x207 or 0x20B;
            string name = button switch { 2 => "鼠标右键 / 驱动侧键", 3 => "鼠标中键", 4 => "鼠标扩展键 1", _ => "鼠标扩展键 2" };
            var signal = new StylusButtonSignal("mouse:" + button, name, down);
            if (!down)
            {
                if (!_mouseButtons.Remove(button)) return CallNextHookEx(0, code, message, data);
                _handle(signal); return 1;
            }
            var target = WindowFromPoint(new NativeMethods.POINT { X = mouse.X, Y = mouse.Y });
            NativeMethods.GetWindowThreadProcessId(target, out uint process);
            if (process != Environment.ProcessId) return CallNextHookEx(0, code, message, data);
            if (_handle(signal)) { _mouseButtons.Add(button); return 1; }
        }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("手写笔鼠标按钮接收 " + ex.Message); }
        return CallNextHookEx(0, code, message, data);
    }

    private static bool Down(int key) => GetAsyncKeyState(key) < 0;
    public void Dispose()
    {
        if (_keyboard != 0) UnhookWindowsHookEx(_keyboard);
        if (_mouse != 0) UnhookWindowsHookEx(_mouse);
        _keyboard = _mouse = 0;
        _keys.Clear(); _mouseButtons.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, Scan, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int kind, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativeMethods.POINT point);
}
