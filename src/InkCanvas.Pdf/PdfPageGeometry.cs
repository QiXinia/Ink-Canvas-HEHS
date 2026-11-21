using System.Numerics;

namespace InkCanvas.Pdf;

/// <summary>Maps the displayed, rotated CropBox in 96-DPI DIPs to native PDF user space.</summary>
public sealed record PdfPageGeometry(double Left, double Bottom, double Right, double Top, int Rotation, double UserUnit = 1)
{
    public int NormalizedRotation => ((Rotation % 360) + 360) % 360;
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public float DipWidth => (float)((NormalizedRotation is 90 or 270 ? Height : Width) * UserUnit * 96 / 72);
    public float DipHeight => (float)((NormalizedRotation is 90 or 270 ? Width : Height) * UserUnit * 96 / 72);
    public Vector2 ToPdf(Vector2 point)
    {
        float factor = (float)(72 / (96 * UserUnit));
        float x = point.X * factor, y = point.Y * factor;
        return NormalizedRotation switch
        {
            90 => new((float)Left + y, (float)Bottom + x),
            180 => new((float)Right - x, (float)Bottom + y),
            270 => new((float)Right - y, (float)Top - x),
            _ => new((float)Left + x, (float)Top - y),
        };
    }
    public Vector2 FromPdf(Vector2 point)
    {
        float factor = (float)(96 * UserUnit / 72);
        return NormalizedRotation switch
        {
            90 => new Vector2(point.Y - (float)Bottom, point.X - (float)Left) * factor,
            180 => new Vector2((float)Right - point.X, point.Y - (float)Bottom) * factor,
            270 => new Vector2((float)Top - point.Y, (float)Right - point.X) * factor,
            _ => new Vector2(point.X - (float)Left, (float)Top - point.Y) * factor,
        };
    }
}

public sealed record PdfSearchHit(int PageIndex, string Preview, IReadOnlyList<Core.Ink.Rect> Bounds = null);
public sealed record PdfBookmark(string Title, int PageIndex, int Depth);
