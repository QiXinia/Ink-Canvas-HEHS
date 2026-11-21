using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace InkCanvas.Features.AiAssistant;

public static class PasteCardRenderer
{
    private static readonly Windows.UI.Color Background = Windows.UI.Color.FromArgb(255, 250, 250, 250);
    private static readonly Windows.UI.Color Border = Windows.UI.Color.FromArgb(255, 220, 220, 224);
    private static readonly Windows.UI.Color TitleColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
    private static readonly Windows.UI.Color TextColor = Windows.UI.Color.FromArgb(255, 48, 48, 48);

    public static async Task<byte[]> RenderPngAsync(string title, IReadOnlyList<string> lines, int width, int height)
    {
        try
        {
            if (width <= 0 || height <= 0) return Array.Empty<byte>();

            var device = CanvasDevice.GetSharedDevice();
            using var target = new CanvasRenderTarget(device, width, height, 96);

            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                session.FillRoundedRectangle(new Rect(0, 0, width, height), 10, 10, Background);
                session.DrawRoundedRectangle(new Rect(0.5, 0.5, width - 1, height - 1), 10, 10, Border, 1);

                using var titleFormat = new CanvasTextFormat
                {
                    FontSize = 15,
                    WordWrapping = CanvasWordWrapping.NoWrap,
                };
                session.DrawText(title ?? "", 14f, 10f, TitleColor, titleFormat);

                using var bodyFormat = new CanvasTextFormat
                {
                    FontSize = 14,
                    WordWrapping = CanvasWordWrapping.Wrap,
                };

                float y = 40f;
                foreach (string line in lines ?? Array.Empty<string>())
                {
                    session.DrawText(line ?? "", 14f, y, TextColor, bodyFormat);
                    y += 22f;
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