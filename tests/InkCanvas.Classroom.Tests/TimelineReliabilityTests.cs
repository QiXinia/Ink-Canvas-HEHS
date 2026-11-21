using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Classroom;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 时光机数据可靠性：事件序号 / 会话归属 / 稳定排序 / 完整性校验 / 迁移备份 /
/// 回放只读 / 长课定位预算。全部为纯逻辑断言，不依赖 UI 或真实课堂素材。
/// </summary>
public sealed class TimelineReliabilityTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public TimelineReliabilityTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "ink-timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
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
        stroke.AddPoint(x + 6f, y);
        stroke.Complete();
        return stroke;
    }

    private static TimelineEvent Added(string sessionId, int seq, long timeMs, int page, InkStroke stroke) => new()
    {
        SessionId = sessionId,
        Seq = seq,
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokeAdded,
        StrokeId = stroke.Id.ToString(),
        StrokeJson = StrokeJsonCodec.Serialize(stroke),
    };

    private static ClassroomSession Session(string id = "s1") => new()
    {
        Id = id,
        Name = "测试会话",
        CreatedAt = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Local),
    };

    // ---- 序号 / 归属 -------------------------------------------------------

    [Fact]
    public void Normalize_FillsSessionIdAndSeq_ForLegacyEvents()
    {
        var session = Session();
        session.Events.Add(new TimelineEvent { TimeMs = 10, PageIndex = 0, Kind = TimelineEventKind.PageChanged });
        session.Events.Add(Added(null, 0, 20, 0, Stroke(1)));

        _ = new InkTimeline(session);

        Assert.All(session.Events, e => Assert.Equal("s1", e.SessionId));
        Assert.Equal(new[] { 1, 2 }, session.Events.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Normalize_ReordersOutOfOrderEvents_ByTimeThenSeq()
    {
        var session = Session();
        var a = Stroke(10);
        var b = Stroke(20);
        session.Events.Add(Added("s1", 3, 300, 0, a));   // 时间乱序
        session.Events.Add(Added("s1", 1, 100, 0, b));

        var timeline = new InkTimeline(session);

        Assert.Equal(new long[] { 100, 300 }, session.Events.Select(e => e.TimeMs).ToArray());
        var frame = timeline.BuildFrame(300);
        Assert.Equal(2, frame.Count);
    }

    [Fact]
    public void SameMillisecondEvents_AreOrderedBySeq_NotInsertionOrder()
    {
        var session = Session();
        var first = Stroke(10);
        var second = Stroke(20);
        // 同毫秒，Seq 决定顺序；先放入 Seq=2，再放入 Seq=1。
        session.Events.Add(Added("s1", 2, 500, 0, second));
        session.Events.Add(Added("s1", 1, 500, 0, first));

        var timeline = new InkTimeline(session);

        Assert.Equal(new[] { 1, 2 }, session.Events.Select(e => e.Seq).ToArray());
        var frame = timeline.BuildFrame(500);
        Assert.Equal(2, frame.Count);
        // 先落 A（Seq=1）再落 B（Seq=2）：首点 X 递增。
        Assert.Equal(10f, frame[0].Points[0].X);
        Assert.Equal(20f, frame[1].Points[0].X);
    }

    [Fact]
    public void Checkpoints_CarrySeq_ForStableRelocation()
    {
        var session = Session();
        for (int i = 0; i < 260; i++)
            session.Events.Add(Added("s1", i + 1, i * 40L, 0, Stroke(i)));

        var timeline = new InkTimeline(session);
        _ = timeline.BuildFrame(260 * 40L);

        Assert.NotEmpty(session.Checkpoints);
        Assert.All(session.Checkpoints, cp => Assert.True(cp.Seq > 0));
    }

    [Fact]
    public void CursorPoint_ReturnsLastPointOfLatestStrokeOnCurrentPage()
    {
        var session = Session();
        var stroke = Stroke(44, 66);
        session.Events.Add(Added("s1", 1, 100, 0, stroke));

        var timeline = new InkTimeline(session);
        var cursor = timeline.CursorPointAt(100);

        Assert.NotNull(cursor);
        Assert.Equal(50f, cursor!.Value.X, 3);
        Assert.Equal(66f, cursor.Value.Y, 3);
    }

    // ---- 回放只读 ----------------------------------------------------------

    [Fact]
    public void BuildFrame_DoesNotMutateEventStream()
    {
        var session = Session();
        for (int i = 0; i < 40; i++)
            session.Events.Add(Added("s1", i + 1, i * 100L, i / 10, Stroke(i)));

        var timeline = new InkTimeline(session);
        string Before() => string.Join("|", session.Events.Select(e =>
            $"{e.Seq}:{e.TimeMs}:{e.Kind}:{e.StrokeId ?? ""}"));

        string before = Before();
        _ = timeline.BuildFrame(0);
        _ = timeline.BuildFrame(1_500);
        _ = timeline.BuildFrame(3_900);
        _ = timeline.BuildFrame(0);

        Assert.Equal(before, Before());
    }

    // ---- 持久化：完整性校验与迁移备份 ---------------------------------------

    [Fact]
    public void Save_WritesChecksumAndLoadVerifiesIt()
    {
        var session = Session();
        session.Events.Add(Added("s1", 1, 100, 0, Stroke(1)));
        string path = ClassroomSessionStore.Save(session, _dir);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(ClassroomSessionStore.PathForChecksum(path)));

        var loaded = ClassroomSessionStore.Load(path);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Events);
    }

    [Fact]
    public void TamperedFile_WithoutBackup_IsRejected()
    {
        var session = Session();
        session.Events.Add(Added("s1", 1, 100, 0, Stroke(1)));
        string path = ClassroomSessionStore.Save(session, _dir);

        File.AppendAllText(path, " ");

        Assert.Throws<InvalidDataException>(() => ClassroomSessionStore.Load(path));
    }

    [Fact]
    public void TamperedFile_WithBackup_FallsBackToBackup()
    {
        var session = Session();
        session.Events.Add(Added("s1", 1, 100, 0, Stroke(1)));
        string path = ClassroomSessionStore.Save(session, _dir);
        File.Copy(path, ClassroomSessionStore.PathForBackup(path), overwrite: true);

        File.AppendAllText(path, " ");

        var loaded = ClassroomSessionStore.Load(path);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Events);
    }

    [Fact]
    public void LegacySessionWithoutVersion_GetsBackupAndUpgradedFields()
    {
        string id = "legacy-1";
        string path = Path.Combine(_dir, id + ".json");
        File.WriteAllText(path,
            "{\"id\":\"legacy-1\",\"name\":\"旧课堂\",\"createdAt\":\"2026-11-01T08:00:00\"," +
            "\"events\":[{\"timeMs\":100,\"pageIndex\":0,\"kind\":0,\"strokeId\":\"abc\",\"strokeJson\":\"\"}]," +
            "\"checkpoints\":[]}");

        var loaded = ClassroomSessionStore.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Version);
        Assert.NotNull(loaded.Bookmarks);
        Assert.True(File.Exists(ClassroomSessionStore.PathForBackup(path)), "旧版会话加载前必须写备份");
    }

    // ---- 90 分钟长课：定位预算与顺序 ---------------------------------------

    [Fact]
    public void NinetyMinuteSession_SeekIsWithinOneSecond()
    {
        const long durationMs = 90L * 60 * 1000;
        const int eventCount = 20_000;

        var session = Session("long");
        session.Version = 2;

        for (int i = 0; i < eventCount; i++)
        {
            long time = durationMs * i / eventCount;
            int page = i / 2_000;
            session.Events.Add(Added("long", i + 1, time, page, Stroke(i % 900)));
        }

        var timeline = new InkTimeline(session);

        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        // 冷启动定位：从最近检查点增量重放。
        var warmup = System.Diagnostics.Stopwatch.StartNew();
        _ = timeline.BuildFrame(durationMs / 2);
        warmup.Stop();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
            _ = timeline.BuildFrame(durationMs * i / 9);
        sw.Stop();

        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        _output.WriteLine($"90 分钟会话：{eventCount} 事件，冷启动定位 {warmup.ElapsedMilliseconds}ms，" +
            $"10 次定位合计 {sw.ElapsedMilliseconds}ms，托管内存增量 " +
            $"{(memoryAfter - memoryBefore) / 1024.0 / 1024.0:0.0}MB");

        // 预算按验收第 5 条「任意位置 1 秒内」；这里的 10 次定位总耗时留 10 倍余量，
        // 以避免 CI 抖动造成假失败，同时足以发现从零重放的量级退化。
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"90 分钟会话 10 次定位耗时 {sw.ElapsedMilliseconds}ms，超出预算");
        Assert.True(timeline.IndexAt(durationMs) == eventCount - 1);
        for (int i = 1; i < session.Events.Count; i++)
        {
            Assert.True(session.Events[i].TimeMs >= session.Events[i - 1].TimeMs,
                "事件时间轴不得倒退");
        }
    }
}