using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.Pdf;

public static class PdfPageEditingDiagnostics
{
    public static async Task RunAsync(IDocumentWorkspaceHost host, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        PdfWindow window = null;
        try
        {
            string path = Path.Combine(directory, "page-editing.pdf");
            CreateFixture(path);
            var file = await PdfAnnotationFile.OpenAsync(path);
            var rasterizer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
            window = new PdfWindow(host, file, rasterizer, () => Task.CompletedTask, () => Task.CompletedTask);
            host.Attach(window); window.Activate(); await Task.Delay(500);
            window.Controller.ViewTransform *= Matrix3x2.CreateScale(1.37f);
            window.Controller.NotifyElementsChanged();
            float scale = Scale();
            var next = Button("下一页"); var previous = Button("上一页");
            for (int iteration = 0; iteration < 5; iteration++)
            {
                Invoke(next); await Task.Delay(40);
                Check("toolbar next page keeps zoom across mixed page sizes", Math.Abs(Scale() - scale) < .0001f, new { iteration, page = window.PageIndex, scale = Scale() });
            }
            Invoke(previous); await Task.Delay(40);
            Check("toolbar previous page keeps zoom", Math.Abs(Scale() - scale) < .0001f);
            window.GoToPage(0);
            window.SetPageLayout(true, false);
            Check("double continuous mode pairs the first two pages", window.DoublePages
                && window.PageLayout[0].Top == window.PageLayout[1].Top && Math.Abs(Scale() - scale) < .0001f);
            var documentView = window.PageLayout.DocumentView(window.PageIndex, window.Controller.ViewTransform);
            var rightCenter = Vector2.Transform(window.PageLayout[1].Center, documentView);
            window.ActivateTouchedPage(new OverlayPointer(8999, rightCenter.X, rightCenter.Y, .5f, 0, 0, OverlayPointerKind.Pen, 0));
            Check("touching the facing page keeps view and changes editable target", window.PageIndex == 1
                && Vector2.Distance(documentView.Translation, window.PageLayout.DocumentView(1, window.Controller.ViewTransform).Translation) < .001f);
            Invoke(next); await Task.Delay(40);
            Check("double page navigation advances one spread", window.PageIndex == 2 && Math.Abs(Scale() - scale) < .0001f);
            window.SetPageLayout(true, true); window.GoToPage(0);
            Check("cover occupies its own row", window.SeparateCover && window.PageLayout[0].Bottom < window.PageLayout[1].Top
                && window.PageLayout[1].Top == window.PageLayout[2].Top);
            Invoke(next); await Task.Delay(40);
            Check("cover navigation reaches first paired spread", window.PageIndex == 1);
            window.SetPageLayout(false, false); window.GoToPage(0);
            var originalLayout = window.PageLayout[0];
            for (int turn = 0; turn < 4; turn++) window.RotatePage(90);
            Check("four clockwise rotations restore page geometry and zoom", window.PageLayout[0] == originalLayout && Math.Abs(Scale() - scale) < .0001f);
            window.RotatePage(-90);
            Check("counterclockwise rotation swaps page dimensions", Math.Abs(window.PageLayout[0].Width - originalLayout.Height) < .001f);
            window.RotatePage(90);

            var first = window.PageController(0);
            first.Tool = InkTool.Pen; first.RecognizeShapes = false;
            first.CurrentAttributes.TaperKind = TaperKind.None;
            float height = file.Pages[0].DipHeight;
            var start = Vector2.Transform(new Vector2(200, height - 35), first.ViewTransform);
            var end = Vector2.Transform(new Vector2(220, height + 64), first.ViewTransform);
            var down = new OverlayPointer(9001, start.X, start.Y, .4f, 0, 0, OverlayPointerKind.Pen, 100);
            window.NavigationInputStarting(down); first.OnPointerDown(down);
            first.OnPointerMove(down with { X = end.X, Y = end.Y, TimeMs = 160 });
            first.OnPointerUp(down with { X = end.X, Y = end.Y, TimeMs = 180 });
            window.NavigationInputEnded(); await Task.Delay(80);
            var second = window.PageController(1);
            Check("continuous handwriting splits into both editable pages", first.Document.Count == 1 && second.Document.Count == 1
                && first.Document.Strokes[0].Id != second.Document.Strokes[0].Id);
            Check("split ink uses one document undo step", first.History.UndoCount == 1 && ReferenceEquals(first.History, second.History));
            second.Undo();
            Check("undo from the next page removes both fragments", first.Document.Count == 0 && second.Document.Count == 0);
            first.Redo();
            Check("redo restores both fragments", first.Document.Count == 1 && second.Document.Count == 1);
            window.GoToPage(1);
            second.Tool = InkTool.Eraser; second.EraserMode = InkEraserMode.WholeStroke;
            var hit = second.Document.Strokes[0].Points.Last().Position;
            var point = Vector2.Transform(hit, second.ViewTransform);
            var erase = down with { Id = 9002, X = point.X, Y = point.Y, TimeMs = 220 };
            window.NavigationInputStarting(erase); second.OnPointerDown(erase); second.OnPointerUp(erase); window.NavigationInputEnded();
            Check("eraser deletes the next-page fragment", first.Document.Count == 1 && second.Document.Count == 0);
            second.Undo();
            Check("erasing a fragment can be undone", second.Document.Count == 1);
            window.RotatePage(90);
            Check("rotating preserves canonical editable ink", second.Document.Count == 1 && second.Document.Strokes[0].Points.Last().Position == hit);
            Check("save succeeds after splitting and rotating", await window.SaveAsync());
            using (var reopened = await PdfAnnotationFile.OpenAsync(path))
            {
                Check("saved PDF retains both fragments and rotation", reopened.GetInk(0).Count == 1 && reopened.GetInk(1).Count == 1
                    && reopened.Pages[1].NormalizedRotation == (file.Pages[1].NormalizedRotation + 90) % 360);
                Check("cross-page stroke leaves page sizes unchanged", reopened.Pages[0].DipWidth == file.Pages[0].DipWidth
                    && reopened.Pages[0].DipHeight == file.Pages[0].DipHeight);
            }
            foreach (int theme in new[] { 1, 2 })
            {
                host.Settings.Appearance.Theme = theme; window.ApplySettings();
                window.SetPageLayout(true, true);
                window.GoToPage(0);
                Invoke(Button("切换整页或页宽"));
                Invoke(Button("切换整页或页宽"));
                await Task.Delay(100);
                await PdfDiagnostics.ScreenshotAsync(window, Path.Combine(directory, $"pdf-cover-{theme}.png"));
                window.GoToPage(1);
                Invoke(Button("切换整页或页宽"));
                Invoke(Button("切换整页或页宽"));
                await Task.Delay(100);
                await PdfDiagnostics.ScreenshotAsync(window, Path.Combine(directory, $"pdf-double-{theme}.png"));
                window.SmoothScrollBy(new(0, -300));
                await Task.Delay(80);
                Check("scroll snapshot draws an opaque page gap", window.NavigationGapOpaque(), new { theme });
                await Task.Delay(500);
                Check("scroll handover completes", !window.NavigationHandoverPending, new { theme });
                await PdfDiagnostics.ScreenshotAsync(window, Path.Combine(directory, $"pdf-double-cover-{theme}.png"));
            }
            window.PageController(0).Document.Clear(); window.PageController(1).Document.Clear();
            Check("deleting and saving clears both page annotations", await window.SaveAsync());
            using (var reopened = await PdfAnnotationFile.OpenAsync(path))
                Check("deleted cross-page ink stays deleted after reopening", !reopened.InkPages.Any());
            window.SetPageLayout(false, false); window.GoToPage(0);
            first.History.Clear(); first.Tool = InkTool.Pen;
            first.MultiTouchEnabled = true;
            start = Vector2.Transform(new Vector2(200, height - 35), first.ViewTransform);
            end = Vector2.Transform(new Vector2(220, height + 64), first.ViewTransform);
            var one = down with { Id = 9010, X = start.X, Y = start.Y, TimeMs = 300 };
            var two = one with { Id = 9011, X = start.X + 30, TimeMs = 310 };
            first.OnPointerDown(one); first.OnPointerDown(two);
            first.OnPointerMove(one with { X = end.X, Y = end.Y, TimeMs = 350 });
            first.OnPointerMove(two with { X = end.X + 30, Y = end.Y, TimeMs = 355 });
            first.OnPointerUp(one with { X = end.X, Y = end.Y, TimeMs = 360 });
            first.OnPointerUp(two with { X = end.X + 30, Y = end.Y, TimeMs = 370 });
            Check("simultaneous cross-page strokes retain separate undo steps", first.Document.Count == 2
                && second.Document.Count == 2 && first.History.UndoCount == 2);
            first.Undo(); await Task.Delay(40);
            Check("first undo keeps one complete split stroke", first.Document.Count == 1 && second.Document.Count == 1);
            first.Undo();
            Check("second undo removes the other complete split stroke", first.Document.Count == 0 && second.Document.Count == 0);
            first.Redo(); first.Redo();
            Check("redo restores both simultaneous split strokes without duplicates", first.Document.Count == 2 && second.Document.Count == 2);
        }
        catch (Exception error) { checks.Add(new { check = "PDF page editing exception", passed = false, detail = error.ToString() }); }
        finally
        {
            if (window is not null) { await window.SaveAsync(); await window.CloseAsync(); await window.Cleanup; }
            await File.WriteAllTextAsync(Path.Combine(directory, "pdf-page-editing.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        float Scale() => MathF.Sqrt(MathF.Abs(window.Controller.ViewTransform.GetDeterminant()));
        Button Button(string name) => Descendants(window.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });
    }

    private static void CreateFixture(string path)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (int page = 0; page < 6; page++)
        {
            var item = document.AddPage(); item.Width = PdfSharp.Drawing.XUnit.FromPoint(600);
            item.Height = PdfSharp.Drawing.XUnit.FromPoint(page == 3 ? 500 : 800); item.Rotate = page % 2 * 90;
            var font = new PdfSharp.Pdf.PdfDictionary(document);
            font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1");
            font.Elements.SetName("/BaseFont", "/Helvetica"); document.Internals.AddObject(font);
            var fonts = new PdfSharp.Pdf.PdfDictionary(document); fonts.Elements["/F1"] = font.Reference;
            var resources = new PdfSharp.Pdf.PdfDictionary(document); resources.Elements["/Font"] = fonts;
            item.Elements["/Resources"] = resources;
            var content = new PdfSharp.Pdf.PdfDictionary(document);
            content.CreateStream(System.Text.Encoding.ASCII.GetBytes($"q .92 .96 1 rg 30 30 540 {(page == 3 ? 440 : 740)} re f Q\n"
                + $"BT /F1 36 Tf 60 {(page == 3 ? 410 : 710)} Td (Page {page + 1}) Tj ET\nq .9 .2 .1 rg 60 60 80 40 re f Q\n"));
            document.Internals.AddObject(content); item.Elements["/Contents"] = content.Reference;
        }
        document.Save(path);
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
