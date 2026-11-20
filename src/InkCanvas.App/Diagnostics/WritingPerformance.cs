using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Runtime.InteropServices;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;

namespace InkCanvas.App.Diagnostics;

internal static class WritingPerformance
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        foreach (int background in new[] { 0, 1200 })
        foreach (TaperKind style in new[] { TaperKind.Natural, TaperKind.Calligraphy, TaperKind.InkWash })
        {
            using var controller = new InkController { RecognizeShapes = false };
            for (int i = 0; i < background; i++)
            {
                var s = new InkStroke(new StrokeAttributes { BaseWidth = 3, TaperKind = TaperKind.None });
                for (int j = 0; j < 100; j++) s.AddPoint(20 + i % 8 * 160 + j * 1.4f, 20 + i % 30 * 20 + MathF.Sin(j * .2f) * 10);
                s.Complete(); controller.Document.Add(s);
            }
            using var scene = new InkSceneRenderer();
            using var committed = new CommittedInkLayer();
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1280, 720, 96);
            var sample = new OverlayPointer(9900, 20, 300, .5f, 0, 0, OverlayPointerKind.Pen, 0);
            controller.CurrentAttributes.TaperKind = style;
            controller.OnPointerDown(sample);
            var frames = new List<double>();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var total = Stopwatch.StartNew();
            for (int frame = 0; frame < 120; frame++)
            {
                for (int j = 0; j < 4; j++)
                {
                    int index = frame * 4 + j;
                    controller.OnPointerMove(sample with { X = 20 + index * 2.3f, Y = 300 + MathF.Sin(index * .12f) * 80, TimeMs = (uint)(index * 4) });
                }
                var watch = Stopwatch.StartNew();
                using (var draw = target.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.Transparent);
                    committed.Draw(draw, scene, controller, 1280, 720); scene.DrawLive(draw, controller);
                }
                // Flush each frame so the measurement includes actual raster work.
                target.GetPixelBytes(0, 0, 1, 1);
                frames.Add(watch.Elapsed.TotalMilliseconds);
            }
            total.Stop(); frames.Sort();
            results.Add(new { brush = style.ToString(), backgroundStrokes = background, frames = frames.Count, points = controller.LiveStrokes.Single().Count,
                totalMs = total.Elapsed.TotalMilliseconds, medianMs = frames[frames.Count / 2], p95Ms = frames[(int)(frames.Count * .95)],
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, geometryBuilds = scene.Strokes.GeometryBuildCount,
                committedStrokeDraws = committed.CommittedStrokeDraws, segmentBuilds = scene.Strokes.SegmentBuildCount });
            controller.OnPointerUp(sample with { TimeMs = 2000 });
            controller.Tool = InkTool.Pan;
            var pan = sample with { Id = 9901, X = 400, Y = 300 };
            controller.OnPointerDown(pan);
            long rebuilds = committed.RebuildCount;
            var panFrames = new List<double>();
            for (int frame = 0; frame < 120; frame++)
            {
                controller.OnPointerMove(pan with { X = 400 + frame * 1.3f, Y = 300 + frame * .3f });
                var watch = Stopwatch.StartNew();
                using (var draw = target.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.Transparent);
                    committed.Draw(draw, scene, controller, 1280, 720);
                }
                target.GetPixelBytes(0, 0, 1, 1);
                panFrames.Add(watch.Elapsed.TotalMilliseconds);
            }
            panFrames.Sort();
            results.Add(new { scenario = "pan", brush = style.ToString(), backgroundStrokes = background,
                medianMs = panFrames[60], p95Ms = panFrames[114], rebuilds = committed.RebuildCount - rebuilds,
                rasterBytes = committed.RasterBytes });
            controller.OnPointerUp(pan);
            controller.PanZoomEnabled = true;
            controller.ConfigureGestures(true, true, false);
            var first = pan with { Id = 9903, Kind = OverlayPointerKind.Touch, X = 400, Y = 300, ContactWidth = 4 };
            var second = first with { Id = 9904, X = 600 };
            controller.OnPointerDown(first); controller.OnPointerDown(second);
            rebuilds = committed.RebuildCount;
            var zoomFrames = new List<double>();
            for (int frame = 0; frame < 120; frame++)
            {
                first = first with { X = 400 - frame * 1.4f };
                second = second with { X = 600 + frame * 1.4f };
                controller.OnPointerMove(first); controller.OnPointerMove(second);
                var watch = Stopwatch.StartNew();
                using (var draw = target.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.Transparent);
                    committed.Draw(draw, scene, controller, 1280, 720);
                }
                target.GetPixelBytes(0, 0, 1, 1); zoomFrames.Add(watch.Elapsed.TotalMilliseconds);
            }
            zoomFrames.Sort();
            results.Add(new { scenario = "pinch", brush = style.ToString(), backgroundStrokes = background,
                medianMs = zoomFrames[60], p95Ms = zoomFrames[114], maximumMs = zoomFrames[^1],
                rebuilds = committed.RebuildCount - rebuilds });
            controller.OnPointerUp(first); controller.OnPointerUp(second);
        }
        File.WriteAllText(Path.Combine(directory, "writing.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        CheckOverlayDamage(directory);
        CheckOverlayTransitions(directory);
        await CheckHoldClock(directory);
    }

    private static async Task CheckHoldClock(string directory)
    {
        using var ink = new InkController();
        int frames = 0;
        using var clock = new DotAnimationClock(ink, () => frames++);
        var pointer = new OverlayPointer(9950, 100, 100, .5f, 0, 0, OverlayPointerKind.Pen, 100);
        ink.OnPointerDown(pointer);
        await Task.Delay(550);
        bool tapStayedFreehand = ink.RecognitionPreview.Count == 0;
        // Continuing after a failed hold must rearm the stopped clock.
        for (uint i = 1; i <= 40; i++)
            ink.OnPointerMove(pointer with { X = 100 + i * 5, TimeMs = 700 + i * 8 });
        await Task.Delay(550);
        bool preview = ink.RecognitionPreview.Count == 1;
        bool originalHidden = ink.IsRecognitionPreviewSource(ink.LiveStrokes.Single());
        ink.OnPointerUp(pointer with { X = 300, TimeMs = 1600 });
        bool committed = ink.Document.Count == 1 && ink.Document.Strokes[0].IsRecognizedShape && ink.RecognitionPreview.Count == 0;
        int stoppedFrames = frames;
        await Task.Delay(100);
        File.WriteAllText(Path.Combine(directory, "hold-clock.json"), JsonSerializer.Serialize(new
        { passed = tapStayedFreehand && preview && originalHidden && committed && stoppedFrames == frames,
            tapStayedFreehand, preview, originalHidden, committed, stoppedWhenIdle = stoppedFrames == frames },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CheckOverlayTransitions(string directory)
    {
        using var ink = new InkController { RecognizeShapes = false, Tool = InkTool.Highlighter };
        ink.CurrentAttributes.BaseWidth = 18;
        using var window = new LayeredInkWindow(0, 0, 800, 450);
        using var presenter = new OverlayInkPresenter(window, ink);
        using var reference = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 800, 450, 96);
        using var scene = new InkSceneRenderer();
        presenter.Attach();
        var checks = new List<object>();
        var first = new OverlayPointer(9961, 100, 100, .5f, 4, 4, OverlayPointerKind.Touch, 100);
        var second = first with { Id = 9962, X = 240 };
        ink.OnPointerDown(first); ink.OnPointerDown(second);
        for (uint i = 1; i < 140; i++)
        {
            first = first with { X = 180 + MathF.Sin(i * .1f) * 100, Y = 190 + MathF.Sin(i * .2f) * 100, TimeMs = 100 + i * 8 };
            second = second with { X = 250 + i * 2, Y = 220 + MathF.Sin(i * .1f) * 50, TimeMs = first.TimeMs };
            ink.OnPointerMove(first); ink.OnPointerMove(second);
        }
        Check("two simultaneous translucent self-crossing strokes");
        ink.OnPointerUp(first);
        Check("commit one finger while the other continues");
        ink.Tool = InkTool.Pen;
        var pen = second with { Id = 9963, Kind = OverlayPointerKind.Pen, X = 600, Y = 100 };
        ink.OnPointerDown(pen);
        Check("pen priority removes canceled touch pixels");
        ink.OnPointerMove(pen with { X = 650, Y = 150 });
        ink.OnPointerUp(pen with { X = 650, Y = 150 }); ink.OnPointerUp(second);
        Check("commit after mixed input transition");
        File.WriteAllText(Path.Combine(directory, "overlay-transitions.json"), JsonSerializer.Serialize(checks,
            new JsonSerializerOptions { WriteIndented = true }));

        void Check(string name)
        {
            using (var draw = reference.CreateDrawingSession())
            {
                draw.Clear(Windows.UI.Color.FromArgb(1, 1, 1, 1));
                scene.DrawCommitted(draw, ink, Matrix3x2.CreateScale(window.DpiScale));
                scene.DrawLive(draw, ink, Matrix3x2.CreateScale(window.DpiScale));
            }
            var expected = reference.GetPixelBytes();
            var actual = new byte[expected.Length]; Marshal.Copy(window.PixelBuffer, actual, 0, actual.Length);
            int differences = actual.Zip(expected).Count(pair => Math.Abs(pair.First - pair.Second) > 2);
            checks.Add(new { check = name, passed = differences == 0, differentChannels = differences });
        }
    }

    private static void CheckOverlayDamage(string directory)
    {
        var checks = new List<object>();
        foreach (var style in Enum.GetValues<TaperKind>())
        foreach (bool highlighter in new[] { false, true })
        {
            using var ink = new InkController { RecognizeShapes = false, Tool = highlighter ? InkTool.Highlighter : InkTool.Pen };
            ink.CurrentAttributes.BaseWidth = 12; ink.CurrentAttributes.TaperKind = style;
            using var window = new LayeredInkWindow(0, 0, 800, 450);
            using var presenter = new OverlayInkPresenter(window, ink);
            using var reference = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 800, 450, 96);
            using var scene = new InkSceneRenderer();
            presenter.Attach();
            var pointer = new OverlayPointer(9902, 30, 140, .5f, 0, 0, OverlayPointerKind.Pen, 100)
                { HasPressure = true, TiltX = 30, TiltY = 20 };
            ink.OnPointerDown(pointer);
            long bytes = presenter.GpuReadbackBytes;
            long fullBoundsBytes = 0;
            var frames = new List<double>();
            int different = 0;
            for (uint i = 1; i <= 360; i++)
            {
                pointer = pointer with { X = 30 + i * 1.8f, Y = 140 + MathF.Sin(i * .035f) * 100,
                    TimeMs = 100 + i * 4, Pressure = .2f + .6f * (i % 100) / 100, TiltX = 30 + i % 20 };
                var watch = Stopwatch.StartNew();
                ink.OnPointerMove(pointer);
                frames.Add(watch.Elapsed.TotalMilliseconds);
                var bounds = ink.LiveStrokes.Single().Bounds;
                fullBoundsBytes += (long)MathF.Ceiling(bounds.Width * window.DpiScale + 8)
                    * (long)MathF.Ceiling(bounds.Height * window.DpiScale + 8) * 4;
                if (i % 60 != 0) continue;
                using (var draw = reference.CreateDrawingSession())
                {
                    draw.Clear(Windows.UI.Color.FromArgb(1, 1, 1, 1));
                    scene.DrawLive(draw, ink, Matrix3x2.CreateScale(window.DpiScale));
                }
                var expected = reference.GetPixelBytes();
                var actual = new byte[expected.Length]; Marshal.Copy(window.PixelBuffer, actual, 0, actual.Length);
                different += actual.Zip(expected).Count(pair => Math.Abs(pair.First - pair.Second) > 2);
            }
            long actualBytes = presenter.GpuReadbackBytes - bytes;
            ink.OnPointerUp(pointer);
            frames.Sort();
            checks.Add(new { brush = style.ToString(), highlighter, hardwarePressure = true,
                passed = different == 0, differentChannels = different,
                gpuReadbackBytes = actualBytes, fullBoundsBytes, reductionPercent = 100d * (1 - (double)actualBytes / fullBoundsBytes),
                medianMs = frames[180], p95Ms = frames[342] });
        }
        File.WriteAllText(Path.Combine(directory, "overlay-damage.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
