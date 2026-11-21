using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>批次 C：笔画抽稀、增量日志恢复、时间检查点与 MP4 导出的纯逻辑测试。</summary>
public sealed class BatchCTests
{
    // ---- helpers ---------------------------------------------------------

    private static InkStroke Stroke(params (float X, float Y)[] points)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        foreach (var (x, y) in points) stroke.AddPoint(x, y, pressure: 0.4f, timeMs: points.Length);
        stroke.Complete();
        return stroke;
    }

    private static TimelineEvent AddEvent(long timeMs, int page, InkStroke stroke) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokeAdded,
        StrokeId = stroke.Id.ToString(),
        StrokeJson = StrokeJsonCodec.Serialize(StrokeSimplifier.Simplify(stroke)),
    };

    private static TimelineEvent PageEvent(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PageChanged,
    };

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "batch-c",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
    };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-batchc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (float X, float Y)[] Arc(int count, float radius)
    {
        var points = new (float, float)[count];
        for (int i = 0; i < count; i++)
        {
            double angle = Math.PI * i / (count - 1);
            points[i] = ((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)));
        }
        return points;
    }

    // ---- StrokeSimplifier ------------------------------------------------

    [Fact]
    public void Simplify_KeepsFirstAndLastPoints()
    {
        var stroke = Stroke(Arc(60, 100f));
        var simplified = StrokeSimplifier.Simplify(stroke, 0.8);

        Assert.True(simplified.Points.Count >= 2);
        Assert.Equal(stroke.Points[0].X, simplified.Points[0].X);
        Assert.Equal(stroke.Points[0].Y, simplified.Points[0].Y);
        Assert.Equal(stroke.Points[^1].X, simplified.Points[^1].X);
        Assert.Equal(stroke.Points[^1].Y, simplified.Points[^1].Y);
    }

    [Fact]
    public void Simplify_ReducesPointCount()
    {
        var stroke = Stroke(Arc(120, 100f));
        var simplified = StrokeSimplifier.Simplify(stroke, 0.8);

        Assert.True(simplified.Points.Count < stroke.Points.Count);
    }

    [Fact]

    public void Simplify_MaxDeviationFromPolylineWithinTolerance()
    {
        const double tolerance = 1.5;
        var original = Arc(100, 120f);
        var simplified = StrokeSimplifier.Simplify(Stroke(original), tolerance);

        foreach (var (x, y) in original)
        {
            var point = new System.Numerics.Vector2(x, y);
            float best = float.MaxValue;
            for (int i = 1; i < simplified.Points.Count; i++)
            {
                float distance = InkStroke.DistanceToSegment(
                    point, simplified.Points[i - 1].Position, simplified.Points[i].Position);
                if (distance < best) best = distance;
            }
            Assert.True(best <= tolerance + 0.01f, $"最大偏离 {best} 超过容差 {tolerance}");
        }
    }

    [Fact]
    public void Simplify_StraightLine_KeepsOnlyEndpoints()
    {
        var stroke = Stroke(Enumerable.Range(0, 50).Select(i => (i * 2f, i * 1f)).ToArray());
        var simplified = StrokeSimplifier.Simplify(stroke);

        Assert.Equal(2, simplified.Points.Count);
    }

    [Fact]
    public void Simplify_EmptyAndSinglePoint_DoNotThrow()
    {
        Assert.Empty(StrokeSimplifier.Simplify(Array.Empty<InkPoint>()));

        var single = Stroke((3f, 4f));
        var simplified = StrokeSimplifier.Simplify(single);
        Assert.Single(simplified.Points);
        Assert.Equal(3f, simplified.Points[0].X);
    }

    [Fact]
    public void Simplify_ReturnsNewStroke_PreservingAttributesAndPressure()
    {
        var stroke = Stroke(Arc(40, 50f));
        stroke.Attributes.BaseWidth = 7f;
        stroke.Attributes.IsHighlighter = true;
        stroke.IsRecognizedShape = true;

        var simplified = StrokeSimplifier.Simplify(stroke, 0.5);

        Assert.NotSame(stroke, simplified);
        Assert.Equal(stroke.Id, simplified.Id);
        Assert.Equal(stroke.Points.Count, stroke.Count);
        Assert.True(simplified.IsCompleted);
        Assert.True(simplified.IsRecognizedShape);
        Assert.Equal(7f, simplified.Attributes.BaseWidth);
        Assert.True(simplified.Attributes.IsHighlighter);
        Assert.All(simplified.Points, p => Assert.Equal(0.4f, p.Pressure));
    }

    [Fact]
    public void Simplify_ReplayBoundsAreClose()
    {
        var stroke = Stroke(Arc(80, 60f));
        var simplified = StrokeSimplifier.Simplify(stroke, 0.8);

        var original = stroke.Bounds;
        var reduced = simplified.Bounds;

        Assert.True(Math.Abs(original.Left - reduced.Left) < 2f);
        Assert.True(Math.Abs(original.Top - reduced.Top) < 2f);
        Assert.True(Math.Abs(original.Right - reduced.Right) < 2f);
        Assert.True(Math.Abs(original.Bottom - reduced.Bottom) < 2f);
    }

    // ---- SessionJournal ---------------------------------------------------

    [Fact]
    public void Journal_AppendAndRecover_ReturnsSameEvents()
    {
        string dir = TempDir();
        try
        {
            var first = Stroke((1f, 1f), (2f, 2f));
            var second = Stroke((3f, 3f), (4f, 4f));
            var session = Session(PageEvent(0, 0), AddEvent(10, 0, first), AddEvent(20, 0, second));

            using (var journal = SessionJournal.Open(session.Id, dir, session.Name, session.CreatedAt))
            {
                foreach (var ev in session.Events) journal.Append(ev);
                journal.Flush();
            }

            var recovered = SessionJournal.Recover(session.Id, dir);

            Assert.NotNull(recovered);
            Assert.Equal(session.Id, recovered.Id);
            Assert.Equal(session.Name, recovered.Name);
            Assert.Equal(session.Events.Count, recovered.Events.Count);
            Assert.Equal(session.Events.Select(e => (e.TimeMs, e.Kind)),
                         recovered.Events.Select(e => (e.TimeMs, e.Kind)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Journal_TruncatedLastLine_RecoversWithoutThrowing()
    {
        string dir = TempDir();
        try
        {
            var stroke = Stroke((1f, 1f), (2f, 2f));
            var session = Session(PageEvent(0, 0), AddEvent(100, 0, stroke));

            using (var journal = SessionJournal.Open(session.Id, dir, session.Name, session.CreatedAt))
            {
                foreach (var ev in session.Events) journal.Append(ev);
                journal.Flush();
            }

            File.AppendAllText(SessionJournal.PathFor(session.Id, dir), "{\"type\":\"event\",\"event\":{\"timeMs\":2");

            var recovered = SessionJournal.Recover(session.Id, dir);

            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Events.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Journal_MergesWithSnapshot_AndPrefersLongerEventStream()
    {
        string dir = TempDir();
        try
        {
            var s1 = Stroke((1f, 1f), (2f, 2f));
            var s2 = Stroke((3f, 3f), (4f, 4f));
            var s3 = Stroke((5f, 5f), (6f, 6f));

            var snapshot = Session(PageEvent(0, 0), AddEvent(10, 0, s1), AddEvent(20, 0, s2));
            ClassroomSessionStore.SaveSnapshot(snapshot, dir);

            using (var journal = SessionJournal.Open(snapshot.Id, dir, snapshot.Name, snapshot.CreatedAt))
            {
                journal.Append(PageEvent(0, 0));
                journal.Append(AddEvent(10, 0, s1));
                journal.Append(AddEvent(20, 0, s2));
                journal.Append(AddEvent(30, 0, s3));
                journal.Flush();
            }

            var recovered = SessionJournal.Recover(snapshot.Id, dir);

            Assert.NotNull(recovered);
            Assert.Equal(4, recovered.Events.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Journal_RecoveredSessionMatchesFullSave()
    {
        string dir = TempDir();
        try
        {
            var stroke = Stroke(Arc(30, 20f));
            var session = Session(PageEvent(0, 0), AddEvent(50, 0, stroke));

            using (var journal = SessionJournal.Open(session.Id, dir, session.Name, session.CreatedAt))
            {
                foreach (var ev in session.Events) journal.Append(ev);
            }
            ClassroomSessionStore.Save(session, dir);

            var saved = ClassroomSessionStore.Load(ClassroomSessionStore.PathFor(session.Id, dir));
            var recovered = ClassroomSessionStore.LoadWithJournal(session.Id, dir);

            Assert.NotNull(saved);
            Assert.NotNull(recovered);
            Assert.Equal(saved.Events.Count, recovered.Events.Count);
            Assert.Equal(saved.Events.Select(e => e.Kind), recovered.Events.Select(e => e.Kind));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Journal_OldSessionWithoutJournal_LoadsNormally()
    {
        string dir = TempDir();
        try
        {
            var session = Session(PageEvent(0, 0));

            // v1 会话：没有 version / bookmarks。
            string path = ClassroomSessionStore.PathFor(session.Id, dir);
            File.WriteAllText(path,
                "{\"id\":\"" + session.Id + "\",\"name\":\"old\",\"createdAt\":\"2024-01-01T00:00:00\"," +
                "\"events\":[{\"timeMs\":0,\"pageIndex\":0,\"kind\":0}],\"checkpoints\":[]}");

            var loaded = ClassroomSessionStore.LoadWithJournal(session.Id, dir);

            Assert.NotNull(loaded);
            Assert.Single(loaded.Events);
            Assert.Empty(ClassroomSessionStore.PendingJournalIds(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- 时间检查点 -------------------------------------------------------

    [Fact]
    public void Checkpoints_GeneratedByTimeInterval()
    {
        var events = new List<TimelineEvent>();
        for (int i = 0; i < 30; i++) events.Add(AddEvent(i * 1000L, 0, Stroke((i, i), (i + 1, i + 1))));
        var session = Session(events.ToArray());

        var timeline = new InkTimeline(session, eventsPerCheckpoint: 1000, checkpointIntervalMs: 10_000);
        timeline.BuildFrame(29_000);

        Assert.True(session.Checkpoints.Count >= 2);

        long previous = 0;
        foreach (var cp in session.Checkpoints)
        {
            Assert.True(cp.TimeMs - previous >= 10_000, $"检查点间隔 {cp.TimeMs - previous}ms 小于 10s");
            previous = cp.TimeMs;
        }
    }

    [Fact]
    public void NearestCheckpoint_IsClosestAndNeverAheadOfTarget()
    {
        var events = new List<TimelineEvent>();
        for (int i = 0; i < 60; i++)
        {
            // 同一毫秒内的多个事件（PageChanged + StrokeAdded）用于验证事件索引过滤。
            events.Add(PageEvent(i * 500L, 0));
            events.Add(AddEvent(i * 500L, 0, Stroke((i, i), (i + 1, i + 1))));
        }
        var session = Session(events.ToArray());

        var timeline = new InkTimeline(session, eventsPerCheckpoint: 10, checkpointIntervalMs: 100_000);
        timeline.BuildFrame(30_000);

        Assert.NotEmpty(session.Checkpoints);
        foreach (long time in new long[] { 0, 750, 5_000, 17_250, 30_000 })
        {
            int cutoff = timeline.IndexAt(time);
            var checkpoint = timeline.NearestCheckpoint(time);

            bool hasEligible = session.Checkpoints.Any(cp => cp.EventIndex <= cutoff && cp.TimeMs <= time);
            if (!hasEligible)
            {
                // 目标早于第一个检查点：从头重放，返回 null 是正确的。
                Assert.Null(checkpoint);
                continue;
            }

            Assert.NotNull(checkpoint);
            Assert.True(checkpoint.EventIndex <= cutoff);
            Assert.True(checkpoint.TimeMs <= time);

            var expected = session.Checkpoints
                .Where(cp => cp.EventIndex <= cutoff && cp.TimeMs <= time)
                .OrderByDescending(cp => cp.TimeMs)
                .ThenByDescending(cp => cp.EventIndex)
                .First();
            Assert.Equal(expected.EventIndex, checkpoint.EventIndex);
            Assert.Equal(expected.TimeMs, checkpoint.TimeMs);
        }

        Assert.Null(timeline.NearestCheckpoint(-1));
    }

    // ---- 导出参数与帧采样 -------------------------------------------------

    [Fact]
    public void ExportOptions_ValidateRejectsBadRangesAndPaths()
    {
        Assert.Throws<ArgumentException>(() =>
            TimeMachineExporter.Validate(new ExportOptions("a.mp4", 100, 100)));
        Assert.Throws<ArgumentException>(() =>
            TimeMachineExporter.Validate(new ExportOptions("a.mp4", -1, 100)));
        Assert.Throws<ArgumentException>(() =>
            TimeMachineExporter.Validate(new ExportOptions("a.avi", 0, 1000)));
        Assert.Throws<ArgumentException>(() =>
            TimeMachineExporter.Validate(new ExportOptions("  ", 0, 1000)));
        Assert.Throws<ArgumentException>(() =>
            TimeMachineExporter.Validate(new ExportOptions("a.mp4", 0, 1000, Fps: 0)));
        Assert.Throws<ArgumentException>(() =>
            TimeMachineExporter.Validate(new ExportOptions("a.mp4", 0, 1000, BitRate: 1000)));

        TimeMachineExporter.Validate(new ExportOptions("a.mp4", 0, 1000));
    }

    [Fact]
    public void ExportOptions_SampleFrameTimes_RespectsFpsAndBounds()
    {
        var options = new ExportOptions("a.mp4", 0, 2000, Fps: 30);
        var times = TimeMachineExporter.SampleFrameTimes(options);

        Assert.Equal(60, times.Count);
        Assert.Equal(0, times[0]);
        Assert.True(times[^1] <= 2000);
        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i] > times[i - 1]);

        var single = TimeMachineExporter.SampleFrameTimes(new ExportOptions("a.mp4", 500, 1300, Fps: 1));
        Assert.Single(single);
        Assert.Equal(500, single[0]);
    }
}