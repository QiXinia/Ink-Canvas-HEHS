using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Storage.Streams;

namespace InkCanvas.Features.Ppt;

public sealed class HandoutRenderRequest
{
    public SlideContentIndex Index { get; set; }

    public HandoutOptions Options { get; set; } = new();

    public int PageWidthPx { get; set; } = 1240;

    public int PageHeightPx { get; set; } = 1754;
}

/// <summary>
/// 讲义导出：把选中的课件页截图与教师解析按页合成 PNG，再写入 PDF。
/// 只读取课件目录中的截图与索引，不修改原课件；失败时整体重试一次。
/// </summary>
public static class HandoutExportService
{
    public static async Task<HandoutExportResult> ExportAsync(
        HandoutRenderRequest request, string outputDirectory)
    {
        HandoutExportResult first = await ExportOnceAsync(request, outputDirectory);
        if (first.Success) return first;
        if (string.IsNullOrWhiteSpace(outputDirectory)) return first;

        HandoutExportResult retry = await ExportOnceAsync(request, outputDirectory);
        retry.Warnings.InsertRange(0, first.Warnings);
        return retry;
    }

    private static async Task<HandoutExportResult> ExportOnceAsync(
        HandoutRenderRequest request, string outputDirectory)
    {
        var result = new HandoutExportResult();
        if (request?.Index is null || request.Options is null)
        {
            result.Warnings.Add("没有可导出的课件页。");
            return result;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            result.Warnings.Add("导出目录不可用。");
            return result;
        }

        IReadOnlyList<HandoutPageSource> pages = HandoutComposer.Select(
            ToSources(request.Index, request.Options), request.Options);
        if (pages.Count == 0)
        {
            result.Warnings.Add("当前筛选下没有可导出的页。");
            return result;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            result.Warnings.Add("无法创建导出目录：" + ex.Message);
            return result;
        }

        var pngPages = new List<byte[]>();
        int width = Math.Max(320, request.PageWidthPx);
        int height = Math.Max(480, request.PageHeightPx);

        foreach (HandoutPageSource page in pages)
        {
            byte[] png = await RenderPageAsync(page, request.Options, width, height);
            if (png.Length == 0)
            {
                result.Warnings.Add("第 " + page.Index + " 页渲染失败，已跳过。");
                continue;
            }

            string path = Path.Combine(
                outputDirectory,
                "handout-" + page.Index.ToString("D3") + ".png");
            await File.WriteAllBytesAsync(path, png);
            pngPages.Add(png);
            result.Pages.Add(new HandoutPageResult
            {
                SlideId = page.SlideId,
                Index = page.Index,
                PageImagePath = path,
            });
        }

        if (result.Pages.Count == 0) return result;

        var pdfPages = new List<HandoutPdfPage>();
        foreach (byte[] png in pngPages)
        {
            HandoutPdfPage decoded = await HandoutImageDecoder.DecodePngAsync(png);
            if (decoded is not null) pdfPages.Add(decoded);
        }

        (double pageWidthPt, double pageHeightPt) = HandoutComposer.PageSize(request.Options);
        byte[] pdf = HandoutPdfBuilder.Build(pdfPages, pageWidthPt, pageHeightPt);
        if (pdf.Length > 0)
        {
            result.PdfPath = Path.Combine(outputDirectory, "handout.pdf");
            await File.WriteAllBytesAsync(result.PdfPath, pdf);
        }
        else
        {
            result.Warnings.Add("PDF 合成失败，已保留逐页 PNG。");
        }

        return result;
    }

    private static IReadOnlyList<HandoutPageSource> ToSources(
        SlideContentIndex index, HandoutOptions options)
    {
        var sources = new List<HandoutPageSource>();
        foreach (SlideContentEntry entry in index.Slides)
        {
            string notes = options.Content == HandoutContentMode.AnnotationsOnly
                ? ""
                : entry.Text ?? "";
            sources.Add(new HandoutPageSource
            {
                SlideId = entry.SlideId,
                Index = entry.Index,
                Title = string.IsNullOrWhiteSpace(entry.Name)
                    ? "第 " + entry.Index + " 页"
                    : "第 " + entry.Index + " 页 · " + entry.Name,
                SlideText = entry.Text ?? "",
                SlideImagePng = TryRead(entry.ThumbnailPath),
                AnnotationMetadataJson = entry.HasInk
                    ? "{\"inkStrokes\":" + entry.InkStrokeCount + "}"
                    : "",
                TeacherNotes = notes,
            });
        }

        return sources;
    }

    private static byte[] TryRead(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? File.ReadAllBytes(path)
                : Array.Empty<byte>();
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    private static async Task<byte[]> RenderPageAsync(
        HandoutPageSource page, HandoutOptions options, int width, int height)
    {
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Windows.UI.Color.FromArgb(255, 255, 255, 255));
                var margin = width * 0.04f;
                float titleHeight = height * 0.06f;
                using var titleFormat = new CanvasTextFormat
                {
                    FontSize = Math.Max(18, width * 0.026f),
                    WordWrapping = CanvasWordWrapping.NoWrap,
                };
                session.DrawText(page.Title, margin, margin, Windows.UI.Color.FromArgb(255, 32, 32, 32), titleFormat);

                float imageTop = margin + titleHeight;
                float notesHeight = string.IsNullOrWhiteSpace(page.TeacherNotes) ? 0 : height * 0.26f;
                float imageHeight = height - imageTop - notesHeight - margin;
                float imageWidth = width - margin * 2;

                if (page.SlideImagePng.Length > 0)
                {
                    using var imageStream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(imageStream))
                    {
                        writer.WriteBytes(page.SlideImagePng);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                    }

                    imageStream.Seek(0);
                    using CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(device, imageStream);
                    double scale = Math.Min(
                        imageWidth / bitmap.SizeInPixels.Width,
                        imageHeight / bitmap.SizeInPixels.Height);
                    float drawWidth = (float)(bitmap.SizeInPixels.Width * scale);
                    float drawHeight = (float)(bitmap.SizeInPixels.Height * scale);
                    float drawX = (width - drawWidth) / 2f;
                    float drawY = imageTop + (imageHeight - drawHeight) / 2f;
                    session.DrawImage(bitmap, new Windows.Foundation.Rect(drawX, drawY, drawWidth, drawHeight));
                }
                else
                {
                    using var emptyFormat = new CanvasTextFormat
                    {
                        FontSize = 16,
                        WordWrapping = CanvasWordWrapping.Wrap,
                    };
                    session.DrawText(
                        "（未找到该页课件截图，仅保留教师解析）",
                        margin, imageTop + 12, Windows.UI.Color.FromArgb(255, 120, 120, 120), emptyFormat);
                }

                if (notesHeight > 0)
                {
                    using var notesFormat = new CanvasTextFormat
                    {
                        FontSize = Math.Max(14, width * 0.019f),
                        WordWrapping = CanvasWordWrapping.Wrap,
                        LineSpacing = 1.25f,
                    };
                    float notesTop = height - margin - notesHeight + 8;
                    session.DrawText(
                        "教师解析：" + page.TeacherNotes,
                        margin, notesTop, Windows.UI.Color.FromArgb(255, 48, 48, 48), notesFormat);
                }
            }

            using var stream = new InMemoryRandomAccessStream();
            await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
            int length = (int)stream.Size;
            var bytes = new byte[length];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)length);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }
}