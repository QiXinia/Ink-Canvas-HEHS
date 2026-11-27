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
            window.Controller.History.Clear();

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

            window.GoToPage(0);
            window.ClearCanvas(inkOnly: false);
            float width = file.Pages[0].DipWidth;
            var guide = new PdfColumnGuide(Guid.NewGuid(), width / 2);
            var upper = new PdfBlankBand(Guid.NewGuid(), null, guide.Id, 320, 100);
            var footer = new PdfBlankBand(Guid.NewGuid(), guide.Id, null, height, 180);
            var spacing = new PdfPageSpacing([guide], [upper, footer]);
            var crossing = new InkStroke(new StrokeAttributes { BaseWidth = 5, TaperKind = TaperKind.None });
            crossing.AddPoint(260, 275); crossing.AddPoint(260, 375); crossing.Complete(); first.Document.Add(crossing);
            window.ChangeSpacing(0, spacing, "插入课堂作答空白");
            Check("open workspace grows the whole page by the tallest column", Math.Abs(window.PageLayout[0].Height - height - 180) < .01f);
            Check("an ink stroke through a split becomes two editable fragments", first.Document.Count == 2
                && first.Document.Strokes[0].Points[^1].Y <= 320 && first.Document.Strokes[1].Points[0].Y >= 420);
            var answer = new InkStroke(new StrokeAttributes { BaseWidth = 5, TaperKind = TaperKind.None });
            answer.AddPoint(100, 355); answer.AddPoint(175, 355); answer.Complete(); first.Document.Add(answer);
            var shape = Core.Shapes.ShapeGenerator.Generate(Core.Shapes.ShapeKind.Rectangle, new(480, 100), new(570, 180), new());
            first.Document.AddRange(shape);
            window.ClearCanvas(inkOnly: true);
            Check("ink-only clear retains drawn shapes and both spaces", first.Document.Count == shape.Count
                && window.PageLayout[0].Height > height && shape.All(first.Document.Strokes.Contains));
            first.Undo();
            Check("undo handwriting clear restores answers without changing paper", first.Document.Strokes.Contains(answer) && window.PageLayout[0].Height > height);
            Check("save retains editable column and foot spaces", await window.SaveAsync());
            using (var reopened = await PdfAnnotationFile.OpenAsync(path))
            {
                var saved = reopened.GetSpacing(0);
                Check("reopened PDF retains structural edits and answer ink", saved.Guides.SequenceEqual(spacing.Guides)
                    && saved.Bands.SequenceEqual(spacing.Bands) && reopened.GetInk(0).Any(stroke => stroke.Id == answer.Id));
                await using var external = await PdfRasterizer.OpenAsync(path, null, CancellationToken.None);
                var expanded = saved.Expand(reopened.Pages[0]);
                using var bitmap = await external.RenderBitmapAsync(0, expanded, new(0, 0, expanded.DipWidth, expanded.DipHeight), .5, CancellationToken.None);
                uint pixelWidth = bitmap.SizeInPixels.Width;
                var pixels = bitmap.GetPixelColors();
                var whiteSpace = pixels[checked((int)(350 / 2 * pixelWidth + 300 / 2))];
                var oppositeColumn = pixels[checked((int)(350 / 2 * pixelWidth + 560 / 2))];
                var footSpace = pixels[checked((int)((uint)((height + 100) / 2) * pixelWidth + 560 / 2))];
                Check("standard PDF rasterizer sees a white cut only in the target column and at the foot",
                    whiteSpace.R > 250 && oppositeColumn.R < 245 && footSpace.R > 250);
            }
            first.Tool = InkTool.Eraser;
            var emptyBand = Vector2.Transform(new Vector2(300, 365), first.ViewTransform);
            var bandPress = down with { Id = 9020, X = emptyBand.X, Y = emptyBand.Y };
            first.OnPointerDown(bandPress); first.OnPointerUp(bandPress);
            Check("eraser cannot delete a blank band", window.PageLayout[0].Height > height);
            first.Tool = InkTool.Select;
            var linePoint = Vector2.Transform(new Vector2(guide.X, 240), first.ViewTransform);
            var linePress = down with { Id = 9021, X = linePoint.X, Y = linePoint.Y };
            Check("selection tool selects the guide and deletion removes both dependent blanks",
                window.TrySelectSpacing(linePress) && window.TryDeleteLayoutSelection()
                && Math.Abs(window.PageLayout[0].Height - height) < .01f
                && first.Document.Strokes.All(stroke => stroke.Id != answer.Id));
            first.Undo();
            Check("undo restores both column blanks and their answers", window.PageLayout[0].Height > height
                && first.Document.Strokes.Contains(answer));
            window.ClearCanvas(inkOnly: false);
            Check("single clear removes all editable page content including the blank bands", first.Document.Count == 0
                && Math.Abs(window.PageLayout[0].Height - height) < .01f);

            var command = (InkCanvas.Controls.Controls.TouchIconButton)Button("页面空白与分栏");
            var more = Button("更多 PDF 操作");
            var menu = (MenuFlyout)more.Flyout;
            menu.ShowAt(more);
            await Task.Delay(80);
            var insert = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "插入纵向空白");
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(insert).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(60);
            menu.Hide();
            Check("toolbar menu enters the vertical space tool", command.IsSelected);
            window.GoToPage(0);
            float fit = Math.Min((window.ViewportSize.X - 50) / width, (window.ViewportSize.Y - 125) / height);
            first.ViewTransform = Matrix3x2.CreateScale(fit) * Matrix3x2.CreateTranslation(25, 20);
            first.NotifyElementsChanged();
            await Task.Delay(100);
            var foot = Vector2.Transform(new Vector2(width / 2, height - 1), first.ViewTransform);
            bool native = await PdfDiagnostics.TryViewportDrag(window, foot, foot + new Vector2(0, 70));
            checks.Add(new { check = "native drag from the bottom edge inserts page space", available = native,
                passed = native ? (bool?)(command.IsSelected && window.PageLayout[0].Height > height + 30) : null });
            if (!native)
            {
                var pending = window.AddBlankBand(0, new(width / 2, height - 1), 120, Guid.NewGuid());
                Check("footer drag snaps to the original bottom edge", pending?.Bands.Single().SourceY == height);
                window.ChangeSpacing(0, pending, "页底空白");
                Check("applying the footer drag expands the page", Math.Abs(window.PageLayout[0].Height - height - 120) < .01f);
            }
            Invoke(Button("页面视图"));
            var grid = Descendants(window.Content).OfType<GridView>().Single(item => AutomationProperties.GetName(item) == "PDF 页面管理网格");
            await WaitUntilAsync(() => window.OrganizerReadyCount > 0);
            Check("page view uses a virtualized thumbnail grid", window.OrganizerOpen && grid.ItemsPanelRoot is ItemsWrapGrid
                && window.OrganizerReadyCount > 0);
            if (grid.ContainerFromIndex(0) is FrameworkElement source && grid.ContainerFromIndex(2) is FrameworkElement target)
            {
                var from = source.TransformToVisual(grid).TransformPoint(new(source.ActualWidth / 2, source.ActualHeight / 2));
                var to = target.TransformToVisual(grid).TransformPoint(new(target.ActualWidth * .8, target.ActualHeight / 2));
                bool drag = await PdfDiagnostics.TryViewportDrag(window, new((float)from.X, (float)from.Y), new((float)to.X, (float)to.Y), grid);
                await Task.Delay(100);
                bool reordered = !window.PageOrder.SequenceEqual(Enumerable.Range(0, 6));
                checks.Add(new { check = "native thumbnail drag reorders PDF pages", available = drag, passed = drag ? (bool?)reordered : null });
                if (reordered) window.Controller.Undo();
            }
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(grid.Items[1]);
            grid.SelectedItems.Add(grid.Items[3]);
            var firstSelected = window.PageLayout[1].Width;
            Invoke(Button("顺时针旋转选中页"));
            Check("batch rotation updates both selected pages", Math.Abs(window.PageLayout[1].Width - firstSelected) > 10);
            Invoke(Button("撤销页面更改"));
            Check("batch rotation is undoable", Math.Abs(window.PageLayout[1].Width - firstSelected) < .01f);
            Invoke(Button("前移选中页"));
            Check("moving selected pages preserves their relative order", window.PageOrder.SequenceEqual(new[] { 1, 0, 3, 2, 4, 5 }));
            Check("the active page keeps its annotation controller when moved", window.PageIndex == 1 && ReferenceEquals(window.Controller, first));
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(grid.Items[1]);
            Invoke(Button("删除选中页"));
            Check("deleting the active page selects a retained page", window.PageOrder.SequenceEqual(new[] { 1, 3, 2, 4, 5 })
                && ReferenceEquals(window.Controller, window.PageController(3)));
            Invoke(Button("撤销页面更改"));
            Check("undo restores page order and the active page's spacing", window.PageIndex == 1 && ReferenceEquals(window.Controller, first)
                && window.PageLayout[0].Height > height);
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(grid.Items[0]);
            Invoke(Button("打开选中页"));
            Check("opening a thumbnail returns to its reordered page", !window.OrganizerOpen && window.PageIndex == 0 && ReferenceEquals(window.Controller, second));
            Check("save writes the updated order", await window.SaveAsync());
            using (var reopened = await PdfAnnotationFile.OpenAsync(path))
                Check("saved page order preserves footer spacing on its original page", reopened.GetSpacing(1).Bands.Length == 1
                    && reopened.GetSpacing(0).IsEmpty && reopened.Pages.Count == 6);
        }
        catch (Exception error) { checks.Add(new { check = "PDF page editing exception", passed = false, detail = error.ToString() }); }
        finally
        {
            try
            {
                if (window is not null) { await window.SaveAsync(); await window.CloseAsync(); await window.Cleanup; }
            }
            catch (Exception error) { checks.Add(new { check = "PDF page editing cleanup", passed = false, detail = error.ToString() }); }
            try { await CheckOrganizerScaleAsync(host, directory, Check); }
            catch (Exception error) { Check("PDF organizer scale exception", false, error.ToString()); }
            await File.WriteAllTextAsync(Path.Combine(directory, "pdf-page-editing.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        float Scale() => MathF.Sqrt(MathF.Abs(window.Controller.ViewTransform.GetDeterminant()));
        Button Button(string name) => Descendants(window.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });
    }

    private static async Task CheckOrganizerScaleAsync(IDocumentWorkspaceHost host, string directory, Action<string, bool, object> check)
    {
        const int pages = 640;
        string path = Path.Combine(directory, "page-organizer-large.pdf");
        CreateFixture(path, pages);
        var file = await PdfAnnotationFile.OpenAsync(path);
        var rasterizer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
        var window = new PdfWindow(host, file, rasterizer, () => Task.CompletedTask, () => Task.CompletedTask);
        try
        {
            host.Attach(window); window.Activate();
            await Task.Delay(350);
            window.SetOrganizerOpen(true);
            var grid = Descendants(window.Content).OfType<GridView>().Single(item => AutomationProperties.GetName(item) == "PDF 页面管理网格");
            await WaitUntilAsync(() => window.OrganizerReadyCount > 0);
            check("large page view only realizes viewport-sized batches", grid.Items.Count == pages && window.OrganizerReadyCount > 0
                && window.OrganizerRealizedCount < 80, new { window.OrganizerRealizedCount, window.OrganizerReadyCount });
            int rendered = window.OrganizerRenderCount, readerRenders = window.BitmapRenderCount;
            for (int index = 80; index < pages; index += 80) grid.ScrollIntoView(grid.Items[index], ScrollIntoViewAlignment.Leading);
            grid.ScrollIntoView(grid.Items[pages - 1], ScrollIntoViewAlignment.Leading);
            await WaitUntilAsync(() => grid.ContainerFromIndex(pages - 1) is not null && window.OrganizerRenderCount > rendered);
            check("rapid jumps recycle thumbnails without rendering skipped pages", grid.ContainerFromIndex(pages - 1) is not null
                && window.OrganizerRenderCount > rendered && window.OrganizerRenderCount - rendered < 80
                && window.BitmapRenderCount == readerRenders && window.OrganizerCacheBytes <= 16 * 1024 * 1024,
                new { before = rendered, window.OrganizerRenderCount, readerRenders, window.BitmapRenderCount, window.OrganizerCacheBytes });
            await PdfDiagnostics.ScreenshotAsync(window, Path.Combine(directory, "pdf-page-organizer.png"));
        }
        finally { window.ForceClose(); await window.Cleanup; }
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 40 && !ready(); attempt++) await Task.Delay(75);
    }

    private static void CreateFixture(string path, int pageCount = 6)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (int page = 0; page < pageCount; page++)
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
