using InkCanvas.Core.AiAssistant;

namespace InkCanvas.Features.AiAssistant;

public static class PasteCardLayout
{
    private const double Margin = 8;
    private const double LineHeight = 22;
    private const double VerticalPadding = 16;
    private const double AverageCharWidth = 7.2;
    private const double HorizontalPadding = 24;

    public static (double Left, double Top, double Width, double Height) Place(
        AssistantRect anchor,
        double width,
        double height,
        double viewportWidth,
        double viewportHeight,
        double gap = 16)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        gap = Math.Max(0, gap);

        double maxRight = Math.Max(Margin, viewportWidth - Margin);
        double maxBottom = Math.Max(Margin, viewportHeight - Margin);

        double left = anchor.Right + gap;
        double top = anchor.Top;

        if (left + width > maxRight)
            left = anchor.Left - gap - width;

        if (left < Margin || left + width > maxRight)
            left = Clamp(left, Margin, maxRight - width);

        if (top < Margin || top + height > maxBottom)
            top = Clamp(top, Margin, maxBottom - height);

        return (left, top, width, height);
    }

    public static (int Width, int Height) Estimate(IReadOnlyList<string> lines, int maxWidth = 320, int minWidth = 180)
    {
        int longest = 0;
        int count = 0;

        if (lines != null)
        {
            count = lines.Count;
            foreach (string line in lines)
            {
                if (line == null) continue;
                longest = Math.Max(longest, line.Length);
            }
        }

        int width = (int)Math.Round(longest * AverageCharWidth + HorizontalPadding);
        if (maxWidth < minWidth) maxWidth = minWidth;
        width = Math.Min(Math.Max(width, minWidth), maxWidth);

        int height = (int)Math.Round(VerticalPadding + LineHeight * Math.Max(1, count));
        return (width, height);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max <= min) return min;
        return Math.Min(Math.Max(value, min), max);
    }
}