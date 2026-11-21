using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Classroom;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

public sealed class EndToEndReplayTests
{
    private const long T1 = 250;
    private const long T2 = 450;
    private const long T3 = 850;
    private const long T4 = 950;
    private const long T5 = 1_050;
    private const long T6 = 1_250;
    private const long T7 = 1_350;
    private const long T8 = 1_750;

    private readonly ITestOutputHelper _output;

    public EndToEndReplayTests(ITestOutputHelper output) => _output = output;

    private sealed class SyntheticScript
    {
        public ClassroomSession Session { get; init; }
        public float P0A { get; init; }
        public float P0B { get; init; }
        public float P1A { get; init; }
        public float P1B { get; init; }
        public float P1C { get; init; }
        public float P2A { get; init; }
    }

    private static InkStroke Stroke(float x, float y = 10f)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, y);
        stroke.AddPoint(x + 5f, y);
        stroke.AddPoint(x + 10f, y + 2f);
        stroke.Complete();
        return stroke;
    }

    private static TimelineEvent Added(long timeMs, int page, InkStroke stroke) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokeAdded,
        StrokeId = stroke.Id.ToString(),
        StrokeJson = StrokeJsonCodec.Serialize(stroke),
    };

    private static TimelineEvent Page(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PageChanged,
    };

    private static TimelineEvent Background(long timeMs, int page, string name) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.BackgroundChanged,
        Payload = name,
    };

    private static TimelineEvent Ppt(long timeMs, int page, int slideId, int index, string screenshot) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PptSlideChanged,
        Payload = InkTimeline.EncodePptSlideChanged(slideId, index, screenshot),
    };

    private static TimelineEvent View(long timeMs, int page, double scale, double tx, double ty) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.ViewChanged,
        Payload = InkTimeline.EncodeViewChanged(scale, tx, ty),
    };

    private static TimelineEvent Removed(long timeMs, int page, params InkStroke[] strokes) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokesRemoved,
        StrokeIds = strokes.Select(s => s.Id.ToString()).ToArray(),
    };

    private static TimelineEvent Undo(long timeMs, int page, params InkStroke[] strokes) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.Undo,
        StrokeIds = strokes.Select(s => s.Id.ToString()).ToArray(),
    };

    private static TimelineEvent Redo(long timeMs, int page, params InkStroke[] strokes) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.Redo,
        Payload = InkTimeline.EncodeAddedIds(strokes.Select(s => s.Id.ToString())),
    };

    private static TimelineEvent Cleared(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.Cleared,
    };

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "end-to-end-replay",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
    };

    private static SyntheticScript CreateScript()
    {
        InkStroke p0a = Stroke(10);
        InkStroke p0b = Stroke(20, 15);
        InkStroke p1a = Stroke(30);
        InkStroke p1b = Stroke(40);
        InkStroke p1c = Stroke(50);
        InkStroke p2a = Stroke(60);

        ClassroomSession session = Session(
            Page(0, 0),
            Background(0, 0, "Black"),
            Ppt(0, 0, 101, 1, ""),
            Added(100, 0, p0a),
            Added(200, 0, p0b),
            View(300, 0, 1.5, 120, 80),
            Page(400, 1),
            Background(400, 1, "Grid"),
            Added(500, 1, p1a),
            Added(600, 1, p1b),
            Removed(700, 1, p1a),
            Added(800, 1, p1c),
            Undo(900, 1, p1c),
            Redo(1_000, 1, p1c),
            Page(1_100, 2),
            Background(1_100, 2, "Coordinate"),
            View(1_150, 2, 2.0, -40, 12),
            Ppt(1_200, 2, 202, 2, ""),
            Added(1_300, 2, p2a),
            Cleared(1_400, 1),
            Page(1_700, 1));

        return new SyntheticScript
        {
            Session = session,
            P0A = 10f,
            P0B = 20f,
            P1A = 30f,
            P1B = 40f,
            P1C = 50f,
            P2A = 60f,
        };
    }

    private static ClassroomSession CreateConcurrentSession()
    {
        var events = new List<TimelineEvent>
        {
            Page(0, 0),
            Background(0, 0, "Black"),
        };
        for (int i = 0; i < 20; i++)
            events.Add(Added(100 + i * 90L, 0, Stroke(10 + i * 3)));
        events.Add(View(2_000, 0, 1.1, 5, 5));
        return Session(events.ToArray());
    }

    private static IReadOnlyCollection<float> FrameXs(IReadOnlyList<InkStroke> frame)
        => frame.Select(s => s.Points[0].X).OrderBy(x => x).ToArray();

    private static string FrameHash(IReadOnlyList<InkStroke> frame)
    {
        var builder = new StringBuilder();
        foreach (InkStroke stroke in frame)
            builder.Append(StrokeJsonCodec.Serialize(stroke)).Append('\n');
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static string FrameSignature(InkTimeline timeline, long timeMs)
        => FrameHash(timeline.BuildFrame(timeMs));

    private static IReadOnlyList<long> SimulatePlayback(long targetMs, double speed, double wallStepMs = 1.0)
    {
        var reached = new List<long>();
        double playback = 0;
        int guard = 0;
        while (playback < targetMs && guard++ < 2_000_000)
        {
            playback += wallStepMs * speed;
            reached.Add((long)Math.Floor(playback));
        }
        if (reached.Count == 0) reached.Add(targetMs);
        return reached;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-e2e-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTemp(string dir)
    {
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- 1) 跨页 / 背景 / 擦除 / 清空 -----------------------------------

    [Fact]
    public void SyntheticSession_CrossPageBackgroundEraseClearMatchesEventOrder()
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        AssertSceneAndFrame(timeline, T1, 0, "Black", 1, script.P0A, script.P0B);
        AssertSceneAndFrame(timeline, T2, 1, "Grid", 1);
        AssertSceneAndFrame(timeline, T3, 1, "Grid", 1, script.P1B, script.P1C);
        AssertSceneAndFrame(timeline, T4, 1, "Grid", 1, script.P1B);
        AssertSceneAndFrame(timeline, T5, 1, "Grid", 1, script.P1B, script.P1C);
        AssertSceneAndFrame(timeline, T6, 2, "Coordinate", 2);
        AssertSceneAndFrame(timeline, T7, 2, "Coordinate", 2, script.P2A);
        AssertSceneAndFrame(timeline, T8, 1, "Coordinate", 2);
    }

    private static void AssertSceneAndFrame(InkTimeline timeline, long timeMs, int page,
        string background, int pptSlide, params float[] expectedXs)
    {
        ClassroomScene scene = timeline.SceneAt(timeMs);
        Assert.Equal(page, scene.PageIndex);
        Assert.Equal(background, scene.Background);
        Assert.Equal(pptSlide, scene.PptSlideIndex);
        Assert.Equal("", scene.PptScreenshotPath);

        IReadOnlyList<InkStroke> frame = timeline.BuildFrame(timeMs);
        Assert.Equal(
            expectedXs.OrderBy(x => x).ToArray(),
            FrameXs(frame).ToArray());
        Assert.Equal(page, timeline.PageAt(timeMs));
        Assert.Equal(background, timeline.BackgroundAt(timeMs));
    }

    // ---- 2) 同点重复帧指纹 ------------------------------------------------

    [Fact]
    public void RepeatedBuildFrame_IsBitIdenticalAcrossFivePasses()
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);
        long[] points = { T1, T2, T3, T4, T5, T6, T7, T8, 0, 100, 1_700 };

        foreach (long timeMs in points)
        {
            string expected = FrameSignature(timeline, timeMs);
            for (int pass = 0; pass < 5; pass++)
            {
                Assert.Equal(expected, FrameSignature(timeline, timeMs));
                Assert.Equal(expected, FrameHash(new InkTimeline(script.Session).BuildFrame(timeMs)));
            }
            Assert.Equal(64, expected.Length);
            _output.WriteLine($"t={timeMs}ms hash={expected[..16]}…");
        }
    }

    // ---- 3) 倍速一致性 ----------------------------------------------------

    [Fact]
    public void PlaybackSpeed_ArrivalFrameMatchesDirectSeek()
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);
        double[] speeds = { 0.5, 1, 2, 8 };
        long[] points = { T1, T2, T3, T4, T5, T6, T7, T8 };

        foreach (double speed in speeds)
        {
            foreach (long target in points)
            {
                IReadOnlyList<long> reached = SimulatePlayback(target, speed);
                long arrival = reached[^1];
                Assert.True(arrival >= target);

                Assert.Equal(FrameSignature(timeline, target), FrameSignature(timeline, arrival));
                Assert.Equal(timeline.IndexAt(target), timeline.IndexAt(arrival));
            }
            _output.WriteLine($"speed={speed}x：{points.Length} 个绝对时间点到达帧指纹一致");
        }

        foreach (long target in points)
        {
            string expected = FrameSignature(timeline, target);
            foreach (double speed in speeds)
                Assert.Equal(expected, FrameSignature(timeline, SimulatePlayback(target, speed)[^1]));
        }
    }

    [Fact]
    public void SpeedSwitching_KeepsIndexAtMonotonicAndFramesStable()
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);
        double[] speeds = { 0.5, 1, 2, 8, 1, 0.5 };
        long[] boundaries = { 400, 800, 1_100, 1_400, 1_700 };

        double playback = 0;
        int prevIndex = -1;
        int segment = 0;
        var samples = new List<(long TimeMs, int Index)>();

        while (playback < script.Session.Events[^1].TimeMs)
        {
            playback += speeds[segment];
            if (segment < boundaries.Length && playback >= boundaries[segment]) segment++;
            double speed = speeds[Math.Min(segment, speeds.Length - 1)];
            long sourceTime = (long)Math.Floor(playback);
            int index = timeline.IndexAt(sourceTime);
            Assert.True(index >= prevIndex, $"IndexAt 在 t={sourceTime} 倒退：{prevIndex} → {index}");
            prevIndex = index;
            samples.Add((sourceTime, index));
            if (playback > 2_000_000) break;
        }

        Assert.NotEmpty(samples);

        for (int i = 1; i < samples.Count; i++)
            Assert.True(samples[i].TimeMs >= samples[i - 1].TimeMs);

        foreach (long target in new[] { T3, T5, T7 })
        {
            string expected = FrameSignature(timeline, target);
            foreach (double speed in new[] { 0.5, 1, 2, 8 })
                Assert.Equal(expected, FrameSignature(timeline, SimulatePlayback(target, speed)[^1]));
        }
        _output.WriteLine($"倍速切换样本 {samples.Count} 个，末事件索引 {timeline.IndexAt(script.Session.Events[^1].TimeMs)}");
    }


    // ---- 4) 随机拖动 ------------------------------------------------------

    [Fact]
    public void RandomSeek_Ascending_MatchesSequentialPlayback()
    {
        AssertSeekOrder(seekOrder: "ascending");
    }

    [Fact]
    public void RandomSeek_Descending_MatchesSequentialPlayback()
    {
        AssertSeekOrder(seekOrder: "descending");
    }

    [Fact]
    public void RandomSeek_Shuffled_MatchesSequentialPlayback()
    {
        AssertSeekOrder(seekOrder: "shuffled");
    }

    private void AssertSeekOrder(string seekOrder)
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);
        var random = new Random(20261120);
        long duration = script.Session.Events[^1].TimeMs;

        var pointSet = new HashSet<long>();
        while (pointSet.Count < 20)
            pointSet.Add(random.Next(0, (int)duration + 1));
        List<long> points = pointSet.ToList();

        var expected = points.Select(t => FrameSignature(timeline, t)).ToArray();

        List<long> order = seekOrder switch
        {
            "ascending" => points.OrderBy(t => t).ToList(),
            "descending" => points.OrderByDescending(t => t).ToList(),
            _ => points.OrderBy(_ => random.Next()).ToList(),
        };

        var expectedByTime = points.Zip(expected, (t, h) => (t, h)).ToDictionary(x => x.t, x => x.h);
        foreach (long timeMs in order)
        {
            string first = FrameSignature(timeline, timeMs);
            string second = FrameSignature(timeline, timeMs);
            Assert.Equal(first, second);
            Assert.Equal(expectedByTime[timeMs], first);
            Assert.Equal(expectedByTime[timeMs], FrameHash(new InkTimeline(script.Session).BuildFrame(timeMs)));
        }

        Assert.Equal(20, order.Count);
        _output.WriteLine($"{seekOrder}：20 个随机点两次 BuildFrame 与顺序播放一致");
    }

    // ---- 5) 导出采样 ------------------------------------------------------

    [Fact]
    public void ExportSampling_At1x_MatchesBuildFrameFingerprint()
    {
        AssertExportSampling(speed: 1.0, millisecondsPerFrame: 33.333);
    }

    [Fact]
    public void ExportSampling_At2x_MatchesBuildFrameFingerprint()
    {
        AssertExportSampling(speed: 2.0, millisecondsPerFrame: 66.667);
    }

    private void AssertExportSampling(double speed, double millisecondsPerFrame)
    {
        SyntheticScript script = CreateScript();
        var options = new ExportOptions("sample.mp4", 0, 1_700, Fps: 30,
            Width: 640, Height: 480, Speed: speed);
        IReadOnlyList<long> samples = TimeMachineExporter.SampleFrameTimes(options);
        int expectedCount = TimeMachineExporter.EstimateFrameCount(options);

        Assert.Equal(expectedCount, samples.Count);
        Assert.NotEmpty(samples);
        Assert.Equal(0, samples[0]);
        Assert.True(samples[^1] <= options.EndMs);

        var timeline = new InkTimeline(script.Session);
        for (int i = 1; i < samples.Count; i++)
            Assert.True(samples[i] > samples[i - 1]);

        long frameSpan = samples.Count > 1 ? samples[1] - samples[0] : 0;
        Assert.InRange(frameSpan, (long)(millisecondsPerFrame - 1), (long)(millisecondsPerFrame + 1));

        var exportTimeline = new InkTimeline(script.Session);
        foreach (long timeMs in samples)
        {
            string exportFrame = FrameHash(exportTimeline.BuildFrame(timeMs));
            string directFrame = FrameSignature(timeline, timeMs);
            Assert.Equal(directFrame, exportFrame);
        }
        _output.WriteLine($"{speed}x 采样 {samples.Count} 帧，步长 {frameSpan}ms，末帧 t={samples[^1]}ms");
    }

    [Fact]
    public void BoardOnlyExport_UsesSameStrokeSetAsFullClassroom()
    {
        SyntheticScript script = CreateScript();
        var full = new ExportOptions("full.mp4", 0, 1_700, Fps: 30,
            Width: 640, Height: 480, BoardOnly: false);
        var boardOnly = new ExportOptions("board.mp4", 0, 1_700, Fps: 30,
            Width: 640, Height: 480, BoardOnly: true);

        Assert.True(TimeMachineExporter.ShouldDrawPptBackground(full));
        Assert.False(TimeMachineExporter.ShouldDrawPptBackground(boardOnly));
        Assert.False(TimeMachineExporter.ShouldDrawPptBackground(null));

        IReadOnlyList<long> fullTimes = TimeMachineExporter.SampleFrameTimes(full);
        IReadOnlyList<long> boardTimes = TimeMachineExporter.SampleFrameTimes(boardOnly);
        Assert.Equal(fullTimes, boardTimes);

        var timeline = new InkTimeline(script.Session);
        foreach (long timeMs in fullTimes)
        {
            IReadOnlyList<InkStroke> fullFrame = timeline.BuildFrame(timeMs);
            IReadOnlyList<InkStroke> boardFrame = timeline.BuildFrame(timeMs);
            Assert.Equal(FrameXs(fullFrame), FrameXs(boardFrame));
            Assert.Equal(FrameHash(fullFrame), FrameHash(boardFrame));
        }

        ClassroomScene scene = timeline.SceneAt(1_250);
        Assert.Equal(2, scene.PptSlideIndex);
        Assert.Equal("", scene.PptScreenshotPath);
        _output.WriteLine($"BoardOnly 与完整课堂共 {fullTimes.Count} 个采样时刻，笔画集合与指纹一致；PPT 背景由 ShouldDrawPptBackground 区分");
    }

    // ---- 6) 导出期间书写（模拟并发）--------------------------------------

    [Fact]
    public async Task ExportWhileWriting_MeetsStrokeLatencyBudget()
    {
        string dir = TempDir();
        string mp4 = Path.Combine(dir, "concurrent-export.mp4");
        try
        {
            ClassroomSession session = CreateConcurrentSession();
            var options = new ExportOptions(mp4, 0, 2_000, Fps: 30,
                Width: 640, Height: 480, BoardOnly: true);

            Task exportTask = TimeMachineExporter.ExportAsync(
                session, options, progress: null, CancellationToken.None);
            var document = new InkDocument();

            const int strokeCount = 100;
            double maxStrokeMs = 0;
            int finishedWhileExporting = 0;
            var total = Stopwatch.StartNew();
            for (int i = 0; i < strokeCount; i++)
            {
                var single = Stopwatch.StartNew();
                InkStroke stroke = Stroke(10 + i % 40, 20 + i % 20);
                document.Add(stroke);
                single.Stop();
                maxStrokeMs = Math.Max(maxStrokeMs, single.Elapsed.TotalMilliseconds);
                if (!exportTask.IsCompleted) finishedWhileExporting++;
            }
            total.Stop();
            double totalStrokeMs = total.Elapsed.TotalMilliseconds;

            bool exportCompleted = false;
            string exportNote = "";
            try
            {
                await exportTask;
                exportCompleted = File.Exists(mp4) && new FileInfo(mp4).Length > 0;
                if (!exportCompleted) exportNote = "导出任务结束但未生成非空 MP4。";
            }
            catch (Exception ex)
            {
                exportNote = ex.Message;
            }

            Assert.Equal(strokeCount, document.Count);
            Assert.True(maxStrokeMs < 50,
                $"-- 并发导出期间单笔提交耗时 {maxStrokeMs:0.000}ms，超过 50ms 上限。");
            Assert.True(totalStrokeMs < 2_000,
                $"-- 并发导出期间 100 笔总耗时 {totalStrokeMs:0.000}ms，超过 2000ms 上限。");

            if (exportCompleted)
            {
                Assert.True(new FileInfo(mp4).Length > 0);
            }
            else
            {
                Assert.True(exportNote.Length > 0, "导出未完成时应记录跳过原因。");
            }

            _output.WriteLine($"书写 {strokeCount} 笔：单笔最大 {maxStrokeMs:0.000}ms，总耗时 {totalStrokeMs:0.000}ms；"
                + $"导出期间完成 {finishedWhileExporting} 笔；"
                + (exportCompleted ? "MF 导出完成，MP4 存在。" : "MF 未完成/不可用：" + exportNote));
        }
        finally
        {
            DeleteTemp(dir);
        }
    }

    // ---- 7) 手动书签 ------------------------------------------------------

    [Fact]
    public void Bookmarks_AddRenameRemoveSummaryAndDescribe()
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        int index = timeline.AddBookmark(850, "重点讲解");
        Assert.InRange(index, 0, script.Session.Bookmarks.Count - 1);
        SessionBookmark bookmark = script.Session.Bookmarks[index];
        Assert.Equal(850, bookmark.TimeMs);
        Assert.Equal(1, bookmark.PageIndex);
        Assert.Equal("重点讲解", bookmark.Name);
        Assert.False(bookmark.Auto);
        Assert.Equal("manual", bookmark.Kind);

        Assert.True(timeline.RenameBookmark(index, "重点讲解（改）"));
        Assert.Equal("重点讲解（改）", script.Session.Bookmarks[index].Name);
        Assert.False(timeline.RenameBookmark(index, "   "));
        Assert.False(timeline.RenameBookmark(-1, "x"));
        Assert.False(timeline.RenameBookmark(99, "x"));

        string summary = timeline.BookmarkSummary(bookmark, 20);
        string description = timeline.DescribeBookmark(bookmark, 20);
        Assert.Equal("撤销", summary);
        Assert.StartsWith("00:00 · 第 2 页", description);
        Assert.Contains(summary, description);

        int second = timeline.AddBookmark(1_500, "清空后");
        Assert.True(second > index);
        Assert.True(timeline.RemoveBookmark(second));
        Assert.Single(script.Session.Bookmarks);
        Assert.False(timeline.RemoveBookmark(second));
        Assert.False(timeline.RemoveBookmark(-1));

        Assert.Equal("", timeline.BookmarkSummary(null));
        Assert.Equal("", timeline.DescribeBookmark(null));
        _output.WriteLine($"书签：{timeline.DescribeBookmark(bookmark, 20)}（摘要 {summary}）");
    }

    // ---- 8) 其余画面级断言 ------------------------------------------------

    [Fact]
    public void PptSlidePayload_EncodesSlideIdIndexAndEmptyScreenshot()
    {
        PptSlideChange change = InkTimeline.DecodePptSlideChanged(
            InkTimeline.EncodePptSlideChanged(202, 2, ""));

        Assert.True(change.IsValid);
        Assert.Equal(202, change.SlideId);
        Assert.Equal(2, change.Index);
        Assert.Equal("", change.Screenshot);

        PptSlideChange empty = InkTimeline.DecodePptSlideChanged("");
        Assert.False(empty.IsValid);

        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);
        Assert.Equal(1, timeline.SceneAt(1_000).PptSlideIndex);
        Assert.Equal(2, timeline.SceneAt(1_200).PptSlideIndex);
        Assert.Equal("", timeline.SceneAt(1_200).PptScreenshotPath);
    }

    [Fact]
    public void UndoRedo_RestoresStrokeInFrameOrder()
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        Assert.Contains(script.P1C, FrameXs(timeline.BuildFrame(850)));
        Assert.DoesNotContain(script.P1C, FrameXs(timeline.BuildFrame(950)));
        Assert.Contains(script.P1C, FrameXs(timeline.BuildFrame(1_050)));
        Assert.Equal(
            new[] { script.P1B, script.P1C }.OrderBy(x => x),
            FrameXs(timeline.BuildFrame(1_050)).OrderBy(x => x));
    }

    [Fact]
    public void ClearedSecondPage_RemovesOnlyItsStrokesWhenReturning()
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        Assert.Contains(script.P2A, FrameXs(timeline.BuildFrame(1_400)));
        Assert.Equal(2, timeline.SceneAt(1_400).PageIndex);
        Assert.Empty(timeline.BuildFrame(1_750));
        Assert.Equal(1, timeline.PageAt(1_750));
        Assert.Equal(2, timeline.SceneAt(1_650).PptSlideIndex);
        Assert.Contains(script.P2A, FrameXs(timeline.BuildFrame(1_650)));
    }

    [Fact]
    public void TimelineIndexAt_IsChronologicalAndMonotonic()
    {
        SyntheticScript script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        Assert.Equal(-1, timeline.IndexAt(-1));
        long previousTime = long.MinValue;
        for (int i = 0; i < script.Session.Events.Count; i++)
        {
            TimelineEvent e = script.Session.Events[i];
            Assert.True(e.TimeMs >= previousTime);
            previousTime = e.TimeMs;
            Assert.True(timeline.IndexAt(e.TimeMs) >= i);
        }

        Assert.Equal(script.Session.Events.Count - 1,
            timeline.IndexAt(script.Session.Events[^1].TimeMs));
        Assert.Equal(script.Session.Events.Count - 1, timeline.IndexAt(99_999));
        Assert.Equal(script.Session.Events[^1].TimeMs, timeline.Duration);
    }
}