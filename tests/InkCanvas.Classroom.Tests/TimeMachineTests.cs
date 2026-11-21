using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Replay;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>板书时光机：时间轴重建、多页/背景、检查点、倍速换算与会话持久化的纯逻辑测试。</summary>
public sealed class TimeMachineTests
{
    // ---- helpers ---------------------------------------------------------

    private static InkStroke Stroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f);
        stroke.AddPoint(x + 5f, 10f);
        stroke.Complete();
        return stroke;
    }

    private static TimelineEvent Add(long timeMs, int page, InkStroke stroke) => new()
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

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "test",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
    };

    private static List<float> FrameXs(IReadOnlyList<InkStroke> frame)
        => frame.Select(s => s.Points[0].X).OrderBy(x => x).ToList();

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- 时间轴 ----------------------------------------------------------

    [Fact]
    public void BuildFrame_AddsStrokesUpToTime()
    {
        var s1 = Stroke(1);
        var s2 = Stroke(2);
        var timeline = new InkTimeline(Session(
            Page(0, 0), Background(0, 0, "White"),
            Add(100, 0, s1), Add(200, 0, s2)));

        Assert.Empty(timeline.BuildFrame(50));
        Assert.Equal(new List<float> { 1 }, FrameXs(timeline.BuildFrame(150)));
        Assert.Equal(new List<float> { 1, 2 }, FrameXs(timeline.BuildFrame(250)));
    }

    [Fact]
    public void BuildFrame_RemovesStrokes()
    {
        var s1 = Stroke(1);
        var s2 = Stroke(2);
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Add(100, 0, s1), Add(200, 0, s2),
            new TimelineEvent
            {
                TimeMs = 300,
                PageIndex = 0,
                Kind = TimelineEventKind.StrokesRemoved,
                StrokeIds = new[] { s1.Id.ToString() },
            }));

        Assert.Equal(2, timeline.BuildFrame(250).Count);
        Assert.Equal(new List<float> { 2 }, FrameXs(timeline.BuildFrame(350)));
    }

    [Fact]
    public void BuildFrame_ClearEmptiesPage()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Add(100, 0, Stroke(1)), Add(200, 0, Stroke(2)),
            new TimelineEvent { TimeMs = 300, PageIndex = 0, Kind = TimelineEventKind.Cleared }));

        Assert.Equal(2, timeline.BuildFrame(250).Count);
        Assert.Empty(timeline.BuildFrame(350));
    }

    [Fact]
    public void BuildFrame_UndoRedoAppliesDiff()
    {
        var s1 = Stroke(1);
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Add(100, 0, s1),
            new TimelineEvent
            {
                TimeMs = 200,
                PageIndex = 0,
                Kind = TimelineEventKind.Undo,
                StrokeIds = new[] { s1.Id.ToString() },
            },
            new TimelineEvent
            {
                TimeMs = 300,
                PageIndex = 0,
                Kind = TimelineEventKind.Redo,
                Payload = InkTimeline.EncodeAddedIds(new[] { s1.Id.ToString() }),
            }));

        Assert.Single(timeline.BuildFrame(150));
        Assert.Empty(timeline.BuildFrame(250));
        Assert.Single(timeline.BuildFrame(350));
    }

    [Fact]
    public void BuildFrame_UndoRedo_ReAddsStrokeWithEmbeddedJson()
    {
        // 该笔画从未作为 StrokeAdded 出现（模拟形状识别替换），仅通过 Redo 载荷重新出现。
        var shape = Stroke(7);
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            new TimelineEvent
            {
                TimeMs = 100,
                PageIndex = 0,
                Kind = TimelineEventKind.Redo,
                Payload = InkTimeline.EncodeAddedStrokes(new[] { shape }),
            }));

        Assert.Equal(new List<float> { 7 }, FrameXs(timeline.BuildFrame(150)));
    }

    // ---- 多页 / 背景 -----------------------------------------------------

    [Fact]
    public void BuildFrame_MultiPageFiltersByCurrentPage()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0), Background(0, 0, "Black"),
            Add(100, 0, Stroke(10)),
            Page(200, 1),
            Add(300, 1, Stroke(20))));

        Assert.Equal(new List<float> { 10 }, FrameXs(timeline.BuildFrame(150)));
        Assert.Empty(timeline.BuildFrame(250));          // 已切到第 2 页，尚无笔画
        Assert.Equal(new List<float> { 20 }, FrameXs(timeline.BuildFrame(350)));
    }

    [Fact]
    public void BackgroundAt_ChangesOverTime()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0), Background(0, 0, "Black"), Background(100, 0, "White")));

        Assert.Equal("Black", timeline.BackgroundAt(50));
        Assert.Equal("White", timeline.BackgroundAt(150));
    }

    [Fact]
    public void PageAt_FollowsPageChanged()
    {
        var timeline = new InkTimeline(Session(Page(0, 0), Page(100, 2)));

        Assert.Equal(0, timeline.PageAt(50));
        Assert.Equal(2, timeline.PageAt(150));
    }

    // ---- 索引 / 检查点 ---------------------------------------------------

    [Fact]
    public void IndexAt_And_TimeAtEvent()
    {
        var timeline = new InkTimeline(Session(
            new TimelineEvent { TimeMs = 0, Kind = TimelineEventKind.PageChanged },
            new TimelineEvent { TimeMs = 100, Kind = TimelineEventKind.PageChanged },
            new TimelineEvent { TimeMs = 200, Kind = TimelineEventKind.PageChanged }));

        Assert.Equal(-1, timeline.IndexAt(-1));
        Assert.Equal(0, timeline.IndexAt(0));
        Assert.Equal(1, timeline.IndexAt(150));
        Assert.Equal(2, timeline.IndexAt(999));
        Assert.Equal(200, timeline.TimeAtEvent(2));
        Assert.Equal(0, timeline.TimeAtEvent(99));
    }

    [Fact]
    public void Checkpoint_GeneratedByCount_AndNearestIsCorrect()
    {
        var events = new List<TimelineEvent> { Page(0, 0) };
        for (int i = 1; i <= 260; i++) events.Add(Add(i, 0, Stroke(i)));
        var timeline = new InkTimeline(Session(events.ToArray()));

        int before = timeline.BuildFrame(260).Count;
        Assert.Equal(260, before);
        Assert.NotEmpty(timeline.Session.Checkpoints);

        var nearest = timeline.NearestCheckpoint(240);
        Assert.NotNull(nearest);
        Assert.True(nearest.EventIndex <= timeline.IndexAt(240));
    }

    [Fact]
    public void BuildFrame_CheckpointIncrementalMatchesExpected()
    {
        var events = new List<TimelineEvent> { Page(0, 0) };
        for (int i = 1; i <= 210; i++) events.Add(Add(i * 10, 0, Stroke(i)));
        var timeline = new InkTimeline(Session(events.ToArray()));

        var frame = timeline.BuildFrame(1500);
        int expected = Enumerable.Range(1, 210).Count(i => i * 10 <= 1500);
        Assert.Equal(expected, frame.Count);
        Assert.NotEmpty(timeline.Session.Checkpoints);
    }

    // ---- 倍速换算 --------------------------------------------------------

    [Fact]
    public void ReplayClock_SpeedScaling_AdvancesElapsed()
    {
        var half = new ReplayClock(TimeSpan.FromMilliseconds(10_000)) { Speed = 0.5f };
        half.Play();
        half.Advance(100);
        Assert.Equal(50f, half.ElapsedMs, 2);

        var doubleSpeed = new ReplayClock(TimeSpan.FromMilliseconds(10_000)) { Speed = 2f };
        doubleSpeed.Play();
        doubleSpeed.Advance(100);
        Assert.Equal(200f, doubleSpeed.ElapsedMs, 2);

        var fast = new ReplayClock(TimeSpan.FromMilliseconds(10_000)) { Speed = 8f };
        fast.Play();
        fast.Advance(100);
        Assert.Equal(800f, fast.ElapsedMs, 2);
    }

    // ---- 会话持久化 ------------------------------------------------------

    [Fact]
    public void Session_SaveLoad_RoundTrips()
    {
        string dir = TempDir();
        try
        {
            var s1 = Stroke(42);
            var session = Session(
                Page(0, 0), Background(0, 0, "Grid"),
                Add(100, 0, s1),
                new TimelineEvent
                {
                    TimeMs = 200,
                    PageIndex = 0,
                    Kind = TimelineEventKind.StrokesRemoved,
                    StrokeIds = new[] { s1.Id.ToString() },
                });
            session.Name = "课堂板书";

            string path = ClassroomSessionStore.Save(session, dir);
            Assert.True(File.Exists(path));

            var loaded = ClassroomSessionStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(session.Id, loaded!.Id);
            Assert.Equal("课堂板书", loaded.Name);
            Assert.Equal(4, loaded.Events.Count);
            Assert.Equal(TimelineEventKind.StrokeAdded, loaded.Events[2].Kind);
            Assert.Single(loaded.Events[3].StrokeIds!);

            var restored = StrokeJsonCodec.Deserialize(loaded.Events[2].StrokeJson!);
            Assert.NotNull(restored);
            Assert.Equal(2, restored!.Count);
            Assert.Equal(42f, restored.Points[0].X, 3);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildFrame_AfterSaveLoad_MatchesOriginal()
    {
        string dir = TempDir();
        try
        {
            var session = Session(
                Page(0, 0),
                Add(100, 0, Stroke(1)),
                Add(200, 0, Stroke(2)));
            var original = new InkTimeline(session).BuildFrame(250);

            string path = ClassroomSessionStore.Save(session, dir);
            var loaded = ClassroomSessionStore.Load(path);
            var replayed = new InkTimeline(loaded!).BuildFrame(250);

            Assert.Equal(original.Count, replayed.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}