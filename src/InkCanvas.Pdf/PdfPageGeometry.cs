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
    public Matrix3x2 TransformTo(PdfPageGeometry target)
    {
        if (Left == target.Left && Bottom == target.Bottom && Right == target.Right && Top == target.Top && UserUnit == target.UserUnit)
            return ((target.NormalizedRotation - NormalizedRotation + 360) % 360) switch
            {
                90 => new(0, 1, -1, 0, DipHeight, 0),
                180 => new(-1, 0, 0, -1, DipWidth, DipHeight),
                270 => new(0, -1, 1, 0, 0, DipWidth),
                _ => Matrix3x2.Identity,
            };
        var origin = target.FromPdf(ToPdf(Vector2.Zero));
        var x = (target.FromPdf(ToPdf(new(DipWidth, 0))) - origin) / DipWidth;
        var y = (target.FromPdf(ToPdf(new(0, DipHeight))) - origin) / DipHeight;
        return new(x.X, x.Y, y.X, y.Y, origin.X, origin.Y);
    }
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
