using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Windows.Storage.Streams;

namespace InkCanvas.Features.Board;

public sealed class BoardPageRenderResult
{
    public byte[] Png { get; init; } = Array.Empty<byte>();

    public int WidthPx { get; init; }

    public int HeightPx { get; init; }

    public string Error { get; init; } = "";

    public bool Success => Png is { Length: > 0 } && WidthPx > 0 && HeightPx > 0;
}

/// <summary>
/// 白板页离屏渲染：背景纸/网格 + 笔画 + 图片（含 AI 卡片 PNG）+ 媒体海报。
/// 复用 <see cref="InkSceneRenderer"/> 的笔画/图片路径，因此与画布和时光机的对象渲染一致。
/// 无 UI 线程依赖（Win2D 共享设备），失败返回错误而不抛异常。
/// </summary>
public static class BoardPageRenderer
{
    private const float Padding = 24f;
    private const int MaxPixels = 8192;

    private static readonly Windows.UI.Color BlackPaper = Windows.UI.Color.FromArgb(255, 10, 10, 10);
    private static readonly Windows.UI.Color LightPaper = Windows.UI.Color.FromArgb(255, 250, 250, 250);
    private static readonly Windows.UI.Color GreenPaper = Windows.UI.Color.FromArgb(255, 0x1E, 0x3B, 0x2F);
    private static readonly Windows.UI.Color CreamPaper = Windows.UI.Color.FromArgb(255, 0xF5, 0xEF, 0xDC);
    private static readonly Windows.UI.Color DarkGridLine = Windows.UI.Color.FromArgb(70, 235, 235, 235);
    private static readonly Windows.UI.Color LightGridLine = Windows.UI.Color.FromArgb(55, 92, 113, 121);
    private static readonly Windows.UI.Color DarkAxis = Windows.UI.Color.FromArgb(200, 230, 230, 230);
    private static readonly Windows.UI.Color LightAxis = Windows.UI.Color.FromArgb(190, 60, 80, 90);

    /// <summary>
    /// 渲染指定页为 PNG。输出宽度不超过 <paramref name="targetWidthPx"/>，默认 2x DPI，
    /// 长边不超过 8192；<paramref name="transparentBackground"/> 为 true 时不画纸背景与网格。
    /// </summary>
    public static async Task<BoardPageRenderResult> RenderPagePngAsync(
        BoardService board,
        int pageIndex,
        int targetWidthPx,
        double scale = 2.0,
        bool transparentBackground = false)
    {
        if (board is null)
            return Fail("画板服务为空。");
        if (pageIndex < 0 || pageIndex >= board.PageCount)
            return Fail("画板页索引越界。");
        if (!board.PageHasContent(pageIndex))
            return Fail("画板页没有可渲染内容。");

        try
        {
            BoardPageInfo info = board.GetPageInfo(pageIndex);
            InkController controller = info.Controller;
            Rect bounds = ContentBounds(board, controller, info.Images, info.Media);
            if (bounds.IsEmpty)
                return Fail("画板页内容包围盒为空。");

            double requestedScale = scale > 0 ? scale : 2.0;
            double widthDip = Math.Max(1f, bounds.Width + Padding * 2);
            double heightDip = Math.Max(1f, bounds.Height + Padding * 2);
            double maxWidth = Math.Clamp(targetWidthPx <= 0 ? 1240 : targetWidthPx, 320, MaxPixels);

            double renderScale = requestedScale;
            double desiredWidth = widthDip * renderScale;
            if (desiredWidth > maxWidth)
                renderScale *= maxWidth / desiredWidth;

            int pixelWidth = Math.Max(1, (int)Math.Round(widthDip * renderScale));
            int pixelHeight = Math.Max(1, (int)Math.Round(heightDip * renderScale));
            if (pixelWidth > MaxPixels || pixelHeight > MaxPixels)
            {
                double factor = MaxPixels / (double)Math.Max(pixelWidth, pixelHeight);
                renderScale *= factor;
                pixelWidth = Math.Max(1, (int)Math.Round(widthDip * renderScale));
                pixelHeight = Math.Max(1, (int)Math.Round(heightDip * renderScale));
            }

            if (pixelWidth <= 0 || pixelHeight <= 0)
                return Fail("画板页目标尺寸无效。");

            double actualScale = pixelWidth / widthDip;
            Matrix3x2 view = Matrix3x2.CreateTranslation(
                    (float)(Padding - bounds.Left), (float)(Padding - bounds.Top))
                * Matrix3x2.CreateScale((float)actualScale);

            var device = GetDevice();
            using var renderer = new InkSceneRenderer { ShowSelectionOutline = false };
            await renderer.Images.PrepareAsync(device, controller);

            using var target = new CanvasRenderTarget(device, pixelWidth, pixelHeight, 96);
            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                DrawPaper(session, info.Background, info.GridSpacing, pixelWidth, pixelHeight,
                    actualScale, transparentBackground);

                foreach (DrawingObject item in controller.DrawingObjects)
                {
                    if (item.Stroke is { } stroke)
                        renderer.Strokes.Draw(session, stroke, view);
                    else if (item.Element is IImageSelectionElement { ImageData.Length: > 0 } image)
                        renderer.Images.Draw(session, image, view);
                }

                await DrawMediaPostersAsync(session, device, info.Media, view);
            }

            using var stream = new InMemoryRandomAccessStream();
            await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
            int length = (int)stream.Size;
            var bytes = new byte[length];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)length);
            reader.ReadBytes(bytes);

            return new BoardPageRenderResult
            {
                Png = bytes,
                WidthPx = pixelWidth,
                HeightPx = pixelHeight,
            };
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书页离屏渲染失败：page={pageIndex}，error={ex.Message}",
                Core.Diagnostics.LogType.Error);
            return Fail(ex.Message ?? "板书页离屏渲染失败。");
        }
    }

    private static Rect ContentBounds(
        BoardService board, InkController controller,
        IReadOnlyList<BoardImageAsset> images, IReadOnlyList<BoardMediaAsset> media)
    {
        Rect bounds = Rect.Empty;
        foreach (InkStroke stroke in controller.Document.Strokes)
            bounds = bounds.Union(stroke.Bounds);

        foreach (BoardImageAsset image in images)
        {
            if (image.Data is not { Length: > 0 } || image.Width <= 0 || image.Height <= 0) continue;
            bounds = bounds.Union(new BoardSelectionElement(board, image, controller).Bounds);
        }

        foreach (BoardMediaAsset item in media)
        {
            if (item.PosterData is not { Length: > 0 } || item.Width <= 0 || item.Height <= 0) continue;
            bounds = bounds.Union(new BoardSelectionElement(board, item, controller).Bounds);
        }

        return bounds;
    }

    private static void DrawPaper(
        CanvasDrawingSession session, BoardBackground background, float gridSpacing,
        int width, int height, double scale, bool transparent)
    {
        if (transparent)
        {
            session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            return;
        }

        bool dark = background is BoardBackground.Black or BoardBackground.Green;
        Windows.UI.Color paper = background switch
        {
            BoardBackground.Black => BlackPaper,
            BoardBackground.Green => GreenPaper,
            BoardBackground.Cream => CreamPaper,
            _ => LightPaper,
        };
        session.Clear(paper);

        float spacing = (float)Math.Clamp(gridSpacing, 12f, 160f) * (float)scale;
        if (spacing <= 0) return;
        Windows.UI.Color line = dark ? DarkGridLine : LightGridLine;
        Windows.UI.Color axis = dark ? DarkAxis : LightAxis;

        switch (background)
        {
            case BoardBackground.Grid:
                for (float y = spacing; y < height; y += spacing)
                    session.DrawLine(0, y, width, y, line, 1f);
                for (float x = spacing; x < width; x += spacing)
                    session.DrawLine(x, 0, x, height, line, 1f);
                break;

            case BoardBackground.Lined:
                for (float y = spacing; y < height; y += spacing)
                    session.DrawLine(0, y, width, y, line, 1f);
                break;

            case BoardBackground.Coordinate:
                DrawCoordinates(session, width, height, spacing, axis, line);
                break;
        }
    }

    private static void DrawCoordinates(
        CanvasDrawingSession session, int width, int height, float spacing,
        Windows.UI.Color axis, Windows.UI.Color tick)
    {
        float originX = width * 0.5f;
        float originY = height * 0.5f;
        const float tickHalf = 4f;

        session.DrawLine(0, originY, width, originY, axis, 1.6f);
        session.DrawLine(originX, 0, originX, height, axis, 1.6f);

        for (float x = originX + spacing; x < width; x += spacing)
            session.DrawLine(x, originY - tickHalf, x, originY + tickHalf, tick, 1f);
        for (float x = originX - spacing; x > 0; x -= spacing)
            session.DrawLine(x, originY - tickHalf, x, originY + tickHalf, tick, 1f);
        for (float y = originY + spacing; y < height; y += spacing)
            session.DrawLine(originX - tickHalf, y, originX + tickHalf, y, tick, 1f);
        for (float y = originY - spacing; y > 0; y -= spacing)
            session.DrawLine(originX - tickHalf, y, originX + tickHalf, y, tick, 1f);
    }

    private static async Task DrawMediaPostersAsync(
        CanvasDrawingSession session, CanvasDevice device,
        IReadOnlyList<BoardMediaAsset> mediaObjects, Matrix3x2 view)
    {
        foreach (BoardMediaAsset media in mediaObjects)
        {
            if (media.PosterData is not { Length: > 0 } || media.Width <= 0 || media.Height <= 0) continue;

            CanvasBitmap bitmap = await LoadBitmapAsync(device, media.PosterData);
            if (bitmap is null) continue;
            using (bitmap)
            {
                var previous = session.Transform;
                try
                {
                    session.Transform = ReadTransform(media.Transform) * view;
                    session.DrawImage(
                        bitmap,
                        new Windows.Foundation.Rect(
                            media.Left, media.Top, media.Left + media.Width, media.Top + media.Height),
                        new Windows.Foundation.Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height));
                }
                finally
                {
                    session.Transform = previous;
                }
            }
        }
    }

    private static async Task<CanvasBitmap> LoadBitmapAsync(CanvasDevice device, byte[] data)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(data);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            return await CanvasBitmap.LoadAsync(device, stream);
        }
        catch
        {
            return null;
        }
    }

    private static Matrix3x2 ReadTransform(float[] values)
        => values is { Length: >= 6 }
            ? new Matrix3x2(values[0], values[1], values[2], values[3], values[4], values[5])
            : Matrix3x2.Identity;

    /// <summary>
    /// Win2D 共享设备在无 GPU / 远程会话 / 并行测试负载下可能不可用；回退软件渲染，
    /// 保证讲义与时光机的离屏渲染仍可完成，并将回退记录到日志。
    /// </summary>
    private static CanvasDevice GetDevice()
    {
        try
        {
            return CanvasDevice.GetSharedDevice();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] Win2D 共享设备不可用，回退软件渲染：{ex.Message}", Core.Diagnostics.LogType.Info);
            return new CanvasDevice(forceSoftwareRenderer: true);
        }
    }

    private static BoardPageRenderResult Fail(string error)
        => new() { Error = error ?? "板书页离屏渲染失败。" };
}