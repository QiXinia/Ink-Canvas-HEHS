using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Persistence;
using System.Text.Json;

namespace InkCanvas.Features.Ppt;

/// <summary>Outcome of a legacy slide-ink migration. Never implies old data was deleted.</summary>
public sealed record SlideInkMigrationReport(
    bool Migrated, string BackupDirectory, int MappedSlides, int SkippedSlides, string Message);

/// <summary>
/// Upgrades pre-v2 per-presentation ink archives (page-number based, manifest without a
/// Version field, or legacy .icstk/.icart files) to manifest version 2, which keys strokes by
/// PowerPoint SlideId. The directory is always backed up before anything is written; old data
/// files are left in place and a mapping mismatch is reported instead of clearing anything.
/// Migration is idempotent: a v2 manifest returns <c>Migrated=false</c> immediately.
/// </summary>
public static class SlideInkMigrator
{
    public const int TargetManifestVersion = 2;
    private const string ManifestName = "InkCanvas-Ppt.json";
    private const string SlideFilePrefix = "slide-";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Migrates <paramref name="archive"/> when it is a legacy archive with data. All failures
    /// are contained: the method logs, returns a non-migrated report, and leaves the source
    /// directory byte-for-byte untouched (unless the backup itself succeeded, which only adds
    /// a sibling directory).
    /// </summary>
    public static SlideInkMigrationReport MigrateIfNeeded(
        PptInkArchive archive, PptPresentationDetails details, string presentationDirectory)
    {
        string backup = null;
        try
        {
            if (archive is null) return Skip("没有可迁移的墨迹档案。");
            if (archive.ManifestVersion >= TargetManifestVersion)
                return Skip("墨迹档案已是新版格式，无需迁移。");
            if (string.IsNullOrWhiteSpace(presentationDirectory) || !Directory.Exists(presentationDirectory))
                return Skip("课件的墨迹目录不存在，未执行迁移。");

            IReadOnlyList<PptSlideInfo> slides = details?.Slides ?? Array.Empty<PptSlideInfo>();
            if (slides.Count == 0) return Skip("当前课件的页面信息不可用，未执行迁移。");
            bool hasData = archive.LegacyInkFilesDetected || (archive.Slides?.Count ?? 0) > 0 ||
                Directory.EnumerateFiles(presentationDirectory, "*" + InkFileStore.Extension,
                    SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(presentationDirectory, "*.icstk", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(presentationDirectory, "*.icart", SearchOption.TopDirectoryOnly).Any();
            if (!hasData) return Skip("没有可迁移的墨迹数据。");

            backup = Backup(presentationDirectory);
            if (string.IsNullOrEmpty(backup))
                return new SlideInkMigrationReport(false, null, 0, 0,
                    "备份失败，已中止迁移（未修改任何墨迹数据）。");

            var byIndex = new Dictionary<int, PptSlideInfo>();
            foreach (PptSlideInfo slide in slides)
            {
                if (slide is null || slide.SlideId <= 0 || slide.Index <= 0) continue;
                byIndex.TryAdd(slide.Index, slide);
            }

            var mapped = new List<MappedSlide>();
            var usedIndexes = new HashSet<int>();
            int skipped = 0;
            foreach (PptStoredSlide stored in archive.Slides ?? Array.Empty<PptStoredSlide>())
            {
                if (stored is null) continue;
                if (stored.SlideIndex <= 0 || !byIndex.TryGetValue(stored.SlideIndex, out PptSlideInfo target) ||
                    !usedIndexes.Add(stored.SlideIndex))
                {
                    skipped++;
                    continue;
                }

                string source = ResolveSourceFile(presentationDirectory, stored.SlideId, stored.SlideIndex);
                IReadOnlyList<InkStroke> strokes = source is not null
                    ? TryLoad(source)
                    : stored.Strokes ?? Array.Empty<InkStroke>();
                if (strokes is null || strokes.Count == 0)
                {
                    skipped++;
                    continue;
                }
                mapped.Add(new MappedSlide(target.SlideId, target.Index, strokes, source));
            }

            if (mapped.Count == 0)
                return new SlideInkMigrationReport(false, backup, 0, skipped,
                    $"旧版墨迹与当前页序无法对应（跳过 {skipped} 页），未修改数据。备份在 {backup}。");

            string directory = Path.GetFullPath(presentationDirectory);
            Directory.CreateDirectory(directory);
            var entries = new List<ManifestSlide>();
            foreach (MappedSlide item in mapped.OrderBy(m => m.SlideIndex))
            {
                string fileName = $"{SlideFilePrefix}{item.SlideId}{InkFileStore.Extension}";
                string destination = Path.Combine(directory, fileName);
                bool copied = false;
                if (item.Source is not null)
                {
                    try
                    {
                        if (!PathsEqual(item.Source, destination))
                            File.Copy(item.Source, destination, overwrite: true);
                        copied = true;
                    }
                    catch (Exception ex)
                    {
                        FileLog.WriteLogToFile($"PPT ink migration: copy {item.Source}: {ex}",
                            LogType.Error);
                    }
                }
                if (!copied) InkFileStore.Save(destination, item.Strokes);
                entries.Add(new ManifestSlide
                {
                    SlideId = item.SlideId,
                    SlideIndex = item.SlideIndex,
                    File = fileName,
                });
            }

            int? lastSlideId = ResolveLastSlideId(archive, byIndex, slides);
            int? lastIndex = archive.LastSlideIndex;
            if (lastIndex is > 0 && byIndex.TryGetValue(lastIndex.Value, out PptSlideInfo resumed))
                lastIndex = resumed.Index;

            var manifest = new Manifest
            {
                Version = TargetManifestVersion,
                LastSlideIndex = lastIndex,
                LastSlideId = lastSlideId,
                LastShowPosition = archive.LastShowPosition,
                Slides = entries,
            };
            string manifestPath = Path.Combine(directory, ManifestName);
            string temp = manifestPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temp, manifestPath, overwrite: true);

            var migratedArchive = new PptInkArchive(archive.Presentation, lastIndex, archive.LastShowPosition,
                entries.Select(e => new PptStoredSlide(e.SlideId, e.SlideIndex,
                    mapped.First(m => m.SlideId == e.SlideId).Strokes)).ToList(),
                archive.LegacyInkFilesDetected, lastSlideId, TargetManifestVersion);
            SlideMappingIssue issue = SlideMappingInspector.Inspect(migratedArchive, slides);

            string message = $"已将 {entries.Count} 页旧版批注按当前页序映射到 SlideId，跳过 {skipped} 页；" +
                             $"原文件保留，备份在 {backup}。";
            if (issue.NeedsWarning)
                message += " 注意：课件页数或页序与存档不一致，未清空任何批注，请确认是否继续使用。";
            FileLog.WriteLogToFile($"PPT ink migration: {message}", LogType.Info);
            return new SlideInkMigrationReport(true, backup, entries.Count, skipped, message);
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"PPT ink migration failed: {ex}", LogType.Error);
            return new SlideInkMigrationReport(false, backup, 0, 0,
                backup is null ? $"迁移失败，未修改数据：{ex.Message}" : $"迁移失败，未修改数据；备份在 {backup}：{ex.Message}");
        }
    }

    /// <summary>
    /// Copies <paramref name="presentationDirectory"/> to a sibling
    /// <c>&lt;name&gt;.bak-yyyyMMdd-HHmmss</c> directory. Returns null when the copy fails.
    /// </summary>
    public static string Backup(string presentationDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(presentationDirectory) || !Directory.Exists(presentationDirectory))
                return null;
            string source = Path.GetFullPath(presentationDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string target = $"{source}.bak-{stamp}";
            for (int suffix = 2; Directory.Exists(target); suffix++)
                target = $"{source}.bak-{stamp}-{suffix}";
            CopyDirectory(source, target);
            return target;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"PPT ink backup failed: {ex}", LogType.Error);
            return null;
        }
    }

    private static string ResolveSourceFile(string directory, int slideId, int slideIndex)
    {
        if (slideId > 0)
        {
            string byId = Path.Combine(directory, $"{SlideFilePrefix}{slideId}{InkFileStore.Extension}");
            if (File.Exists(byId)) return byId;
        }
        if (slideIndex > 0)
        {
            string old = Path.Combine(directory, $"{SlideFilePrefix}{slideIndex}{InkFileStore.Extension}");
            if (File.Exists(old)) return old;
        }
        return null;
    }

    private static int? ResolveLastSlideId(PptInkArchive archive, IReadOnlyDictionary<int, PptSlideInfo> byIndex,
        IReadOnlyList<PptSlideInfo> slides)
    {
        if (archive.LastSlideIndex is > 0 && byIndex.TryGetValue(archive.LastSlideIndex.Value, out PptSlideInfo byPage))
            return byPage.SlideId;
        if (archive.LastSlideId is > 0 && slides.Any(s => s is not null && s.SlideId == archive.LastSlideId.Value))
            return archive.LastSlideId;
        return null;
    }

    private static IReadOnlyList<InkStroke> TryLoad(string path)
    {
        try { return InkFileStore.Load(path); }
        catch { return null; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static SlideInkMigrationReport Skip(string message) =>
        new(false, null, 0, 0, message);

    private sealed record MappedSlide(
        int SlideId, int SlideIndex, IReadOnlyList<InkStroke> Strokes, string Source);

    private sealed class Manifest
    {
        public int Version { get; set; } = TargetManifestVersion;
        public int? LastSlideIndex { get; set; }
        public int? LastSlideId { get; set; }
        public int? LastShowPosition { get; set; }
        public List<ManifestSlide> Slides { get; set; } = new();
    }

    private sealed class ManifestSlide
    {
        public int SlideId { get; set; }
        public int SlideIndex { get; set; }
        public string File { get; set; }
    }
}