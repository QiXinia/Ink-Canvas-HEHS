using System.Runtime.CompilerServices;
using InkCanvas.Host.Contracts;
using InkCanvas.Pdf;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private void NotifyDocumentAi(string path)
    {
        if (_host.DocumentAi?.Available != true) return;
        string password = _file.Password;
        _host.DocumentAi.Saved(new(path, "inkcanvas.pdf") { ExportImages = (directory, token) => ExportAiPagesAsync(path, password, directory, token) });
    }

    private static async IAsyncEnumerable<AiDocumentImage> ExportAiPagesAsync(string path, string password, string directory,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var file = await PdfAnnotationFile.OpenAsync(path, password, token);
        await using var renderer = await PdfRasterizer.OpenAsync(path, password, token);
        for (int index = 0; index < file.Pages.Count; index++)
        {
            var page = file.GetSpacing(index).Expand(file.Pages[index]);
            page = page with { Rotation = page.Rotation + file.GetDisplayRotation(index) };
            double scale = Math.Min(1, Math.Min(3840 / (double)Math.Max(page.DipWidth, page.DipHeight), Math.Sqrt(3840d * 2160 / (page.DipWidth * page.DipHeight))));
            byte[] png = await renderer.RenderAsync(index, page, new Windows.Foundation.Rect(0, 0, page.DipWidth, page.DipHeight), scale, token);
            string output = Path.Combine(directory, $"pdf-{index + 1:D4}.png");
            await File.WriteAllBytesAsync(output, png, token);
            yield return new(output, index + 1, "page", (int)Math.Ceiling(page.DipWidth * scale), (int)Math.Ceiling(page.DipHeight * scale));
        }
    }
}
