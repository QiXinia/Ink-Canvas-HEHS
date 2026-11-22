using InkCanvas.Features.Board;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Storage.Streams;

namespace InkCanvas.Features.Ppt;

public sealed class HandoutRenderRequest
{
    public SlideContentIndex Index { get; set; }

    public HandoutOptions Options { get; set; } = new();

    /// <summary>
    /// 可选白板服务：提供后，在 PPT 页之后追加以 <see cref="BoardPageRenderer"/> 渲染的板书页；
    /// 为空或没有内容时保持原 PPT 讲义结构不变。
    /// </summary>
    public BoardService Board { get; set; }

    /// <summary>
    /// 可选板书页渲染注入点：测试可注入确定性 PNG，避免无 GPU/远程会话下依赖 Win2D；
    /// 生产为空时走 <see cref="BoardPageRenderer.RenderPagePngAsync"/>。
    /// </summary>
    public Func<BoardService, int, int, double, Task<BoardPageRenderResult>> BoardPageRender { get; set; }

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

        int width = Math.Max(320, request.PageWidthPx);
        int height = Math.Max(480, request.PageHeightPx);

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            result.Warnings.Add("无法创建导出目录：" + ex.Message);
            return result;
        }

        IReadOnlyList<HandoutPageSource> sources;
        bool boardPageFailed;
        (sources, boardPageFailed) = await ToSourcesAsync(
            request.Index, request.Options, request.Board, width, request.BoardPageRender, result.Warnings);
        result.BoardPageFailed = boardPageFailed;
        IReadOnlyList<HandoutPageSource> pages = HandoutComposer.Select(sources, request.Options);
        if (pages.Count == 0)
        {
            result.Warnings.Add("当前筛选下没有可导出的页。");
            return result;
        }

        var pngPages = new List<byte[]>();

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

    private static async Task<(IReadOnlyList<HandoutPageSource> Sources, bool BoardPageFailed)> ToSourcesAsync(
        SlideContentIndex index, HandoutOptions options, BoardService board,
        int pageWidthPx,
        Func<BoardService, int, int, double, Task<BoardPageRenderResult>> boardPageRender,
        List<string> warnings)
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

        if (board is null) return (sources, false);

        // 板书页追加在 PPT 页之后；每页直接使用 BoardPageRenderer 的完整快照，
        // 因此讲义 PNG 与画布/时光机使用同一渲染路径。
        bool boardPageFailed = false;
        int nextIndex = sources.Count == 0 ? 1 : sources.Max(page => page.Index) + 1;
        for (int boardPage = 0; boardPage < board.PageCount; boardPage++)
        {
            if (!board.PageHasContent(boardPage)) continue;

            BoardPageRenderResult rendered = boardPageRender is null
                ? await BoardPageRenderer.RenderPagePngAsync(board, boardPage, pageWidthPx, 2.0)
                : await boardPageRender(board, boardPage, pageWidthPx, 2.0);
            if (!rendered.Success)
            {
                boardPageFailed = true;
                warnings.Add("板书 第 " + (boardPage + 1) + " 页渲染失败：" + rendered.Error);
                continue;
            }

            sources.Add(new HandoutPageSource
            {
                IsBoardPage = true,
                SlideId = -1000 - boardPage,
                Index = nextIndex++,
                Title = "板书 第 " + (boardPage + 1) + " 页",
                SlideImagePng = rendered.Png,
                AnnotationMetadataJson = "{\"boardPage\":" + boardPage + "}",
            });
        }

        return (sources, boardPageFailed);
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
        // 板书页由 BoardPageRenderer 生成完整画面，不再叠加讲义标题/教师解析，
        // 保证讲义 PNG 与直接渲染结果逐字节一致。
        if (page.IsBoardPage)
            return page.SlideImagePng is { Length: > 0 } ? page.SlideImagePng : Array.Empty<byte>();

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