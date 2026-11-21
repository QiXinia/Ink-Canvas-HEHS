using InkCanvas.Core.Ink;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Ppt;
using System.Text.Json;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// SlideId-based resume and legacy (page-number based) archive migration. Everything runs in a
/// temp directory with synthetic managed snapshots, so no COM or real Win2D surface is involved.
/// </summary>
public sealed class SlideMigrationTests : IDisposable
{
    private readonly string _root;

    public SlideMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inkcanvas-slide-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static InkStroke NewStroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Opaque(10, 20, 30),
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f, 0.5f, 0f);
        stroke.AddPoint(x + 5f, 10f, 0.5f, 10f);
        stroke.Complete();
        return stroke;
    }

    private static PptPresentationInfo Info(string name, int slideCount) => new(
        "com-identity", name, Path.Combine(Path.GetTempPath(), name), true, slideCount,
        960, 540, "PowerPoint.Application");

    private static PptSlideInfo Slide(int slideId, int index) =>
        new(slideId, index, $"slide-{index}", null, null, null);

    private static PptPresentationDetails Details(int slideCount, params PptSlideInfo[] slides) =>
        new(Info("deck.pptx", slideCount), slides, null);

    private static PptStoredSlide Stored(int slideId, int slideIndex) =>
        new(slideId, slideIndex, new[] { NewStroke(slideIndex * 10f) });

    private static PptInkArchive Archive(int slideCount, params PptStoredSlide[] slides) =>
        new(Info("deck.pptx", slideCount), null, null, slides);

    private static string ManifestPath(string directory) => Path.Combine(directory, "InkCanvas-Ppt.json");

    private static void WriteLegacyManifest(string directory, int? lastSlideIndex, params (int Id, int Index)[] slides)
    {
        Directory.CreateDirectory(directory);
        var payload = new
        {
            LastSlideIndex = lastSlideIndex,
            LastShowPosition = lastSlideIndex,
            Slides = slides.Select(s => new { SlideId = s.Id, SlideIndex = s.Index, File = $"slide-{s.Id}.icink" }),
        };
        File.WriteAllText(ManifestPath(directory), JsonSerializer.Serialize(payload));
    }

    private static string WriteSlideFile(string directory, string name, params InkStroke[] strokes)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        InkFileStore.Save(path, strokes);
        return path;
    }

    [Fact]
    public void LegacyManifestWithoutVersion_MigratesToVersion2AndMapsSlideIds()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 2);
        string directory = store.GetPresentationDirectory(presentation);
        WriteLegacyManifest(directory, 2, (11, 1), (12, 2));
        string oldOne = WriteSlideFile(directory, "slide-11.icink", NewStroke(1f));
        string oldTwo = WriteSlideFile(directory, "slide-12.icink", NewStroke(2f));

        PptInkArchive archive = store.Load(presentation);
        Assert.Equal(1, archive.ManifestVersion);
        Assert.Null(archive.LastSlideId);

        SlideInkMigrationReport report = SlideInkMigrator.MigrateIfNeeded(
            archive, Details(2, Slide(101, 1), Slide(102, 2)), directory);

        Assert.True(report.Migrated);
        Assert.Equal(2, report.MappedSlides);
        Assert.Equal(0, report.SkippedSlides);
        Assert.NotNull(report.BackupDirectory);
        Assert.True(Directory.Exists(report.BackupDirectory));
        Assert.True(File.Exists(oldOne));
        Assert.True(File.Exists(oldTwo));
        Assert.True(File.Exists(Path.Combine(directory, "slide-101.icink")));
        Assert.True(File.Exists(Path.Combine(directory, "slide-102.icink")));

        PptInkArchive reloaded = store.Load(presentation);
        Assert.Equal(2, reloaded.ManifestVersion);
        Assert.Equal(102, reloaded.LastSlideId);
        Assert.Equal(new[] { 101, 102 }, reloaded.Slides.Select(s => s.SlideId).OrderBy(i => i).ToArray());
        Assert.All(reloaded.Slides, s => Assert.Single(s.Strokes));
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 1);
        string directory = store.GetPresentationDirectory(presentation);
        WriteLegacyManifest(directory, 1, (11, 1));
        WriteSlideFile(directory, "slide-11.icink", NewStroke(1f));
        var details = Details(1, Slide(101, 1));

        var first = SlideInkMigrator.MigrateIfNeeded(store.Load(presentation), details, directory);
        Assert.True(first.Migrated);

        var second = SlideInkMigrator.MigrateIfNeeded(store.Load(presentation), details, directory);
        Assert.False(second.Migrated);
        Assert.Null(second.BackupDirectory);
    }

    [Fact]
    public void MissingSlides_AreSkippedAndOldFilesSurvive()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 3);
        string directory = store.GetPresentationDirectory(presentation);
        WriteLegacyManifest(directory, 3, (11, 1), (12, 2), (13, 3));
        WriteSlideFile(directory, "slide-11.icink", NewStroke(1f));
        WriteSlideFile(directory, "slide-12.icink", NewStroke(2f));
        string orphan = WriteSlideFile(directory, "slide-13.icink", NewStroke(3f));

        var details = Details(2, Slide(101, 1), Slide(102, 2));
        SlideInkMigrationReport report = SlideInkMigrator.MigrateIfNeeded(
            store.Load(presentation), details, directory);

        Assert.True(report.Migrated);
        Assert.Equal(2, report.MappedSlides);
        Assert.Equal(1, report.SkippedSlides);
        Assert.True(File.Exists(orphan));
        Assert.Contains("未清空", report.Message);
    }

    [Fact]
    public void SaveAndLoad_RoundTripLastSlideId()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 2);
        var archive = new PptInkArchive(presentation, 2, 2,
            new[] { new PptStoredSlide(102, 2, new[] { NewStroke(2f) }) },
            LastSlideId: 102, ManifestVersion: 2);

        store.Save(archive);
        PptInkArchive loaded = store.Load(presentation);

        Assert.Equal(2, loaded.ManifestVersion);
        Assert.Equal(102, loaded.LastSlideId);
        Assert.Equal(2, loaded.LastSlideIndex);
        Assert.Equal(102, loaded.Slides[0].SlideId);
        string json = File.ReadAllText(ManifestPath(store.GetPresentationDirectory(presentation)));
        Assert.Contains("\"Version\": 2", json);
        Assert.Contains("\"LastSlideId\": 102", json);
    }

    [Fact]
    public void ManifestWithoutLastSlideId_LoadsVersion2AndKeepsIndexFallback()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 2);
        string directory = store.GetPresentationDirectory(presentation);
        Directory.CreateDirectory(directory);
        WriteSlideFile(directory, "slide-101.icink", NewStroke(1f));
        File.WriteAllText(ManifestPath(directory), JsonSerializer.Serialize(new
        {
            Version = 2,
            LastSlideIndex = 1,
            LastShowPosition = 1,
            Slides = new[] { new { SlideId = 101, SlideIndex = 1, File = "slide-101.icink" } },
        }));

        PptInkArchive archive = store.Load(presentation);

        Assert.Equal(2, archive.ManifestVersion);
        Assert.Null(archive.LastSlideId);
        Assert.Equal(1, archive.LastSlideIndex);

        bool degraded;
        int? resume = PptInkSession.ResolveResumeIndex(archive.LastSlideId, archive.LastSlideIndex,
            new[] { Slide(101, 1) }, out degraded);
        Assert.Equal(1, resume);
        Assert.True(degraded);
    }

    [Fact]
    public void IndexNamedFilesWithoutManifest_AreMigratedByIndex()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 2);
        string directory = store.GetPresentationDirectory(presentation);
        string first = WriteSlideFile(directory, "slide-1.icink", NewStroke(1f));
        string second = WriteSlideFile(directory, "slide-2.icink", NewStroke(2f));

        PptInkArchive archive = store.Load(presentation);
        Assert.Equal(1, archive.ManifestVersion);
        Assert.Equal(2, archive.Slides.Count);
        Assert.All(archive.Slides, s => Assert.Equal(0, s.SlideId));

        SlideInkMigrationReport report = SlideInkMigrator.MigrateIfNeeded(
            archive, Details(2, Slide(201, 1), Slide(202, 2)), directory);

        Assert.True(report.Migrated);
        Assert.Equal(2, report.MappedSlides);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(Path.Combine(directory, "slide-201.icink")));
        Assert.True(File.Exists(Path.Combine(directory, "slide-202.icink")));
        PptInkArchive reloaded = store.Load(presentation);
        Assert.Equal(2, reloaded.ManifestVersion);
        Assert.Contains(reloaded.Slides, s => s.SlideId == 201);
    }

    [Fact]
    public void Migration_KeepsLegacyIsfFilesAndWritesBackup()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 1);
        string directory = store.GetPresentationDirectory(presentation);
        WriteLegacyManifest(directory, 1, (11, 1));
        WriteSlideFile(directory, "slide-11.icink", NewStroke(1f));
        string isf = Path.Combine(directory, "legacy.icstk");
        File.WriteAllText(isf, "legacy-isf");

        PptInkArchive archive = store.Load(presentation);
        Assert.True(archive.LegacyInkFilesDetected);

        SlideInkMigrationReport report = SlideInkMigrator.MigrateIfNeeded(
            archive, Details(1, Slide(101, 1)), directory);

        Assert.True(report.Migrated);
        Assert.True(File.Exists(isf));
        Assert.True(File.Exists(Path.Combine(report.BackupDirectory, "legacy.icstk")));
        Assert.True(File.Exists(Path.Combine(report.BackupDirectory, "InkCanvas-Ppt.json")));
    }

    [Fact]
    public void Inspector_DetectsCountAndOrderChanges()
    {
        var archive = new PptInkArchive(Info("deck.pptx", 2), null, null,
            new[] { Stored(10, 1), Stored(20, 2) });

        SlideMappingIssue issue = SlideMappingInspector.Inspect(archive,
            new[] { Slide(20, 1), Slide(10, 2), Slide(30, 3) });

        Assert.True(issue.NeedsWarning);
        Assert.True(issue.CountChanged);
        Assert.True(issue.PageOrderChanged);
        Assert.Empty(issue.MissingSlideIds);
    }

    [Fact]
    public void Inspector_MissingRatioAboveThirtyPercent_NeedsWarning()
    {
        var archived = Enumerable.Range(1, 10).Select(i => Stored(100 + i, i)).ToArray();
        var archive = new PptInkArchive(Info("deck.pptx", 10), null, null, archived);
        var current = Enumerable.Range(1, 5).Select(i => Slide(100 + i, i)).ToArray();

        SlideMappingIssue issue = SlideMappingInspector.Inspect(archive, current);

        Assert.True(issue.NeedsWarning);
        Assert.Equal(5, issue.MissingSlideIds.Count);
        Assert.True(issue.MissingSlideIds.Count * 100 / archived.Length > 30);
    }

    [Fact]
    public void Migration_WithLargeMissingRatio_ReportsSkippedWithoutClearing()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 10);
        string directory = store.GetPresentationDirectory(presentation);
        WriteLegacyManifest(directory, 10,
            Enumerable.Range(1, 10).Select(i => (100 + i, i)).ToArray());
        for (int i = 1; i <= 10; i++) WriteSlideFile(directory, $"slide-{100 + i}.icink", NewStroke(i));

        var current = Enumerable.Range(1, 5).Select(i => Slide(100 + i, i)).ToArray();
        SlideInkMigrationReport report = SlideInkMigrator.MigrateIfNeeded(
            store.Load(presentation), Details(5, current), directory);

        Assert.True(report.Migrated);
        Assert.Equal(5, report.MappedSlides);
        Assert.Equal(5, report.SkippedSlides);
        Assert.Contains("页数或页序", report.Message);
        Assert.Equal(10, Directory.GetFiles(directory, "slide-*.icink").Length);
    }

    [Fact]
    public void ResumeIndex_PrefersSlideIdOverPageNumber()
    {
        var slides = new[] { Slide(101, 1), Slide(102, 2), Slide(103, 3) };

        Assert.Equal(2, PptInkSession.ResolveResumeIndex(102, 3, slides, out bool exact));
        Assert.False(exact);

        Assert.Equal(3, PptInkSession.ResolveResumeIndex(999, 3, slides, out bool degraded));
        Assert.True(degraded);

        Assert.Null(PptInkSession.ResolveResumeIndex(999, 9, slides, out bool missing));
        Assert.False(missing);
    }

    [Fact]
    public void MigrateWithoutData_DoesNothing()
    {
        var store = new PptInkFileStore(_root);
        PptPresentationInfo presentation = Info("deck.pptx", 2);
        string directory = store.GetPresentationDirectory(presentation);
        Directory.CreateDirectory(directory);

        SlideInkMigrationReport report = SlideInkMigrator.MigrateIfNeeded(
            store.Load(presentation), Details(2, Slide(101, 1), Slide(102, 2)), directory);

        Assert.False(report.Migrated);
        Assert.False(File.Exists(ManifestPath(directory)));
        Assert.Empty(Directory.GetDirectories(_root, "*.bak-*", SearchOption.AllDirectories));
    }
}