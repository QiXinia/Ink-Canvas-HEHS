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
/// 「课件插页/删页后旧批注在文件层不可见」的回归测试。
/// <para>
/// 存档目录名是 <c>{课件名}_{页数}</c>：插页后页数 3→4，应用按 4 计算目录名，
/// 而批注仍留在 <c>deck.pptx_3</c>，新目录不存在 ⇒ 文件层读不到旧批注。
/// 本测试用真实 <see cref="PptInkFileStore"/>：在临时目录构造旧目录
/// <c>deck.pptx_3</c>（<c>slide-256.icink</c> + manifest），再用页数 4 的课件信息
/// 调用 <see cref="PptInkFileStore.Load(PptPresentationInfo, out string)"/>，断言：
/// 新目录 <c>deck.pptx_4</c> 被创建、文件按原名原样复制、旧目录保持不变，
/// 且真实单页读取路径 <see cref="PptInkFileStore.TryLoadSlide"/> 能读到这笔批注。
/// </para>
/// <para>临时目录在 <see cref="Dispose"/> 中清理；不触碰任何真实课件或真实存档。</para>
/// </summary>
public sealed class PptInkLegacyDirectoryTests : IDisposable
{
    private const int SlideId = 256;
    private const string DeckName = "deck.pptx";
    private const string ManifestName = "InkCanvas-Ppt.json";
    private const string SlideFilePattern = "slide-{0}.icink";

    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public PptInkLegacyDirectoryTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "inkcanvas-ppt-legacy-dir-" + Guid.NewGuid().ToString("N"));
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
    public void SlideCountChanged_LoadCopiesLegacyDirectoryAndSingleSlideBecomesReadable()
    {
        var store = new PptInkFileStore(Path.Combine(_root, "InkRoot"));
        PptPresentationInfo before = Info(slideCount: 3);
        PptPresentationInfo after = Info(slideCount: 4);

        // 1) 用真实命名规则得到旧目录 deck.pptx_3，写入最小但真实的 .icink 与 manifest。
        string legacyDirectory = store.GetPresentationDirectory(before);
        Assert.EndsWith(DeckName + "_3", legacyDirectory, StringComparison.Ordinal);
        Directory.CreateDirectory(legacyDirectory);
        string legacySlide = Path.Combine(legacyDirectory, string.Format(SlideFilePattern, SlideId));
        InkFileStore.Save(legacySlide, new[] { MarkerStroke(SlideId) });
        byte[] expectedSlideBytes = File.ReadAllBytes(legacySlide);
        string legacyManifest = Path.Combine(legacyDirectory, ManifestName);
        File.WriteAllText(legacyManifest,
            $$"""{"Version":2,"LastSlideIndex":1,"LastSlideId":{{SlideId}},"Slides":[{"SlideId":{{SlideId}},"SlideIndex":1,"File":"slide-{{SlideId}}.icink"}]}""");
        byte[] expectedManifestBytes = File.ReadAllBytes(legacyManifest);

        // 2) 前置条件：按页数 4 算出的新目录尚不存在。
        string currentDirectory = store.GetPresentationDirectory(after);
        Assert.EndsWith(DeckName + "_4", currentDirectory, StringComparison.Ordinal);
        Assert.False(Directory.Exists(currentDirectory), "前置条件：页数变化后的新目录 deck.pptx_4 不应存在。");

        // 3) 真实 Load：应从旧目录回填，且给出可诊断的成功原因。
        PptInkArchive archive = store.Load(after, out string diagnostic);
        Assert.NotNull(archive);
        Assert.NotNull(diagnostic);
        Assert.Contains("旧墨迹目录", diagnostic, StringComparison.Ordinal);

        // 3b) 首次 Load 的返回值必须已包含回填的批注（缩略图/导航无需等 TryLoadSlide 或下一次 Load）。
        PptStoredSlide restored = Assert.Single(archive.Slides);
        Assert.Equal(SlideId, restored.SlideId);
        Assert.Equal(1, restored.SlideIndex);
        Assert.NotEmpty(restored.Strokes);
        Assert.NotEmpty(restored.Strokes[0].Points);
        Assert.Equal((float)SlideId, restored.Strokes[0].Points[0].X);

        // 4) 新目录被创建，slide-256.icink 按文件名原样复制且内容完全一致。
        Assert.True(Directory.Exists(currentDirectory), "Load 后应创建当前页数对应的目录 deck.pptx_4。");
        string copiedSlide = Path.Combine(currentDirectory, string.Format(SlideFilePattern, SlideId));
        Assert.True(File.Exists(copiedSlide), $"旧目录的 slide-{SlideId}.icink 应被复制到 {currentDirectory}。");
        Assert.Equal(expectedSlideBytes, File.ReadAllBytes(copiedSlide));

        // 5) manifest 也一并复制，内容保持原样（便于后续按 SlideId 解析）。
        string copiedManifest = Path.Combine(currentDirectory, ManifestName);
        Assert.True(File.Exists(copiedManifest), "manifest 应被复制到当前目录。");
        Assert.Equal(expectedManifestBytes, File.ReadAllBytes(copiedManifest));

        // 6) 旧目录不修改、不删除：目录与文件都仍在，内容字节不变。
        Assert.True(Directory.Exists(legacyDirectory), "旧存档目录 deck.pptx_3 必须保留。");
        Assert.True(File.Exists(legacySlide), "旧存档文件必须保留。");
        Assert.Equal(expectedSlideBytes, File.ReadAllBytes(legacySlide));
        Assert.Equal(expectedManifestBytes, File.ReadAllBytes(legacyManifest));

        // 7) 真实单页读取路径（PptInkSession.ResolveSlideInk / 邻页预取使用）能读到这笔批注。
        IReadOnlyList<InkStroke> loaded = store.TryLoadSlide(after, SlideId);
        Assert.NotNull(loaded);
        Assert.NotEmpty(loaded);
        Assert.NotEmpty(loaded[0].Points);
        Assert.Equal((float)SlideId, loaded[0].Points[0].X);

        // 7b) Load 返回的 Slides 与 TryLoadSlide 读到的是同一笔内容（笔数、点数与点坐标一致）。
        Assert.Equal(restored.Strokes.Count, loaded.Count);
        Assert.Equal(restored.Strokes[0].Points.Count, loaded[0].Points.Count);
        Assert.Equal(restored.Strokes[0].Points[0].X, loaded[0].Points[0].X);
        Assert.Equal(restored.Strokes[0].Points[1].X, loaded[0].Points[1].X);

        _output.WriteLine($"回填诊断：{diagnostic}");
        _output.WriteLine($"旧目录：{legacyDirectory}（保留 {expectedSlideBytes.Length} 字节 .icink）");
        _output.WriteLine($"当前目录：{currentDirectory}（slide-{SlideId}.icink {expectedSlideBytes.Length} 字节，内容一致）");
        _output.WriteLine($"Load 返回 Slides={archive.Slides.Count}，SlideId={restored.SlideId}，SlideIndex={restored.SlideIndex}。");
        _output.WriteLine($"TryLoadSlide({SlideId}) 读到 {loaded.Count} 笔，首点 X={loaded[0].Points[0].X}（页标识）。");
    }

[Fact]
    public void ExistingCurrentManifest_MergesLegacyEntriesAndKeepsTargetValues()
    {
        var store = new PptInkFileStore(Path.Combine(_root, "InkRoot"));
        PptPresentationInfo before = Info(slideCount: 3);
        PptPresentationInfo after = Info(slideCount: 4);

        // 1) 旧目录 deck.pptx_3：manifest 含 256/257/258，三个 .icink 都在位。
        //    256/257 的索引故意与目标不同，用于验证「同 SlideId 以目标为准」。
        string legacyDirectory = store.GetPresentationDirectory(before);
        Assert.EndsWith(DeckName + "_3", legacyDirectory, StringComparison.Ordinal);
        Directory.CreateDirectory(legacyDirectory);
        foreach (int slideId in new[] { 256, 257, 258 }) WriteSlide(legacyDirectory, slideId);
        File.WriteAllText(Path.Combine(legacyDirectory, ManifestName),
            """{"Version":1,"LastSlideIndex":3,"LastSlideId":258,"LastShowPosition":3,"Slides":[{"SlideId":256,"SlideIndex":2,"File":"slide-256.icink"},{"SlideId":257,"SlideIndex":3,"File":"slide-257.icink"},{"SlideId":258,"SlideIndex":4,"File":"slide-258.icink"}]}""");

        // 2) 当前目录 deck.pptx_4 已存在且已有 manifest：放映时先写入 256/257/259（当前页），
        //    258 尚未登记，但第 4 页的批注仍只在旧目录中。
        string currentDirectory = store.GetPresentationDirectory(after);
        Assert.EndsWith(DeckName + "_4", currentDirectory, StringComparison.Ordinal);
        Directory.CreateDirectory(currentDirectory);
        foreach (int slideId in new[] { 256, 257, 259 }) WriteSlide(currentDirectory, slideId);
        string currentManifest = Path.Combine(currentDirectory, ManifestName);
        File.WriteAllText(currentManifest,
            """{"Version":2,"LastSlideIndex":3,"LastSlideId":259,"LastShowPosition":1,"Slides":[{"SlideId":256,"SlideIndex":1,"File":"slide-256.icink"},{"SlideId":257,"SlideIndex":2,"File":"slide-257.icink"},{"SlideId":259,"SlideIndex":3,"File":"slide-259.icink"}]}""");

        // 3) 真实 Load：目标目录已有 manifest，必须只做「补齐合并」，不得丢弃目标条目。
        PptInkArchive archive = store.Load(after, out string diagnostic);
        Assert.NotNull(archive);
        Assert.NotNull(diagnostic);
        Assert.Contains("旧墨迹目录", diagnostic, StringComparison.Ordinal);

        // 3b) 旧目录第 4 页的 .icink 已复制，Load 返回值即可看到 258 的批注。
        Assert.True(File.Exists(Path.Combine(currentDirectory, string.Format(SlideFilePattern, 258))),
            "旧目录的 slide-258.icink 应被复制到 deck.pptx_4。");
        Assert.Contains(archive.Slides, s => s.SlideId == 258 && s.SlideIndex == 4 && s.Strokes.Count > 0);

        // 4) 合并后的 manifest 含 256/257/258/259，并按 SlideIndex 升序写回。
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(currentManifest));
        JsonElement root = document.RootElement;
        List<(int SlideId, int SlideIndex, string File)> slides = root.GetProperty("Slides").EnumerateArray()
            .Select(e => (e.GetProperty("SlideId").GetInt32(), e.GetProperty("SlideIndex").GetInt32(),
                e.GetProperty("File").GetString()!))
            .ToList();
        Assert.Equal(new[] { 256, 257, 259, 258 }, slides.Select(s => s.SlideId).ToArray());

        // 4b) 258 的 File/SlideIndex 沿用旧 manifest 的登记值。
        (int SlideId, int SlideIndex, string File) merged258 = Assert.Single(slides, s => s.SlideId == 258);
        Assert.Equal(4, merged258.SlideIndex);
        Assert.Equal("slide-258.icink", merged258.File);

        // 4c) 同 SlideId 以目标为准：256/257 仍是目标的索引（1/2），未被旧 manifest 的 2/3 覆盖。
        Assert.Equal(1, Assert.Single(slides, s => s.SlideId == 256).SlideIndex);
        Assert.Equal(2, Assert.Single(slides, s => s.SlideId == 257).SlideIndex);

        // 5) 顶层字段保留目标值：Version/Last* 取 deck.pptx_4 的 manifest。
        Assert.Equal(2, root.GetProperty("Version").GetInt32());
        Assert.Equal(3, root.GetProperty("LastSlideIndex").GetInt32());
        Assert.Equal(259, root.GetProperty("LastSlideId").GetInt32());
        Assert.Equal(1, root.GetProperty("LastShowPosition").GetInt32());

        // 6) 旧目录保留且内容不变（回填只写当前目录）。
        Assert.True(Directory.Exists(legacyDirectory), "旧存档目录 deck.pptx_3 必须保留。");
        Assert.Equal(3, Directory.GetFiles(legacyDirectory, "*" + InkFileStore.Extension).Length);

        _output.WriteLine($"合并诊断：{diagnostic}");
        _output.WriteLine(
            $"合并后 manifest 条目：{string.Join("，", slides.Select(s => $"{s.SlideId}@{s.SlideIndex}->{s.File}"))}");
        _output.WriteLine($"Version={root.GetProperty("Version").GetInt32()}，LastSlideId={root.GetProperty("LastSlideId").GetInt32()}");
    }

    /// <summary>
    /// 合并后索引冲突消解：目标 256@1、257@2、259@3，旧 manifest 256@1、257@2、258@3，
    /// 补入的 258 与目标的 259 索引重复 ⇒ 258 应被改到「已用最大索引 + 1」= 4，索引唯一。
    /// </summary>
    [Fact]
    public void ExistingCurrentManifest_ConflictingLegacyIndex_RemapsAddedSlideIndex()
    {
        var store = new PptInkFileStore(Path.Combine(_root, "InkRoot"));
        PptPresentationInfo before = Info(slideCount: 3);
        PptPresentationInfo after = Info(slideCount: 4);

        // 1) 旧目录 deck.pptx_3：manifest 含 256@1、257@2、258@3（删页前的旧页序）。
        string legacyDirectory = store.GetPresentationDirectory(before);
        Directory.CreateDirectory(legacyDirectory);
        foreach (int slideId in new[] { 256, 257, 258 }) WriteSlide(legacyDirectory, slideId);
        File.WriteAllText(Path.Combine(legacyDirectory, ManifestName),
            """{"Version":1,"LastSlideIndex":3,"LastSlideId":258,"LastShowPosition":3,"Slides":[{"SlideId":256,"SlideIndex":1,"File":"slide-256.icink"},{"SlideId":257,"SlideIndex":2,"File":"slide-257.icink"},{"SlideId":258,"SlideIndex":3,"File":"slide-258.icink"}]}""");

        // 2) 当前目录 deck.pptx_4：插页后 manifest 为 256@1、257@2、259@3。
        //    补入的 258 仍是旧索引 3，与目标的 259@3 冲突，必须重映射为唯一索引。
        string currentDirectory = store.GetPresentationDirectory(after);
        Directory.CreateDirectory(currentDirectory);
        foreach (int slideId in new[] { 256, 257, 259 }) WriteSlide(currentDirectory, slideId);
        string currentManifest = Path.Combine(currentDirectory, ManifestName);
        File.WriteAllText(currentManifest,
            """{"Version":2,"LastSlideIndex":3,"LastSlideId":259,"LastShowPosition":1,"Slides":[{"SlideId":256,"SlideIndex":1,"File":"slide-256.icink"},{"SlideId":257,"SlideIndex":2,"File":"slide-257.icink"},{"SlideId":259,"SlideIndex":3,"File":"slide-259.icink"}]}""");

        PptInkArchive archive = store.Load(after, out string diagnostic);
        Assert.NotNull(archive);
        Assert.NotNull(diagnostic);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(currentManifest));
        JsonElement root = document.RootElement;
        List<(int SlideId, int SlideIndex, string File)> slides = root.GetProperty("Slides").EnumerateArray()
            .Select(e => (e.GetProperty("SlideId").GetInt32(), e.GetProperty("SlideIndex").GetInt32(),
                e.GetProperty("File").GetString()!))
            .ToList();

        // 3) 4 条条目、4 个索引互不重复，并按 SlideIndex 升序写回。
        Assert.Equal(new[] { 256, 257, 259, 258 }, slides.Select(s => s.SlideId).ToArray());
        Assert.Equal(4, slides.Select(s => s.SlideIndex).Distinct().Count());
        Assert.Equal(slides.Select(s => s.SlideIndex).OrderBy(i => i).ToArray(),
            slides.Select(s => s.SlideIndex).ToArray());

        // 4) 目标条目的索引保持不变。
        Assert.Equal(1, Assert.Single(slides, s => s.SlideId == 256).SlideIndex);
        Assert.Equal(2, Assert.Single(slides, s => s.SlideId == 257).SlideIndex);
        Assert.Equal(3, Assert.Single(slides, s => s.SlideId == 259).SlideIndex);

        // 5) 补入的 258 被改到「已用最大索引 + 1」= 4（大于 259 的索引），文件不变。
        (int SlideId, int SlideIndex, string File) merged258 = Assert.Single(slides, s => s.SlideId == 258);
        Assert.Equal(4, merged258.SlideIndex);
        Assert.True(merged258.SlideIndex > Assert.Single(slides, s => s.SlideId == 259).SlideIndex,
            "补入条目重映射后的索引必须大于目标条目的最大索引。");
        Assert.Equal("slide-258.icink", merged258.File);
        // 重读的存档也应按消解后的索引返回 258。
        Assert.Contains(archive.Slides, s => s.SlideId == 258 && s.SlideIndex == 4 && s.Strokes.Count > 0);

        // 6) 顶层字段保留目标值：Version/Last* 语义不变。
        Assert.Equal(2, root.GetProperty("Version").GetInt32());
        Assert.Equal(3, root.GetProperty("LastSlideIndex").GetInt32());
        Assert.Equal(259, root.GetProperty("LastSlideId").GetInt32());
        Assert.Equal(1, root.GetProperty("LastShowPosition").GetInt32());

        _output.WriteLine($"合并诊断：{diagnostic}");
        _output.WriteLine(
            $"合并后 manifest 条目：{string.Join("，", slides.Select(s => $"{s.SlideId}@{s.SlideIndex}->{s.File}"))}");
        _output.WriteLine($"索引互不重复：{slides.Select(s => s.SlideIndex).Distinct().Count()} 个 → 258@{merged258.SlideIndex}");
    }

    [Fact]
    public void NoCurrentOrLegacyDirectory_LoadReturnsEmptySlides()
    {
        // 确认构造确实没有旧目录可回填：存档根目录尚未创建，且没有任何同课件名的兄弟目录。
        var store = new PptInkFileStore(Path.Combine(_root, "InkRoot"));
        PptPresentationInfo presentation = Info(slideCount: 4);
        string currentDirectory = store.GetPresentationDirectory(presentation);
        Assert.False(Directory.Exists(currentDirectory));
        Assert.False(Directory.Exists(Path.GetDirectoryName(currentDirectory)),
            "前置条件：存档根目录下不存在任何可回填的旧墨迹目录。");

        PptInkArchive archive = store.Load(presentation, out string diagnostic);

        Assert.NotNull(archive);
        Assert.Empty(archive.Slides);
        Assert.Null(archive.LastSlideId);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic));
        _output.WriteLine($"无旧目录回填诊断：{diagnostic}");
    }

    [Fact]
    public void CurrentDirectoryWithPayload_IsNotTouchedByLegacyRestore()
    {
        var store = new PptInkFileStore(Path.Combine(_root, "InkRoot"));
        PptPresentationInfo before = Info(slideCount: 3);
        PptPresentationInfo after = Info(slideCount: 4);

        string legacyDirectory = store.GetPresentationDirectory(before);
        Directory.CreateDirectory(legacyDirectory);
        InkFileStore.Save(Path.Combine(legacyDirectory, string.Format(SlideFilePattern, SlideId)),
            new[] { MarkerStroke(SlideId) });
        File.WriteAllText(Path.Combine(legacyDirectory, ManifestName),
            $$"""{"Version":2,"LastSlideIndex":1,"LastSlideId":{{SlideId}},"Slides":[{"SlideId":{{SlideId}},"SlideIndex":1,"File":"slide-{{SlideId}}.icink"}]}""");

        // 当前目录已有自己的 .icink（页号命名旧布局），属于「可读数据」 ⇒ 不触发回填。
        string currentDirectory = store.GetPresentationDirectory(after);
        Directory.CreateDirectory(currentDirectory);
        string currentSlide = Path.Combine(currentDirectory, "slide-300.icink");
        InkFileStore.Save(currentSlide, new[] { MarkerStroke(300) });
        byte[] currentBytes = File.ReadAllBytes(currentSlide);

        PptInkArchive archive = store.Load(after, out string diagnostic);

        // 未触发回填：不产生诊断，不复制旧目录 manifest，也不覆盖/删除当前目录已有文件。
        Assert.Null(diagnostic);
        Assert.False(File.Exists(Path.Combine(currentDirectory, ManifestName)));
        Assert.Equal(currentBytes, File.ReadAllBytes(currentSlide));
        Assert.DoesNotContain(archive.Slides, s => s.SlideId == SlideId);
        PptStoredSlide current = Assert.Single(archive.Slides);
        Assert.Equal(0, current.SlideId);
        Assert.Equal(300, current.SlideIndex);
        _output.WriteLine($"未回填（当前目录已有墨迹数据）诊断：{diagnostic ?? "<null>"}");
    }

    /// <summary>在指定目录写出某页的真实 .icink（一笔两点，X 坐标即 SlideID）。</summary>
    private static void WriteSlide(string directory, int slideId) =>
        InkFileStore.Save(Path.Combine(directory, string.Format(SlideFilePattern, slideId)),
            new[] { MarkerStroke(slideId) });

    private static PptPresentationInfo Info(int slideCount) =>
        new("legacy-directory-test", DeckName, Path.Combine(@"C:\Decks", DeckName), true,
            slideCount, 960, 540, "PowerPoint.Application");

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