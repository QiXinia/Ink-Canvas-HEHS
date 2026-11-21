using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Core.Ink;
using InkCanvas.Platform.Windows;

namespace InkCanvas.App.Diagnostics;

/// <summary>Opt-in repeatable memory/render workload. Runs only in a dedicated diagnostic instance.</summary>
internal static class PerformanceCheck
{
    public static async Task Run(ShellController shell, string directory)
    {
        Directory.CreateDirectory(directory);
        var samples = new List<object>();
        var controller = shell.Services.OverlayInk;
        controller.RecognizeShapes = false;
        controller.Tool = Ink.InkTool.Pen;
        Sample("idle");
        var strokes = Enumerable.Range(0, 240).Select(i =>
        {
            var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
            for (int j = 0; j < 100; j++)
                stroke.AddPoint(20 + j * 5, 20 + i % 32 * 20 + MathF.Sin(j * 0.2f) * 12);
            stroke.Complete();
            return stroke;
        }).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        controller.Document.AddRange(strokes);
        clock.Stop();
        samples.Add(new { phase = "render 240 strokes x 100 points", elapsedMs = clock.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
        Sample("document rendered");
        before = GC.GetAllocatedBytesForCurrentThread();
        clock.Restart();
        for (int i = 0; i < 8; i++) controller.Document.Add(strokes[i].Clone());
        clock.Stop();
        samples.Add(new { phase = "append 8 strokes", elapsedMs = clock.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
        controller.Document.Clear();
        controller.History.Clear();
        await Task.Delay(100);
        Sample("document cleared");
        var start = new OverlayPointer(8001, 10, 10, 0.5f, 0, 0, OverlayPointerKind.Pen, 0);
        controller.OnPointerDown(start);
        before = GC.GetAllocatedBytesForCurrentThread();
        clock.Restart();
        for (int i = 1; i <= 160; i++)
            controller.OnPointerMove(start with { X = 10 + i * 7, Y = 10 + i * 4, TimeMs = (uint)(i * 8) });
        controller.OnPointerUp(start with { X = 1130, Y = 650, TimeMs = 1300 });
        clock.Stop();
        samples.Add(new { phase = "160 live pointer updates", elapsedMs = clock.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
        controller.Document.Clear();
        controller.History.Clear();
        Sample("large live stroke finished");
        bool emptyBuffersReleased = shell.Overlay.Presenter.ManagedBufferBytes == 0
            && shell.Overlay.Presenter.RenderTargetBytes == 0
            && shell.Overlay.Presenter.Scene.Strokes.CachedGeometryCount == 0;
        for (int i = 0; i < 6; i++)
        {
            shell.ToggleBoard();
            await Task.Delay(120);
            shell.ToggleBoard();
            await Task.Delay(120);
        }
        Sample("6 board open/close cycles");
        // Diagnostic-only collection distinguishes retained objects from garbage awaiting GC.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(200);
        Sample("after diagnostic GC");
        samples.Add(new { phase = "resource checks", emptyBuffersReleased,
            hiddenSurfaceHasNoLiveInk = !controller.LiveStrokes.Any() });
        File.WriteAllText(Path.Combine(directory, "performance.json"),
            JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));

        void Sample(string phase)
        {
            using var process = Process.GetCurrentProcess();
            samples.Add(new { phase, workingSetBytes = process.WorkingSet64,
                privateBytes = process.PrivateMemorySize64, managedBytes = GC.GetTotalMemory(false),
                handles = process.HandleCount, cpuMs = process.TotalProcessorTime.TotalMilliseconds,
                overlayManagedBufferBytes = shell.Overlay.Presenter.ManagedBufferBytes,
                overlayRenderTargetBytes = shell.Overlay.Presenter.RenderTargetBytes,
                cachedGeometries = shell.Overlay.Presenter.Scene.Strokes.CachedGeometryCount,
                geometryBuilds = shell.Overlay.Presenter.Scene.Strokes.GeometryBuildCount });
        }
    }
}
