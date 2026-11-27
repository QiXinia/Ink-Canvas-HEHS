using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;

namespace InkCanvas.Pdf;

/// <summary>Places the original vector page in clipped column strips and preserves its editable source.</summary>
internal static class PdfSpacingContent
{
    private const string Metadata = "/InkCanvasSpacing";
    private static readonly string[] PageBounds = ["/MediaBox", "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox", "/Rotate"];
    private const string AnnotationRect = "/InkCanvasSpacingRect";

    public static void Apply(PdfDocument document, PdfPage page, PdfPageGeometry geometry, PdfPageSpacing spacing)
    {
        var state = new PdfDictionary(document);
        state.Elements.SetInteger("/V", 1);
        state.Elements.SetString("/Layout", JsonSerializer.Serialize(spacing));
        state.Elements.SetString("/Geometry", JsonSerializer.Serialize(geometry));
        page.Elements[Metadata] = state;

        var original = new PdfDictionary(document);
        foreach (string key in PageBounds)
            if (page.Elements[key] is { } item) original.Elements[key] = item;
        state.Elements["/Page"] = original;
        if (spacing.Bands.Length == 0) return;

        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = Box(geometry.Left, geometry.Bottom, geometry.Right, geometry.Top);
        form.Elements["/Resources"] = page.Elements["/Resources"] ?? new PdfDictionary(document);
        using (var compressed = new MemoryStream())
        {
            using (var compressor = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                compressor.Write(page.Contents.CreateSingleContent().Stream?.UnfilteredValue ?? Array.Empty<byte>());
            form.CreateStream(compressed.ToArray());
        }
        form.Elements.SetName("/Filter", "/FlateDecode");
        if (page.Elements["/Group"] is { } group) form.Elements["/Group"] = group;
        document.Internals.AddObject(form);
        state.Elements["/Source"] = form.Reference;

        var objects = new PdfDictionary(document);
        objects.Elements["/InkCanvasPage"] = form.Reference;
        var resources = new PdfDictionary(document);
        resources.Elements["/XObject"] = objects;
        page.Elements["/Resources"] = resources;
        var expanded = spacing.Expand(geometry);
        var content = new StringBuilder();
        content.Append("q 1 1 1 rg ");
        AppendRect(content, expanded.Left, expanded.Bottom, expanded.Width, expanded.Height);
        content.Append(" f Q\n");
        foreach (var lane in spacing.Lanes(geometry))
        foreach (var (source, shift) in lane.ContentSlices(geometry.DipHeight))
        {
            var a = geometry.ToPdf(new Vector2(source.Left, source.Top + shift));
            var b = geometry.ToPdf(new Vector2(source.Right, source.Bottom + shift));
            var delta = geometry.ToPdf(new Vector2(0, shift)) - geometry.ToPdf(Vector2.Zero);
            content.Append("q ");
            AppendRect(content, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            content.Append(" W n 1 0 0 1 ").Append(Number(delta.X)).Append(' ').Append(Number(delta.Y))
                .Append(" cm /InkCanvasPage Do Q\n");
        }
        ReplaceContent(page, Encoding.ASCII.GetBytes(content.ToString()));
        page.CropBox = Box(expanded.Left, expanded.Bottom, expanded.Right, expanded.Top);
        GrowMedia(page, expanded);

        if (!page.Elements.ContainsKey("/Annots")) return;
        for (int index = 0; index < page.Annotations.Count; index++)
        {
            var annotation = page.Annotations[index];
            var box = annotation.Rectangle;
            var nativeCenter = new Vector2((float)((box.X1 + box.X2) / 2), (float)((box.Y1 + box.Y2) / 2));
            var mapped = geometry.ToPdf(spacing.MapSource(geometry, geometry.FromPdf(nativeCenter)));
            var delta = mapped - nativeCenter;
            if (delta.LengthSquared() < .000001f) continue;
            annotation.Elements[AnnotationRect] = box;
            annotation.Rectangle = Box(box.X1 + delta.X, box.Y1 + delta.Y, box.X2 + delta.X, box.Y2 + delta.Y);
        }
    }

    public static bool TryRestore(PdfPage page, PdfPageGeometry displayed, out PdfPageGeometry geometry, out PdfPageSpacing spacing)
    {
        geometry = displayed;
        spacing = PdfPageSpacing.Empty;
        var state = page.Elements.GetDictionary(Metadata);
        if (state?.Elements.GetInteger("/V") != 1) return false;
        PdfPageGeometry original;
        PdfPageSpacing layout;
        try
        {
            string raw = state.Elements.GetString("/Layout");
            if (raw.Length is 0 or > 65536) return false;
            original = JsonSerializer.Deserialize<PdfPageGeometry>(state.Elements.GetString("/Geometry"));
            layout = JsonSerializer.Deserialize<PdfPageSpacing>(raw);
        }
        catch (JsonException) { return false; }
        if (original is null || !float.IsFinite(original.DipWidth) || !float.IsFinite(original.DipHeight)
            || original.DipWidth <= 0 || original.DipHeight <= 0 || layout is null || !layout.IsValid(original)) return false;

        // Even a guide-only page can have outside ink, so export records its original boxes.
        var boxes = state.Elements.GetDictionary("/Page");
        if (layout.Bands.Length > 0)
        {
            var source = state.Elements.GetDictionary("/Source");
            if (source?.Stream is null || boxes is null) return false;
            ReplaceContent(page, source.Stream.UnfilteredValue);
            page.Elements["/Resources"] = source.Elements["/Resources"];
        }
        if (boxes is not null)
        foreach (string key in PageBounds)
        {
            if (boxes.Elements[key] is { } value) page.Elements[key] = value;
            else page.Elements.Remove(key);
        }
        if (page.Elements.ContainsKey("/Annots"))
        for (int index = 0; index < page.Annotations.Count; index++)
        {
            var annotation = page.Annotations[index];
            if (annotation.Elements[AnnotationRect] is not { } rect) continue;
            annotation.Elements["/Rect"] = rect;
            annotation.Elements.Remove(AnnotationRect);
        }
        page.Elements.Remove(Metadata);
        geometry = original;
        spacing = layout;
        return true;
    }

    private static void GrowMedia(PdfPage page, PdfPageGeometry extent)
    {
        var media = page.MediaBox;
        var bleed = page.Elements.ContainsKey("/BleedBox") ? page.BleedBox : media;
        page.BleedBox = Box(Math.Min(bleed.X1, extent.Left), Math.Min(bleed.Y1, extent.Bottom),
            Math.Max(bleed.X2, extent.Right), Math.Max(bleed.Y2, extent.Top));
        page.MediaBox = Box(Math.Min(media.X1, page.BleedBox.X1), Math.Min(media.Y1, page.BleedBox.Y1),
            Math.Max(media.X2, page.BleedBox.X2), Math.Max(media.Y2, page.BleedBox.Y2));
    }

    private static void ReplaceContent(PdfPage page, byte[] content)
    {
        page.Contents.Elements.Clear();
        page.Contents.AppendContent().CreateStream(content);
    }

    private static void AppendRect(StringBuilder result, double x, double y, double width, double height) =>
        result.Append(Number(x)).Append(' ').Append(Number(y)).Append(' ').Append(Number(width)).Append(' ').Append(Number(height)).Append(" re");

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static PdfRectangle Box(double left, double bottom, double right, double top) =>
        new(new PdfSharp.Drawing.XRect(left, bottom, right - left, top - bottom));
}
