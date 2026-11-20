using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.Pdf;

/// <summary>Runs inside the real WinUI host, including native PDF rasterization and GPU ink.</summary>
public static class PdfDiagnostics
{
    public static async Task RunAsync(IDocumentWorkspaceHost host, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); PdfWindow window = null;
        try
        {
            string source = Path.Combine(directory, "classroom.pdf"), saved = Path.Combine(directory, "annotations.pdf");
            CreateFixture(source, 6);
            var inks = CreateInk();
            using (var file = await PdfAnnotationFile.OpenAsync(source))
            {
                Check("crop rotation and user unit metadata", file.Pages.Count == 6 && file.Pages[1].NormalizedRotation == 90 && file.Pages[4].UserUnit == 2);
                Check("document bookmarks", file.Bookmarks.Count == 1 && file.Bookmarks[0].PageIndex == 5);
                var pages = Enumerable.Range(0, 5).ToDictionary(index => index, _ => (IReadOnlyList<InkStroke>)inks);
                var saveTime = Stopwatch.StartNew();
                await file.SaveAsync(saved, pages);
                long initialSize = new FileInfo(saved).Length;
                for (int repeat = 0; repeat < 3; repeat++) await file.SaveAsync(saved, pages);
                Check("repeated saves do not accumulate appearances", new FileInfo(saved).Length <= initialSize + 1024, new { bytes = initialSize, saveMilliseconds = saveTime.ElapsedMilliseconds / 4 });
            }
            using (var file = await PdfAnnotationFile.OpenAsync(saved))
            {
                Check("all brush points restore exactly", Enumerable.Range(0, 5).All(index => file.GetInk(index).Count == inks.Count && file.GetInk(index).All(stroke => stroke.Points.SequenceEqual(inks.Single(original => original.Id == stroke.Id).Points))));
                Check("original text remains searchable", (await file.SearchAsync("classroom")).Single().PageIndex == 5);
                await using var raw = await PdfRasterizer.OpenAsync(saved, null, CancellationToken.None);
                var device = CanvasDevice.GetSharedDevice();
                for (int index = 0; index < 5; index++)
                {
                    var geometry = file.Pages[index];
                    var png = await raw.RenderAsync(index, geometry, new(0, 0, geometry.DipWidth, geometry.DipHeight), 1, CancellationToken.None);
                    await File.WriteAllBytesAsync(Path.Combine(directory, "pdf-" + index + ".png"), png);
                    using var native = await BitmapAsync(png, device);
                    using var controller = new InkController(); controller.Document.AddRange(inks);
                    using var reference = new CanvasRenderTarget(device, (float)native.Size.Width, (float)native.Size.Height, 96);
                    using var scene = new InkSceneRenderer();
                    using (var draw = reference.CreateDrawingSession())
                    {
                        draw.Clear(Microsoft.UI.Colors.White);
                        scene.DrawCommitted(draw, controller, Matrix3x2.CreateScale((float)native.Size.Width / geometry.DipWidth, (float)native.Size.Height / geometry.DipHeight));
                    }
                    await reference.SaveAsync(Path.Combine(directory, "ink-reference-" + index + ".png"), CanvasBitmapFileFormat.Png);
                    byte[] actual = native.GetPixelBytes(), expected = reference.GetPixelBytes();
                    long union = 0, intersection = 0, totalError = 0;
                    for (int p = 0; p < expected.Length; p += 4)
                    {
                        bool a = Math.Min(actual[p], Math.Min(actual[p + 1], actual[p + 2])) < 235;
                        bool b = Math.Min(expected[p], Math.Min(expected[p + 1], expected[p + 2])) < 235;
                        if (a || b) { union++; totalError += Math.Abs(actual[p] - expected[p]) + Math.Abs(actual[p + 1] - expected[p + 1]) + Math.Abs(actual[p + 2] - expected[p + 2]); }
                        if (a && b) intersection++;
                    }
                    double overlap = intersection / (double)Math.Max(1, union), error = totalError / (double)Math.Max(1, union * 3);
                    Check("vector appearance matches native ink page " + index, overlap > .995 && error < 1, new { overlap, meanChannelError = error, rotation = geometry.NormalizedRotation, geometry.UserUnit });
                }
            }
            await CheckMarginRenderingAsync();
            var workspaceFile = await PdfAnnotationFile.OpenAsync(source);
            var rasterizer = await PdfRasterizer.OpenAsync(workspaceFile, CancellationToken.None);
            window = new PdfWindow(host, workspaceFile, rasterizer, () => Task.CompletedTask, () => Task.CompletedTask);
            host.Attach(window); window.Activate(); await Task.Delay(900);
            Check("native document workspace attaches", ReferenceEquals(host.Owner, window));
            Check("PDF uses shared WinUI title bar and current file title", window.ExtendsContentIntoTitleBar
                && window.Content is Controls.Controls.WindowFrame frame && frame.Header.Title == window.Title);
            Check("PDF is a normal non-topmost window", window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { IsAlwaysOnTop: false }
                && !Platform.Interop.NativeMethods.HasExStyle(WinRT.Interop.WindowNative.GetWindowHandle(window), Platform.Interop.NativeMethods.WS_EX_TOPMOST));
            Check("sidebar generates realized page thumbnails", window.SidebarOpen && window.RealizedThumbnailCount > 0,
                new { thumbnails = window.RealizedThumbnailCount });
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-thumbnails.png"));
            float expandedWidth = window.ViewportSize.X;
            window.SetSidebarOpen(false); await Task.Delay(150);
            Check("collapsing sidebar gives its width back to the page", !window.SidebarOpen && window.ViewportSize.X > expandedWidth + 150);
            window.SetSidebarOpen(true); await Task.Delay(150);
            Check("sidebar expands without shifting the page outside viewport", window.SidebarOpen && Math.Abs(window.ViewportSize.X - expandedWidth) < 2);
            var windowSize = window.AppWindow.ClientSize;
            double windowDpi = ((FrameworkElement)window.Content).XamlRoot.RasterizationScale;
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)Math.Ceiling(620 * windowDpi),
                (int)Math.Ceiling(720 * windowDpi))); await Task.Delay(150);
            Check("narrow window folds sidebar to preserve page width", !window.SidebarOpen && window.ViewportSize.X > 550);
            window.SetSidebarOpen(true); await Task.Delay(150);
            Check("narrow window uses overlay sidebar", window.SidebarOpen && window.ViewportSize.X > 550,
                new { window.SidebarOpen, window.SidebarMode, width = window.ViewportSize.X });
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-narrow-sidebar.png"));
            window.SetSidebarOpen(false); await Task.Delay(80);
            Check("sidebar remains folded on narrow window resize", !window.SidebarOpen);
            window.AppWindow.ResizeClient(windowSize); await Task.Delay(150); window.SetSidebarOpen(true); await Task.Delay(150);

            var continuous = new PdfContinuousLayout(workspaceFile.Pages);
            window.GoToPage(0);
            var documentView = continuous.DocumentView(window.PageIndex, window.Controller.ViewTransform);
            float boundary = Vector2.Transform(new(0, continuous[0].Bottom), documentView).Y;
            window.ScrollBy(window.ViewportSize.Y / 2 - boundary);
            await Task.Delay(250);
            Check("continuous viewport shows both pages at a boundary", window.VisiblePageCount >= 2);
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-continuous-boundary.png"));
            documentView = continuous.DocumentView(window.PageIndex, window.Controller.ViewTransform);
            var secondPagePoint = Vector2.Transform(new(workspaceFile.Pages[1].DipWidth / 2, 30), continuous.PageView(1, documentView));
            var secondInput = new OverlayPointer(432, secondPagePoint.X, secondPagePoint.Y, .5f, 0, 0, OverlayPointerKind.Pen, 500);
            window.ActivateTouchedPage(secondInput);
            Check("touching adjacent page rebinds ink without changing document view", window.PageIndex == 1
                && MatrixDistance(documentView, continuous.DocumentView(window.PageIndex, window.Controller.ViewTransform)) < .001f);
            window.Controller.RecognizeShapes = false;
            window.Controller.OnPointerDown(secondInput);
            window.Controller.OnPointerMove(secondInput with { X = secondInput.X + 30, Y = secondInput.Y + 6, TimeMs = 520 });
            window.Controller.OnPointerUp(secondInput with { X = secondInput.X + 50, Y = secondInput.Y + 12, TimeMs = 540 });
            window.Controller.FinishDotAnimations();
            Check("adjacent page annotation has independent history", window.Controller.Document.Count == 1 && window.Controller.History.CanUndo);
            window.Controller.Undo();
            Check("adjacent page undo retains other pages", window.Controller.Document.Count == 0);
            // Exercise the XAML pointer entry point, including InputStarting and page rebinding.
            window.GoToPage(0); await Task.Delay(150);
            window.Controller.Tool = InkTool.Pen;
            window.Controller.RecognizeShapes = false;
            var surface = window.AnnotationSurface;
            var pageRight = Vector2.Transform(new(workspaceFile.Pages[0].DipWidth, 180), window.Controller.ViewTransform);
            var local = new Vector2(Math.Min(window.ViewportSize.X - 12, pageRight.X + 18), pageRight.Y);
            var screen = surface.TransformToVisual(null).TransformPoint(new(local.X, local.Y));
            var origin = new Platform.Interop.NativeMethods.POINT();
            ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(window), ref origin);
            float dpi = (float)surface.XamlRoot.RasterizationScale;
            bool nativeInput = GetCursorPos(out var oldCursor);
            bool inputBegan = false;
            surface.InputBegan += () => inputBegan = true;
            try
            {
                SetCursorPos(origin.X + (int)(screen.X * dpi), origin.Y + (int)(screen.Y * dpi));
                var hit = WindowFromPoint(new Platform.Interop.NativeMethods.POINT { X = origin.X + (int)(screen.X * dpi), Y = origin.Y + (int)(screen.Y * dpi) });
                MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40);
                SetCursorPos(origin.X + (int)(screen.X * dpi), origin.Y + (int)((screen.Y + 30) * dpi)); await Task.Delay(40);
                MouseEvent(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(180);
                window.Controller.FinishDotAnimations();
                if (nativeInput)
                {
                    Check("native pointer can annotate page margin", inputBegan && window.Controller.Document.Count == 1
                        && window.Controller.Document.Strokes[0].Bounds.Right > workspaceFile.Pages[0].DipWidth,
                        new { hit = hit.ToInt64(), window = WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64(), count = window.Controller.Document.Count,
                            bounds = window.Controller.Document.Bounds, screen, dpi });
                    window.Controller.Undo();
                }
                else
                {
                    checks.Add(new { check = "native pointer margin input unavailable", available = false, detail = "GetCursorPos returned false. Interactive input requires an unlocked input desktop." });
                    var sample = new OverlayPointer(441, local.X, local.Y, .5f, 0, 0, OverlayPointerKind.Pen, 1000);
                    window.ActivateTouchedPage(sample);
                    window.Controller.OnPointerDown(sample);
                    window.Controller.OnPointerMove(sample with { Y = sample.Y + 30, TimeMs = 1030 });
                    window.Controller.OnPointerUp(sample with { Y = sample.Y + 35, TimeMs = 1040 });
                    window.Controller.FinishDotAnimations();
                    Check("page margin input commits to the nearest page without clipping", surface.ContentBounds is null && window.Controller.Document.Count == 1
                        && window.Controller.Document.Strokes[0].Bounds.Right > workspaceFile.Pages[0].DipWidth);
                    window.Controller.Undo();
                }
            }
            finally { MouseEvent(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(oldCursor.X, oldCursor.Y); }
            window.GoToPage(0); await Task.Delay(100);
            var pen = window.Controller; pen.RecognizeShapes = false;
            var first = Vector2.Transform(new(140, 200), pen.ViewTransform);
            var input = new OverlayPointer(431, first.X, first.Y, .5f, 4, 4, OverlayPointerKind.Touch, 1000);
            pen.OnPointerDown(input);
            for (int i = 1; i <= 45; i++)
            {
                var point = Vector2.Transform(new(140 + i * 4, 200 + MathF.Sin(i / 5f) * 24), pen.ViewTransform);
                input = input with { X = point.X, Y = point.Y, TimeMs = (uint)(1000 + i * 6) }; pen.OnPointerMove(input);
            }
            pen.OnPointerUp(input); pen.FinishDotAnimations();
            Check("original touch ink controller commits PDF annotation", pen.Document.Count == 1 && window.IsDirty);
            var expectedPoints = pen.Document.Strokes[0].Points.ToArray();
            window.GoToPage(1); var second = window.Controller;
            window.GoToPage(0);
            Check("page changes restore independent ink and history", !ReferenceEquals(pen, second) && ReferenceEquals(pen, window.Controller) && pen.Document.Count == 1 && second.Document.Count == 0);
            Check("native window save", await window.SaveAsync() && !window.IsDirty);
            using (var restored = await PdfAnnotationFile.OpenAsync(source)) Check("workspace edits persist inside PDF", restored.GetInk(0).Single().Points.SequenceEqual(expectedPoints));
            await window.SearchAsync("classroom");
            Check("search opens sidebar and navigates to highlighted result", window.SidebarOpen && window.SearchResultCount == 1 && window.PageIndex == 5);
            window.NextSearchResult(1); window.NextSearchResult(-1);
            Check("search previous next wraps around results", window.PageIndex == 5);
            await Task.Delay(250); await ScreenshotAsync(window, Path.Combine(directory, "pdf-search.png"));
            await window.SearchAsync("missing classroom phrase");
            Check("search clears stale highlights when no match", window.SearchResultCount == 0);
            await window.SearchAsync("");
            Check("empty search restores page previews", window.SearchResultCount == 0);
            for (int i = 0; i < 16; i++) { window.GoToPage(i % 6); await Task.Delay(40); }
            window.GoToPage(0); await Task.Delay(800);
            Check("preview cache remains bounded", window.PreviewCacheBytes <= 16 * 1024 * 1024 && window.VisiblePreviewBytes <= 104 * 1024 * 1024,
                new { bytes = window.PreviewCacheBytes, visibleBytes = window.VisiblePreviewBytes, workingSet = Process.GetCurrentProcess().WorkingSet64 });
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-window.png"));
            long warmPreviewBytes = window.VisiblePreviewBytes;
            var warmController = window.Controller;
            var warmView = warmController.ViewTransform;
            int warmUndo = warmController.History.UndoCount;
            window.AppWindow.Hide();
            Check("hidden PDF releases page rendering resources", window.ReleaseInactiveResources() && window.VisiblePreviewBytes == 0,
                new { warmPreviewBytes, releasedBytes = warmPreviewBytes });
            await Task.Delay(200);
            Check("hidden PDF does not recreate page previews", window.VisiblePreviewBytes == 0);
            var restoreTimer = Stopwatch.StartNew();
            window.Activate();
            for (int i = 0; i < 40 && window.VisiblePreviewBytes == 0; i++) await Task.Delay(50);
            Check("restoring PDF preserves page ink undo and view", window.VisiblePreviewBytes > 0
                && ReferenceEquals(window.Controller, warmController) && window.Controller.ViewTransform == warmView
                && window.Controller.History.UndoCount == warmUndo,
                new { milliseconds = restoreTimer.ElapsedMilliseconds, bytes = window.VisiblePreviewBytes });
            Check("window closes cleanly", await window.CloseAsync()); window = null;
            CreateFixture(Path.Combine(directory, "many-pages.pdf"), 160);
            using (var large = await PdfAnnotationFile.OpenAsync(Path.Combine(directory, "many-pages.pdf")))
            {
                var watch = Stopwatch.StartNew(); await using var renderer = await PdfRasterizer.OpenAsync(large, CancellationToken.None);
                for (int index = 0; index < 30; index++) await renderer.PreviewAsync(index, large.Pages[index], CancellationToken.None);
                Check("large PDFs render on demand with bounded cache", renderer.CachedBytes <= 16 * 1024 * 1024, new { pages = large.Pages.Count, renderedPages = 30, milliseconds = watch.ElapsedMilliseconds, renderer.CachedBytes });
            }
            string fixtures = Path.GetFullPath(Path.Combine(directory, "..", "pdf-fixtures"));
            if (Directory.Exists(fixtures))
            foreach (string path in Directory.GetFiles(fixtures, "*.pdf"))
            {
                using var original = await PdfAnnotationFile.OpenAsync(path);
                await using var before = await PdfRasterizer.OpenAsync(original, CancellationToken.None);
                var preview = await before.PreviewAsync(0, original.Pages[0], CancellationToken.None);
                string output = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "-annotated.pdf");
                await original.SaveAsync(output, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = inks });
                using var reloaded = await PdfAnnotationFile.OpenAsync(output);
                await using var after = await PdfRasterizer.OpenAsync(reloaded, CancellationToken.None);
                var restored = await after.PreviewAsync(0, reloaded.Pages[0], CancellationToken.None);
                using var sourceBitmap = await BitmapAsync(preview, CanvasDevice.GetSharedDevice());
                using var restoredBitmap = await BitmapAsync(restored, CanvasDevice.GetSharedDevice());
                Check("external PDF original content preserved " + Path.GetFileName(path), reloaded.Pages.Count == original.Pages.Count && sourceBitmap.GetPixelBytes().SequenceEqual(restoredBitmap.GetPixelBytes()),
                    new { pages = original.Pages.Count, sourceBytes = new FileInfo(path).Length, savedBytes = new FileInfo(output).Length });
            }
        }
        catch (Exception ex) { checks.Add(new { check = "unhandled PDF regression error", pass = false, detail = ex.ToString() }); }
        finally
        {
            if (window is not null) { window.ForceClose(); await window.Cleanup; }
            await File.WriteAllTextAsync(Path.Combine(directory, "pdf.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Check(string check, bool pass, object detail = null) => checks.Add(new { check, pass, detail });

        async Task CheckMarginRenderingAsync()
        {
            string marginSource = Path.Combine(directory, "margin-source.pdf"), marginSaved = Path.Combine(directory, "margin-saved.pdf");
            CreateFixture(marginSource, 5);
            using (var pdf = PdfSharp.Pdf.IO.PdfReader.Open(marginSource, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
            {
                foreach (PdfPage page in pdf.Pages)
                    page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("q 0 1 0 rg 0 0 600 800 re f 1 1 1 rg 20 30 560 740 re f 0 0 0 rg 80 100 40 40 re f Q"));
                pdf.Save(marginSource);
            }
            using var file = await PdfAnnotationFile.OpenAsync(marginSource);
            var pages = new Dictionary<int, IReadOnlyList<InkStroke>>();
            for (int index = 0; index < file.Pages.Count; index++)
            {
                var page = file.Pages[index];
                var strokes = new List<InkStroke>();
                foreach (var point in new[] { new Vector2(-40, 200), new(200, -40), new(page.DipWidth + 40, 200), new(200, page.DipHeight + 40) })
                {
                    var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 20, TaperKind = TaperKind.None, Color = InkColor.Red, Nib = PenNib.Ellipse45 });
                    stroke.AddPoint(point.X, point.Y); stroke.AddPoint(point.X + 12, point.Y + 15); stroke.Complete(); strokes.Add(stroke);
                }
                pages[index] = strokes;
            }
            await file.SaveAsync(marginSaved, pages);
            using var restored = await PdfAnnotationFile.OpenAsync(marginSaved);
            await using var original = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
            await using var output = await PdfRasterizer.OpenAsync(marginSaved, null, CancellationToken.None);
            var device = CanvasDevice.GetSharedDevice();
            for (int index = 0; index < file.Pages.Count; index++)
            {
                var page = file.Pages[index]; var expanded = restored.Pages[index];
                var offset = PdfPageBleed.InkOffset(page, expanded);
                using var background = await BitmapAsync(await original.RenderAsync(index, page, new(0, 0, page.DipWidth, page.DipHeight), 1, CancellationToken.None), device);
                using var actual = await BitmapAsync(await output.RenderAsync(index, expanded, new(0, 0, expanded.DipWidth, expanded.DipHeight), 1, CancellationToken.None), device);
                using var expected = new CanvasRenderTarget(device, (float)actual.Size.Width, (float)actual.Size.Height, 96);
                using var controller = new InkController(); controller.Document.AddRange(pages[index]);
                using var scene = new InkSceneRenderer();
                var scale = new Vector2((float)actual.Size.Width / expanded.DipWidth, (float)actual.Size.Height / expanded.DipHeight);
                using (var draw = expected.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.White);
                    draw.DrawImage(background, new Windows.Foundation.Rect(offset.X * scale.X, offset.Y * scale.Y, page.DipWidth * scale.X, page.DipHeight * scale.Y), background.Bounds);
                    scene.DrawCommitted(draw, controller, Matrix3x2.CreateTranslation(offset) * Matrix3x2.CreateScale(scale));
                }
                byte[] a = actual.GetPixelBytes(), b = expected.GetPixelBytes();
                long redUnion = 0, redIntersection = 0, exposedGreen = 0;
                var actualContent = Core.Ink.Rect.Empty;
                var expectedContent = Core.Ink.Rect.Empty;
                int width = (int)actual.SizeInPixels.Width;
                for (int pixel = 0; pixel < a.Length; pixel += 4)
                {
                    bool redA = a[pixel] < 100 && a[pixel + 1] < 100 && a[pixel + 2] > 150;
                    bool redB = b[pixel] < 100 && b[pixel + 1] < 100 && b[pixel + 2] > 150;
                    if (redA || redB) redUnion++;
                    if (redA && redB) redIntersection++;
                    if (a[pixel] < 100 && a[pixel + 1] > 180 && a[pixel + 2] < 100) exposedGreen++;
                    int x = pixel / 4 % width, y = pixel / 4 / width;
                    if (Math.Max(a[pixel], Math.Max(a[pixel + 1], a[pixel + 2])) < 100)
                        actualContent = actualContent.Union(new(x, y, x + 1, y + 1));
                    if (Math.Max(b[pixel], Math.Max(b[pixel + 1], b[pixel + 2])) < 100)
                        expectedContent = expectedContent.Union(new(x, y, x + 1, y + 1));
                }
                double overlap = redIntersection / (double)Math.Max(1, redUnion);
                float contentShift = new[] { Math.Abs(actualContent.Left - expectedContent.Left), Math.Abs(actualContent.Top - expectedContent.Top),
                    Math.Abs(actualContent.Right - expectedContent.Right), Math.Abs(actualContent.Bottom - expectedContent.Bottom) }.Max();
                // Re-rasterizing vector content at a different pixel origin can move an edge by one pixel.
                Check("margin ink and original crop render in place page " + index,
                    overlap > .995 && exposedGreen == 0 && !actualContent.IsEmpty && contentShift <= 2,
                    new { overlap, exposedGreen, contentShift, page.Rotation, page.UserUnit });
                await actual.SaveAsync(Path.Combine(directory, "margin-" + index + ".png"), CanvasBitmapFileFormat.Png);
                await expected.SaveAsync(Path.Combine(directory, "margin-reference-" + index + ".png"), CanvasBitmapFileFormat.Png);
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Platform.Interop.NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Platform.Interop.NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Platform.Interop.NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);

    private static float MatrixDistance(Matrix3x2 a, Matrix3x2 b)
        => Vector2.Distance(a.Translation, b.Translation) + Math.Abs(a.M11 - b.M11) + Math.Abs(a.M22 - b.M22);
    private static List<InkStroke> CreateInk()
    {
        var result = new List<InkStroke>();
        foreach (var nib in Enum.GetValues<PenNib>())
        foreach (var taper in new[] { TaperKind.None, TaperKind.Tail, TaperKind.Calligraphy })
        {
            float y = 150 + result.Count * 64;
            var stroke = new InkStroke(new StrokeAttributes { Color = InkColor.Red, BaseWidth = 15, Nib = nib, TaperKind = taper });
            for (int p = 0; p < 140; p++) stroke.AddPoint(90 + p * 3, y + MathF.Sin(p / 12f) * 16, .5f, 1000 + p * 5);
            stroke.Complete(); result.Add(stroke);
        }
        var marker = new InkStroke(new StrokeAttributes { Color = new InkColor(255, 255, 210, 0), BaseWidth = 36, IsHighlighter = true, HighlighterAlpha = 96, Nib = PenNib.Ellipse45 }) { LayerOrder = 2 };
        for (int p = 0; p < 180; p++) marker.AddPoint(100 + MathF.Sin(p / 30f) * 95 + p * 1.8f, 600 + MathF.Cos(p / 18f) * 22, .5f, 1000 + p * 4);
        marker.Complete(); result.Add(marker);
        var dot = new InkStroke(new StrokeAttributes { Color = InkColor.Black, BaseWidth = 20, TaperKind = TaperKind.None }); dot.AddPoint(540, 300); dot.Complete(); result.Add(dot);
        var under = new InkStroke(new StrokeAttributes { Color = InkColor.Black, BaseWidth = 14, TaperKind = TaperKind.None }) { LayerOrder = 1 };
        under.AddPoint(90, 600); under.AddPoint(550, 600); under.Complete(); result.Add(under);
        var longStroke = new InkStroke(new StrokeAttributes { Color = InkColor.Red, BaseWidth = 7, TaperKind = TaperKind.Velocity });
        for (int p = 0; p < 1100; p++) longStroke.AddPoint(100 + p * .4f, 700 + MathF.Sin(p / 22f) * 12, .5f, p * 4);
        longStroke.Complete(); result.Add(longStroke);
        return result;
    }
    private static void CreateFixture(string path, int count)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (int p = 0; p < count; p++)
        {
            var page = document.AddPage(); page.Width = XUnit.FromPoint(600); page.Height = XUnit.FromPoint(800); page.Rotate = (p % 4) * 90;
            page.CropBox = new PdfRectangle(new XRect(20, 30, 560, 740));
            if (p == 4) page.Elements.SetReal("/UserUnit", 2);
            if (p == 5)
            {
                var font = new PdfDictionary(document); font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1"); font.Elements.SetName("/BaseFont", "/Helvetica"); document.Internals.AddObject(font);
                var fonts = new PdfDictionary(document); fonts.Elements["/F1"] = font.Reference;
                var resources = new PdfDictionary(document); resources.Elements["/Font"] = fonts; page.Elements["/Resources"] = resources;
                var content = new PdfDictionary(document); content.CreateStream(Encoding.ASCII.GetBytes("BT /F1 20 Tf 70 720 Td (InkCanvas classroom PDF) Tj ET")); document.Internals.AddObject(content); page.Elements["/Contents"] = content.Reference;
                document.Outlines.Add("Classroom", page, true);
            }
        }
        document.Save(path);
    }
    private static async Task<CanvasBitmap> BitmapAsync(byte[] bytes, CanvasDevice device)
    {
        using var memory = new InMemoryRandomAccessStream(); using var writer = new DataWriter(memory); writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); memory.Seek(0);
        return await CanvasBitmap.LoadAsync(device, memory, 96);
    }
    private static async Task ScreenshotAsync(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(window.Content as FrameworkElement);
        var buffer = await bitmap.GetPixelsAsync(); var bytes = new byte[buffer.Length]; DataReader.FromBuffer(buffer).ReadBytes(bytes);
        using var memory = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes); await encoder.FlushAsync();
        memory.Seek(0); using var output = File.Create(path); await memory.AsStreamForRead().CopyToAsync(output);
    }
}
