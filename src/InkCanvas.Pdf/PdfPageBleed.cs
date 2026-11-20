using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Pdf;

/// <summary>Expands visible page bounds around margin ink without moving the original PDF content.</summary>
public static class PdfPageBleed
{
    public static PdfPageGeometry IncludeInk(PdfPageGeometry page, IReadOnlyList<InkStroke> strokes)
    {
        var bounds = new Rect(0, 0, page.DipWidth, page.DipHeight);
        foreach (var stroke in strokes) bounds = bounds.Union(stroke.Bounds);
        if (bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= page.DipWidth && bounds.Bottom <= page.DipHeight) return page;
        const float padding = 4;
        float left = bounds.Left < 0 ? bounds.Left - padding : 0;
        float top = bounds.Top < 0 ? bounds.Top - padding : 0;
        float right = bounds.Right > page.DipWidth ? bounds.Right + padding : page.DipWidth;
        float bottom = bounds.Bottom > page.DipHeight ? bounds.Bottom + padding : page.DipHeight;
        var corners = new[] { page.ToPdf(new(left, top)), page.ToPdf(new(right, top)), page.ToPdf(new(left, bottom)), page.ToPdf(new(right, bottom)) };
        double x1 = Math.Floor(corners.Min(point => point.X) * 100d) / 100;
        double y1 = Math.Floor(corners.Min(point => point.Y) * 100d) / 100;
        double x2 = Math.Ceiling(corners.Max(point => point.X) * 100d) / 100;
        double y2 = Math.Ceiling(corners.Max(point => point.Y) * 100d) / 100;
        return new(x1, y1, x2, y2, page.Rotation, page.UserUnit);
    }

    public static Vector2 InkOffset(PdfPageGeometry original, PdfPageGeometry expanded)
        => expanded.FromPdf(original.ToPdf(Vector2.Zero));
}
