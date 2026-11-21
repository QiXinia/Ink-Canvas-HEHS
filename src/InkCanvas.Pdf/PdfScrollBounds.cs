using System.Numerics;

namespace InkCanvas.Pdf;

/// <summary>Shared document limits for touch, wheel and both scroll bars, in viewport DIPs.</summary>
public readonly record struct PdfScrollBounds(Vector2 Minimum, Vector2 Maximum)
{
    public Vector2 Extent => Maximum - Minimum;
    public Vector2 Clamp(Vector2 translation) => Vector2.Clamp(translation, Minimum, Maximum);
    public Vector2 Offset(Vector2 translation) => Maximum - Clamp(translation);
    public Vector2 Translation(Vector2 offset) => Clamp(Maximum - offset);

    public static PdfScrollBounds Create(Vector2 documentSize, Vector2 viewportSize, float scale)
    {
        var content = documentSize * scale;
        var maximum = new Vector2(16);
        var minimum = Vector2.Min(maximum, viewportSize - content - new Vector2(16));
        // A document narrower/shorter than the viewport stays centered with a full-size thumb.
        if (content.X + 32 <= viewportSize.X) minimum.X = maximum.X = (viewportSize.X - content.X) / 2;
        if (content.Y + 32 <= viewportSize.Y) minimum.Y = maximum.Y = (viewportSize.Y - content.Y) / 2;
        return new(minimum, maximum);
    }
}
