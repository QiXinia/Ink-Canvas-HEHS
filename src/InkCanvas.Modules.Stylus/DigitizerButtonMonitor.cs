using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using InkCanvas.Platform.Interop;

namespace InkCanvas.Modules.Stylus;

/// <summary>Reads standard digitizer HID button usages, retaining independent state for each device and report.</summary>
internal sealed class DigitizerButtonMonitor : IDisposable
{
    private delegate nint WindowProc(nint window, uint message, nint w, nint l);
    private sealed record Device(string Key, byte[] Preparsed);
    private readonly Func<StylusButtonSignal, bool> _handle;
    private readonly Func<bool> _enabled;
    private readonly WindowProc _callback;
    private readonly string _class = "InkCanvasStylus" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<nint, Device> _devices = new();
    private readonly Dictionary<(nint Device, byte Report), HashSet<(ushort Page, ushort Usage)>> _pressed = new();
    private nint _window;
    private bool _registered;
    public string Status { get; private set; } = "标准 HID 按钮接收未启动";

    public DigitizerButtonMonitor(Func<StylusButtonSignal, bool> handle, Func<bool> enabled)
    {
        _handle = handle; _enabled = enabled; _callback = Receive;
        // Avoid replacing a raw-input destination that another host component already owns.
        uint count = 0;
        GetRegisteredRawInputDevices(null, ref count, (uint)Marshal.SizeOf<RawDevice>());
        var existing = new RawDevice[count];
        if (count > 0 && GetRegisteredRawInputDevices(existing, ref count, (uint)Marshal.SizeOf<RawDevice>()) == uint.MaxValue) return;
        if (existing.Any(device => device.Page == 0x0D)) { Status = "HID 接收已由其他组件管理，可使用驱动快捷键"; return; }
        var wc = new NativeMethods.WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_callback), lpszClassName = _class };
        if (NativeMethods.RegisterClassW(ref wc) == 0) return;
        _window = NativeMethods.CreateWindowExW(0, _class, "", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
        if (_window == 0) return;
        var registration = new RawDevice { Page = 0x0D, Usage = 0, Flags = 0x100 | 0x20 | 0x2000, Target = _window };
        _registered = RegisterRawInputDevices(new[] { registration }, 1, (uint)Marshal.SizeOf<RawDevice>());
        Status = _registered ? "标准 HID 数位板按钮接收正常" : "HID 注册失败，可使用驱动快捷键";
    }

    private nint Receive(nint window, uint message, nint w, nint l)
    {
        try
        {
            if (message == 0x00FF && _enabled()) Read(l);
            if (message == 0x00FE && w == 2) ReleaseDevice(l);
        }
        catch (Exception ex) { Status = "部分 HID 报告不可用 · " + ex.Message; }
        return NativeMethods.DefWindowProcW(window, message, w, l);
    }

    private void Read(nint input)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawHeader>();
        if (GetRawInputData(input, 0x10000003, null, ref size, headerSize) == uint.MaxValue || size < headerSize + 8 || size > 65536) return;
        var data = new byte[size];
        if (GetRawInputData(input, 0x10000003, data, ref size, headerSize) != size) return;
        var header = MemoryMarshal.Read<RawHeader>(data);
        if (header.Type != 2) return;
        if (!_devices.TryGetValue(header.Device, out var device))
        {
            if (_devices.Count >= 32) return;
            uint length = 0;
            GetRawInputDeviceInfoW(header.Device, 0x20000005, null, ref length);
            if (length is 0 or > 65536) return;
            var preparsed = new byte[length];
            if (GetRawInputDeviceInfoW(header.Device, 0x20000005, preparsed, ref length) == uint.MaxValue) return;
            uint nameLength = 0;
            GetRawInputDeviceInfoW(header.Device, 0x20000007, null, ref nameLength);
            if (nameLength is 0 or > 4096) return;
            var name = new byte[(nameLength + 1) * 2];
            if (GetRawInputDeviceInfoW(header.Device, 0x20000007, name, ref nameLength) == uint.MaxValue) return;
            string key = Convert.ToHexString(SHA256.HashData(name)).Substring(0, 16);
            device = new(key, preparsed);
            _devices[header.Device] = device;
        }
        int reportSize = BitConverter.ToInt32(data, (int)headerSize);
        int reportCount = BitConverter.ToInt32(data, (int)headerSize + 4);
        if (reportSize <= 0 || reportCount <= 0 || reportCount > 256 || (long)reportSize * reportCount > size - headerSize - 8) return;
        for (int index = 0; index < reportCount; index++)
        {
            var report = data.AsSpan((int)headerSize + 8 + index * reportSize, reportSize).ToArray();
            var usages = new Usage[128];
            uint usageCount = (uint)usages.Length;
            if (HidP_GetUsagesEx(0, 0, usages, ref usageCount, device.Preparsed, report, (uint)report.Length) != 0x00110000) continue;
            var current = usages.Take((int)Math.Min(usageCount, usages.Length))
                .Where(usage => usage.Page == 9 || usage.Page == 0x0D && usage.Id == 0x5A)
                .Select(usage => (usage.Page, usage.Id)).ToHashSet();
            var key = (header.Device, report[0]);
            var previous = _pressed.GetValueOrDefault(key) ?? new();
            foreach (var usage in previous.Except(current)) Emit(device, report[0], usage, false);
            foreach (var usage in current.Except(previous)) Emit(device, report[0], usage, true);
            if (current.Count == 0) _pressed.Remove(key);
            else _pressed[key] = current;
        }
    }

    private void Emit(Device device, byte report, (ushort Page, ushort Usage) usage, bool down)
        => _handle(new($"hid:{device.Key}:{report}:{usage.Page}:{usage.Usage}",
            $"数位板 {device.Key[..4]} · 按钮 {usage.Page:X2}/{usage.Usage:X2}", down));

    private void ReleaseDevice(nint handle)
    {
        if (!_devices.Remove(handle, out var device)) return;
        foreach (var pair in _pressed.Where(pair => pair.Key.Device == handle).ToArray())
        {
            foreach (var usage in pair.Value) Emit(device, pair.Key.Report, usage, false);
            _pressed.Remove(pair.Key);
        }
    }

    public void Dispose()
    {
        foreach (var device in _devices.Keys.ToArray()) ReleaseDevice(device);
        if (_registered)
        {
            uint count = 0;
            GetRegisteredRawInputDevices(null, ref count, (uint)Marshal.SizeOf<RawDevice>());
            var devices = new RawDevice[count];
            if (GetRegisteredRawInputDevices(devices, ref count, (uint)Marshal.SizeOf<RawDevice>()) != uint.MaxValue
                && devices.Any(device => device.Page == 0x0D && device.Target == _window))
                RegisterRawInputDevices(new[] { new RawDevice { Page = 0x0D, Flags = 1 } }, 1, (uint)Marshal.SizeOf<RawDevice>());
        }
        if (_window != 0) NativeMethods.DestroyWindow(_window);
        _window = 0;
        UnregisterClassW(_class, 0);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public nint Device, Parameter; }
    [StructLayout(LayoutKind.Sequential)] private struct Usage { public ushort Id, Page; }
    [DllImport("user32.dll")] private static extern uint GetRegisteredRawInputDevices([Out] RawDevice[] devices, ref uint count, uint size);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint input, uint command, [Out] byte[] data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceInfoW(nint device, uint command, [Out] byte[] data, ref uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint module);
    [DllImport("hid.dll")] private static extern int HidP_GetUsagesEx(int type, ushort collection, [Out] Usage[] usages, ref uint length, byte[] preparsed, byte[] report, uint reportLength);
}
