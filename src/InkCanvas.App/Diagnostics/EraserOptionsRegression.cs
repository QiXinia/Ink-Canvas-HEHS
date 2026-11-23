using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Settings;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace InkCanvas.App.Diagnostics;

internal static class EraserOptionsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var services = new AppServices(System.IO.Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.SaveOnExit = false;
        services.Settings.Resume.Whiteboard = ResumeBehavior.Disabled;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        var checks = new List<object>();
        int applications = 0;
        services.SettingsApplied += () => applications++;
        try
        {
            shell.Start(); shell.StartInClickThrough();
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            foreach (int theme in new[] { 1, 2 })
            {
                services.Settings.Appearance.Theme = theme;
                services.Settings.Canvas.EraserDiameter = null;
                services.Settings.Canvas.EraserSize = 2;
                services.ApplySettings(); WindowChrome.ApplyTheme(bar, theme);
                await Task.Delay(80); bar.Bar.RefreshSettingsVisuals();
                services.OverlayInk.Tool = InkTool.Pen;
                Invoke((Button)bar.Bar.GetToolButton(InkTool.Eraser));
                Check("first eraser click only activates tool " + theme, services.OverlayInk.Tool == InkTool.Eraser
                    && bar.GetOpenPanel(BarPanelKind.Eraser) is null);
                Invoke((Button)bar.Bar.GetToolButton(InkTool.Eraser)); await Task.Delay(100);
                var window = bar.GetOpenPanel(BarPanelKind.Eraser);
                var palette = Descendants(window.Content).OfType<EraserPalette>().Single();
                var slider = Descendants(palette).OfType<PointerSlider>().Single();
                var preview = Descendants(palette).OfType<Ellipse>().Single(item => AutomationProperties.GetName(item) == "面积擦范围预览");
                Check("slider restores legacy diameter " + theme, slider.Value == 90 && preview.Width == 90);
                Check("size selector uses a nonactivating slider " + theme, !slider.AllowFocusOnInteraction
                    && !Descendants(palette).OfType<ComboBox>().Any());
                int before = applications;
                for (int i = 0; i < 600; i++) slider.Value = 10 + i % 191;
                slider.Value = 200;
                Check("live diameter and preview update without global work " + theme,
                    applications == before && services.OverlayInk.EraserRadius == 100 && preview.Width == 200 && preview.Height == 200);
                await Task.Delay(300);
                Check("slider burst saves once " + theme, applications == before + 1 && services.Store.Load().Canvas.EraserDiameter == 200);
                Check("largest preview stays inside its surface " + theme, Fits(preview, (FrameworkElement)preview.Parent)
                    && Fits(slider, palette) && Fits(preview, palette));
                var stroke = (SolidColorBrush)preview.Stroke;
                Check("range preview follows light and dark theme " + theme,
                    theme == 2 ? stroke.Color.R == 255 : stroke.Color.R == 0);
                await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, System.IO.Path.Combine(directory, "eraser-" + theme + ".png"));
                before = applications;
                bool? dragged = await PenOptionsRegression.TryPointer(window, slider, drag: true);
                checks.Add(new { check = "native inactive eraser slider drag " + theme, available = dragged.HasValue,
                    passed = dragged.HasValue ? (bool?)(dragged.Value && slider.Value < 200 && applications == before + 1
                        && services.Store.Load().Canvas.EraserDiameter == slider.Value && preview.Width == slider.Value) : null });
                slider.Value = 117; window.Close();
                Check("closing immediately preserves final diameter " + theme, services.Store.Load().Canvas.EraserDiameter == 117);
                Invoke((Button)bar.Bar.GetToolButton(InkTool.Eraser)); await Task.Delay(70);
                palette = Descendants(bar.GetOpenPanel(BarPanelKind.Eraser).Content).OfType<EraserPalette>().Single();
                Check("reopening restores exact custom diameter " + theme, Descendants(palette).OfType<Slider>().Single().Value == 117);
                Invoke(Descendants(palette).OfType<TouchIconButton>().Single(button => button.Label == "套索擦"));
                Check("lasso remains selectable " + theme, services.OverlayInk.EraserMode == InkEraserMode.Lasso);
            }

            shell.ToggleBoard(); await Task.Delay(100);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            board.ShowEraserPalette(); await Task.Delay(80);
            var boardWindow = board.ToolPanels.Single(window => Descendants(window.Content).OfType<EraserPalette>().Any());
            var boardPalette = Descendants(boardWindow.Content).OfType<EraserPalette>().Single();
            Descendants(boardPalette).OfType<Slider>().Single().Value = 54;
            Check("board diameter changes live", board.CurrentController.EraserRadius == 27);
            boardWindow.Close();
            Check("board close flushes pending size", services.Store.Load().Canvas.EraserDiameter == 54);
            foreach (var (mode, label) in new[] { (InkEraserMode.Area, "面积擦"), (InkEraserMode.WholeStroke, "墨迹擦"), (InkEraserMode.Lasso, "套索擦") })
            {
                board.ShowEraserPalette(); await Task.Delay(50);
                boardPalette = board.ToolPanels.SelectMany(window => Descendants(window.Content)).OfType<EraserPalette>().Single();
                Invoke(Descendants(boardPalette).OfType<TouchIconButton>().Single(button => button.Label == label));
                Check("board mode still works " + label, board.CurrentController.EraserMode == mode);
            }
            await board.HideForAnnotationAsync();

            var pdf = services.Modules.GetExport<IPdfWorkspaceService>();
            string file = System.IO.Path.Combine(directory, "eraser.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics").GetMethod("CreateFixture",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { file, 2 });
            await shell.OpenPdfAsync(file); await Task.Delay(100);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
            workspace.Controller.Tool = InkTool.Eraser;
            Invoke((Button)bar.Bar.GetToolButton(InkTool.Eraser)); await Task.Delay(70);
            if (bar.GetOpenPanel(BarPanelKind.Eraser) is null)
            {
                shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
                Invoke((Button)bar.Bar.GetToolButton(InkTool.Eraser)); await Task.Delay(70);
            }
            var pdfWindow = bar.GetOpenPanel(BarPanelKind.Eraser);
            var pdfSlider = Descendants(pdfWindow.Content).OfType<Slider>().Single();
            pdfSlider.Value = 138;
            Check("PDF targets the document eraser", workspace.Controller.EraserRadius == 69);
            pdfWindow.Close();
            Check("PDF diameter persists on close", services.Store.Load().Canvas.EraserDiameter == 138);
            await workspace.CloseAsync();
        }
        catch (Exception error) { checks.Add(new { check = "eraser options exception", passed = false, error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "eraser-options.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            var windows = shell.OpenWindows.Where(window => window is not BoardWindow).ToArray();
            shell.Dispose();
            foreach (var window in windows) if (window.AppWindow is not null) window.Close();
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static bool Fits(FrameworkElement child, FrameworkElement parent)
    {
        var rect = child.TransformToVisual(parent).TransformBounds(new Windows.Foundation.Rect(0, 0, child.ActualWidth, child.ActualHeight));
        return rect.Left >= -1 && rect.Top >= -1 && rect.Right <= parent.ActualWidth + 1 && rect.Bottom <= parent.ActualHeight + 1;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
