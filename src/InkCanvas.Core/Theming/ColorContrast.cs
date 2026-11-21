using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Theming;

public static class ColorContrast
{
    /// <summary>Choose the higher-contrast black or white foreground for an opaque fill.</summary>
    public static InkColor Foreground(InkColor background)
        => Ratio(InkColor.Black, background) >= Ratio(InkColor.White, background) ? InkColor.Black : InkColor.White;

    public static double Ratio(InkColor first, InkColor second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static double Luminance(InkColor color)
        => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);

    private static double Linear(byte component)
    {
        double value = component / 255d;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}
