using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace InkCanvas.App.Diagnostics;

internal static class SelectionRegression
{
    public static async Task SavePreview(string directory)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1020, 330, 96);
        using (var draw = target.CreateDrawingSession())
        {
            draw.Clear(Microsoft.UI.Colors.White);
            using var font = new CanvasTextFormat { FontSize = 16 };
            for (int column = 0; column < 3; column++)
            {
                using var controller = new InkController { Tool = InkTool.Select, ViewTransform = Matrix3x2.CreateTranslation(column * 340, 0) };
                controller.Document.Add(Rectangle());
                Vector2 View(Vector2 p) => Vector2.Transform(p, controller.ViewTransform);
                if (column == 0)
                {
                    controller.OnPointerDown(new OverlayPointer(9700, 70, 70, 0.5f, 0, 0, OverlayPointerKind.Mouse, 0));
                    controller.OnPointerMove(new OverlayPointer(9700, 230, 190, 0.5f, 0, 0, OverlayPointerKind.Mouse, 10));
                }
                else
                {
                    Drag(controller, View(new Vector2(70, 70)), View(new Vector2(230, 190)), OverlayPointerKind.Mouse);
                    if (column == 1)
                    {
                        var frame = controller.SelectionFrame;
                        var from = frame.HandlePosition(SelectionHandle.Rotate, controller.ViewTransform);
                        var to = Vector2.Transform(from, Matrix3x2.CreateRotation(30 * MathF.PI / 180, frame.Center(controller.ViewTransform)));
                        controller.OnPointerDown(new OverlayPointer(9700, from.X, from.Y, 0.5f, 0, 0, OverlayPointerKind.Mouse, 0));
                        controller.OnPointerMove(new OverlayPointer(9700, to.X, to.Y, 0.5f, 0, 0, OverlayPointerKind.Mouse, 10));
                    }
                    else
                    {
                        controller.CloneMode = true;
                        Drag(controller, View(new Vector2(150, 130)), View(new Vector2(230, 220)), OverlayPointerKind.Mouse);
                    }
                }
                using var renderer = new InkSceneRenderer();
                renderer.Draw(draw, controller);
                draw.DrawText(new[] { "拖动框选", "旋转与角度提示", "克隆：保留原对象" }[column], column * 340 + 20, 15,
                    Microsoft.UI.Colors.Black, font);
            }
        }
        await target.SaveAsync(Path.Combine(directory, "selection-ui.png"), CanvasBitmapFileFormat.Png);
    }

    public static bool Run(List<object> checks)
    {
        bool passed = true;
        foreach (var kind in new[] { OverlayPointerKind.Mouse, OverlayPointerKind.Pen, OverlayPointerKind.Touch })
        foreach (float zoom in new[] { 0.5f, 1f, 2f })
        {
            using var controller = new InkController { Tool = InkTool.Select, ViewTransform = Matrix3x2.CreateScale(zoom) };
            var source = Rectangle();
            controller.Document.Add(source);
            var start = new Vector2(70, 70) * zoom;
            var end = new Vector2(230, 190) * zoom;
            Drag(controller, start, end, kind);
            bool marquee = controller.Selection.Count == 1 && ReferenceEquals(controller.Selection[0], source)
                && !controller.IsMarqueeSelecting && controller.History.UndoCount == 0;
            // Click on the selection must not create a transform/clone history entry.
            controller.CloneMode = true;
            Drag(controller, new Vector2(150, 130) * zoom, new Vector2(150, 130) * zoom, kind);
            bool noClickClone = controller.Document.Count == 1 && controller.History.UndoCount == 0;
            for (int i = 0; i < 2; i++)
                Drag(controller, new Vector2(150, 130) * zoom, new Vector2(310, 250 + i * 100) * zoom, kind);
            bool clones = controller.Document.Count == 3 && controller.Document.Strokes.Select(s => s.Id).Distinct().Count() == 3
                && source.Points[0].Position == new Vector2(100, 100) && ReferenceEquals(controller.Selection[0], source);
            controller.Undo();
            bool undo = controller.Document.Count == 2;
            controller.Redo();
            bool redo = controller.Document.Count == 3;
            controller.CloneMode = false;
            var before = source.Points.Select(p => p.Position).ToArray();
            var frame = controller.SelectionFrame;
            var rotationStart = frame.HandlePosition(SelectionHandle.Rotate, controller.ViewTransform);
            var rotationEnd = Vector2.Transform(rotationStart,
                Matrix3x2.CreateRotation(23 * MathF.PI / 180, frame.Center(controller.ViewTransform)));
            Drag(controller, rotationStart, rotationEnd, kind, shift: true);
            bool rotated = MathF.Abs(controller.SelectionRotationDegrees - 30) < 0.01f
                && controller.SelectionFrame.HitTest(controller.SelectionFrame.HandlePosition(SelectionHandle.Rotate, controller.ViewTransform),
                    controller.ViewTransform) == SelectionHandle.Rotate;
            controller.Undo();
            bool rotationUndo = before.SequenceEqual(source.Points.Select(p => p.Position));
            controller.Redo();
            bool rotationRedo = MathF.Abs(controller.SelectionRotationDegrees - 30) < 0.01f;
            controller.ClearSelection();
            var hit = source.Points[0].Position * zoom;
            Drag(controller, hit, hit, kind);
            bool reselectKeepsAngle = MathF.Abs(controller.SelectionRotationDegrees - 30) < 0.01f;
            passed &= marquee && noClickClone && clones && undo && redo && rotated && rotationUndo && rotationRedo && reselectKeepsAngle;
            checks.Add(new { check = "marquee, repeated clone and snapped rotation", kind = kind.ToString(), zoom,
                marquee, noClickClone, clones, undo, redo, rotated, rotationUndo, rotationRedo, reselectKeepsAngle });
        }
        var board = new BoardService();
        {
            var controller = board.Current;
            controller.Tool = InkTool.Select;
            var source = Rectangle(); controller.Document.Add(source);
            var image = new BoardImageAsset { Data = new byte[] { 1 }, Left = 300, Top = 100, Width = 60, Height = 60 };
            var media = new BoardMediaAsset { Data = new byte[] { 2 }, Left = 420, Top = 100, Width = 60, Height = 60 };
            board.AddImage(image); board.AddMedia(media); controller.History.Clear();
            var elements = new ISelectionElement[] { new BoardSelectionElement(board, image), new BoardSelectionElement(board, media) };
            controller.GetSelectableElements = () => elements;
            Drag(controller, new Vector2(70, 70), new Vector2(500, 190), OverlayPointerKind.Mouse);
            bool selectedTogether = controller.Selection.Count == 1 && controller.SelectedElements.Count == 2;
            controller.CloneMode = true;
            Drag(controller, new Vector2(280, 130), new Vector2(380, 280), OverlayPointerKind.Mouse);
            bool mixedClone = controller.Document.Count == 2 && board.CurrentImages.Count == 2 && board.CurrentMedia.Count == 2
                && board.CurrentImages[0].Id != board.CurrentImages[1].Id && board.CurrentMedia[0].Id != board.CurrentMedia[1].Id
                && image.Transform is null && media.Transform is null && source.Points[0].Position == new Vector2(100, 100);
            controller.Undo();
            bool mixedUndo = controller.Document.Count == 1 && board.CurrentImages.Count == 1 && board.CurrentMedia.Count == 1;
            controller.Redo();
            bool mixedRedo = controller.Document.Count == 2 && board.CurrentImages.Count == 2 && board.CurrentMedia.Count == 2;
            passed &= selectedTogether && mixedClone && mixedUndo && mixedRedo;
            checks.Add(new { check = "mixed ink/image/video clone is one undo step", selectedTogether, mixedClone, mixedUndo, mixedRedo });
            controller.CloneMode = false;
            int undoBefore = controller.History.UndoCount;
            controller.BeginSelectionGesture();
            controller.ApplySelectionGesture(Matrix3x2.CreateTranslation(20, 30));
            controller.ApplySelectionGesture(Matrix3x2.CreateTranslation(30, 20));
            controller.EndSelectionGesture();
            bool gestureMoved = source.Points[0].Position == new Vector2(150, 150)
                && elements.All(e => e.Frame.Transform.M31 == 50 && e.Frame.Transform.M32 == 50)
                && controller.History.UndoCount == undoBefore + 1;
            controller.Undo();
            bool gestureUndo = source.Points[0].Position == new Vector2(100, 100)
                && image.Transform is null && media.Transform is null;
            controller.Redo();
            bool gestureRedo = source.Points[0].Position == new Vector2(150, 150)
                && elements.All(e => e.Frame.Transform.M31 == 50 && e.Frame.Transform.M32 == 50);
            passed &= gestureMoved && gestureUndo && gestureRedo;
            checks.Add(new { check = "mixed object gesture records one undo step", gestureMoved, gestureUndo, gestureRedo });
            controller.Dispose();
        }
        using (var controller = new InkController { Tool = InkTool.Select, MultiTouchEnabled = false })
        {
            var source = Rectangle(); controller.Document.Add(source);
            controller.ConfigureGestures(zoom: false, translate: true, rotate: false);
            Drag(controller, new Vector2(70, 70), new Vector2(230, 190), OverlayPointerKind.Mouse);
            var a = new OverlayPointer(9800, 150, 130, 0.5f, 1, 1, OverlayPointerKind.Touch, 0);
            var b = a with { Id = 9801, X = 180, Y = 140 };
            controller.OnPointerDown(a); controller.OnPointerDown(b);
            controller.OnPointerMove(a with { X = 190, Y = 160 });
            controller.OnPointerMove(b with { X = 220, Y = 170 });
            controller.OnPointerUp(a with { X = 190, Y = 160 }); controller.OnPointerUp(b with { X = 220, Y = 170 });
            bool touchMove = source.Points[0].Position == new Vector2(140, 130) && controller.History.UndoCount == 1;
            controller.Undo();
            bool touchUndo = source.Points[0].Position == new Vector2(100, 100);
            controller.OnPointerDown(a); controller.OnPointerDown(b);
            controller.OnPointerMove(a with { X = 190, Y = 160 });
            controller.Tool = InkTool.Pen;
            controller.OnPointerUp(a); controller.OnPointerUp(b);
            Drag(controller, new Vector2(400, 300), new Vector2(500, 350), OverlayPointerKind.Pen);
            bool switchFinishes = !controller.HasSelection && controller.Document.Count == 2 && controller.LiveStrokes.Count == 0;
            passed &= touchMove && touchUndo && switchFinishes;
            checks.Add(new { check = "two-finger selection and tool switch finish cleanly", touchMove, touchUndo, switchFinishes });
        }
        using (var controller = new InkController { Tool = InkTool.Select })
        {
            var a = Rectangle(); var b = Rectangle(); b.Transform(Matrix3x2.CreateTranslation(400, 0));
            controller.Document.AddRange(new[] { a, b });
            Drag(controller, new Vector2(70, 70), new Vector2(230, 190), OverlayPointerKind.Mouse);
            controller.TransformSelectionUndoable(Matrix3x2.CreateRotation(0.7f, a.Bounds.Center), "rotate");
            controller.ClearSelection();
            Drag(controller, new Vector2(500, 100), new Vector2(500, 100), OverlayPointerKind.Mouse);
            controller.Undo();
            bool otherFrameUnchanged = controller.Selection.Count == 1 && ReferenceEquals(controller.Selection[0], b)
                && MathF.Abs(controller.SelectionRotationDegrees) < 0.01f && controller.SelectionFrame.Bounds == b.ObjectFrame.Bounds;
            controller.Clear();
            bool clearDropsFrame = !controller.HasSelection;
            passed &= otherFrameUnchanged && clearDropsFrame;
            checks.Add(new { check = "undo of another object preserves current selection frame", otherFrameUnchanged, clearDropsFrame });
        }
        return passed;
    }

    private static InkStroke Rectangle()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
        foreach (var point in new[] { new Vector2(100, 100), new Vector2(200, 100), new Vector2(200, 160), new Vector2(100, 160), new Vector2(100, 100) })
            stroke.AddPoint(point.X, point.Y);
        stroke.Complete(); return stroke;
    }
    private static void Drag(InkController controller, Vector2 from, Vector2 to, OverlayPointerKind kind, bool shift = false)
    {
        var sample = new OverlayPointer(9700, from.X, from.Y, 0.5f, 1, 1, kind, 0, shift);
        controller.OnPointerDown(sample);
        controller.OnPointerMove(sample with { X = to.X, Y = to.Y, TimeMs = 10 });
        controller.OnPointerUp(sample with { X = to.X, Y = to.Y, TimeMs = 20 });
    }
}
