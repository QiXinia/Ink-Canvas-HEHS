using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;

namespace InkCanvas.App.Diagnostics;

internal static class EraserRegression
{
    public static bool Run(List<object> checks)
    {
        bool allPassed = true;
        foreach (var kind in new[] { OverlayPointerKind.Mouse, OverlayPointerKind.Pen, OverlayPointerKind.Touch })
        foreach (var mode in new[] { InkEraserMode.Area, InkEraserMode.WholeStroke })
        {
            using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = mode,
                EraserRadius = 10, RecognizeShapes = false, ViewTransform = Matrix3x2.CreateScale(2) };
            controller.Palm.Advanced = new AdvancedSettings();
            var original = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
            original.AddPoint(0, 50); original.AddPoint(200, 50); original.Complete();
            var overlaid = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
            overlaid.AddPoint(0, 90); overlaid.AddPoint(200, 90); overlaid.Complete();
            controller.Document.AddRange(new[] { original, overlaid });
            var sample = new OverlayPointer(9800, 200, 100, 0.5f, 50, 50, kind, 0);
            controller.OnPointerDown(sample);
            controller.OnPointerUp(sample with { TimeMs = 10 });
            bool erased = mode == InkEraserMode.Area
                ? controller.Document.Count == 3 && controller.Document.Strokes[0].Points[^1].X < 100
                    && controller.Document.Strokes[1].Points[0].X > 100
                    && ReferenceEquals(controller.Document.Strokes[2], overlaid)
                : controller.Document.Count == 1 && ReferenceEquals(controller.Document.Strokes[0], overlaid);
            controller.Undo();
            bool undo = controller.Document.Count == 2 && controller.Document.Strokes[0].Points[0].X == 0
                && controller.Document.Strokes[0].Points[^1].X == 200;
            controller.Redo();
            bool redo = controller.Document.Count == (mode == InkEraserMode.Area ? 3 : 1);
            allPassed &= erased && undo && redo;
            checks.Add(new { check = "eraser preserves outside ink and supports undo/redo",
                kind = kind.ToString(), mode = mode.ToString(), erased, undo, redo });
        }

        using (var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Area,
            EraserRadius = 8, RecognizeShapes = false })
        {
            var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
            stroke.AddPoint(0, 50); stroke.AddPoint(200, 50); stroke.Complete();
            controller.Document.Add(stroke);
            var sample = new OverlayPointer(9801, 100, 20, 0.5f, 1, 1, OverlayPointerKind.Touch, 0);
            controller.OnPointerDown(sample);
            // A second contact may not move or end the first contact's eraser gesture.
            controller.OnPointerDown(sample with { Id = 9802, X = 180 });
            controller.OnPointerMove(sample with { Id = 9802, Y = 50 });
            controller.OnPointerUp(sample with { Id = 9802, Y = 50 });
            bool otherContactIgnored = controller.Document.Count == 1;
            controller.OnPointerUp(sample with { Y = 80, TimeMs = 20 });
            bool swept = controller.Document.Count == 2;
            controller.Undo();
            bool undoWholeGesture = controller.Document.Count == 1 && controller.History.UndoCount == 0;
            allPassed &= otherContactIgnored && swept && undoWholeGesture;
            checks.Add(new { check = "fast area erase handles final up and ignores other contacts",
                otherContactIgnored, swept, undoWholeGesture });
        }
        return allPassed;
    }
}
