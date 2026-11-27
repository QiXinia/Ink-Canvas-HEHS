using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Settings;

/// <summary>
/// Menu labels, palette colours, and control ranges transcribed from the WPF menu at
/// commit 8b2c95b. Keep persistence values in the owning settings model.
/// </summary>
public static class OriginalMenuCatalog
{
    public readonly record struct Swatch(string Name, InkColor DarkTheme, InkColor LightTheme);

    public static IReadOnlyList<Swatch> PenColors { get; } = Array.AsReadOnly(new[]
    {
        new Swatch("黑色", InkColor.Opaque(0, 0, 0), InkColor.Opaque(0, 0, 0)),
        new Swatch("白色", InkColor.Opaque(255, 255, 255), InkColor.Opaque(255, 255, 255)),
        new Swatch("红色", InkColor.Opaque(220, 38, 38), InkColor.Opaque(239, 68, 68)),
        new Swatch("粉色", InkColor.Opaque(147, 51, 234), InkColor.Opaque(236, 72, 153)),
        new Swatch("橙色", InkColor.Opaque(234, 88, 12), InkColor.Opaque(249, 115, 22)),
        new Swatch("黄色", InkColor.Opaque(234, 179, 8), InkColor.Opaque(250, 204, 21)),
        new Swatch("绿色", InkColor.Opaque(22, 163, 74), InkColor.Opaque(34, 197, 94)),
        new Swatch("青色", InkColor.Opaque(13, 148, 136), InkColor.Opaque(20, 184, 166)),
        new Swatch("蓝色", InkColor.Opaque(37, 99, 235), InkColor.Opaque(59, 130, 246)),
    });

    public static IReadOnlyList<Swatch> HighlighterColors { get; } = Array.AsReadOnly(new[]
    {
        new Swatch("荧光笔红色", InkColor.Opaque(220, 38, 38), InkColor.Opaque(220, 38, 38)),
        new Swatch("荧光笔橙色", InkColor.Opaque(234, 88, 12), InkColor.Opaque(234, 88, 12)),
        new Swatch("荧光笔黄色", InkColor.Opaque(234, 179, 8), InkColor.Opaque(234, 179, 8)),
        new Swatch("荧光笔绿色", InkColor.Opaque(22, 163, 74), InkColor.Opaque(22, 163, 74)),
        new Swatch("荧光笔青色", InkColor.Opaque(13, 148, 136), InkColor.Opaque(13, 148, 136)),
        new Swatch("荧光笔蓝色", InkColor.Opaque(37, 99, 235), InkColor.Opaque(37, 99, 235)),
        new Swatch("荧光笔紫色", InkColor.Opaque(147, 51, 234), InkColor.Opaque(147, 51, 234)),
    });

    public static IReadOnlyList<string> PenNibs { get; } = Array.AsReadOnly(new[] { "圆形", "椭圆 · 右斜 45°" });

    public static IReadOnlyList<string> PenStyles { get; } = Array.AsReadOnly(new[]
    {
        "点集笔锋", "速率笔锋", "关闭笔锋",
        "平滑硬笔", "柔和压感", "自然笔锋", "书法笔锋", "水墨笔锋",
    });

    public static TaperKind ToTaperKind(int style) => style switch
    {
        1 => TaperKind.Velocity,
        2 => TaperKind.None,
        3 => TaperKind.SmoothPen,
        4 => TaperKind.SoftPen,
        5 => TaperKind.Natural,
        6 => TaperKind.Calligraphy,
        7 => TaperKind.InkWash,
        _ => TaperKind.Tail,
    };

    public static int ToPenStyle(TaperKind kind) => kind switch
    {
        TaperKind.Velocity => 1,
        TaperKind.None => 2,
        TaperKind.SmoothPen => 3,
        TaperKind.SoftPen => 4,
        TaperKind.Natural => 5,
        TaperKind.Calligraphy => 6,
        TaperKind.InkWash => 7,
        _ => 0,
    };

    private static readonly float[] EraserFactors = { .5f, .8f, 1f, 1.25f, 1.8f };
    public static float ToEraserRadius(int size) => 45 * EraserFactors[Math.Clamp(size, 0, EraserFactors.Length - 1)];

    public const double EraserDiameterMinimum = 10;
    public const double EraserDiameterMaximum = 200;

    public static double GetEraserDiameter(CanvasSettings settings) => settings.EraserDiameter is double diameter && double.IsFinite(diameter)
        ? Math.Clamp(diameter, EraserDiameterMinimum, EraserDiameterMaximum)
        : ToEraserRadius(settings.EraserSize) * 2;

    public static float ToEraserRadius(CanvasSettings settings) => (float)(GetEraserDiameter(settings) / 2);

    public static IReadOnlyList<string> EraserSizes { get; } = Array.AsReadOnly(new[]
    {
        "小", "较小", "中（默认）", "较大", "大",
    });

    public static IReadOnlyList<string> EraserModes { get; } = Array.AsReadOnly(new[]
    {
        "图标切换", "面积擦", "线条擦", "套索擦",
    });

    public static IReadOnlyList<int> AutoDeleteDays { get; } = Array.AsReadOnly(new[]
    {
        1, 3, 5, 7, 15, 30, 60, 100, 365,
    });

    public static IReadOnlyList<string> ThemeOptions { get; } = Array.AsReadOnly(new[]
    {
        "浅色主题", "深色主题", "跟随系统",
    });

    public const double PenWidthMinimum = 1;
    public const double PenWidthMaximum = 30;
    public const double HighlighterWidthMaximum = 120;
    public const double PenWidthStep = 1;
    public const double HighlighterOpacityMinimum = 0;
    public const double HighlighterOpacityMaximum = 100;
    public const double HighlighterOpacityStep = 1;

    public static double ToHighlighterOpacityPercent(int alpha) =>
        Math.Round(Math.Clamp(alpha, 0, 255) * 100d / 255, MidpointRounding.AwayFromZero);

    public static int ToStoredHighlighterAlpha(double percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100, MidpointRounding.AwayFromZero);

    public static double ToStoredPenWidth(double menuWidth) => menuWidth / 2;

    public static double ToMenuPenWidth(double storedWidth) => storedWidth * 2;
}
