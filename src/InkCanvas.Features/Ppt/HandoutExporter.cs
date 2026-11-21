using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Features.Ppt;

public enum HandoutContentMode
{
    AnnotationsOnly,
    AnnotationsWithTeacherNotes,
}

public sealed class HandoutOptions
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public HandoutContentMode Content { get; set; } = HandoutContentMode.AnnotationsWithTeacherNotes;

    public List<int> SelectedSlideIds { get; set; } = new();

    public double PageWidthPt { get; set; } = 595.28;

    public double PageHeightPt { get; set; } = 841.89;
}

public sealed class HandoutPageSource
{
    public int SlideId { get; set; }

    public int Index { get; set; }

    public string Title { get; set; } = "";

    public string SlideText { get; set; } = "";

    public byte[] SlideImagePng { get; set; } = Array.Empty<byte>();

    public string AnnotationMetadataJson { get; set; } = "";

    public string TeacherNotes { get; set; } = "";

    public bool HasAnnotations => !string.IsNullOrWhiteSpace(AnnotationMetadataJson);

    public bool HasTeacherNotes => !string.IsNullOrWhiteSpace(TeacherNotes);
}

public sealed class HandoutPageResult
{
    public int SlideId { get; set; }

    public int Index { get; set; }

    public string PageImagePath { get; set; } = "";

    public List<string> Warnings { get; set; } = new();
}

public sealed class HandoutExportResult
{
    public int Version { get; set; } = 1;

    public List<HandoutPageResult> Pages { get; set; } = new();

    public string PdfPath { get; set; } = "";

    public List<string> Warnings { get; set; } = new();

    public bool Success => Pages.Count > 0;
}

public static class HandoutComposer
{
    /// <summary>按「全部 / 仅批注 / 选中页」筛选页，保持原页序。</summary>
    public static IReadOnlyList<HandoutPageSource> Select(
        IReadOnlyList<HandoutPageSource> pages, HandoutOptions options)
    {
        var result = new List<HandoutPageSource>();
        if (pages is null || pages.Count == 0) return result;

        bool hasSelection = options?.SelectedSlideIds is { Count: > 0 };
        foreach (HandoutPageSource page in pages)
        {
            if (page is null) continue;
            if (hasSelection && !options.SelectedSlideIds.Contains(page.SlideId)) continue;
            if (options?.Content == HandoutContentMode.AnnotationsOnly && !page.HasAnnotations) continue;
            result.Add(page);
        }

        return result;
    }

    /// <summary>讲义页面尺寸校验：返回可用页宽高（pt），非法时回退 A4。</summary>
    public static (double Width, double Height) PageSize(HandoutOptions options)
    {
        double width = options?.PageWidthPt ?? 0;
        double height = options?.PageHeightPt ?? 0;
        if (width < 72 || height < 72) return (595.28, 841.89);
        return (width, height);
    }

    public static string DescribeMode(HandoutContentMode mode)
        => mode == HandoutContentMode.AnnotationsOnly ? "仅批注" : "批注 + 教师解析";
}

/// <summary>把 PNG 解码为 RGB 像素，供最小 PDF 写入器嵌入。</summary>
public static class HandoutImageDecoder
{
    public static async Task<HandoutPdfPage> DecodePngAsync(byte[] png)
    {
        if (png is null || png.Length == 0) return null;

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

        var bgra = new byte[4 * bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyToBuffer(bgra.AsBuffer());

        var rgb = new byte[3 * bitmap.PixelWidth * bitmap.PixelHeight];
        for (int i = 0, j = 0; i + 3 < bgra.Length; i += 4, j += 3)
        {
            rgb[j] = bgra[i + 2];
            rgb[j + 1] = bgra[i + 1];
            rgb[j + 2] = bgra[i];
        }

        return new HandoutPdfPage(bitmap.PixelWidth, bitmap.PixelHeight, rgb);
    }
}

public sealed record HandoutPdfPage(int Width, int Height, byte[] Rgb);

/// <summary>
/// 不依赖外部 PDF 库的最小写入器：每页一张 Flate 压缩的 RGB 图像，按 A4 内切居中。
/// 失败时返回空数组，由调用方重试；不修改任何原始课件文件。
/// </summary>
public static class HandoutPdfBuilder
{
    public static byte[] Build(
        IReadOnlyList<HandoutPdfPage> pages,
        double pageWidthPt = 595.28,
        double pageHeightPt = 841.89,
        double marginPt = 36)
    {
        if (pages is null || pages.Count == 0) return Array.Empty<byte>();
        if (pageWidthPt < 72 || pageHeightPt < 72) return Array.Empty<byte>();

        var output = new MemoryStream();
        var offsets = new List<long> { 0 };
        WriteAscii(output, "%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");

        int objectCount = 2 + pages.Count * 3;
        AddObject(output, offsets, "<< /Type /Catalog /Pages 2 0 R >>");

        var kids = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            if (i > 0) kids.Append(' ');
            kids.Append(3 + i * 3).Append(" 0 R");
        }

        AddObject(output, offsets, "<< /Type /Pages /Kids [" + kids + "] /Count " + pages.Count + " >>");

        for (int i = 0; i < pages.Count; i++)
        {
            HandoutPdfPage page = pages[i];
            if (page is null || page.Width <= 0 || page.Height <= 0 || page.Rgb is null
                || page.Rgb.Length < page.Width * page.Height * 3)
            {
                return Array.Empty<byte>();
            }

            int pageObject = 3 + i * 3;
            int imageObject = pageObject + 1;
            int contentObject = pageObject + 2;

            double availableWidth = Math.Max(1, pageWidthPt - marginPt * 2);
            double availableHeight = Math.Max(1, pageHeightPt - marginPt * 2);
            double scale = Math.Min(availableWidth / page.Width, availableHeight / page.Height);
            double drawWidth = page.Width * scale;
            double drawHeight = page.Height * scale;
            double drawX = (pageWidthPt - drawWidth) / 2.0;
            double drawY = (pageHeightPt - drawHeight) / 2.0;

            AddObject(output, offsets,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 "
                + Fmt(pageWidthPt) + " " + Fmt(pageHeightPt)
                + "] /Resources << /XObject << /Im0 " + imageObject + " 0 R >> >>"
                + " /Contents " + contentObject + " 0 R >>");

            byte[] compressed = Deflate(page.Rgb);
            offsets.Add(output.Position);
            WriteAscii(output, imageObject + " 0 obj\n<< /Type /XObject /Subtype /Image /Width "
                + page.Width + " /Height " + page.Height
                + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length "
                + compressed.Length + " >>\nstream\n");
            output.Write(compressed, 0, compressed.Length);
            WriteAscii(output, "\nendstream\nendobj\n");

            string content = "q\n" + Fmt(drawWidth) + " 0 0 " + Fmt(drawHeight) + " "
                + Fmt(drawX) + " " + Fmt(drawY) + " cm\n/Im0 Do\nQ\n";
            byte[] contentBytes = Encoding.ASCII.GetBytes(content);
            offsets.Add(output.Position);
            WriteAscii(output, contentObject + " 0 obj\n<< /Length " + contentBytes.Length + " >>\nstream\n");
            output.Write(contentBytes, 0, contentBytes.Length);
            WriteAscii(output, "endstream\nendobj\n");
        }

        long xref = output.Position;
        WriteAscii(output, "xref\n0 " + (objectCount + 1) + "\n");
        WriteAscii(output, "0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++)
        {
            long offset = i < offsets.Count ? offsets[i] : 0;
            WriteAscii(output, offset.ToString("D10") + " 00000 n \n");
        }

        WriteAscii(output, "trailer\n<< /Size " + (objectCount + 1) + " /Root 1 0 R >>\nstartxref\n"
            + xref + "\n%%EOF\n");
        return output.ToArray();
    }

    private static void AddObject(MemoryStream output, List<long> offsets, string body)
    {
        offsets.Add(output.Position);
        WriteAscii(output, (offsets.Count - 1) + " 0 obj\n" + body + "\nendobj\n");
    }

    private static void WriteAscii(MemoryStream output, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x9C);
        using (var zip = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zip.Write(data, 0, data.Length);
        }

        uint adler = Adler32(data);
        compressed.WriteByte((byte)(adler >> 24));
        compressed.WriteByte((byte)(adler >> 16));
        compressed.WriteByte((byte)(adler >> 8));
        compressed.WriteByte((byte)adler);
        return compressed.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static string Fmt(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}