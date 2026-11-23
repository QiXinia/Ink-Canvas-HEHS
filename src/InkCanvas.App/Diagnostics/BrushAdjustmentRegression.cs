using System.Diagnostics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class BrushAdjustmentRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false; services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.SaveOnExit = false; services.Settings.Resume.Whiteboard = ResumeBehavior.Disabled;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        var checks = new List<object>();
        int applications = 0;
        services.SettingsApplied += () => applications++;
        try
        {
            shell.Start(); shell.StartInClickThrough();
            foreach (var (highlighter, taper) in new[] { (false, TaperKind.Tail), (true, TaperKind.None), (false, TaperKind.InkWash) })
            {
                services.Settings.Canvas.InkStyle = OriginalMenuCatalog.ToPenStyle(taper); services.ApplySettings();
                shell.OpenPenPalette(highlighter); await Task.Delay(160);
                var window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                var palette = Descendants(window.Content).OfType<PenPalette>().Single();
                var width = Slider(palette, highlighter ? "荧光笔粗细" : "画笔粗细");
                var preview = Descendants(palette).OfType<PenStrokePreview>().Single();
                var colors = Descendants(palette).OfType<InkColorPalette>().Single();
                var swatches = Descendants(colors).OfType<Button>().ToArray();
                int before = applications;
                var watch = Stopwatch.StartNew();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 600; index++) width.Value = 1 + index % (int)width.Maximum;
                width.Value = 18;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; watch.Stop();
                string name = highlighter ? "highlighter" : taper.ToString();
                Check("slider edits avoid synchronous global settings work " + name, applications == before);
                Check("slider edits retain color controls " + name, swatches.SequenceEqual(Descendants(colors).OfType<Button>()));
                Check("width and preview update before persistence " + name, preview.Stroke.Attributes.BaseWidth == (taper == TaperKind.InkWash ? 18 : 9));
                checks.Add(new { check = "600 width changes", name, milliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes = allocated });
                await Task.Delay(300);
                Check("width changes produce one settings commit " + name, applications == before + 1);
                var saved = services.Store.Load().Canvas;
                Check("last width is saved " + name, (highlighter ? saved.HighlighterWidth : taper == TaperKind.InkWash ? saved.InkWashWidth : saved.InkWidth)
                    == (taper == TaperKind.InkWash ? 18 : 9));
                if (highlighter)
                {
                    var alpha = Descendants(palette).OfType<Slider>().Single(item => AutomationProperties.GetName(item).StartsWith("荧光笔") && !AutomationProperties.GetName(item).Contains("粗细"));
                    before = applications; long geometry = preview.GeometryBuildCount;
                    for (int index = 0; index < 600; index++) alpha.Value = alpha.Minimum + index % ((int)((alpha.Maximum - alpha.Minimum) / alpha.StepFrequency) + 1) * alpha.StepFrequency;
                    alpha.Value = alpha.Maximum - alpha.StepFrequency;
                    Check("opacity updates the live brush immediately", services.OverlayInk.CurrentAttributes.HighlighterAlpha == services.Settings.Canvas.InkAlpha);
                    Check("opacity changes avoid synchronous global settings work", applications == before);
                    await Task.Delay(300);
                    Check("opacity changes produce one settings commit", applications == before + 1);
                    Check("opacity reuses preview geometry", preview.GeometryBuildCount == geometry);
                    Check("last opacity is saved", services.Store.Load().Canvas.InkAlpha == services.OverlayInk.CurrentAttributes.HighlighterAlpha);
                }
                width.Value = 22; ((Window)window).Close();
                saved = services.Store.Load().Canvas;
                Check("closing before debounce saves final width " + name, (highlighter ? saved.HighlighterWidth : taper == TaperKind.InkWash ? saved.InkWashWidth : saved.InkWidth)
                    == (taper == TaperKind.InkWash ? 22 : 11));
            }

            services.Settings.Canvas.InkStyle = OriginalMenuCatalog.ToPenStyle(TaperKind.InkWash); services.ApplySettings();
            shell.ToggleBoard(); await Task.Delay(100);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            board.ShowPenPalette(); await Task.Delay(100);
            var boardPalette = board.ToolPanels.SelectMany(window => Descendants(window.Content)).OfType<BoardPenPalette>().Single();
            foreach (var slider in Descendants(boardPalette).OfType<Slider>().ToArray())
            {
                int before = applications;
                for (int index = 0; index < 300; index++) slider.Value = slider.Minimum + index % ((int)((slider.Maximum - slider.Minimum) / slider.StepFrequency) + 1) * slider.StepFrequency;
                Check("board slider avoids synchronous settings work " + AutomationProperties.GetName(slider), applications == before);
                await Task.Delay(280);
                Check("board slider commits once " + AutomationProperties.GetName(slider), applications == before + 1);
            }
            Slider(boardPalette, "荧光笔粗细").Value = 78;
            board.ToolPanels.Single(window => Descendants(window.Content).Contains(boardPalette)).Close();
            Check("closing board options before debounce saves final width", services.Store.Load().Canvas.HighlighterWidth == 39);
            await board.HideForAnnotationAsync();

            var pdf = services.Modules.GetExport<IPdfWorkspaceService>();
            string file = Path.Combine(directory, "brush.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics").GetMethod("CreateFixture",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { file, 2 });
            await shell.OpenPdfAsync(file); await Task.Delay(100);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
            shell.OpenPenPalette(true); await Task.Delay(80);
            var pdfWindow = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
            var pdfWidth = Slider(pdfWindow.Content, "荧光笔粗细");
            int pdfBefore = applications;
            for (int index = 0; index < 300; index++) pdfWidth.Value = 1 + index % (int)pdfWidth.Maximum;
            pdfWidth.Value = 80;
            Check("PDF slider targets active brush without full refresh", applications == pdfBefore && workspace.Controller.GetDrawingAttributes(true).BaseWidth == 40);
            pdfWindow.Close();
            Check("PDF closing commits the last width", services.Store.Load().Canvas.HighlighterWidth == 40);
            await workspace.CloseAsync();
        }
        catch (Exception error) { checks.Add(new { check = "brush adjustment exception", passed = false, error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "brush-adjustments.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            var windows = shell.OpenWindows.Where(window => window is not BoardWindow).ToArray();
            shell.Dispose(); foreach (var window in windows) if (window.AppWindow is not null) window.Close();
            Application.Current.Exit();
        }
        void Check(string check, bool passed) => checks.Add(new { check, passed });
    }
    private static Slider Slider(DependencyObject root, string prefix) => Descendants(root).OfType<Slider>().First(slider => AutomationProperties.GetName(slider).StartsWith(prefix));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
