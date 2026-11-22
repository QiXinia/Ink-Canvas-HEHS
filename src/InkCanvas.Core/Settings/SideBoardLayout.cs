namespace InkCanvas.Core.Settings;

/// <summary>显示器工作区（物理像素）。纯数据，便于在非 UI 线程/单元测试中计算。</summary>
public readonly record struct SideBoardWorkArea(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>窗口矩形（物理像素）。</summary>
public readonly record struct SideBoardRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// 板中板窗口的默认布局与位置回退规则。全部以物理像素计算，调用方传入当前 DPI 缩放，
/// 使最小 360×280 DIP 与主展板 40%×70% 的比例在不同缩放下保持一致。
/// </summary>
public static class SideBoardLayout
{
    public const double DefaultWidthFraction = 0.4;
    public const double DefaultHeightFraction = 0.7;
    public const int MinimumWidthDip = 360;
    public const int MinimumHeightDip = 280;

    /// <summary>默认右侧抽屉：宽度为主展板所在工作区的 40%，高度 70%，垂直居中并贴右。</summary>
    public static SideBoardRect Default(SideBoardWorkArea work, double dpiScale)
    {
        if (work.IsEmpty) return default;
        var (minimumWidth, minimumHeight) = MinimumSize(work, dpiScale);
        int width = (int)Math.Round(work.Width * DefaultWidthFraction);
        int height = (int)Math.Round(work.Height * DefaultHeightFraction);
        width = Math.Clamp(width, Math.Min(minimumWidth, work.Width), work.Width);
        height = Math.Clamp(height, Math.Min(minimumHeight, work.Height), work.Height);
        int x = work.Right - width;
        int y = work.Y + (work.Height - height) / 2;
        return new SideBoardRect(x, y, width, height);
    }

    /// <summary>
    /// 打开窗口时使用：记忆矩形完整落在当前工作区内才恢复，否则回退默认右侧抽屉。
    /// 尺寸仍受最小/最大约束。
    /// </summary>
    public static SideBoardRect Resolve(SideBoardSettings saved, SideBoardWorkArea work, double dpiScale)
    {
        if (work.IsEmpty) return default;
        if (saved is not null && saved.HasSavedBounds)
        {
            var rect = new SideBoardRect(saved.X!.Value, saved.Y!.Value, saved.W!.Value, saved.H!.Value);
            if (FitsInside(rect, work)) return Clamp(rect, work, dpiScale);
        }
        return Default(work, dpiScale);
    }

    /// <summary>把矩形夹到工作区内，并保证最小尺寸与不超过工作区。</summary>
    public static SideBoardRect Clamp(SideBoardRect rect, SideBoardWorkArea work, double dpiScale)
    {
        if (work.IsEmpty) return default;
        var (minimumWidth, minimumHeight) = MinimumSize(work, dpiScale);
        int width = Math.Clamp(rect.Width, Math.Min(minimumWidth, work.Width), work.Width);
        int height = Math.Clamp(rect.Height, Math.Min(minimumHeight, work.Height), work.Height);
        int x = Math.Clamp(rect.X, work.X, work.Right - width);
        int y = Math.Clamp(rect.Y, work.Y, work.Bottom - height);
        return new SideBoardRect(x, y, width, height);
    }

    public static bool FitsInside(SideBoardRect rect, SideBoardWorkArea work)
        => !rect.IsEmpty && !work.IsEmpty
           && rect.X >= work.X && rect.Y >= work.Y
           && rect.Right <= work.Right && rect.Bottom <= work.Bottom;

    private static (int Width, int Height) MinimumSize(SideBoardWorkArea work, double dpiScale)
    {
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        return ((int)Math.Ceiling(MinimumWidthDip * scale), (int)Math.Ceiling(MinimumHeightDip * scale));
    }
}

[Flags]
public enum SideBoardHotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>板中板快捷键的默认值与字符串解析（不依赖 UI 线程或 Win32）。</summary>
public static class SideBoardHotkey
{
    public const string DefaultModifiers = "Alt";
    public const string DefaultKey = "N";
    public const uint DefaultVirtualKey = 0x4E; // N

    /// <summary>读取配置并规范化；空/无效时回退 Alt+N。</summary>
    public static (string Modifiers, string Key) Normalize(HotkeySettings settings)
    {
        var binding = settings?.SideBoard;
        string modifiers = NormalizeModifiers(binding?.Modifiers);
        string key = NormalizeKey(binding?.Key);
        return (modifiers, key);
    }

    /// <summary>把规范化后的修饰键解析为标志位；完全无效时回退 Alt。</summary>
    public static bool TryParseModifiers(string text, out SideBoardHotkeyModifiers modifiers)
    {
        modifiers = SideBoardHotkeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text)) { modifiers = SideBoardHotkeyModifiers.Alt; return true; }
        foreach (var part in text.Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "alt": modifiers |= SideBoardHotkeyModifiers.Alt; break;
                case "ctrl":
                case "control": modifiers |= SideBoardHotkeyModifiers.Control; break;
                case "shift": modifiers |= SideBoardHotkeyModifiers.Shift; break;
                case "win":
                case "windows": modifiers |= SideBoardHotkeyModifiers.Win; break;
                default: return false;
            }
        }
        return modifiers != SideBoardHotkeyModifiers.None;
    }

    /// <summary>解析主键为虚拟键码：A-Z、0-9、F1-F24。无效时回退 N。</summary>
    public static uint ParseVirtualKey(string key)
    {
        string text = (key ?? "").Trim();
        if (text.Length == 1)
        {
            char c = char.ToUpperInvariant(text[0]);
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }
        if (text.Length is 2 or 3 && (text[0] is 'F' or 'f')
            && int.TryParse(text.AsSpan(1), out int function) && function is >= 1 and <= 24)
            return (uint)(0x70 + function - 1);
        return DefaultVirtualKey;
    }

    private static string NormalizeModifiers(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryParseModifiers(text, out var flags)) return DefaultModifiers;
        // 固定顺序，保证同一组合写入 JSON 时结果稳定（避免 HashSet 枚举顺序差异）。
        var parts = new List<string>(4);
        if ((flags & SideBoardHotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((flags & SideBoardHotkeyModifiers.Control) != 0) parts.Add("Control");
        if ((flags & SideBoardHotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((flags & SideBoardHotkeyModifiers.Win) != 0) parts.Add("Win");
        return parts.Count == 0 ? DefaultModifiers : string.Join("+", parts);
    }

    private static string NormalizeKey(string key)
    {
        string text = (key ?? "").Trim();
        if (text.Length == 1 && char.IsLetterOrDigit(text[0])) return char.ToUpperInvariant(text[0]).ToString();
        if (text.Length is 2 or 3 && (text[0] is 'F' or 'f')
            && int.TryParse(text.AsSpan(1), out int function) && function is >= 1 and <= 24)
            return "F" + function;
        return DefaultKey;
    }
}