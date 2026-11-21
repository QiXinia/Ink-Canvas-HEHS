using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class OptionPanelsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var windows = new List<Window>();
        var closed = new HashSet<Window>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        try
        {
            await Task.Delay(180);
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            var pen = await Pen(false);
            await Task.Delay(60);
            var shape = Shapes();
            await Task.Delay(60);
            Check("shape pane replaces unpinned pen", closed.Contains(pen) && !closed.Contains(shape));
            var more = Panel(BarPanelKind.More);
            await Task.Delay(60);
            Check("more pane replaces unpinned shape", closed.Contains(shape) && !closed.Contains(more));
            var highlighter = await Pen(true);
            await Task.Delay(60);
            Check("highlighter replaces unpinned more pane", closed.Contains(more) && !closed.Contains(highlighter));
            var gesture = Panel(BarPanelKind.Gesture);
            await Task.Delay(60);
            Check("gesture pane replaces unpinned highlighter", closed.Contains(highlighter) && !closed.Contains(gesture));
            var eraser = Panel(BarPanelKind.Eraser);
            await Task.Delay(60);
            Check("eraser replaces unpinned gesture pane", closed.Contains(gesture) && !closed.Contains(eraser));
            pen = await Pen(false);
            await Task.Delay(60);
            Check("pen replaces unpinned eraser", closed.Contains(eraser) && !closed.Contains(pen));
            Header(pen).IsPinned = true;
            highlighter = await Pen(true);
            await Task.Delay(60);
            Check("pinned attached pen survives opening highlighter", !closed.Contains(pen) && !pen.IsDetached && !closed.Contains(highlighter));
            Header(highlighter).IsPinned = true;
            shape = Shapes();
            await Task.Delay(60);
            Check("pinned pen and highlighter coexist with an unpinned shape pane",
                !closed.Contains(pen) && !closed.Contains(highlighter) && !closed.Contains(shape));
            more = Panel(BarPanelKind.More);
            await Task.Delay(60);
            Check("opening another pane preserves pinned panes and replaces the transient pane",
                !closed.Contains(pen) && !closed.Contains(highlighter) && closed.Contains(shape) && !closed.Contains(more));
            Header(pen).IsPinned = false;
            Check("unpinning closes the other transient pane", closed.Contains(more) && !closed.Contains(pen) && !closed.Contains(highlighter));
            shape = Shapes();
            await Task.Delay(60);
            Check("new pane closes a previously pinned but now unpinned pen", closed.Contains(pen) && !closed.Contains(shape) && !closed.Contains(highlighter));
            Header(highlighter).IsPinned = false;
            Check("unpinning highlighter also enforces exclusivity", closed.Contains(shape) && !closed.Contains(highlighter));
            highlighter.Close();

            await CheckShapeLayout(false);
            shell.ToggleBoard();
            await Task.Delay(160);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            board.ShowPenPalette();
            var boardPen = Track(board.ToolPanels.Single());
            shape = Shapes();
            await Task.Delay(60);
            Check("board and shape option panes are mutually exclusive", closed.Contains(boardPen) && !closed.Contains(shape));
            board.ShowPenPalette();
            boardPen = Track(board.ToolPanels.Single());
            await Task.Delay(60);
            Check("board pane replaces the unpinned shape pane", closed.Contains(shape) && !closed.Contains(boardPen));
            Header(boardPen).IsPinned = true;
            shape = Shapes();
            await Task.Delay(60);
            Check("pinned board pane survives opening shapes", !closed.Contains(boardPen) && !closed.Contains(shape));
            Header(boardPen).IsPinned = false;
            Check("unpinning board pane closes other transient panes", closed.Contains(shape) && !closed.Contains(boardPen));
            boardPen.Close();
            await CheckShapeLayout(true);
            board.Close();

            Window Track(Window window)
            {
                windows.Add(window);
                window.Closed += (_, _) => closed.Add(window);
                return window;
            }
            async Task<PenPaletteWindow> Pen(bool highlight)
            {
                shell.OpenPenPalette(highlight);
                var window = shell.OpenWindows.OfType<PenPaletteWindow>().Single(item => item.IsHighlighter == highlight);
                Track(window);
                await Task.Delay(70);
                Header(window).IsPinned = false;
                return window;
            }
            ShapePickerWindow Shapes()
            {
                shell.OpenShapePicker();
                var window = shell.OpenWindows.OfType<ShapePickerWindow>().Single();
                Track(window); window.AutoHide = true;
                return window;
            }
            BarPanelWindow Panel(BarPanelKind kind)
            {
                var panel = new BarPanelWindow(bar, bar.Bar, kind);
                Track(panel);
                var position = bar.AppWindow.Position;
                panel.ShowAboveBar(position.X, position.Y, bar.AppWindow.Size.Width);
                return panel;
            }
            async Task CheckShapeLayout(bool boardMode)
            {
                var window = Shapes(); window.AutoHide = false;
                await Task.Delay(100);
                var preview = Descendants(window.Content).OfType<ShapePreview>().Single();
                var choices = Descendants(Descendants(preview).OfType<SegmentedSelector>().Single()).OfType<ToggleButton>().ToArray();
                Check("all-shapes category is available " + boardMode,
                    choices.Length == ShapeMenuCatalog.Categories.Length + 1 && choices[^1].Content?.ToString() == "全部");
                foreach (double scale in new[] { 50d, 100d, 150d })
                {
                    window.ApplyScale(scale);
                    preview.SelectCategory(0);
                    await Task.Delay(100);
                    var standard = window.AppWindow.ClientSize;
                    for (int category = 0; category < ShapeMenuCatalog.Categories.Length; category++)
                    {
                        preview.SelectCategory(category); await Task.Delay(70);
                        var size = window.AppWindow.ClientSize;
                        Check($"ordinary categories keep identical size {boardMode} {scale} {category}",
                            Math.Abs(size.Width - standard.Width) <= 1 && Math.Abs(size.Height - standard.Height) <= 1);
                        Check($"category contains its own shapes {boardMode} {scale} {category}",
                            preview.VisibleCells.Count == ShapeMenuCatalog.Categories[category].Rows.Sum(row => row.Length));
                    }
                    preview.SelectCategory(ShapePreview.AllCategoryIndex);
                    await Task.Delay(120);
                    var all = window.AppWindow.ClientSize;
                    Check($"all-shapes pane grows taller {boardMode} {scale}", all.Height > standard.Height + 12);
                    Check($"all shapes appear exactly once {boardMode} {scale}", preview.VisibleCells.Count == preview.ShapeCount
                        && preview.VisibleCells.Select(cell => ((ShapeMenuItem)cell.Tag).Kind).Distinct().Count() == Enum.GetValues<ShapeKind>().Length);
                    var root = (FrameworkElement)window.Content;
                    Check($"all-shapes cells fit the window {boardMode} {scale}", preview.VisibleCells.All(cell =>
                    {
                        var bounds = cell.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
                        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
                    }));
                    foreach (int item in new[] { 0, preview.ShapeCount / 2, preview.ShapeCount - 1 })
                    {
                        preview.SelectItem(item, sticky: true); await Task.Delay(35);
                        Check($"selecting a shape retains all-shapes view {boardMode} {scale} {item}",
                            choices[^1].IsChecked == true && preview.VisibleCells.Count == preview.ShapeCount
                            && window.AppWindow.ClientSize.Height == all.Height);
                    }
                    if (scale == 100) await BoardWindowRegression.SaveVisual(preview, Path.Combine(directory, $"all-shapes-{boardMode}.png"));
                    preview.SelectCategory(0); await Task.Delay(70);
                    Check($"leaving all-shapes restores normal height {boardMode} {scale}",
                        Math.Abs(window.AppWindow.ClientSize.Height - standard.Height) <= 1);
                }
                window.Close();
            }
        }
        catch (Exception error) { checks.Add(new { check = "option panel regression exception", passed = false, error = error.ToString() }); }
        finally
        {
            foreach (var window in windows.Where(window => !closed.Contains(window)).ToArray()) window.Close();
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "option-panels.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static PopupHeader Header(Window window) => Descendants(window.Content).OfType<PopupHeader>().Single();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
