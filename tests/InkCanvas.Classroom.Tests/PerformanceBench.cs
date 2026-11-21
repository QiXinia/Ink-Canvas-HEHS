using System.Diagnostics;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using InkCanvas.Platform.Ai;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 可复现的本地性能基准（合成数据，无 UI / 无真实 AI 端点依赖）。
/// 每一项以 <c>[PERF] name=... ms=... detail=...</c> 输出，供
/// <c>dotnet test --filter FullyQualifiedName~PerformanceBench --logger "console;verbosity=detailed"</c>
/// 采集真实数字并写入 <c>docs/optimization-report.md</c>。
/// <para>
/// 说明：这些数字是本机合成负载下的耗时，不代表真实课堂硬件、真实 PPT/WPS 进程或
/// 真实 AI 服务的表现；mock 首字延迟只验证 mock 流的调度开销。
/// </para>
/// </summary>
public sealed class PerformanceBench
{
    private readonly ITestOutputHelper _output;

    public PerformanceBench(ITestOutputHelper output)
    {
        _output = output;
    }

    private void Report(string name, double ms, string detail)
        => _output.WriteLine($"[PERF] name={name} ms={ms:F3} detail={detail}");

    private static InkStroke MakeStroke(int seed, int points = 12)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        for (int i = 0; i < points; i++)
        {
            float x = seed + i * 0.6f;
            float y = 10f + (i % 5) * 1.5f;
            stroke.AddPoint(x, y, 0.5f, i * 8f);
        }
        stroke.Complete();
        return stroke;
    }

    // ---- 1. 切页：SlideInkCache 命中 vs 未命中 ---------------------------

    [Fact]
    public void Bench_SlideInkCache_HitVsMiss_100Iterations()
    {
        const int iterationCount = 100;
        const int strokeCount = 1000;

        var strokes = new List<InkStroke>(strokeCount);
        for (int i = 0; i < strokeCount; i++) strokes.Add(MakeStroke(i));

        var cache = new SlideInkCache(8);
        // 预热：首个 Set/Get 包含 JIT 与字典初始化，不计入平均值。
        cache.Set(1, strokes);
        cache.Get(1);

        var setWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterationCount; i++) cache.Set(1, strokes);
        setWatch.Stop();

        var hitWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterationCount; i++)
        {
            IReadOnlyList<InkStroke> hit = cache.Get(1);
            Assert.NotNull(hit);
        }
        hitWatch.Stop();

        var missWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterationCount; i++)
        {
            IReadOnlyList<InkStroke> miss = cache.Get(1_000_000 + i);
            Assert.Null(miss);
        }
        missWatch.Stop();

        string common = $"strokes={strokeCount};pointsPerStroke=12;iterations={iterationCount}";
        Report("slidecache.set.avg", setWatch.Elapsed.TotalMilliseconds / iterationCount,
            common + $";totalMs={setWatch.Elapsed.TotalMilliseconds:F3}");
        Report("slidecache.getHit.avg", hitWatch.Elapsed.TotalMilliseconds / iterationCount,
            common + $";totalMs={hitWatch.Elapsed.TotalMilliseconds:F3};hits={cache.Hits};deepCopy=true");
        Report("slidecache.getMiss.avg", missWatch.Elapsed.TotalMilliseconds / iterationCount,
            common + $";totalMs={missWatch.Elapsed.TotalMilliseconds:F3};misses={cache.Misses}");
        // 上述命中测试每次返回深拷贝，缓存未被破坏。
        Assert.Equal(strokeCount, cache.Get(1).Count);
    }

    // ---- 2. 回放定位：InkTimeline 从最近检查点 BuildFrame ----------------

    [Fact]
    public void Bench_InkTimeline_BuildFrameFromCheckpoint()
    {
        const int slideCount = 500;
        const int strokesPerSlide = 100;
        var session = new ClassroomSession
        {
            Id = "bench-timeline",
            Name = "bench",
            CreatedAt = DateTime.Now,
            Events = new List<TimelineEvent>(slideCount * (strokesPerSlide + 1)),
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
        };

        long timeMs = 0;
        for (int slide = 0; slide < slideCount; slide++)
        {
            timeMs += 10;
            session.Events.Add(new TimelineEvent
            {
                TimeMs = timeMs,
                PageIndex = slide,
                Kind = TimelineEventKind.PageChanged,
            });
            for (int i = 0; i < strokesPerSlide; i++)
            {
                InkStroke stroke = MakeStroke(i, points: 8);
                timeMs += 10;
                session.Events.Add(new TimelineEvent
                {
                    TimeMs = timeMs,
                    PageIndex = slide,
                    Kind = TimelineEventKind.StrokeAdded,
                    StrokeId = stroke.Id.ToString(),
                    StrokeJson = StrokeJsonCodec.Serialize(stroke),
                });
            }
        }

        var timeline = new InkTimeline(session);
        long duration = timeline.Duration;
        long target = (long)(duration * 0.8);

        // 冷路径：首次 BuildFrame 触发 registry 反序列化 + 全量检查点生成，不计入巡航延迟。
        var coldWatch = Stopwatch.StartNew();
        IReadOnlyList<InkStroke> coldFrame = timeline.BuildFrame(0);
        coldWatch.Stop();
        Assert.NotNull(coldFrame);

        const int runs = 5;
        var samples = new List<double>(runs);
        IReadOnlyList<InkStroke> frame = Array.Empty<InkStroke>();
        for (int i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            frame = timeline.BuildFrame(target);
            watch.Stop();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        int cutoff = timeline.IndexAt(target);
        SessionCheckpoint nearest = timeline.NearestCheckpoint(target);
        int replayed = nearest is null ? cutoff + 1 : cutoff - nearest.EventIndex;
        double average = samples.Sum() / samples.Count;

        Report("timeline.buildFrame.seek.avg", average,
            $"events={session.Events.Count};strokes={slideCount * strokesPerSlide};durationMs={duration};targetMs={target}"
            + $";checkpoints={session.Checkpoints.Count};replayEvents={replayed};frameStrokes={frame.Count}"
            + $";coldMs={coldWatch.Elapsed.TotalMilliseconds:F3};runs={runs}"
            + $";minMs={samples.Min():F3};maxMs={samples.Max():F3}");
        Assert.Equal(strokesPerSlide, frame.Count);

        // 0.8 目标恰好落在检查点边界（replayEvents=0）；前移 234ms 后仍在同一页，
        // 测量真实的增量重放成本（帧内笔画数保持 100，可对比）。
        long offsetTarget = target - 234;
        var offsetSamples = new List<double>(runs);
        IReadOnlyList<InkStroke> offsetFrame = Array.Empty<InkStroke>();
        for (int i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            offsetFrame = timeline.BuildFrame(offsetTarget);
            watch.Stop();
            offsetSamples.Add(watch.Elapsed.TotalMilliseconds);
        }

        int offsetCutoff = timeline.IndexAt(offsetTarget);
        SessionCheckpoint offsetNearest = timeline.NearestCheckpoint(offsetTarget);
        int offsetReplayed = offsetNearest is null ? offsetCutoff + 1 : offsetCutoff - offsetNearest.EventIndex;
        Report("timeline.buildFrame.seekOffset.avg", offsetSamples.Sum() / offsetSamples.Count,
            $"events={session.Events.Count};targetMs={offsetTarget}"
            + $";replayEvents={offsetReplayed};frameStrokes={offsetFrame.Count};runs={runs}"
            + $";minMs={offsetSamples.Min():F3};maxMs={offsetSamples.Max():F3}");
        Assert.True(offsetReplayed > 0);
    }

    // ---- 3. 抽稀：StrokeSimplifier 1 万点 --------------------------------

    [Fact]
    public void Bench_StrokeSimplifier_10000Points()
    {
        const int pointCount = 10_000;
        var points = new InkPoint[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            // 近似手写曲线：主正弦 + 小幅高频抖动，避免退化成直线而低估保留率。
            float x = i * 0.6f;
            float y = 50f + 40f * MathF.Sin(i * 0.01f) + 1.5f * MathF.Sin(i * 0.37f);
            points[i] = new InkPoint(x, y, 0.5f, 2f, i * 4f);
        }

        // 预热一次。
        _ = StrokeSimplifier.Simplify(points);

        const int runs = 5;
        var samples = new List<double>(runs);
        IReadOnlyList<InkPoint> simplified = Array.Empty<InkPoint>();
        for (int i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            simplified = StrokeSimplifier.Simplify(points);
            watch.Stop();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        var whole = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        whole.RestorePoints(points);
        whole.Complete();
        var wholeWatch = Stopwatch.StartNew();
        InkStroke simplifiedStroke = StrokeSimplifier.Simplify(whole);
        wholeWatch.Stop();

        double retention = 100.0 * simplified.Count / pointCount;
        double average = samples.Sum() / samples.Count;
        Report("simplifier.points10k.avg", average,
            $"points={pointCount};tolerance={StrokeSimplifier.DefaultTolerance};runs={runs}"
            + $";kept={simplified.Count};retentionPct={retention:F2}"
            + $";minMs={samples.Min():F3};maxMs={samples.Max():F3}"
            + $";wholeStrokeMs={wholeWatch.Elapsed.TotalMilliseconds:F3}"
            + $";wholeKept={simplifiedStroke.Points.Count}");
        Assert.True(simplified.Count >= 2);
    }

    // ---- 4. 首字（mock）：AiClient 空端点 ---------------------------------

    [Fact]
    public void Bench_AiClient_MockFirstTokenLatency()
    {
        const int runs = 3;
        var samples = new List<double>(runs);
        long firstEventIndex = -1;

        for (int run = 0; run < runs; run++)
        {
            using var client = new AiClient(new AiConfig { Endpoint = "" });
            using var done = new ManualResetEventSlim(false);
            double firstMs = -1;
            long eventCount = 0;
            var gate = new object();
            var watch = Stopwatch.StartNew();

            AiStreamHandle handle = client.Start(
                new AssistantRequest { Mode = AssistantMode.Formula },
                ev =>
                {
                    lock (gate)
                    {
                        if (firstMs < 0) firstMs = watch.Elapsed.TotalMilliseconds;
                        eventCount++;
                    }
                },
                _ => done.Set(),
                () => done.Set());

            bool completed = done.Wait(TimeSpan.FromSeconds(5));
            handle.Abort();
            watch.Stop();

            Assert.True(completed, "mock 流未在 5 秒内结束");
            Assert.True(firstMs >= 0, "mock 流未产生任何事件");
            samples.Add(firstMs);
            if (firstEventIndex < 0) firstEventIndex = 0;
        }

        double average = samples.Sum() / samples.Count;
        Report("ai.mock.firstToken.avg", average,
            $"endpoint=mock;mode=Formula;runs={runs};samplesMs={string.Join('/', samples.Select(s => s.ToString("F1")))}"
            + $";expectedFirstDelayMs=60;note=不含真实网络/模型推理");
        Assert.True(firstEventIndex >= 0);
    }

    // ---- 5. 内存：1000 笔画 SlideInkCache --------------------------------

    [Fact]
    public void Bench_Memory_1000StrokesCache()
    {
        const int strokeCount = 1000;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long workingSetBefore = Process.GetCurrentProcess().WorkingSet64;

        var watch = Stopwatch.StartNew();
        var source = new List<InkStroke>(strokeCount);
        for (int i = 0; i < strokeCount; i++) source.Add(MakeStroke(i, points: 16));
        var cache = new SlideInkCache(8);
        cache.Set(1, source);
        watch.Stop();

        long after = GC.GetTotalMemory(forceFullCollection: true);
        long workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
        GC.KeepAlive(cache);
        GC.KeepAlive(source);

        long deltaBytes = after - before;
        double perStrokeBytes = deltaBytes / (double)strokeCount;
        Report("memory.cache1000Strokes", watch.Elapsed.TotalMilliseconds,
            $"strokes={strokeCount};pointsPerStroke=16;managedDeltaBytes={deltaBytes}"
            + $";managedDeltaKB={deltaBytes / 1024.0:F1};bytesPerStroke={perStrokeBytes:F1}"
            + $";workingSetDeltaKB={(workingSetAfter - workingSetBefore) / 1024.0:F1}"
            + $";note=含源笔画与缓存深拷贝，GC.GetTotalMemory(true) 前后差");
        Assert.Equal(strokeCount, cache.Get(1).Count);
    }
}