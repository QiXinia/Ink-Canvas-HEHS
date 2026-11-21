using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace InkCanvas.App.Diagnostics;

internal static class LassoEraserRegression
{
    public static bool Run(List<object> checks)
    {
        bool passed = true;
        foreach (var kind in new[] { OverlayPointerKind.Mouse, OverlayPointerKind.Pen, OverlayPointerKind.Touch })
        foreach (float zoom in new[] { 0.5f, 1f, 2f })
        {
            using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Lasso,
                MultiTouchEnabled = false, ViewTransform = Matrix3x2.CreateRotation(0.3f) * Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateTranslation(80, 60) };
            var outside = Stroke(new(250, 120), new(300, 120));
            var inside = Stroke(new(100, 120), new(150, 120));
            var crossing = Stroke(new(50, 140), new(240, 140));
            var dot = Stroke(new Vector2(130, 150));
            var originals = new[] { outside, inside, crossing, dot };
            controller.Document.AddRange(originals);
            var path = Box(90, 90, 180, 180).Select(p => Vector2.Transform(p, controller.ViewTransform)).ToArray();
            var sample = Sample(path[0], kind);
            controller.OnPointerDown(sample);
            controller.OnPointerMove(sample with { X = path[1].X, Y = path[1].Y });
            controller.OnPointerMove(sample with { X = path[2].X, Y = path[2].Y });
            bool deferred = controller.Document.Count == 4 && controller.History.UndoCount == 0 && controller.IsLassoErasing;
            controller.OnPointerDown(sample with { Id = 9921, X = 900, Y = 900 });
            controller.OnPointerMove(sample with { Id = 9921, X = 950, Y = 950 });
            controller.OnPointerUp(sample with { Id = 9921, X = 950, Y = 950 });
            bool owned = controller.IsLassoErasing && controller.LassoPath.Count == 3;
            // The last polygon vertex arrives only in up; the controller closes the path itself.
            var up = sample with { X = path[3].X, Y = path[3].Y, TimeMs = 40 };
            controller.OnPointerUp(up); controller.OnPointerUp(up);
            bool erased = controller.Document.Count == 1 && ReferenceEquals(controller.Document.Strokes[0], outside)
                && !controller.IsLassoErasing && controller.LassoPath.Count == 0 && controller.History.UndoCount == 1;
            controller.Undo();
            bool undo = controller.Document.Strokes.SequenceEqual(originals) && controller.History.UndoCount == 0;
            controller.Redo();
            bool redo = controller.Document.Count == 1 && ReferenceEquals(controller.Document.Strokes[0], outside);
            passed &= deferred && owned && erased && undo && redo;
            checks.Add(new { check = "lasso eraser defers deletion, respects polygon and restores order", kind = kind.ToString(), zoom,
                deferred, owned, erased, undo, redo });
        }
        using (var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Lasso })
        {
            var source = Stroke(new(100, 120), new(150, 120)); controller.Document.Add(source);
            var path = Box(80, 80, 180, 180);
            Trace(controller, path);
            controller.OnPointerCanceled(Sample(path[^1]));
            bool canceled = !controller.IsLassoErasing && controller.Document.Count == 1 && controller.History.UndoCount == 0;
            Trace(controller, path);
            controller.Tool = InkTool.Pen;
            controller.OnPointerUp(Sample(path[^1]));
            bool switchCancels = controller.Document.Count == 1 && controller.History.UndoCount == 0 && !controller.IsLassoErasing;
            controller.Tool = InkTool.Eraser;
            Trace(controller, path);
            controller.EraserMode = InkEraserMode.Area;
            controller.OnPointerUp(Sample(path[^1]));
            bool modeCancels = controller.Document.Count == 1 && controller.History.UndoCount == 0 && !controller.IsLassoErasing;
            controller.EraserMode = InkEraserMode.Lasso;
            controller.EraserMode = InkEraserMode.Area;
            controller.OnPointerDown(Sample(new Vector2(400, 400)));
            controller.EraserMode = InkEraserMode.Lasso;
            controller.OnPointerUp(Sample(new Vector2(400, 400)));
            Trace(controller, path);
            bool areaToLassoReady = controller.IsLassoErasing;
            controller.OnPointerCanceled(Sample(path[^1]));
            foreach (var noOp in new[] { new[] { new Vector2(110, 120) }, new[] { new Vector2(80, 80), new Vector2(180, 180) }, Box(500, 500, 600, 600) })
            {
                Trace(controller, noOp); controller.OnPointerUp(Sample(noOp[^1]));
            }
            bool noOpIgnored = controller.Document.Count == 1 && controller.History.UndoCount == 0;
            passed &= canceled && switchCancels && modeCancels && noOpIgnored && areaToLassoReady;
            checks.Add(new { check = "lasso cancel, mode changes and empty gestures do not erase", canceled, switchCancels, modeCancels, noOpIgnored, areaToLassoReady });
        }
        var board = new BoardService();
        using (var controller = board.Current)
        {
            var images = new[] { Image(100, 100), Image(500, 100), Image(100, 200) };
            foreach (var image in images) board.AddImage(image);
            var media = new BoardMediaAsset { Data = new byte[] { 1 }, Left = 200, Top = 130, Width = 50, Height = 50 };
            board.AddMedia(media);
            var source = Stroke(new(120, 120), new(160, 160)); controller.Document.Add(source);
            var elements = images.Select(image => new BoardSelectionElement(board, image)).Append(new BoardSelectionElement(board, media)).ToArray();
            controller.GetSelectableElements = () => elements;
            controller.History.Clear(); controller.Tool = InkTool.Eraser; controller.EraserMode = InkEraserMode.Lasso;
            var path = Box(70, 70, 300, 300);
            Trace(controller, path); controller.OnPointerUp(Sample(path[^1]));
            bool mixed = controller.Document.Count == 0 && board.CurrentImages.SequenceEqual(new[] { images[1] })
                && board.CurrentMedia.Count == 0 && controller.History.UndoCount == 1;
            controller.Undo();
            bool mixedUndo = controller.Document.Count == 1 && ReferenceEquals(controller.Document.Strokes[0], source)
                && board.CurrentImages.SequenceEqual(images) && board.CurrentMedia.Count == 1 && ReferenceEquals(board.CurrentMedia[0], media);
            controller.Redo();
            bool mixedRedo = controller.Document.Count == 0 && board.CurrentImages.SequenceEqual(new[] { images[1] }) && board.CurrentMedia.Count == 0;
            passed &= mixed && mixedUndo && mixedRedo;
            checks.Add(new { check = "lasso erases ink/image/video together with one ordered undo", mixed, mixedUndo, mixedRedo });
        }
        return passed;
    }

    public static async Task SavePreview(string directory)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 800, 300, 96);
        using (var draw = target.CreateDrawingSession())
        {
            draw.Clear(Microsoft.UI.Colors.White);
            using var font = new CanvasTextFormat { FontSize = 16 };
            for (int column = 0; column < 2; column++)
            {
                using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Lasso,
                    ViewTransform = Matrix3x2.CreateTranslation(column * 400, 0) };
                var inside = Stroke(new(100, 100), new(190, 150)); inside.Attributes.Color = InkColor.Opaque(220, 38, 38);
                var outside = Stroke(new(290, 100), new(350, 190));
                controller.Document.AddRange(new[] { inside, outside });
                var path = new[] { new Vector2(70, 60), new Vector2(230, 65), new Vector2(255, 170), new Vector2(80, 200), new Vector2(55, 125) }
                    .Select(p => Vector2.Transform(p, controller.ViewTransform)).ToArray();
                Trace(controller, path);
                if (column == 1) controller.OnPointerUp(Sample(path[^1]));
                using var scene = new InkSceneRenderer(); scene.Draw(draw, controller);
                draw.DrawText(column == 0 ? "圈选待擦除对象" : "抬手擦除，圈外保留", column * 400 + 20, 20, Microsoft.UI.Colors.Black, font);
            }
        }
        await target.SaveAsync(Path.Combine(directory, "lasso-eraser-ui.png"), CanvasBitmapFileFormat.Png);
    }

    private static BoardImageAsset Image(float left, float top) => new() { Data = new byte[] { 1 }, Left = left, Top = top, Width = 50, Height = 50 };
    private static Vector2[] Box(float left, float top, float right, float bottom)
        => new[] { new Vector2(left, top), new Vector2(right, top), new Vector2(right, bottom), new Vector2(left, bottom) };
    private static OverlayPointer Sample(Vector2 p, OverlayPointerKind kind = OverlayPointerKind.Mouse)
        => new(9920, p.X, p.Y, 0.5f, 1, 1, kind, 0);
    private static void Trace(InkController controller, Vector2[] path)
    {
        controller.OnPointerDown(Sample(path[0]));
        foreach (var point in path.Skip(1)) controller.OnPointerMove(Sample(point));
    }
    private static InkStroke Stroke(params Vector2[] points)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
        foreach (var point in points) stroke.AddPoint(point.X, point.Y);
        stroke.Complete(); return stroke;
    }
}
