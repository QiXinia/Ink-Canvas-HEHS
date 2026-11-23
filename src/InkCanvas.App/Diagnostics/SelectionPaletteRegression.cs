using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Host.Contracts;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class SelectionPaletteRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.SaveOnExit = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        var checks = new List<object>();
        try
        {
            shell.Start(); shell.StartInClickThrough();
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            foreach (int theme in new[] { 1, 2 })
            {
                services.Settings.Appearance.Theme = theme;
                services.ApplySettings();
                WindowChrome.ApplyTheme(bar, theme);
                await Task.Delay(50);
                services.OverlayInk.Tool = Ink.InkTool.Pen;
                services.OverlayInk.Document.Clear();
                services.OverlayInk.Document.Add(Stroke());
                Invoke((Button)bar.Bar.GetToolButton(Ink.InkTool.Select));
                await Task.Delay(80);
                Check("first selection click only enters selection mode " + theme, services.OverlayInk.Tool == Ink.InkTool.Select
                    && !services.OverlayInk.HasSelection && bar.GetOpenPanel(BarPanelKind.Selection) is null);
                Invoke((Button)bar.Bar.GetToolButton(Ink.InkTool.Select));
                await Task.Delay(100);
                var popup = bar.GetOpenPanel(BarPanelKind.Selection);
                Check("second selection click opens options and selects all " + theme, popup is not null && services.OverlayInk.Selection.Count == 1);
                var palette = Descendants(popup.Content).OfType<SelectionPalette>().Single();
                CheckActions(palette, services.OverlayInk, "desktop " + theme);
                Invoke(Button(palette, "套索选择"));
                Check("lasso preference saves and reaches controller " + theme, services.Store.Load().Canvas.SelectionMode == InkSelectionMode.Lasso
                    && services.OverlayInk.SelectionMode == InkSelectionMode.Lasso);
                foreach (double scale in new[] { 50d, 100d, 150d, 200d })
                {
                    bar.Reposition(services.Settings.Appearance.FloatingBarBottomMargin, scale, recenter: false);
                    await Task.Delay(30);
                    var work = WindowChrome.GetWorkArea(popup);
                    var pos = popup.AppWindow.Position; var size = popup.AppWindow.Size;
                    Check("selection options fit the working screen " + theme + "-" + scale, pos.X >= work.X - 1 && pos.Y >= work.Y - 1
                        && pos.X + size.Width <= work.X + work.Width + 1 && pos.Y + size.Height <= work.Y + work.Height + 1);
                }
                await BoardWindowRegression.SaveVisual((FrameworkElement)popup.Content, Path.Combine(directory, "selection-options-" + theme + ".png"));
                services.OverlayInk.ClearSelection();
                Invoke((Button)bar.Bar.GetToolButton(Ink.InkTool.Select)); await Task.Delay(50);
                Check("closing options does not select everything again " + theme, bar.GetOpenPanel(BarPanelKind.Selection) is null && !services.OverlayInk.HasSelection);
                Invoke((Button)bar.Bar.GetToolButton(Ink.InkTool.Select)); await Task.Delay(50);
                Invoke((Button)bar.Bar.GetToolButton(Ink.InkTool.Pen)); await Task.Delay(50);
                Check("switching tools closes transient selection options " + theme, bar.GetOpenPanel(BarPanelKind.Selection) is null);

                shell.ToggleBoard(); await Task.Delay(100);
                var board = shell.OpenWindows.OfType<BoardWindow>().Single();
                services.Board.SetBackground(theme == 1 ? Features.Board.BoardBackground.White : Features.Board.BoardBackground.Black);
                board.CurrentController.Tool = Ink.InkTool.Pen;
                board.CurrentController.Document.Clear();
                board.CurrentController.Document.Add(Stroke());
                board.SelectOrSelectAll();
                Check("board first click only selects the tool " + theme, !board.CurrentController.HasSelection
                    && !board.ToolPanels.SelectMany(window => Descendants(window.Content)).OfType<SelectionPalette>().Any());
                board.SelectOrSelectAll(); await Task.Delay(80);
                var boardPalette = board.ToolPanels.SelectMany(window => Descendants(window.Content)).OfType<SelectionPalette>().Single();
                Check("board second click selects all and opens options " + theme, board.CurrentController.Selection.Count == 1);
                CheckActions(boardPalette, board.CurrentController, "board " + theme);
                Invoke(Button(boardPalette, "矩形选择"));
                Check("board rectangle preference saves " + theme, services.Store.Load().Canvas.SelectionMode == InkSelectionMode.Rectangle
                    && board.CurrentController.SelectionMode == InkSelectionMode.Rectangle);
                board.Close(); await Task.Delay(70);
            }
            var pdf = services.Modules.GetExport<IPdfWorkspaceService>();
            string file = Path.Combine(directory, "selection.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics").GetMethod("CreateFixture",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { file, 2 });
            await shell.OpenPdfAsync(file); await Task.Delay(100);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            workspace.Window.Activate();
            NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(workspace.Window));
            await Task.Delay(250);
            bool suppliedPdfForeground = NativeMethods.GetForegroundWindow() != WindowChrome.GetHwnd(workspace.Window);
            if (suppliedPdfForeground) shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
            checks.Add(new { check = "PDF selection uses the document toolbar target",
                passed = ReferenceEquals(bar.Bar.Controller, workspace.Controller), foregroundContextSuppliedByTest = suppliedPdfForeground });
            workspace.Controller.Tool = Ink.InkTool.Pen;
            workspace.Controller.Document.Add(Stroke());
            Invoke((Button)bar.Bar.GetToolButton(Ink.InkTool.Select));
            Invoke((Button)bar.Bar.GetToolButton(Ink.InkTool.Select)); await Task.Delay(80);
            var documentPalette = Descendants(bar.GetOpenPanel(BarPanelKind.Selection).Content).OfType<SelectionPalette>().Single();
            Check("PDF uses the selection options and automatic full selection", workspace.Controller.Selection.Count == 1);
            CheckActions(documentPalette, workspace.Controller, "PDF");
            Invoke(Button(documentPalette, "套索选择"));
            Check("PDF receives the shared lasso preference", workspace.Controller.SelectionMode == InkSelectionMode.Lasso
                && services.Store.Load().Canvas.SelectionMode == InkSelectionMode.Lasso);
            workspace.Controller.Document.Clear();
            workspace.Window.Close();
        }
        catch (Exception error) { checks.Add(new { check = "selection palette exception", passed = false, error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "selection-palette.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }

        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        void CheckActions(SelectionPalette palette, Ink.InkController controller, string name)
        {
            Invoke(Button(palette, "反选")); Check("invert deselects a fully selected canvas " + name, !controller.HasSelection);
            Invoke(Button(palette, "反选")); Check("invert selects an empty selection " + name, controller.Selection.Count == 1);
            controller.ClearSelection(); Invoke(Button(palette, "全选"));
            Check("select all uses the current canvas " + name, controller.Selection.Count == 1);
        }
    }

    private static InkStroke Stroke()
    {
        var stroke = new InkStroke(new() { BaseWidth = 3, TaperKind = TaperKind.None });
        stroke.AddPoint(100, 100); stroke.AddPoint(220, 160); stroke.Complete(); return stroke;
    }
    private static Button Button(DependencyObject root, string name) => Descendants(root).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
