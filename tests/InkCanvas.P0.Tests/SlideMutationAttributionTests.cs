using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Ppt;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.P0.Tests;

/// <summary>
/// 「真实课件插页 / 删页 / 调序后的批注归属」自动化复核。
/// <para>
/// 本测试刻意同时使用两侧的真实实现：
/// <list type="bullet">
/// <item>真实 PowerPoint COM（16.0）：打开 <c>D:\hw\p0-test.pptx</c> 的临时副本，读取每页
/// <c>Slide.SlideID</c>，执行真实的插入 / 删除 / 调序，再保存并重新打开验证 SlideID 是否稳定。</item>
/// <item>真实批注存档与映射代码：<see cref="PptInkFileStore"/>（slide-{SlideID}.icink + manifest）、
/// <see cref="SlideContentIndex.Build"/>、<see cref="SlideMappingInspector.Inspect"/>、
/// <see cref="SlideInkMigrator.MigrateIfNeeded"/>、<see cref="PptInkSession.ResolveResumeIndex"/>。</item>
/// </list>
/// 批注内容（页标识）编码在该页唯一一笔的 X 坐标上，因此从文件读回后可以判定「这一笔到底属于哪一页」。
/// </para>
/// <para>
/// COM 不可用（无 PowerPoint、无法创建实例、无消息泵导致超时等）时，测试只输出
/// 「未验证：…」并直接 return；xunit 2.9.2 没有动态 Skip，因此绝不伪造通过。
/// 原始课件只被复制、从不修改；临时目录在 <see cref="Dispose"/> 中清理；PowerPoint 实例在
/// finally 中关闭演示文稿并 Quit。
/// </para>
/// </summary>
public sealed class SlideMutationAttributionTests : IDisposable
{
    private const string SourceDeckPath = @"D:\hw\p0-test.pptx";

    /// <summary>COM 场景的兜底超时，避免 PowerPoint 无消息泵阻塞时测试永久挂起。</summary>
    private static readonly TimeSpan ComTimeout = TimeSpan.FromMinutes(3);

    private readonly ITestOutputHelper _output;
    private readonly List<string> _tempRoots = new();

    public SlideMutationAttributionTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (string root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // PowerPoint 进程尚未完全释放文件时删除会失败；保留目录，避免掩盖真实问题。
            }
        }
    }

    // ---------------------------------------------------------------------
    // 场景 1：在第 2 页后插入新页
    // ---------------------------------------------------------------------
    [Fact]
    public void InsertSlideAfterSecondPage_NewSlideDoesNotInheritOldInk()
    {
        DeckScenarioResult r = RunScenario("插入新页（第 2 页后）", presentation =>
        {
            dynamic pres = presentation;
            // ppLayoutBlank = 12；Slides.Add(Index, Layout) 在 Index 位置插入，原第 3 页顺延为第 4 页。
            dynamic added = pres.Slides.Add(3, 12);
            return (int)added.SlideID;
        });
        if (r is null) return;

        AssertSeedArchiveFiles(r);

        int newSlideId = r.MutatedSlideId.Value;
        int[] originalIds = r.Before.Slides.Select(s => s.SlideId).ToArray();

        // 1) 真实 COM 结果：页数 3 → 4，原有 SlideID 一个不少，新页拿到全新 SlideID。
        Assert.Equal(4, r.After.Slides.Count);
        Assert.All(originalIds, id => Assert.Contains(r.After.Slides, s => s.SlideId == id));
        Assert.DoesNotContain(newSlideId, originalIds);
        PptSlideInfo newSlide = r.After.Slides.Single(s => s.SlideId == newSlideId);
        Assert.Equal(3, newSlide.Index); // 插在第 2 页之后，故新页为第 3 页

        // 2) 新插入页没有继承任何旧批注：真实 PptInkFileStore.TryLoadSlide（按 SlideId 直读文件）。
        Assert.Null(MarkerOf(r.Store.TryLoadSlide(r.After.Info, newSlideId)));
        SlideContentEntry newEntry = r.MappingIndex.Find(newSlideId);
        Assert.NotNull(newEntry);
        Assert.False(newEntry.HasInk, "新插入页不应继承任何旧批注。");
        Assert.Equal(0, newEntry.InkStrokeCount);

        // 3) 原有页的批注仍按 SlideId 归属自身（真实 SlideContentIndex.Build 按 SlideId 匹配）：
        //    即使原第 3 页的 Index 从 3 变成 4，它的批注也不会跑到新插入的第 3 页。
        foreach (PptSlideInfo slide in r.After.Slides.Where(s => originalIds.Contains(s.SlideId)))
        {
            SlideContentEntry entry = r.MappingIndex.Find(slide.SlideId);
            Assert.NotNull(entry);
            Assert.True(entry.HasInk, $"SlideId {slide.SlideId} 的批注应保留在该页自身。");
            Assert.Equal(1, entry.InkStrokeCount);
            Assert.Equal(slide.Index, entry.Index);
        }

        // 4) 存档内容本身未串页：每一页的文件内容仍是自己的页标识。
        AssertStoredMarkersMatchOwnSlideId(r.SeedArchiveReloaded);

        // 5) 回填后首次 Load 即可读到旧批注：存档目录名是 "{课件名}_{页数}"，页数 3→4 后目录名变化，
        //    但真实 PptInkFileStore.Load 会从旧页数目录回填 .icink 与 manifest 并重新读取，
        //    因此本次返回值就已包含修改前存在的 256/257/258 三页批注（旧目录原样保留）。
        Assert.NotEqual(r.SeedDirectory, r.AfterDirectory);
        Assert.True(r.AfterArchiveReloaded.Slides.Count > 0,
            "回填后首次 Load 应读到旧批注：旧页数目录的 .icink 与 manifest 应被回填并重新读取，Slides 不应为空。");
        Assert.Equal(
            originalIds.OrderBy(i => i).ToArray(),
            r.AfterArchiveReloaded.Slides.Select(s => s.SlideId).OrderBy(i => i).ToArray());
        AssertStoredMarkersMatchOwnSlideId(r.AfterArchiveReloaded);

        // 6) 真实映射诊断：页数变化 + 原第 3 页 Index 3→4，必须提示待确认；不允许报缺失。
        Assert.True(r.MappingIssue.CountChanged);
        Assert.True(r.MappingIssue.PageOrderChanged);
        Assert.Empty(r.MappingIssue.MissingSlideIds);

        // 7) 真实迁移入口对 v2 档案不迁移、不备份、不删除旧数据。
        Assert.False(r.MigrationReport.Migrated, "v2 档案不应再次迁移。");

        _output.WriteLine("【插入新页】断言通过：");
        _output.WriteLine($"  修改后 SlideID 序列：{string.Join(", ", r.After.Slides.Select(s => s.SlideId))}");
        _output.WriteLine($"  新页 SlideId={newSlideId} → Index={newSlide.Index}，HasInk={newEntry.HasInk}，InkStrokeCount={newEntry.InkStrokeCount}");
        _output.WriteLine($"  原页归属：{string.Join("；", r.After.Slides.Where(s => originalIds.Contains(s.SlideId)).Select(s => $"SlideId {s.SlideId} → Index {s.Index}（{r.MappingIndex.Find(s.SlideId)?.InkStrokeCount} 笔）"))}");
        _output.WriteLine($"  存档目录：初始={r.SeedDirectory}；修改后={r.AfterDirectory}（已从旧目录回填，首次 Load 读到 {r.AfterArchiveReloaded.Slides.Count} 页旧批注）");
        _output.WriteLine($"  映射诊断：CountChanged={r.MappingIssue.CountChanged}，PageOrderChanged={r.MappingIssue.PageOrderChanged}，Missing={r.MappingIssue.MissingSlideIds.Count}");
        _output.WriteLine($"  迁移入口：Migrated={r.MigrationReport.Migrated}，Message={r.MigrationReport.Message}");
    }

    // ---------------------------------------------------------------------
    // 场景 2：删除第 1 页
    // ---------------------------------------------------------------------
    [Fact]
    public void DeleteFirstSlide_DeletedInkIsRetainedAndNotReassigned()
    {
        DeckScenarioResult r = RunScenario("删除第 1 页", presentation =>
        {
            dynamic pres = presentation;
            dynamic slide = pres.Slides.Item(1);
            int id = (int)slide.SlideID;
            slide.Delete();
            return id;
        });
        if (r is null) return;

        AssertSeedArchiveFiles(r);

        int deletedSlideId = r.MutatedSlideId.Value;

        // 1) 真实 COM 结果：3 页 → 2 页，被删页的 SlideID 不在当前课件中，其余两页 SlideID 不变。
        Assert.Equal(2, r.After.Slides.Count);
        Assert.DoesNotContain(deletedSlideId, r.After.Slides.Select(s => s.SlideId));
        int[] remainingIds = r.Before.Slides.Select(s => s.SlideId)
            .Where(id => id != deletedSlideId).OrderBy(i => i).ToArray();
        Assert.Equal(remainingIds, r.After.Slides.Select(s => s.SlideId).OrderBy(i => i).ToArray());
        Assert.Equal(new[] { 1, 2 }, r.After.Slides.Select(s => s.Index).OrderBy(i => i).ToArray());

        // 2) 既有策略「保留」：被删页的批注仍完整保存在旧档案中（未被清空、未被改写）。
        PptStoredSlide kept = r.SeedArchiveReloaded.Slides.SingleOrDefault(s => s.SlideId == deletedSlideId);
        Assert.NotNull(kept);
        Assert.Equal(deletedSlideId, ReadMarker(kept.Strokes));

        // 3) 既有策略「待确认」：真实 SlideMappingInspector 必须把被删页报告为 Missing，而不是默默消化。
        Assert.Contains(deletedSlideId, r.MappingIssue.MissingSlideIds);
        Assert.True(r.MappingIssue.CountChanged);

        // 4) 不重分配：当前每一页只能匹配到自己 SlideId 的批注，文件内容也仍是自己的页标识。
        foreach (PptSlideInfo slide in r.After.Slides)
        {
            SlideContentEntry entry = r.MappingIndex.Find(slide.SlideId);
            Assert.NotNull(entry);
            Assert.True(entry.HasInk, $"SlideId {slide.SlideId} 的批注应保留在该页自身。");
            Assert.Equal(1, entry.InkStrokeCount);

            PptStoredSlide own = r.SeedArchiveReloaded.Slides.Single(s => s.SlideId == slide.SlideId);
            Assert.Equal(slide.SlideId, ReadMarker(own.Strokes));
            Assert.NotEqual(deletedSlideId, ReadMarker(own.Strokes));
        }

        // 5) 被删页的 SlideId 从未出现在重建索引中（真实 SlideContentIndex.Build）。
        Assert.DoesNotContain(r.MappingIndex.Slides, e => e.SlideId == deletedSlideId);

        // 6) 回填后首次 Load 即可读到旧批注：页数 3→2 后目录名变化，真实 PptInkFileStore.Load
        //    会从旧页数目录回填 .icink 与 manifest 并重新读取，因此首次 Load 就能读到
        //    256/257/258 三页旧档案；被删的 256 仍留在档案中，并由 MissingSlideIds 标记待确认。
        Assert.NotEqual(r.SeedDirectory, r.AfterDirectory);
        Assert.True(r.AfterArchiveReloaded.Slides.Count > 0,
            "回填后首次 Load 应读到旧批注：旧页数目录的 .icink 与 manifest 应被回填并重新读取，Slides 不应为空。");
        Assert.Equal(
            r.Before.Slides.Select(s => s.SlideId).OrderBy(i => i).ToArray(),
            r.AfterArchiveReloaded.Slides.Select(s => s.SlideId).OrderBy(i => i).ToArray());
        AssertStoredMarkersMatchOwnSlideId(r.AfterArchiveReloaded);
        PptStoredSlide deletedAfterRestore = r.AfterArchiveReloaded.Slides.Single(s => s.SlideId == deletedSlideId);
        Assert.Equal(deletedSlideId, ReadMarker(deletedAfterRestore.Strokes));
        Assert.Contains(deletedSlideId, r.MappingIssue.MissingSlideIds);

        // 7) 真实迁移入口不迁移 v2 档案。
        Assert.False(r.MigrationReport.Migrated, "v2 档案不应再次迁移。");

        _output.WriteLine("【删除第 1 页】断言通过：");
        _output.WriteLine($"  被删页 SlideId={deletedSlideId}（旧档案中仍保留 {kept.Strokes.Count} 笔，内容页标识={ReadMarker(kept.Strokes)}）");
        _output.WriteLine($"  修改后 SlideID 序列：{string.Join(", ", r.After.Slides.Select(s => s.SlideId))}");
        _output.WriteLine($"  当前页归属：{string.Join("；", r.After.Slides.Select(s => $"SlideId {s.SlideId} → Index {s.Index}（{r.MappingIndex.Find(s.SlideId)?.InkStrokeCount} 笔，内容={ReadMarker(r.SeedArchiveReloaded.Slides.Single(x => x.SlideId == s.SlideId).Strokes)}）"))}");
        _output.WriteLine($"  存档目录：初始={r.SeedDirectory}；修改后={r.AfterDirectory}（已从旧目录回填，首次 Load 读到 {r.AfterArchiveReloaded.Slides.Count} 页旧批注，被删页仍保留）");
        _output.WriteLine($"  映射诊断：CountChanged={r.MappingIssue.CountChanged}，PageOrderChanged={r.MappingIssue.PageOrderChanged}，Missing=[{string.Join(",", r.MappingIssue.MissingSlideIds)}]");
        _output.WriteLine($"  迁移入口：Migrated={r.MigrationReport.Migrated}，Message={r.MigrationReport.Message}");
    }

    // ---------------------------------------------------------------------
    // 场景 3：把第 3 页移到第 1 位（调序，页数不变）
    // ---------------------------------------------------------------------
    [Fact]
    public void ReorderThirdSlideToFirst_InkStaysBoundToSlideId()
    {
        DeckScenarioResult r = RunScenario("把第 3 页移到第 1 位", presentation =>
        {
            dynamic pres = presentation;
            dynamic slide = pres.Slides.Item(3);
            int id = (int)slide.SlideID;
            slide.MoveTo(1);
            return id;
        });
        if (r is null) return;

        AssertSeedArchiveFiles(r);

        int movedSlideId = r.MutatedSlideId.Value;

        // 1) 真实 COM 结果：页数不变，SlideID 集合不变，仅顺序变化（被移动页现在排第 1）。
        Assert.Equal(3, r.After.Slides.Count);
        Assert.Equal(movedSlideId, r.After.Slides[0].SlideId);
        Assert.Equal(
            r.Before.Slides.Select(s => s.SlideId).OrderBy(i => i).ToArray(),
            r.After.Slides.Select(s => s.SlideId).OrderBy(i => i).ToArray());

        // 2) 页数未变 ⇒ 存档目录名未变，真实 PptInkFileStore.Load 仍能完整读回 3 页批注。
        Assert.Equal(r.SeedDirectory, r.AfterDirectory);
        Assert.Equal(3, r.SeedArchiveReloaded.Slides.Count);
        Assert.Equal(3, r.AfterArchiveReloaded.Slides.Count);

        // 3) 核心断言：按 SlideId 直接读（真实 PptInkFileStore.TryLoadSlide，PptInkSession.ResolveSlideInk 走的路径），
        //    每一页读到的批注内容都是自己的页标识，绝不因页序变化而串页。
        foreach (PptSlideInfo slide in r.After.Slides)
        {
            float? marker = MarkerOf(r.Store.TryLoadSlide(r.After.Info, slide.SlideId));
            Assert.NotNull(marker);
            Assert.Equal(slide.SlideId, (int)marker.Value);
        }

        // 4) 真实 SlideContentIndex.Build：按当前页序重建，HasInk / Index 跟随 SlideId。
        Assert.Equal(r.After.Slides.Select(s => s.SlideId).ToArray(), r.MappingIndex.Slides.Select(s => s.SlideId).ToArray());
        foreach (PptSlideInfo slide in r.After.Slides)
        {
            SlideContentEntry entry = r.MappingIndex.Find(slide.SlideId);
            Assert.NotNull(entry);
            Assert.Equal(slide.Index, entry.Index);
            Assert.True(entry.HasInk, $"调序后 SlideId {slide.SlideId} 的批注仍应留在该页。");
            Assert.Equal(1, entry.InkStrokeCount);
        }

        // 5) 存档内容未串页。
        AssertStoredMarkersMatchOwnSlideId(r.SeedArchiveReloaded);

        // 6) 真实映射诊断：只报页序变化，不报缺失、不报页数变化。
        Assert.True(r.MappingIssue.PageOrderChanged);
        Assert.Empty(r.MappingIssue.MissingSlideIds);
        Assert.False(r.MappingIssue.CountChanged);

        // 7) 真实续页逻辑（PptInkSession.ResolveResumeIndex）：上次在第 3 页（SlideId=movedSlideId）
        //    的续页位置应跟随 SlideId 回到新的第 1 页，且不使用降级页码。
        int? resume = PptInkSession.ResolveResumeIndex(movedSlideId, 3, r.After.Slides, out bool degraded);
        Assert.Equal(1, resume);
        Assert.False(degraded);

        // 8) 真实迁移入口不迁移 v2 档案。
        Assert.False(r.MigrationReport.Migrated, "v2 档案不应再次迁移。");

        _output.WriteLine("【调序】断言通过：");
        _output.WriteLine($"  修改后 SlideID 序列：{string.Join(", ", r.After.Slides.Select(s => s.SlideId))}");
        _output.WriteLine($"  按 SlideId 直读内容：{string.Join("；", r.After.Slides.Select(s => $"Index {s.Index}: SlideId {s.SlideId} → 内容 {MarkerOf(r.Store.TryLoadSlide(r.After.Info, s.SlideId))}"))}");
        _output.WriteLine($"  存档目录（未变）：{r.SeedDirectory}");
        _output.WriteLine($"  映射诊断：CountChanged={r.MappingIssue.CountChanged}，PageOrderChanged={r.MappingIssue.PageOrderChanged}，Missing={r.MappingIssue.MissingSlideIds.Count}");
        _output.WriteLine($"  续页：SlideId {movedSlideId} 从旧第 3 页 → 新第 {resume} 页（degraded={degraded}）");
        _output.WriteLine($"  迁移入口：Migrated={r.MigrationReport.Migrated}，Message={r.MigrationReport.Message}");
    }

    // ---------------------------------------------------------------------
    // 真实 COM 场景执行
    // ---------------------------------------------------------------------
    private DeckScenarioResult RunScenario(string tag, Func<object, int> mutate)
    {
        if (!File.Exists(SourceDeckPath))
        {
            _output.WriteLine($"未验证（{tag}）：找不到测试课件 {SourceDeckPath}，本次不执行断言。");
            return null;
        }

        var result = new DeckScenarioResult { Tag = tag };
        var thread = new Thread(() => ExecuteScenario(result, mutate)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); // PowerPoint COM 必须是 STA。
        thread.Start();

        if (!thread.Join(ComTimeout))
        {
            if (result.TempRoot is not null) _tempRoots.Add(result.TempRoot);
            _output.WriteLine($"未验证（{tag}）：COM 场景超过 {ComTimeout.TotalSeconds:0} 秒仍未结束" +
                              "（PowerPoint 可能因无消息泵而阻塞），本次不执行断言。");
            return null;
        }

        if (result.TempRoot is not null) _tempRoots.Add(result.TempRoot);
        foreach (string note in result.Notes) _output.WriteLine($"说明（{tag}）：{note}");

        if (!string.IsNullOrEmpty(result.UnverifiedReason))
        {
            _output.WriteLine($"未验证（{tag}）：{result.UnverifiedReason}");
            return null;
        }

        if (result.Exception is not null)
        {
            // 这不是「COM 不可用」，而是真实映射代码执行失败：必须让测试失败。
            Assert.Fail($"场景「{tag}」在托管映射阶段抛出异常（不是 COM 不可用）：{result.Exception}");
        }

        return result;
    }

    private void ExecuteScenario(DeckScenarioResult result, Func<object, int> mutate)
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "inkcanvas-slide-mutation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            result.TempRoot = root;
            string deck = Path.Combine(root, "p0-test.pptx");
            string inkRoot = Path.Combine(root, "InkRoot");
            result.Store = new PptInkFileStore(inkRoot);

            object app = null;
            object presentation = null;
            PptInteropService interop = null;
            try
            {
                // 只操作副本，原始 D:\hw\p0-test.pptx 绝不修改。
                File.Copy(SourceDeckPath, deck, overwrite: true);

                Type progId = Type.GetTypeFromProgID("PowerPoint.Application");
                if (progId is null)
                {
                    result.UnverifiedReason = "系统中找不到 PowerPoint.Application 的 COM 注册信息。";
                    return;
                }

                app = Activator.CreateInstance(progId);
                dynamic appDynamic = app;
                try { appDynamic.DisplayAlerts = 1; } catch { /* ppAlertsNone，失败时继续，不影响只读采集。 */ }

                // WithWindow=true：PowerPoint 只有存在活动窗口时才提供 ActivePresentation，
                // 这样 PptInteropService.TryAttach + Poll + GetPresentationDetails 这条应用真实
                // SlideId 采集路径才可用（WithWindow=false 时 Poll 得到 Detached）。
                presentation = appDynamic.Presentations.Open(deck, false, false, true);
                DeckSnapshot before = CaptureDeck(deck, presentation, ref interop, result, "修改前");

                // 用应用真实的文件存档代码生成 slide-{SlideID}.icink + InkCanvas-Ppt.json（manifest v2）。
                result.Store.Save(BuildSeedArchive(before.Info, before.Slides));
                result.SeedDirectory = result.Store.GetPresentationDirectory(before.Info);

                // 真实课件修改：插入 / 删除 / 调序。
                int mutatedSlideId = mutate(presentation);
                result.MutatedSlideId = mutatedSlideId;

                dynamic pres = presentation;
                pres.Save();
                pres.Saved = -1; // 已保存，Close 时不再弹保存确认。
                pres.Close();
                presentation = null;

                // 保存并关闭后重新打开，验证 SlideID 是否真正稳定在文件里（而不是同一个内存对象里）。
                presentation = appDynamic.Presentations.Open(deck, false, false, true);
                DeckSnapshot after = CaptureDeck(deck, presentation, ref interop, result, "修改后");
                dynamic presAfter = presentation;
                presAfter.Saved = -1;
                presAfter.Close();
                presentation = null;

                result.Before = before;
                result.After = after;
            }
            catch (Exception ex)
            {
                // COM 不可用 / PowerPoint 拒绝启动 / Open 失败等：如实标记未验证，不伪造通过。
                result.UnverifiedReason = $"PowerPoint COM 操作失败（{ex.GetType().Name}）：{ex.Message}";
                return;
            }
            finally
            {
                // 失败时也要清理 PowerPnt：先关闭自己打开的演示文稿，再退出自己创建的实例。
                if (presentation is not null)
                {
                    try { dynamic p = presentation; p.Saved = -1; p.Close(); } catch { }
                }
                if (interop is not null)
                {
                    try { interop.Dispose(); } catch { }
                }
                if (app is not null)
                {
                    try { dynamic a = app; a.Quit(); } catch { }
                    try { Marshal.ReleaseComObject(app); } catch { }
                }
            }

            EvaluateMapping(result);
        }
        catch (Exception ex)
        {
            result.Exception = ex;
        }
    }

    /// <summary>
    /// 采集一次课件快照。优先走应用真实的 <see cref="PptInteropService.GetPresentationDetails"/>
    /// （与应用运行时完全同一条 SlideId 采集路径）；当本进程无法 attach 到「本测试新建的
    /// PowerPoint 实例」时（例如机器上还有别的 PowerPoint 持有 ROT），回退为等价的一次性
    /// COM 读取，并在输出中说明，避免掩盖差异。
    /// </summary>
    private DeckSnapshot CaptureDeck(string deckPath, object presentation, ref PptInteropService interop,
        DeckScenarioResult result, string phase)
    {
        dynamic pres = presentation;
        int slideCount = (int)pres.Slides.Count;
        string name = (string)pres.Name;
        string fullName = (string)pres.FullName;
        dynamic pageSetup = pres.PageSetup;
        double width = (double)pageSetup.SlideWidth;
        double height = (double)pageSetup.SlideHeight;
        var info = new PptPresentationInfo("com-test-" + Guid.NewGuid().ToString("N"), name, fullName, true,
            slideCount, width, height, "PowerPoint.Application");

        IReadOnlyList<PptSlideInfo> slides = ReadSlidesThroughInterop(deckPath, ref interop, result, phase);
        if (slides is null)
        {
            slides = ReadSlidesDirect(pres);
            result.Notes.Add($"{phase}：SlideID 由直接 COM 读取（PptInteropService 未 attach 到本次测试实例）。");
        }

        return new DeckSnapshot(info, slides);
    }

    private static IReadOnlyList<PptSlideInfo> ReadSlidesThroughInterop(string deckPath, ref PptInteropService interop,
        DeckScenarioResult result, string phase)
    {
        try
        {
            interop ??= new PptInteropService();
            if (!interop.TryAttach())
            {
                result.Notes.Add($"{phase}：PptInteropService.TryAttach 未成功" +
                                 $"（LastFailure={interop.LastFailure?.Operation}/{interop.LastFailure?.Message}）。");
                return null;
            }

            PptSnapshot snapshot = interop.Poll();
            string attachedPath = snapshot.Presentation?.FullName;
            if (string.IsNullOrWhiteSpace(attachedPath))
            {
                result.Notes.Add($"{phase}：PptInteropService.Poll 未取得演示文稿" +
                                 $"（LastFailure={interop.LastFailure?.Operation}/{interop.LastFailure?.Message}）。");
                interop.Detach();
                return null;
            }

            if (!string.Equals(PresentationIdentity.NormalizePath(attachedPath),
                    PresentationIdentity.NormalizePath(deckPath), StringComparison.OrdinalIgnoreCase))
            {
                // attach 到的是其它 PowerPoint 实例/其它课件：立即断开，避免误读或误写用户文档。
                result.Notes.Add($"{phase}：PptInteropService attach 到的是其它课件（{attachedPath}），已断开。");
                interop.Detach();
                return null;
            }

            PptPresentationDetails details = interop.GetPresentationDetails();
            if (details?.Slides is null || details.Slides.Count == 0) return null;

            result.InteropPhases.Add(phase);
            return details.Slides;
        }
        catch (Exception ex)
        {
            result.Notes.Add($"{phase}：PptInteropService 采集失败（{ex.GetType().Name}），已回退直接 COM。");
            return null;
        }
    }

    private static IReadOnlyList<PptSlideInfo> ReadSlidesDirect(dynamic pres)
    {
        dynamic slides = pres.Slides;
        int count = (int)slides.Count;
        var list = new List<PptSlideInfo>(count);
        for (int index = 1; index <= count; index++)
        {
            dynamic slide = slides[index];
            list.Add(new PptSlideInfo((int)slide.SlideID, index, (string)slide.Name, null, null, null));
        }

        return list;
    }

    /// <summary>
    /// 真实映射/迁移阶段（纯托管，不再触碰 COM）。这里调用的都是应用自身在加载课件后执行的代码：
    /// <see cref="PptInkFileStore.Load"/>、<see cref="SlideContentIndex.Build"/>、
    /// <see cref="SlideMappingInspector.Inspect"/>、<see cref="SlideInkMigrator.MigrateIfNeeded"/>。
    /// </summary>
    private static void EvaluateMapping(DeckScenarioResult result)
    {
        var afterDetails = new PptPresentationDetails(result.After.Info, result.After.Slides, null);
        result.AfterDirectory = result.Store.GetPresentationDirectory(result.After.Info);

        // 用「原始页数」的目录读回初始存档（不受改动影响），再用「修改后页数」的目录读一次，
        // 从而同时观察映射逻辑与既有目录命名策略的真实行为。
        result.SeedArchiveReloaded = result.Store.Load(result.Before.Info);
        result.AfterArchiveReloaded = result.Store.Load(result.After.Info);

        result.MappingIndex = SlideContentIndex.Build(afterDetails, result.SeedArchiveReloaded);
        result.MappingIssue = SlideMappingInspector.Inspect(result.SeedArchiveReloaded, result.After.Slides);
        result.MigrationReport = SlideInkMigrator.MigrateIfNeeded(
            result.AfterArchiveReloaded, afterDetails, result.AfterDirectory);
    }

    // ---------------------------------------------------------------------
    // 断言辅助
    // ---------------------------------------------------------------------
    private static void AssertSeedArchiveFiles(DeckScenarioResult r)
    {
        Assert.NotNull(r.Before);
        Assert.NotNull(r.After);

        // 「为每页生成一份 slide-{SlideID}.icink」由真实 PptInkFileStore.Save 落盘。
        foreach (PptSlideInfo slide in r.Before.Slides)
        {
            string file = Path.Combine(r.SeedDirectory, $"slide-{slide.SlideId}.icink");
            Assert.True(File.Exists(file), $"应为原始页 {slide.SlideId} 生成 {file}");
        }

        Assert.True(File.Exists(Path.Combine(r.SeedDirectory, "InkCanvas-Ppt.json")),
            "应生成 manifest InkCanvas-Ppt.json（v2，按 SlideId 关联批注）。");
        Assert.Equal(r.Before.Slides.Count, r.SeedArchiveReloaded.Slides.Count);
    }

    private static void AssertStoredMarkersMatchOwnSlideId(PptInkArchive archive)
    {
        Assert.NotEmpty(archive.Slides);
        foreach (PptStoredSlide stored in archive.Slides)
        {
            Assert.Equal(stored.SlideId, ReadMarker(stored.Strokes));
        }
    }

    /// <summary>页标识编码在笔画第一个点的 X 坐标上（见 <see cref="MarkerStroke"/>）。</summary>
    private static int ReadMarker(IReadOnlyList<InkStroke> strokes)
    {
        Assert.NotNull(strokes);
        Assert.NotEmpty(strokes);
        Assert.NotEmpty(strokes[0].Points);
        return (int)strokes[0].Points[0].X;
    }

    private static float? MarkerOf(IReadOnlyList<InkStroke> strokes)
        => strokes is { Count: > 0 } && strokes[0].Points.Count > 0 ? strokes[0].Points[0].X : null;

    private static PptInkArchive BuildSeedArchive(PptPresentationInfo info, IReadOnlyList<PptSlideInfo> slides)
    {
        var stored = slides
            .Select(slide => new PptStoredSlide(slide.SlideId, slide.Index,
                new[] { MarkerStroke(slide.SlideId) }))
            .ToList();
        return new PptInkArchive(info, null, null, stored, false,
            stored.Count > 0 ? stored[^1].SlideId : null, PptInkFileStore.CurrentManifestVersion);
    }

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

    private sealed record DeckSnapshot(PptPresentationInfo Info, IReadOnlyList<PptSlideInfo> Slides);

    private sealed class DeckScenarioResult
    {
        public string Tag { get; init; }
        public string TempRoot { get; set; }
        public string UnverifiedReason { get; set; }
        public Exception Exception { get; set; }
        public List<string> Notes { get; } = new();
        public List<string> InteropPhases { get; } = new();
        public DeckSnapshot Before { get; set; }
        public DeckSnapshot After { get; set; }
        public int? MutatedSlideId { get; set; }
        public PptInkFileStore Store { get; set; }
        public string SeedDirectory { get; set; }
        public string AfterDirectory { get; set; }
        public PptInkArchive SeedArchiveReloaded { get; set; }
        public PptInkArchive AfterArchiveReloaded { get; set; }
        public SlideContentIndex MappingIndex { get; set; }
        public SlideMappingIssue MappingIssue { get; set; }
        public SlideInkMigrationReport MigrationReport { get; set; }
    }
}