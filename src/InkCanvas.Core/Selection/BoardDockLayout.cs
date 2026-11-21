namespace InkCanvas.Core.Selection;

/// <summary>Keep WPF's three 55-DIP strips centered and non-overlapping on smaller displays.</summary>
public readonly record struct BoardDockLayout(double Scale, bool TwoRows, double Height)
{
    public static BoardDockLayout Calculate(double viewportWidth, double requestedScale, double leftWidth, double centerWidth, double rightWidth)
    {
        double width = Math.Max(1, viewportWidth - 12);
        double scale = Math.Clamp(requestedScale, 0.5, 1.5);
        bool twoRows = (centerWidth + 2 * Math.Max(leftWidth, rightWidth) + 16) * scale > width;
        if (twoRows) scale = Math.Min(scale, width / Math.Max(centerWidth, leftWidth + rightWidth + 12));
        return new BoardDockLayout(scale, twoRows, (twoRows ? 114 : 55) * scale);
    }
}
