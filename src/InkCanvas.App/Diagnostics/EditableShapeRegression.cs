using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class EditableShapeRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        try
        {
            shell.ToggleBoard(); await Task.Delay(200);
            var window = shell.OpenWindows.OfType<BoardWindow>().Single();
            var root = (FrameworkElement)window.Content;
            var c = window.CurrentController;
            services.Board.SetBackground(BoardBackground.White);
            c.ShapeKind = ShapeKind.Triangle; c.Tool = InkTool.Shape;
            c.OnPointerDown(Pointer(new(300, 180))); c.OnPointerUp(Pointer(new(650, 430)));
            c.Tool = InkTool.Select; c.SelectAll(); await Task.Delay(100);
            var toolbar = Descendants(root).OfType<SelectionToolbar>().Single();
            Invoke(toolbar, "图形"); Invoke(toolbar, "填充"); Invoke(toolbar, "蓝色");
            Check("toolbar applies native shape fill", c.Document.Strokes.Any(s => s.ShapeSource.Geometry.IsFill));
            Invoke(toolbar, "图形"); Invoke(toolbar, "线型"); Invoke(toolbar, "虚线");
            Check("toolbar applies dashed outlines", c.Selection.All(s => s.ShapeSource.Geometry.LineStyle == ShapeLineStyle.Dashed));
            Invoke(toolbar, "图形"); Invoke(toolbar, "控制点");
            Check("toolbar exposes polygon control points", c.ShapeEditEnabled && c.ShapeControlPoints.Count == 3);
            var control = c.ShapeControlPoints[0]; var moved = control + new Vector2(60, -25);
            c.OnPointerDown(Pointer(control)); c.OnPointerMove(Pointer(moved)); c.OnPointerUp(Pointer(moved));
            Check("normal input pipeline moves one polygon vertex", Vector2.Distance(c.ShapeControlPoints[0], moved) < .1f);
            await Task.Delay(100);
            await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "editable-triangle-light.png"));
            using (var renderer = new StrokeRenderer())
            using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 900, 600, 96))
            {
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    foreach (var stroke in c.Document.Strokes) renderer.Draw(drawing, stroke, Matrix3x2.Identity);
                }
                var pixels = target.GetPixelBytes(); int center = (350 * 900 + 480) * 4;
                Check("Win2D renders the solid blue polygon interior", pixels[center] > 200 && pixels[center + 2] < 80 && pixels[center + 3] == 255);
                await target.SaveAsync(Path.Combine(directory, "native-shape-render.png"), CanvasBitmapFileFormat.Png);
            }
            services.Board.SetBackground(BoardBackground.Black); await Task.Delay(100);
            toolbar = Descendants(root).OfType<SelectionToolbar>().Single();
            await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "editable-triangle-dark.png"));
            c.ClearSelection();
            c.Document.AddRange(ShapeGenerator.Generate(ShapeKind.Diamond, new(900, 200), new(1150, 460), new StrokeAttributes { TaperKind = TaperKind.None }));
            c.SelectAll(); await Task.Delay(50);
            Invoke(toolbar, "图形"); Invoke(toolbar, "连接线");
            var connector = c.Document.Strokes.Single(s => s.ShapeSource.Geometry.Construction == ShapeConstruction.Connector);
            var source = connector.ShapeSource.Geometry;
            Check("toolbar connects two selected geometric objects", source.StartNode.HasValue && source.EndNode.HasValue);
            toolbar.ClosePanels(); c.ClearSelection();
            c.Selection.AddRange(c.Document.Strokes.Where(s => s.ShapeSource.GroupId == source.EndNode));
            var before = connector.Points[1].Position;
            c.TransformSelectionUndoable(Matrix3x2.CreateTranslation(80, 50), "move node");
            Check("connector tracks a moved node", Vector2.Distance(before, connector.Points[1].Position) > 30);
            c.ClearSelection();
            await Task.Delay(80);
            await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "connected-shapes.png"));
            string path = Path.Combine(directory, "editable-shapes.icink");
            await services.Board.SaveBoardAsync(path); await services.Board.LoadBoardAsync(path);
            c = window.CurrentController;
            toolbar = Descendants(root).OfType<SelectionToolbar>().Single();
            Check("reopened board retains polygon vertices styles and connection IDs", c.Document.Strokes.All(s => s.ShapeSource?.Geometry?.IsValid == true)
                && c.Document.Strokes.Any(s => s.ShapeSource.Geometry.Construction == ShapeConstruction.Connector));
            c.SelectAll(); await Task.Delay(50); Invoke(toolbar, "图形");
            await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "shape-menu.png"));
            var bounds = toolbar.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, toolbar.ActualWidth, toolbar.ActualHeight));
            Check("selection geometry menu stays within the screen", bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= root.ActualHeight + 1);
        }
        catch (Exception ex) { checks.Add(new { check = "editable shape exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "editable-shapes.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }
    private static OverlayPointer Pointer(Vector2 point) => new(993, point.X, point.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 0);
    private static void Invoke(DependencyObject root, string label)
    {
        var button = Descendants(root).OfType<TouchIconButton>().Single(b => b.Label == label);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
