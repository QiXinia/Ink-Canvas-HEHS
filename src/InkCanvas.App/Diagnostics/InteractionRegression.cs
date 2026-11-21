using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace InkCanvas.App.Diagnostics;

internal static class InteractionRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        bool selectionPassed = SelectionRegression.Run(checks);
        checks.Add(new { check = "selection input, mixed objects and undo remain compatible", passed = selectionPassed });
        foreach (float dpi in new[] { 96f, 144f }) CheckTiles(checks, dpi);
        foreach (string operation in new[] { "pan", "two-finger", "selection" })
        {
            var before = Measure(operation, false);
            var after = Measure(operation, true);
            checks.Add(new { operation, before, after });
        }
        RenderingRegression.Run(checks);
        CheckLiveNib(checks);
        await SelectionDragRegression.Run(checks, directory);
        await SaveNibPreview(directory);
        File.WriteAllText(Path.Combine(directory, "interaction.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static InkController Scene()
    {
        var controller = new InkController { RecognizeShapes = false };
        for (int i = 0; i < 240; i++)
        {
            var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 3, TaperKind = TaperKind.None,
                IsHighlighter = i % 7 == 0, Nib = i % 2 == 0 ? PenNib.Round : PenNib.Ellipse45 });
            for (int j = 0; j < 100; j++) stroke.AddPoint(-400 + j * 20, -250 + i % 45 * 24 + MathF.Sin(j * .2f) * 10);
            stroke.Complete(); controller.Document.Add(stroke);
        }
        return controller;
    }
    private static OverlayPointer Pointer(float x, float y) => new(9991, x, y, .5f, 0, 0, OverlayPointerKind.Mouse, 0);

    private static object Measure(string operation, bool optimized)
    {
        using var controller = Scene();
        using var scene = new InkSceneRenderer { ShowSelectionOutline = false };
        using var cache = new CommittedInkLayer();
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1280, 720, 96);
        List<InkStroke> snapshots = null;
        if (operation == "two-finger")
        {
            controller.MultiTouchEnabled = false; controller.PalmErases = false;
            controller.OnPointerDown(Pointer(100, 200) with { Id = 1, Kind = OverlayPointerKind.Touch });
            controller.OnPointerDown(Pointer(500, 200) with { Id = 2, Kind = OverlayPointerKind.Touch });
        }
        if (operation == "selection")
        {
            controller.SelectAll();
            if (optimized) controller.OnPointerDown(Pointer(500, 300));
            else snapshots = controller.Document.Strokes.Select(s => s.Clone()).ToList();
        }
        // Warm geometry and the initially visible tiles before measuring interaction frames.
        using (var draw = target.CreateDrawingSession())
        { draw.Clear(Microsoft.UI.Colors.Transparent); cache.Draw(draw, scene, controller, 1280, 720); }
        target.GetPixelBytes(0, 0, 1, 1);
        long builds = scene.Strokes.GeometryBuildCount;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var times = new List<double>();
        for (int frame = 0; frame < 120; frame++)
        {
            var watch = Stopwatch.StartNew();
            float offset = frame * 2;
            if (operation == "pan") controller.ViewTransform = Matrix3x2.CreateTranslation(offset, offset / 2);
            else if (operation == "two-finger")
                controller.OnPointerMove(Pointer((frame % 2 == 0 ? 100 : 500) + offset, 200 + offset / 2)
                    with { Id = frame % 2 == 0 ? 1u : 2u, Kind = OverlayPointerKind.Touch });
            else if (optimized) controller.OnPointerMove(Pointer(500 + offset, 300 + offset / 2));
            else
            {
                for (int i = 0; i < snapshots.Count; i++)
                {
                    var stroke = controller.Document.Strokes[i];
                    stroke.RestorePoints(snapshots[i].Points);
                    stroke.Transform(Matrix3x2.CreateTranslation(offset, offset / 2));
                }
            }
            using (var draw = target.CreateDrawingSession())
            {
                draw.Clear(Microsoft.UI.Colors.Transparent);
                if (optimized) cache.Draw(draw, scene, controller, 1280, 720);
                else scene.DrawCommitted(draw, controller);
            }
            target.GetPixelBytes(0, 0, 1, 1);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return new { medianMs = times[60], p95Ms = times[114], geometryBuilds = scene.Strokes.GeometryBuildCount - builds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, rasterBytes = cache.RasterBytes, rasterRebuilds = cache.RebuildCount };
    }

    private static void CheckTiles(List<object> checks, float dpi)
    {
        using var controller = Scene();
        using var scene = new InkSceneRenderer { ShowSelectionOutline = false };
        using var cache = new CommittedInkLayer();
        using var tiled = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1280, 720, dpi);
        using var reference = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1280, 720, dpi);
        foreach (var offset in new[] { Vector2.Zero, new Vector2(800, 450), new Vector2(-900, -600), Vector2.Zero })
        {
            controller.ViewTransform = Matrix3x2.CreateTranslation(offset);
            Compare("tiles include offscreen ink while panning");
        }
        controller.ViewTransform = Matrix3x2.CreateScale(1.25f) * Matrix3x2.CreateRotation(.2f) * Matrix3x2.CreateTranslation(240, 80);
        Compare("tiles preserve zoomed and rotated ink");
        controller.ViewTransform = Matrix3x2.Identity;
        // Alternate selected/unchanged runs must preserve alpha and document stacking.
        controller.Tool = InkTool.Select;
        controller.Selection.AddRange(controller.Document.Strokes.Where((_, i) => i >= 60 && i < 180));
        controller.OnPointerDown(Pointer(500, 300));
        controller.OnPointerMove(Pointer(560, 340));
        Compare("selection preserves translucent stacking");
        controller.OnPointerUp(Pointer(560, 340));
        cache.Invalidate(); Compare("selection release matches committed ink");
        controller.BeginSelectionGesture();
        controller.ApplySelectionGesture(Matrix3x2.CreateRotation(.3f, new Vector2(500, 300)));
        Compare("selection rotation preview matches geometry");
        controller.EndSelectionGesture();
        cache.Invalidate(); Compare("selection rotation commit matches geometry");
        controller.ClearSelection(); controller.Tool = InkTool.Pen;
        controller.MultiTouchEnabled = false; controller.PalmErases = false;
        controller.OnPointerDown(Pointer(100, 200) with { Id = 1, Kind = OverlayPointerKind.Touch });
        controller.OnPointerDown(Pointer(500, 200) with { Id = 2, Kind = OverlayPointerKind.Touch });
        long rebuilds = cache.RebuildCount;
        for (int frame = 0; frame < 60; frame++)
        {
            controller.OnPointerMove(Pointer((frame % 2 == 0 ? 100 : 500) + frame * 2, 200 + frame)
                with { Id = frame % 2 == 0 ? 1u : 2u, Kind = OverlayPointerKind.Touch });
            using var draw = tiled.CreateDrawingSession();
            draw.Clear(Microsoft.UI.Colors.Transparent); cache.Draw(draw, scene, controller, 1280, 720, dpi);
        }
        long gestureRebuilds = cache.RebuildCount - rebuilds;
        checks.Add(new { check = "two finger pan reuses history with pinch enabled", dpi, visible = gestureRebuilds <= 1, gestureRebuilds });
        controller.OnPointerUp(Pointer(216, 258) with { Id = 1, Kind = OverlayPointerKind.Touch });
        controller.OnPointerUp(Pointer(618, 259) with { Id = 2, Kind = OverlayPointerKind.Touch });
        Compare("two finger release refreshes final vector quality");

        void Compare(string name)
        {
            using (var draw = tiled.CreateDrawingSession())
            { draw.Clear(Microsoft.UI.Colors.Transparent); cache.Draw(draw, scene, controller, 1280, 720, dpi); }
            using (var draw = reference.CreateDrawingSession())
            { draw.Clear(Microsoft.UI.Colors.Transparent); scene.DrawCommitted(draw, controller); }
            var actual = tiled.GetPixelBytes(); var expected = reference.GetPixelBytes();
            int max = 0, different = 0, outsideEdge = 0;
            int physicalWidth = (int)Math.Round(1280 * dpi / 96), physicalHeight = (int)Math.Round(720 * dpi / 96);
            for (int i = 0; i < actual.Length; i++)
            {
                int delta = Math.Abs(actual[i] - expected[i]); max = Math.Max(max, delta);
                if (delta <= 3) continue;
                different++;
                // D2D quantizes transformed path edges slightly differently at a tile-local origin.
                // Permit a few antialias samples only when they fall within the adjacent reference edge.
                int x = i / 4 % physicalWidth, y = i / 4 / physicalWidth, channel = i % 4;
                int low = 255, high = 0;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(physicalHeight - 1, y + 1); yy++)
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(physicalWidth - 1, x + 1); xx++)
                {
                    int value = expected[(yy * physicalWidth + xx) * 4 + channel];
                    low = Math.Min(low, value); high = Math.Max(high, value);
                }
                if (actual[i] < low - 3 || actual[i] > high + 3) outsideEdge++;
            }
            checks.Add(new { check = name, dpi, offset = controller.ViewTransform.Translation.ToString(),
                visible = outsideEdge == 0 && different <= actual.Length / 20000, differentChannels = different, outsideEdge, maxDifference = max });
        }
    }

    private static void CheckLiveNib(List<object> checks)
    {
        foreach (bool highlighter in new[] { false, true })
        {
            using var incremental = new StrokeRenderer();
            using var complete = new StrokeRenderer();
            using var actual = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 240, 96);
            using var expected = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 240, 96);
            var stroke = new InkStroke(new StrokeAttributes { Nib = PenNib.Ellipse45, BaseWidth = 14, IsHighlighter = highlighter });
            for (int i = 0; i < 400; i++)
            {
                stroke.AddPoint(160 + MathF.Sin(i * .08f) * 120, 120 + MathF.Sin(i * .16f) * 80);
                using var draw = actual.CreateDrawingSession();
                draw.Clear(Microsoft.UI.Colors.Transparent); incremental.Draw(draw, stroke, Matrix3x2.Identity);
            }
            stroke.Complete();
            using (var draw = expected.CreateDrawingSession())
            { draw.Clear(Microsoft.UI.Colors.Transparent); complete.Draw(draw, stroke, Matrix3x2.Identity); }
            var a = actual.GetPixelBytes(); var b = expected.GetPixelBytes();
            int max = 0;
            for (int i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));
            checks.Add(new { check = "elliptical nib live chunks match completed geometry", highlighter, visible = max <= 1, maxDifference = max });
        }
    }

    private static async Task SaveNibPreview(string directory)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 900, 360, 96);
        using var renderer = new StrokeRenderer();
        using (var draw = target.CreateDrawingSession())
        {
            draw.Clear(Microsoft.UI.Colors.White);
            using var font = new CanvasTextFormat { FontSize = 20 };
            for (int row = 0; row < 2; row++)
            {
                var nib = row == 0 ? PenNib.Round : PenNib.Ellipse45;
                draw.DrawText(row == 0 ? "圆形" : "椭圆 · 右斜 45° · 2∶1", 20, 15 + row * 180, Microsoft.UI.Colors.Black, font);
                var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 26, TaperKind = TaperKind.None, Nib = nib });
                stroke.AddPoint(50, 90 + row * 180); stroke.Complete(); renderer.Draw(draw, stroke, Matrix3x2.Identity);
                foreach (var pair in new[] { (new Vector2(130, 120), new Vector2(260, 60)), (new Vector2(330, 60), new Vector2(460, 120)),
                    (new Vector2(530, 90), new Vector2(700, 90)) })
                {
                    var line = new InkStroke(stroke.Attributes.Clone());
                    line.AddPoint(pair.Item1.X, pair.Item1.Y + row * 180); line.AddPoint(pair.Item2.X, pair.Item2.Y + row * 180);
                    line.Complete(); renderer.Draw(draw, line, Matrix3x2.Identity);
                }
            }
        }
        await target.SaveAsync(Path.Combine(directory, "pen-nibs.png"), CanvasBitmapFileFormat.Png);
    }
}
