using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Ppt;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.P0.Tests;

/// <summary>
/// 「保存 manifest 丢掉未随本次 archive 一起提交的页条目」的数据丢失回归测试。
/// <para>
/// 现象：回填旧目录后 <c>InkCanvas-Ppt.json</c> 由 256/257/258 变成 256/257/259，
/// 258 条目消失（<c>slide-258.icink</c> 文件仍在），导致第 4 页批注在导航/加载时不可见。
/// 本测试用真实 <see cref="PptInkFileStore"/>：在临时目录构造 previous manifest（含 256/257/258
/// 与一条指向不存在文件的条目），再以仅含 256/257/259 的 archive 调用 <see cref="PptInkFileStore.Save"/>，
/// 断言合并规则：本次优先（同 SlideId 覆盖索引/文件）、仍有效的 previous 条目保留、
/// 文件已不存在的条目丢弃、结果按 SlideIndex 升序。
/// </para>
/// <para>临时目录在 <see cref="Dispose"/> 中清理；不触碰任何真实课件或真实存档。</para>
/// </summary>
public sealed class PptInkManifestMergeTests : IDisposable
{
    private const string DeckName = "deck.pptx";
    private const string ManifestName = "InkCanvas-Ppt.json";
    private const string SlideFilePattern = "slide-{0}.icink";

    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly PptInkFileStore _store;
    private readonly PptPresentationInfo _presentation;
    private readonly string _directory;
    private readonly string _manifestPath;

    public PptInkManifestMergeTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "inkcanvas-ppt-manifest-merge-" + Guid.NewGuid().ToString("N"));
        _store = new PptInkFileStore(Path.Combine(_root, "InkRoot"));
        _presentation = new PptPresentationInfo("manifest-merge-test", DeckName,
            Path.Combine(@"C:\Decks", DeckName), true, 4, 960, 540, "PowerPoint.Application");
        _directory = _store.GetPresentationDirectory(_presentation);
        _manifestPath = Path.Combine(_directory, ManifestName);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件仍被占用时保留临时目录，避免掩盖真实问题。
            _output.WriteLine($"临时目录未能清理（{_root}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public void Save_MergesPreviousManifestEntriesNotInCurrentArchive()
    {
        // 1) 目录与 256/257/258/259 四个真实 .icink 文件都在，previous manifest 只登记 256/257/258。
        Directory.CreateDirectory(_directory);
        foreach (int slideId in new[] { 256, 257, 258, 259 }) WriteSlideFile(slideId);
        WritePreviousManifest(new[]
        {
            Entry(256, 9, "slide-256.icink"),
            Entry(257, 2, "slide-257.icink"),
            Entry(258, 3, "slide-258.icink"),
            // 指向不存在文件的条目：本次既未提交、文件也不存在 ⇒ 必须被丢弃。
            Entry(999, 4, "slide-999.icink"),
        });

        // 2) 本次 archive 仅含 256/257/259（258 未随本次提交）。
        PptInkArchive archive = Archive(
            Stored(256, 1), Stored(257, 2), Stored(259, 3));

        _store.Save(archive);

        List<ManifestEntryView> slides = ReadManifestSlides();

        // 3) 258 条目必须被保留，并沿用 previous 的 File/SlideIndex。
        Assert.Contains(slides, s => s.SlideId == 258);
        ManifestEntryView retained258 = slides.Single(s => s.SlideId == 258);
        Assert.Equal(3, retained258.SlideIndex);
        Assert.Equal("slide-258.icink", retained258.File);

        // 4) 四条有效页都在，且不存在指向缺失文件的 999 条目。
        Assert.Equal(new[] { 256, 257, 258, 259 }, slides.Select(s => s.SlideId).OrderBy(id => id).ToArray());
        Assert.DoesNotContain(slides, s => s.SlideId == 999);
        Assert.DoesNotContain(slides, s => string.Equals(s.File, "slide-999.icink", StringComparison.OrdinalIgnoreCase));

        // 5) 结果按 SlideIndex 升序；258 与 259 同为 3 时本次的 259 排在 previous 的 258 之前。
        Assert.Equal(new[] { 256, 257, 259, 258 }, slides.Select(s => s.SlideId).ToArray());
        for (int i = 1; i < slides.Count; i++)
            Assert.True(slides[i - 1].SlideIndex <= slides[i].SlideIndex,
                $"合并结果必须按 SlideIndex 升序，但索引 {i - 1}={slides[i - 1].SlideIndex} 大于索引 {i}={slides[i].SlideIndex}。");

        // 6) manifest 顶层仍按本次 archive 写入：Version 不变，定位字段取本次值。
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_manifestPath));
        JsonElement root = document.RootElement;
        Assert.Equal(PptInkFileStore.CurrentManifestVersion, root.GetProperty("Version").GetInt32());
        Assert.Equal(2, root.GetProperty("LastSlideIndex").GetInt32());
        Assert.Equal(257, root.GetProperty("LastSlideId").GetInt32());
        Assert.Equal(1, root.GetProperty("LastShowPosition").GetInt32());

        // 7) 不删除任何 .icink：四个页文件仍在。
        foreach (int slideId in new[] { 256, 257, 258, 259 })
            Assert.True(File.Exists(SlideFilePath(slideId)),
                $"Save 不得删除任何 .icink 文件，但 slide-{slideId}.icink 已不存在。");

        _output.WriteLine($"目录：{_directory}");
        _output.WriteLine($"合并后 manifest 条目：{string.Join("，", slides.Select(s => $"{s.SlideId}@{s.SlideIndex}->{s.File}"))}");
    }

    [Fact]
    public void Save_DropsPreviousEntryWhoseFileNoLongerExists()
    {
        Directory.CreateDirectory(_directory);
        WriteSlideFile(256);
        WriteSlideFile(257);
        // 258 仅在 manifest 中登记，文件已不存在（例如被外部清理）。
        WritePreviousManifest(new[]
        {
            Entry(256, 1, "slide-256.icink"),
            Entry(257, 2, "slide-257.icink"),
            Entry(258, 3, "slide-258.icink"),
        });

        _store.Save(Archive(Stored(256, 1), Stored(257, 2)));

        List<ManifestEntryView> slides = ReadManifestSlides();
        Assert.DoesNotContain(slides, s => s.SlideId == 258);
        Assert.Equal(new[] { 256, 257 }, slides.Select(s => s.SlideId).ToArray());
        Assert.False(File.Exists(SlideFilePath(258)), "前置条件：slide-258.icink 本就不存在。");
        _output.WriteLine($"文件缺失条目被丢弃，保留：{string.Join("，", slides.Select(s => s.SlideId))}");
    }

    [Fact]
    public void Save_CurrentArchiveWinsOverPreviousSlideIndexForSameSlideId()
    {
        Directory.CreateDirectory(_directory);
        WriteSlideFile(256);
        WriteSlideFile(258);
        // previous 把 256 记为索引 9；本次提交 256=索引 1，必须以本次为准。
        WritePreviousManifest(new[]
        {
            Entry(256, 9, "slide-256.icink"),
            Entry(258, 3, "slide-258.icink"),
        });

        _store.Save(Archive(Stored(256, 1)));

        List<ManifestEntryView> slides = ReadManifestSlides();
        ManifestEntryView current256 = Assert.Single(slides, s => s.SlideId == 256);
        Assert.Equal(1, current256.SlideIndex);
        Assert.Equal("slide-256.icink", current256.File);
        // previous 的 258 仍被保留，说明覆盖只作用于同一 SlideId。
        Assert.Contains(slides, s => s.SlideId == 258 && s.SlideIndex == 3);
        _output.WriteLine($"256 索引由 previous 9 覆盖为本次 1；保留条目：{string.Join("，", slides.Select(s => $"{s.SlideId}@{s.SlideIndex}"))}");
    }

    private static PptInkArchive Archive(params PptStoredSlide[] slides) =>
        new(new PptPresentationInfo("manifest-merge-test", DeckName,
                Path.Combine(@"C:\Decks", DeckName), true, 4, 960, 540, "PowerPoint.Application"),
            2, 1, slides, false, 257, PptInkFileStore.CurrentManifestVersion);

    private static PptStoredSlide Stored(int slideId, int slideIndex) =>
        new(slideId, slideIndex, new[] { MarkerStroke(slideId) });

    private static object Entry(int slideId, int slideIndex, string file) =>
        new { SlideId = slideId, SlideIndex = slideIndex, File = file };

    private string SlideFilePath(int slideId) =>
        Path.Combine(_directory, string.Format(SlideFilePattern, slideId));

    private void WriteSlideFile(int slideId) => InkFileStore.Save(SlideFilePath(slideId), new[] { MarkerStroke(slideId) });

    /// <summary>
    /// 写出 previous manifest：字段名与 <see cref="PptInkFileStore"/> 内部模型一致，
    /// 确保它可以被真实 Save 反序列化后参与合并。
    /// </summary>
    private void WritePreviousManifest(object[] slides)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_manifestPath, JsonSerializer.Serialize(new
        {
            Version = PptInkFileStore.CurrentManifestVersion,
            LastSlideIndex = 1,
            LastSlideId = 256,
            LastShowPosition = 1,
            Slides = slides,
        }));
    }

    private List<ManifestEntryView> ReadManifestSlides()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_manifestPath));
        return document.RootElement.GetProperty("Slides").EnumerateArray()
            .Select(e => new ManifestEntryView(
                e.GetProperty("SlideId").GetInt32(),
                e.GetProperty("SlideIndex").GetInt32(),
                e.GetProperty("File").GetString()!))
            .ToList();
    }

    private sealed record ManifestEntryView(int SlideId, int SlideIndex, string File);

    /// <summary>生成一页「批注占位」：一笔两点，X 坐标即该页的 SlideID（页标识）。</summary>
    private static InkStroke MarkerStroke(int slideId)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Opaque(10, 20, 30),
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(slideId, 0f, 0.5f, 0f);
        stroke.AddPoint(slideId, 10f, 0.5f, 10f);
        stroke.Complete();
        return stroke;
    }
}