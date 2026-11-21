namespace InkCanvas.Core.AiAssistant;

public readonly record struct AssistantPlacement(double X, double Y, bool FlippedX, bool FlippedY, int Side);

public static class AssistantLayout
{
    public static AssistantPlacement Place(
        AssistantRect anchor,
        double cardWidth,
        double cardHeight,
        double viewportWidth,
        double viewportHeight,
        double gap = 12,
        double margin = 12)
    {
        cardWidth = Math.Max(0, cardWidth);
        cardHeight = Math.Max(0, cardHeight);
        gap = Math.Max(0, gap);
        margin = Math.Max(0, margin);

        double maxRight = Math.Max(margin, viewportWidth - margin);
        double maxBottom = Math.Max(margin, viewportHeight - margin);

        double left = anchor.Left;
        double top = anchor.Bottom + gap;
        bool flippedX = false;
        bool flippedY = false;
        int side = 0;

        if (top + cardHeight > maxBottom)
        {
            top = anchor.Top - gap - cardHeight;
            flippedY = true;
            side = 1;
        }

        if (top < margin || top + cardHeight > maxBottom)
            top = Clamp(top, margin, maxBottom - cardHeight);

        if (left + cardWidth > maxRight)
        {
            double rightSide = anchor.Right + gap;
            double leftSide = anchor.Left - gap - cardWidth;

            if (rightSide + cardWidth <= maxRight)
            {
                left = rightSide;
                side = 2;
                flippedX = true;
            }
            else if (leftSide >= margin)
            {
                left = leftSide;
                side = 3;
                flippedX = true;
            }
            else
            {
                side = 2;
                flippedX = true;
            }
        }

        if (left < margin || left + cardWidth > maxRight)
            left = Clamp(left, margin, maxRight - cardWidth);

        return new AssistantPlacement(left, top, flippedX, flippedY, side);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max <= min) return min;
        return Math.Min(Math.Max(value, min), max);
    }
}