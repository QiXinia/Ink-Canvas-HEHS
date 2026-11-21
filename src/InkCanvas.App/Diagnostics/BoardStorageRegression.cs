using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Features.Board;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

internal static class BoardStorageRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        int ticks = 0;
        long previous = Stopwatch.GetTimestamp();
        double maximumGap = 0;
        timer.Tick += (_, _) =>
        {
            maximumGap = Math.Max(maximumGap, Stopwatch.GetElapsedTime(previous).TotalMilliseconds);
            previous = Stopwatch.GetTimestamp(); ticks++;
        };
        try
        {
            var payload = await Task.Run(() =>
            {
                var bytes = new byte[64 * 1024 * 1024]; Random.Shared.NextBytes(bytes); return bytes;
            });
            var board = services.Board;
            board.AddMedia(new() { Name = "large-video.mp4", Data = payload, PosterData = new byte[128 * 1024], NaturalWidth = 1920, NaturalHeight = 1080 });
            board.InsertPageAfterCurrent();
            board.AddMedia(new() { Name = "shared-video.mp4", Data = payload });
            string path = Path.Combine(directory, "large-board.icink");
            previous = Stopwatch.GetTimestamp(); timer.Start();
            long allocated = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            var save = board.SaveBoardAsync(path);
            double snapshotMs = watch.Elapsed.TotalMilliseconds;
            await save;
            long saveBytes = GC.GetTotalAllocatedBytes(true) - allocated;
            int saveTicks = ticks;
            checks.Add(new { check = "large save returns to the WinUI dispatcher", passed = saveTicks > 0 && maximumGap < 500,
                saveTicks, snapshotMs, maximumGapMs = maximumGap, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = saveBytes });
            using (var archive = ZipFile.OpenRead(path))
                checks.Add(new { check = "ZIP stores shared video once without Base64", passed = archive.Entries.Count(entry => entry.Length == payload.Length) == 1
                    && new FileInfo(path).Length < payload.Length + 256 * 1024, length = new FileInfo(path).Length });
            watch.Restart(); allocated = GC.GetTotalAllocatedBytes(true);
            var apply = await services.PrepareInkOpenAsync(path, board, entireBoard: true);
            int loadTicks = ticks - saveTicks;
            long loadBytes = GC.GetTotalAllocatedBytes(true) - allocated;
            checks.Add(new { check = "normal file-open pipeline reads off the UI thread", passed = loadTicks > 0 && maximumGap < 500,
                loadTicks, maximumGapMs = maximumGap, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = loadBytes });
            apply();
            var first = board.CurrentMedia.Single(); board.NextPage();
            checks.Add(new { check = "UI applies complete pages and retains shared binary data", passed = board.PageCount == 2
                && first.NaturalWidth == 1920 && first.Data.AsSpan().SequenceEqual(payload) && ReferenceEquals(first.Data, board.CurrentMedia.Single().Data) });
            checks.Add(new { check = "binary save and load have bounded allocation", passed = saveBytes < payload.Length / 2 && loadBytes < payload.Length * 2L, saveBytes, loadBytes });
        }
        catch (Exception ex) { checks.Add(new { check = "board storage exception", passed = false, error = ex.ToString() }); }
        finally
        {
            timer.Stop();
            await File.WriteAllTextAsync(Path.Combine(directory, "board-storage.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
    }
}
