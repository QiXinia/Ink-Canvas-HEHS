using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.Tools;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace InkCanvas.Features.AiAssistant;

public static class AssistantCapture
{
    public static async Task<string> CapturePngBase64Async(int screenX, int screenY, int width, int height,
        int maxEdge = 1600)
    {
        try
        {
            if (width <= 0 || height <= 0) return "";

            var (bgra, capturedWidth, capturedHeight) = ScreenshotService.Capture(screenX, screenY, width, height);
            if (capturedWidth <= 0 || capturedHeight <= 0 || bgra == null || bgra.Length == 0) return "";

            var device = CanvasDevice.GetSharedDevice();
            using var source = CanvasBitmap.CreateFromBytes(device, bgra, capturedWidth, capturedHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);

            int longestEdge = Math.Max(capturedWidth, capturedHeight);

            if (maxEdge > 0 && longestEdge > maxEdge)
            {
                double scale = (double)maxEdge / longestEdge;
                int targetWidth = Math.Max(1, (int)Math.Round(capturedWidth * scale));
                int targetHeight = Math.Max(1, (int)Math.Round(capturedHeight * scale));

                using var target = new CanvasRenderTarget(device, targetWidth, targetHeight, 96);
                using (var session = target.CreateDrawingSession())
                {
                    session.DrawImage(source, new Rect(0, 0, targetWidth, targetHeight),
                        new Rect(0, 0, capturedWidth, capturedHeight));
                }

                return Convert.ToBase64String(await SavePngBytesAsync(target));
            }

            return Convert.ToBase64String(await SavePngBytesAsync(source));
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static async Task<byte[]> SavePngBytesAsync(CanvasBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png);

        int length = (int)stream.Size;
        var bytes = new byte[length];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)length);
        reader.ReadBytes(bytes);
        return bytes;
    }
}