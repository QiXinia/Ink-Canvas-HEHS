using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Persistence;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ResumeTests
{
    [Fact]
    public void BoardAndPdfPreferencesAreIndependentAndPersisted()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}");
        Assert.Equal(ResumeBehavior.Reminder, settings.Resume.Whiteboard);
        Assert.Equal(ResumeBehavior.Reminder, settings.Resume.Pdf);
        settings.Resume.Whiteboard = ResumeBehavior.Automatic;
        settings.Resume.Pdf = ResumeBehavior.Disabled;
        var restored = settings.Clone();
        Assert.Equal(ResumeBehavior.Automatic, restored.Resume.Whiteboard);
        Assert.Equal(ResumeBehavior.Disabled, restored.Resume.Pdf);
    }

    [Fact]
    public async Task BoardSnapshotSurvivesRebootWithPagesPaperMediaAndViewport()
    {
        string directory = Temp();
        var board = new BoardService();
        using var service = new BoardResumeService(board, directory, () => ResumeBehavior.Automatic);
        board.Current.Document.Add(Stroke()); board.Background = BoardBackground.Grid;
        board.NextPage(); board.Background = BoardBackground.Cream;
        board.Current.Document.Add(Stroke());
        board.Current.ViewTransform = Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateTranslation(-200, -100);
        var image = new BoardImageAsset { Data = new byte[] { 1, 2, 3 }, Left = 30, Top = 40, Width = 80, Height = 60 };
        board.AddImage(image);
        await service.SaveAsync();
        var saved = await service.Store.ReadAsync();
        using (var archive = ZipFile.OpenRead(service.Store.ResolveSnapshot(saved))) Assert.Null(archive.GetEntry("preview/cover.png"));
        Assert.Null(await new ResumeStore(directory, "board", saved.BootId).ReadAsync(afterReboot: true));
        Assert.NotNull(await new ResumeStore(directory, "board", saved.BootId + 10).ReadAsync(afterReboot: true));
        var loaded = new BoardService();
        using var recovery = new BoardResumeService(loaded, directory, () => ResumeBehavior.Automatic);
        await recovery.RestoreAsync(saved);
        Assert.Equal(2, loaded.PageCount); Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(BoardBackground.Grid, loaded.PageBackground(0)); Assert.Equal(BoardBackground.Cream, loaded.Background);
        Assert.Equal(board.Current.ViewTransform, loaded.Current.ViewTransform);
        Assert.Equal(image.Data, Assert.Single(loaded.CurrentImages).Data);
        Assert.Single(loaded.Current.Document.Strokes);
        foreach (var page in board.Pages.Concat(loaded.Pages)) page.Dispose();
    }

    [Fact]
    public async Task UnchangedBoardReusesSnapshotAndFailureKeepsPreviousRecord()
    {
        string directory = Temp();
        var board = new BoardService();
        using var service = new BoardResumeService(board, directory, () => ResumeBehavior.Reminder);
        board.Current.Document.Add(Stroke()); await service.SaveAsync();
        var first = await service.Store.ReadAsync();
        await service.SaveAsync();
        Assert.Equal(first.SavedAtUtc, (await service.Store.ReadAsync()).SavedAtUtc);
        await Assert.ThrowsAsync<IOException>(() => service.Store.SaveAsync(first with { PageIndex = 5 }, _ => throw new IOException("interrupted")));
        Assert.Equal(first.PageIndex, (await service.Store.ReadAsync()).PageIndex);
        await service.Store.ClearAsync(first);
        Assert.Null(await service.Store.ReadAsync());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(service.Store.PathName), "board-*.icink"));
        board.Current.Dispose();
    }

    [Fact]
    public async Task ReminderExpiryNeverDeletesNewerProgress()
    {
        string directory = Temp();
        var store = new ResumeStore(directory, "board", 100);
        await store.SaveAsync(new("board", 0, default, PageIndex: 1));
        var previous = await store.ReadAsync();
        await store.SaveAsync(new("board", 0, default, PageIndex: 2));
        await store.ClearAsync(previous);
        Assert.Equal(2, (await store.ReadAsync()).PageIndex);
    }

    [Fact]
    public async Task PdfKeepsSeparatePageNumbersAndHonorsFileIdentityAndReboot()
    {
        string directory = Temp();
        string first = Path.Combine(directory, "first.pdf"), second = Path.Combine(directory, "second.pdf");
        await File.WriteAllTextAsync(first, "pdf one"); await File.WriteAllTextAsync(second, "pdf two");
        var store = new PdfReadingProgressStore(directory, 100);
        await store.SaveAsync(first, 7); await store.SaveAsync(second, 3);
        Assert.Equal(7, (await store.FindAsync(first)).PageIndex); Assert.Equal(3, (await store.FindAsync(second)).PageIndex);
        Assert.Null(await store.FindAsync(afterReboot: true));
        Assert.Equal(second, (await new PdfReadingProgressStore(directory, 200).FindAsync(afterReboot: true)).SourcePath);
        var original = await store.FindAsync(first);
        Assert.True(PdfReadingProgressStore.Matches(original));
        await File.AppendAllTextAsync(first, " updated");
        Assert.False(PdfReadingProgressStore.Matches(original));
        await store.RemoveAsync(original);
        Assert.Null(await store.FindAsync(first)); Assert.NotNull(await store.FindAsync(second));
        await store.RemoveAsync(); Assert.Null(await store.FindAsync());
    }

    [Fact]
    public async Task ConcurrentPdfWritersRetainBothDocuments()
    {
        string directory = Temp(), first = Path.Combine(directory, "a.pdf"), second = Path.Combine(directory, "b.pdf");
        await File.WriteAllTextAsync(first, "a"); await File.WriteAllTextAsync(second, "b");
        await Task.WhenAll(new PdfReadingProgressStore(directory).SaveAsync(first, 3), new PdfReadingProgressStore(directory).SaveAsync(second, 7));
        var store = new PdfReadingProgressStore(directory);
        Assert.Equal(3, (await store.FindAsync(first)).PageIndex); Assert.Equal(7, (await store.FindAsync(second)).PageIndex);
    }

    [Fact]
    public async Task PdfReminderDismissalPreservesNewerPageAndDisablingClearsHistory()
    {
        string directory = Temp(), source = Path.Combine(directory, "lesson.pdf"); await File.WriteAllTextAsync(source, "pdf");
        var store = new PdfReadingProgressStore(directory, 100);
        await store.SaveAsync(source, 4); var previous = await store.FindAsync(source);
        await store.SaveAsync(source, 6); await store.RemoveAsync(previous);
        Assert.Equal(6, (await store.FindAsync(source)).PageIndex);
        await store.RemoveAsync(); Assert.Null(await store.FindAsync());
    }

    private static string Temp()
    {
        string directory = Path.Combine(Path.GetTempPath(), "InkCanvas-ResumeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static InkStroke Stroke()
    {
        var stroke = new InkStroke(new() { BaseWidth = 3, TaperKind = TaperKind.None });
        stroke.AddPoint(100, 100); stroke.AddPoint(200, 150); stroke.Complete(); return stroke;
    }
}
