using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using InkCanvas.Host.Contracts;
using InkCanvas.Pdf;
using Microsoft.Graphics.Canvas;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.Pdf;

/// <summary>Exercises native rendering on UI/background threads and releases it before host exit.</summary>
public static class PdfRasterizerDiagnostics
{
    public static async Task RunAsync(IDocumentWorkspaceHost host, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        try
        {
            string path = Path.Combine(directory, "lifetime.pdf");
            using (var fixture = new PdfSharp.Pdf.PdfDocument())
            {
                for (int index = 0; index < 4; index++)
                {
                    var page = fixture.AddPage();
                    page.Width = XUnit.FromPoint(600); page.Height = XUnit.FromPoint(800); page.Rotate = index * 90;
                    using var drawing = XGraphics.FromPdfPage(page);
                    drawing.DrawRectangle(XBrushes.White, 0, 0, 600, 800);
                    drawing.DrawRectangle(XBrushes.Red, 90, 120, 120, 160);
                    drawing.DrawEllipse(XBrushes.Blue, 300, 400, 150, 190);
                }
                fixture.Save(path);
            }
            using var file = await PdfAnnotationFile.OpenAsync(path);
            using var device = new CanvasDevice();
            for (int index = 0; index < file.Pages.Count; index++)
            {
                await using var renderer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
                var page = file.Pages[index];
                var source = new Rect(0, 0, page.DipWidth, page.DipHeight);
                byte[] png = await renderer.RenderAsync(index, page, source, 1, CancellationToken.None);
                Check("page dimensions and rotation " + index, Width(png) == (int)Math.Ceiling(page.DipWidth)
                    && Height(png) == (int)Math.Ceiling(page.DipHeight));
                using var full = await BitmapAsync(png, device);
                using var direct = await renderer.RenderBitmapAsync(index, page, source, 1, CancellationToken.None);
                byte[] fullPixels = full.GetPixelBytes(), directPixels = direct.GetPixelBytes();
                Check("direct GPU preview matches PNG export " + index, directPixels.SequenceEqual(fullPixels));
                var crop = new Rect(Math.Floor(page.DipWidth * .125), Math.Floor(page.DipHeight * .125),
                    Math.Floor(page.DipWidth * .625), Math.Floor(page.DipHeight * .625));
                using var detail = await BitmapAsync(await renderer.RenderAsync(index, page, crop, 1, CancellationToken.None), device);
                using var reference = new CanvasRenderTarget(device, (float)crop.Width, (float)crop.Height, 96);
                using (var draw = reference.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.White);
                    draw.DrawImage(full, Vector2.Zero, crop);
                }
                byte[] actual = detail.GetPixelBytes(), expected = reference.GetPixelBytes();
                long error = 0;
                for (int channel = 0; channel < actual.Length; channel++) error += Math.Abs(actual[channel] - expected[channel]);
                double meanError = error / (double)actual.Length;
                Check("partial page keeps pixel origin " + index, meanError < 1, new { meanError });
            }

            for (int cycle = 0; cycle < 3; cycle++)
            {
                // AI exports call the same rasterizer from background work after the PDF window closes.
                await Task.Run(async () =>
                {
                    await using var renderer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
                    await renderer.PreviewAsync(0, file.Pages[0], CancellationToken.None);
                });
            }
            Check("background export repeatedly opens renders and disposes", true);

            using (var input = File.OpenRead(path))
            using (var stream = input.AsRandomAccessStream())
            {
                var document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(stream);
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    using var software = new PdfNativeRenderer(forceSoftwareRenderer: true);
                    using var page = document.GetPage(0);
                    await page.PreparePageAsync();
                    var png = await software.RenderAsync(page, new(0, 0, (float)page.Size.Width, (float)page.Size.Height), 600, 800, CancellationToken.None);
                    Check("WARP renders and releases before process exit " + cycle, Width(png) == 600 && Height(png) == 800);
                }
            }

            var closing = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
            var geometry = file.Pages[0];
            var rendering = closing.RenderAsync(0, geometry, new(0, 0, geometry.DipWidth, geometry.DipHeight), 2, CancellationToken.None);
            var disposing = closing.DisposeAsync().AsTask();
            Check("dispose waits for active rendering", (await rendering).Length > 0);
            await disposing;
            await closing.DisposeAsync();
            bool disposedRejected = false;
            try { await closing.PreviewAsync(0, geometry, CancellationToken.None); }
            catch (ObjectDisposedException) { disposedRejected = true; }
            Check("disposed renderer rejects work and repeated disposal is safe", disposedRejected);

            await using (var canceled = await PdfRasterizer.OpenAsync(file, CancellationToken.None))
            {
                using var cancellation = new CancellationTokenSource();
                var pending = canceled.RenderAsync(0, geometry, new(0, 0, geometry.DipWidth, geometry.DipHeight), 2, cancellation.Token);
                cancellation.Cancel();
                bool canceledObserved = false;
                try { await pending; }
                catch (OperationCanceledException) { canceledObserved = true; }
                Check("canceled render drains before device release", canceledObserved);
                Check("renderer remains usable after cancellation", (await canceled.PreviewAsync(0, geometry, CancellationToken.None)).Length > 0);
            }

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool canceledObserved = false;
                try { await using var canceled = await PdfRasterizer.OpenAsync(file, cancellation.Token); }
                catch (OperationCanceledException) { canceledObserved = true; }
                Check("canceled open releases input file", canceledObserved);
            }
            var windowRenderer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
            var windowFile = await PdfAnnotationFile.OpenAsync(path);
            var window = new PdfWindow(host, windowFile, windowRenderer, () => Task.CompletedTask, () => Task.CompletedTask);
            host.Attach(window); window.Activate();
            await Task.Delay(80);
            Check("window closes with pending page rendering", await window.CloseAsync());
            await MeasureComplexOpenAsync(host, directory, Check);
        }
        catch (Exception exception) { Check("native PDF lifetime exception", false, exception.ToString()); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "pdf-lifetime.json"), JsonSerializer.Serialize(checks,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });
    }

    private static async Task MeasureComplexOpenAsync(IDocumentWorkspaceHost host, string directory, Action<string, bool, object> check)
    {
        string source = Path.Combine(directory, "complex-open.pdf");
        string annotated = Path.Combine(directory, "complex-open-ink.pdf");
        const int pageCount = 160;
        if (!File.Exists(source) || !File.Exists(annotated))
        {
            using (var document = new PdfDocument())
            {
                var scan = new PdfDictionary(document);
                scan.Elements.SetName("/Type", "/XObject");
                scan.Elements.SetName("/Subtype", "/Image");
                scan.Elements.SetName("/ColorSpace", "/DeviceRGB");
                scan.Elements.SetInteger("/BitsPerComponent", 8);
                scan.Elements.SetInteger("/Width", 2400);
                scan.Elements.SetInteger("/Height", 3200);
                var pixels = new byte[2400 * 3200 * 3];
                var random = new Random(117);
                random.NextBytes(pixels);
                scan.CreateStream(pixels);
                document.Internals.AddObject(scan);
                for (int i = 0; i < pageCount; i++)
                {
                    var page = document.AddPage();
                    page.Width = XUnit.FromPoint(600); page.Height = XUnit.FromPoint(800);
                    var objects = new PdfDictionary(document);
                    objects.Elements["/Scan"] = scan.Reference;
                    var resources = new PdfDictionary(document);
                    resources.Elements["/XObject"] = objects;
                    page.Elements["/Resources"] = resources;
                    var vectors = new System.Text.StringBuilder("q 600 0 0 800 0 0 cm /Scan Do Q\n0 0 1 RG .25 w\n");
                    for (int item = 0; item < (i == 0 ? 35000 : 1200); item++)
                        vectors.Append(random.Next(570)).Append(' ').Append(random.Next(780)).Append(" m ")
                            .Append(random.Next(570)).Append(' ').Append(random.Next(780)).Append(" l S\n");
                    var content = new PdfDictionary(document);
                    content.CreateStream(System.Text.Encoding.ASCII.GetBytes(vectors.ToString()));
                    document.Internals.AddObject(content);
                    page.Elements["/Contents"] = content.Reference;
                }
                document.Save(source);
            }
            using var file = await PdfAnnotationFile.OpenAsync(source);
            var ink = new Dictionary<int, IReadOnlyList<InkCanvas.Core.Ink.InkStroke>>();
            for (int i = 0; i < pageCount; i++)
            {
                var strokes = new List<InkCanvas.Core.Ink.InkStroke>();
                for (int row = 0; row < 16; row++)
                {
                    var stroke = new InkCanvas.Core.Ink.InkStroke(new() { TaperKind = InkCanvas.Core.Ink.TaperKind.None });
                    for (int point = 0; point < 256; point++)
                        stroke.AddPoint(150 + point, 125 + row * 25 + MathF.Sin(point * .04f) * 9);
                    stroke.Complete();
                    strokes.Add(stroke);
                }
                ink[i] = strokes;
            }
            await file.SaveAsync(annotated, ink);
        }

        foreach (string path in new[] { source, annotated })
        {
            var runs = new List<object>();
            for (int iteration = 0; iteration < 3; iteration++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var file = await PdfAnnotationFile.OpenAsync(path);
                long metadata = clock.ElapsedMilliseconds;
                var renderer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
                long native = clock.ElapsedMilliseconds;
                var window = new PdfWindow(host, file, renderer, () => Task.CompletedTask, () => Task.CompletedTask);
                try
                {
                    host.Attach(window);
                    window.Activate();
                    while (!window.CachedPages.Contains(0) && clock.ElapsedMilliseconds < 15000) await Task.Delay(10);
                    long firstPreview = clock.ElapsedMilliseconds;
                    runs.Add(new { metadata, native, firstPreview, window.RealizedThumbnailCount });
                    if (!window.CachedPages.Contains(0)) throw new TimeoutException("Complex PDF did not present the first page.");
                }
                finally { window.ForceClose(); await window.Cleanup; }
            }
            check("complex PDF opens and renders the first page", true,
                new { file = Path.GetFileName(path), bytes = new FileInfo(path).Length, pages = pageCount, runs });
        }
    }

    private static int Width(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
    private static int Height(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
    private static async Task<CanvasBitmap> BitmapAsync(byte[] png, CanvasDevice device)
    {
        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); stream.Seek(0);
        return await CanvasBitmap.LoadAsync(device, stream, 96);
    }
}
