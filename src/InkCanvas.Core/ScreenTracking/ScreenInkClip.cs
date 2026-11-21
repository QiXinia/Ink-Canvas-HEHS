using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.ScreenTracking;

/// <summary>Clips alpha in physical overlay pixels without allocating full-screen masks.</summary>
public sealed record ScreenInkClip(Rect Viewport, IReadOnlyList<Rect> Occlusions)
{
    public bool Contains(Vector2 point) => Viewport.Contains(point) && !Occlusions.Any(rect => rect.Contains(point));

    public void Apply(Span<byte> pixels, int width, int height, int originX = 0, int originY = 0, byte background = 1)
    {
        if (pixels.Length < checked(width * height * 4)) throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));
        int left = Math.Clamp((int)Math.Ceiling(Viewport.Left) - originX, 0, width);
        int right = Math.Clamp((int)Math.Floor(Viewport.Right) - originX, left, width);
        int top = Math.Clamp((int)Math.Ceiling(Viewport.Top) - originY, 0, height);
        int bottom = Math.Clamp((int)Math.Floor(Viewport.Bottom) - originY, top, height);
        pixels[..(top * width * 4)].Fill(background);
        pixels[(bottom * width * 4)..(height * width * 4)].Fill(background);
        for (int y = top; y < bottom; y++)
        {
            pixels.Slice(y * width * 4, left * 4).Fill(background);
            pixels.Slice((y * width + right) * 4, (width - right) * 4).Fill(background);
        }
        foreach (var mask in Occlusions)
        {
            int x1 = Math.Clamp((int)Math.Floor(mask.Left) - originX, 0, width);
            int x2 = Math.Clamp((int)Math.Ceiling(mask.Right) - originX, x1, width);
            int y1 = Math.Clamp((int)Math.Floor(mask.Top) - originY, 0, height);
            int y2 = Math.Clamp((int)Math.Ceiling(mask.Bottom) - originY, y1, height);
            for (int y = y1; y < y2; y++) pixels.Slice((y * width + x1) * 4, (x2 - x1) * 4).Fill(background);
        }
    }
}
