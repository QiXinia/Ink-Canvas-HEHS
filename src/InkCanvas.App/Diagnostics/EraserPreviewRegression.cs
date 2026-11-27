using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class EraserPreviewRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var services = new AppServices(Path.Combine(directory, Guid.NewGuid().ToString("N")));
        services.Settings.Startup.IsAutoUpdate = false;
        using var shell = new ShellController(services);
        shell.Start();
        var checks = new List<object>();
        NativeMethods.GetCursorPos(out var original);
        try
        {
            using (var controller = new InkController { Tool = InkTool.Eraser, EraserRadius = 25 })
            using (var scene = new InkSceneRenderer())
            using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 240, 96))
            {
                controller.OnPointerHover(Sample(100, 100));
                Draw();
                var pixels = target.GetPixelBytes();
                bool ring = Alpha(pixels, 320, 125, 100) > 150 && Alpha(pixels, 320, 75, 100) > 150;
                bool fill = Alpha(pixels, 320, 100, 100) is > 0 and < 80;
                Check("area preview draws outlined translucent circle at eraser radius", ring && fill && Alpha(pixels, 320, 135, 100) == 0);
                await target.SaveAsync(Path.Combine(directory, "area-eraser-preview.png"), CanvasBitmapFileFormat.Png);
                controller.ViewTransform = Matrix3x2.CreateScale(2);
                Draw();
                pixels = target.GetPixelBytes();
                Check("preview scales with the actual world eraser", Alpha(pixels, 320, 150, 100) > 150 && Alpha(pixels, 320, 155, 100) == 0);
                using (var drawing = target.CreateDrawingSession()) { drawing.Clear(Microsoft.UI.Colors.Transparent); scene.DrawCommitted(drawing, controller); }
                Check("preview stays out of committed ink", target.GetPixelBytes().All(value => value == 0));
                controller.EraserMode = InkEraserMode.WholeStroke;
                Draw();
                Check("stroke eraser hides area circle", target.GetPixelBytes().All(value => value == 0));

                controller.ViewTransform = Matrix3x2.Identity;
                controller.EraserMode = InkEraserMode.Area;
                controller.Document.AddRange(new ShapeDefinition
                {
                    Kind = ShapeKind.Rectangle, Parameters = [20, 20, 180, 140],
                    Vertices = [20, 20, 180, 20, 180, 140, 20, 140], FillColor = 0xFF369AFF,
                }.Generate(new StrokeAttributes { BaseWidth = 4 }, Guid.NewGuid()));
                controller.OnPointerDown(Sample(100, 80)); controller.OnPointerUp(Sample(100, 80));
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    scene.DrawCommitted(drawing, controller);
                }
                pixels = target.GetPixelBytes();
                Check("Win2D retains the outline and a transparent cut in the fill",
                    Alpha(pixels, 320, 100, 80) == 0 && Alpha(pixels, 320, 60, 80) == 255 && Alpha(pixels, 320, 100, 20) == 255);
                await target.SaveAsync(Path.Combine(directory, "area-erased-shape.png"), CanvasBitmapFileFormat.Png);

                void Draw()
                {
                    using var drawing = target.CreateDrawingSession();
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    scene.DrawLive(drawing, controller);
                }
            }

            var overlay = shell.Overlay;
            var ink = services.OverlayInk;
            ink.Tool = InkTool.Eraser;
            ink.EraserMode = InkEraserMode.Area;
            ink.EraserRadius = 30;
            ink.OnPointerHover(Sample(300, 200));
            Check("empty desktop presents preview", RingInOverlay(overlay, 300, 200, 30));
            ink.OnPointerHover(Sample(500, 200));
            Check("moving desktop preview restores previous pixels", RingInOverlay(overlay, 500, 200, 30) && OverlayAlpha(overlay, 330, 200) < 5);
            ink.EraserRadius = 45;
            Check("changing eraser size redraws desktop circle", RingInOverlay(overlay, 500, 200, 45) && OverlayAlpha(overlay, 530, 200) < 80);
            ink.HideEraserPreview();
            Check("leaving empty desktop clears circle and releases caches", OverlayAlpha(overlay, 545, 200) < 5
                && overlay.Presenter.ManagedBufferBytes == 0 && overlay.Presenter.RenderTargetBytes == 0);

            var point = new NativeMethods.POINT { X = 700, Y = 350 };
            bool native = SetCursorPos(point.X, point.Y) && GetAncestor(WindowFromPoint(point), 2) == overlay.Window.Handle;
            await Task.Delay(100);
            checks.Add(new { check = "native desktop mouse hover shows area preview", available = native,
                passed = native ? (bool?)ink.IsEraserPreviewVisible : null });
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            var anchor = bar.GetToolAnchor(InkTool.Pen);
            SetCursorPos(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2);
            await Task.Delay(100);
            checks.Add(new { check = "moving onto toolbar clears desktop preview", available = native,
                passed = native ? (bool?)!ink.IsEraserPreviewVisible : null });

            shell.ToggleBoard();
            await Task.Delay(200);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            var surface = Descendants(board.Content).OfType<InkSurface>().Single();
            board.CurrentController.Tool = InkTool.Eraser;
            board.CurrentController.EraserRadius = 40;
            board.CurrentController.EraserMode = InkEraserMode.Area;
            foreach (var background in new[] { BoardBackground.Black, BoardBackground.White })
            {
                services.Board.SetBackground(background);
                board.CurrentController.OnPointerHover(Sample(400, 300));
                await Task.Delay(100);
                await BoardWindowRegression.SaveVisual((FrameworkElement)board.Content, Path.Combine(directory, "board-eraser-" + background + ".png"));
                Check("board area preview state " + background, surface.Controller.IsEraserPreviewVisible && surface.Controller.EraserPreviewRadius == 40);
            }
            SetCursorPos(750, 400);
            await Task.Delay(100);
            bool boardNative = GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = 750, Y = 400 }), 2) == WindowChrome.GetHwnd(board);
            checks.Add(new { check = "native board hover updates preview", available = boardNative,
                passed = boardNative ? (bool?)(board.CurrentController.IsEraserPreviewVisible && board.CurrentController.EraserPreviewCenter.X != 400) : null });
            var toolbar = board.GetToolAnchor(InkTool.Pen);
            SetCursorPos(toolbar.X + toolbar.Width / 2, toolbar.Y + toolbar.Height / 2);
            await Task.Delay(100);
            checks.Add(new { check = "moving onto board toolbar clears preview", available = boardNative,
                passed = boardNative ? (bool?)!board.CurrentController.IsEraserPreviewVisible : null });
        }
        catch (Exception exception) { checks.Add(new { check = "eraser preview regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            SetCursorPos(original.X, original.Y);
            shell.OpenWindows.OfType<BoardWindow>().FirstOrDefault()?.Close();
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "eraser-preview.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }

        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static OverlayPointer Sample(float x, float y) => new(9100, x, y, .5f, 0, 0, OverlayPointerKind.Mouse, 0);
    private static byte Alpha(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];
    private static byte OverlayAlpha(OverlayHost overlay, float x, float y)
    {
        int px = (int)MathF.Round(x * overlay.Window.DpiScale), py = (int)MathF.Round(y * overlay.Window.DpiScale);
        return Marshal.ReadByte(overlay.Window.PixelBuffer, (py * overlay.Window.Width + px) * 4 + 3);
    }
    private static bool RingInOverlay(OverlayHost overlay, float x, float y, float radius)
        => Enumerable.Range(-2, 5).Any(offset => OverlayAlpha(overlay, x + radius + offset / overlay.Window.DpiScale, y) > 100);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
