using InkCanvas.Core.Classroom;
using InkCanvas.Features.Board;
using InkCanvas.Features.Classroom;
using InkCanvas.Ink;
using System.Numerics;
using System.Text.Json;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 录制增强：暂停 / 继续计时、事件去重与单调、视图事件记录与场景恢复、未完成日志检测。
/// </summary>
public sealed class RecordingEnhancementTests
{
    // ---- helpers ---------------------------------------------------------

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-recording-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RemoveDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 测试清理失败不影响断言 */ }
    }

    private static InkRecorder NewRecorder(string directory)
    {
        var recorder = new InkRecorder(new BoardService()) { SessionDirectory = directory };
        recorder.Start("录制测试");
        return recorder;
    }

    private static TimelineEvent Ev(long timeMs, TimelineEventKind kind, string payload = null, int page = 0)
        => new() { TimeMs = timeMs, PageIndex = page, Kind = kind, Payload = payload };

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "test",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
    };

    // ---- 暂停 / 继续 -----------------------------------------------------

    [Fact]
    public void Pause_DiscardsAppendedEvents()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            recorder.Append(Ev(100, TimelineEventKind.RosterDrawn, "张三"));
            int before = recorder.Session.Events.Count;

            recorder.Pause();
            recorder.Append(Ev(200, TimelineEventKind.RosterDrawn, "李四"));
            recorder.Append(Ev(300, TimelineEventKind.TimerStarted, "60000"));

            Assert.True(recorder.IsPaused);
            Assert.Equal(before, recorder.Session.Events.Count);
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void Pause_DiscardsAutomaticViewEventsAndResumeRestoresThem()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = new InkRecorder(board) { SessionDirectory = dir };
            recorder.Start("录制测试");
            var controller = board.Current;
            int before = recorder.Session.Events.Count;

            recorder.Pause();
            controller.ViewTransform = Matrix3x2.CreateScale(2f);
            Assert.Equal(before, recorder.Session.Events.Count);

            recorder.Resume();
            controller.ViewTransform = Matrix3x2.CreateScale(3f);
            Assert.Equal(before + 1, recorder.Session.Events.Count);
            Assert.Equal(TimelineEventKind.ViewChanged, recorder.Session.Events[^1].Kind);
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void PauseAndResume_DoNotWriteTimelineEvents()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            int before = recorder.Session.Events.Count;

            recorder.Pause();
            recorder.Resume();

            Assert.False(recorder.IsPaused);
            Assert.Equal(before, recorder.Session.Events.Count);
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void Resume_ElapsedExcludesPausedDuration()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            recorder.Append(Ev(1, TimelineEventKind.RosterDrawn, "计时"));

            long before = recorder.ElapsedMs;
            recorder.Pause();
            Thread.Sleep(220);
            recorder.Resume();
            long after = recorder.ElapsedMs;

            Assert.True(after >= before);
            Assert.True(after - before < 120, $"暂停时长未扣除：before={before}, after={after}");
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void Elapsed_WhilePaused_StaysAtPauseBoundary()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            long before = recorder.ElapsedMs;

            recorder.Pause();
            Thread.Sleep(120);
            long mid = recorder.ElapsedMs;
            recorder.Resume();

            // 暂停期间读到的仍是「暂停开始时刻」的录制时间（连续、无空档）。
            Assert.True(mid - before < 60, $"暂停期间时间前进了 {mid - before}ms");
            Assert.True(recorder.ElapsedMs - before < 90);
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void RecordedTimestamps_RemainMonotonicAcrossPauseResume()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            recorder.Append(Ev(500, TimelineEventKind.RosterDrawn, "甲"));
            recorder.Pause();
            Thread.Sleep(150);
            recorder.Resume();
            recorder.Append(Ev(1000, TimelineEventKind.TimerStarted, "1000"));

            var times = recorder.Session.Events.Select(e => e.TimeMs).ToList();
            Assert.Equal(times.OrderBy(t => t), times);
        }
        finally { RemoveDir(dir); }
    }

    // ---- 去重与排序 ------------------------------------------------------

    [Fact]
    public void Append_DedupesAdjacentIdenticalEvents()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            var duplicate = Ev(500, TimelineEventKind.RosterDrawn, "张三");

            recorder.Append(duplicate);
            recorder.Append(Ev(500, TimelineEventKind.RosterDrawn, "张三"));

            Assert.Single(recorder.Session.Events, e => e.Kind == TimelineEventKind.RosterDrawn);
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void Append_KeepsNonAdjacentDuplicateEvents()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            recorder.Append(Ev(500, TimelineEventKind.RosterDrawn, "张三"));
            recorder.Append(Ev(600, TimelineEventKind.TimerStarted, "5000"));
            recorder.Append(Ev(500, TimelineEventKind.RosterDrawn, "张三"));

            Assert.Equal(2, recorder.Session.Events.Count(e => e.Kind == TimelineEventKind.RosterDrawn));
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void Append_DoesNotDedupeDifferentKindsOrStrokeIds()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            recorder.Append(Ev(500, TimelineEventKind.RosterDrawn, "张三"));
            recorder.Append(Ev(500, TimelineEventKind.TimerStarted, "张三"));

            var byStroke = new TimelineEvent
            {
                TimeMs = 700,
                PageIndex = 0,
                Kind = TimelineEventKind.StrokeAdded,
                StrokeId = "a",
                Payload = "same",
            };
            var byStroke2 = new TimelineEvent
            {
                TimeMs = 700,
                PageIndex = 0,
                Kind = TimelineEventKind.StrokeAdded,
                StrokeId = "b",
                Payload = "same",
            };
            recorder.Append(byStroke);
            recorder.Append(byStroke2);

            // 初始 PageChanged + BackgroundChanged 各一条，再叠加 4 条外部事件。
            Assert.Equal(6, recorder.Session.Events.Count);
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void Append_DedupeUsesFirst64PayloadChars()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            string prefix = new string('x', 64);
            recorder.Append(Ev(500, TimelineEventKind.RosterDrawn, prefix + "-first"));
            recorder.Append(Ev(500, TimelineEventKind.RosterDrawn, prefix + "-second"));

            // 初始 PageChanged + BackgroundChanged 各一条，重复事件只入一条。
            Assert.Equal(3, recorder.Session.Events.Count);
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void Append_EarlierEventIsInsertedInTimeOrder()
    {
        string dir = TempDir();
        try
        {
            using var recorder = NewRecorder(dir);
            recorder.Append(Ev(800, TimelineEventKind.RosterDrawn, "后"));
            recorder.Append(Ev(200, TimelineEventKind.RosterDrawn, "前"));
            recorder.Append(Ev(500, TimelineEventKind.RosterDrawn, "中"));

            var times = recorder.Session.Events.Select(e => e.TimeMs).ToList();
            Assert.Equal(times.OrderBy(t => t), times);
            Assert.Equal(200, times[^3]);
        }
        finally { RemoveDir(dir); }
    }

    // ---- 视图事件与场景恢复 ----------------------------------------------

    [Fact]
    public void SceneAt_RestoresViewStateFromTimeline()
    {
        var timeline = new InkTimeline(Session(
            Ev(0, TimelineEventKind.ViewChanged,
                InkTimeline.EncodeViewChanged(1.5, 120, 80)),
            Ev(1000, TimelineEventKind.ViewChanged,
                InkTimeline.EncodeViewChanged(2.0, -30, 40))));

        var early = timeline.SceneAt(0);
        Assert.Equal(1.5, early.ViewScale, 3);
        Assert.Equal(120, early.ViewTx, 3);
        Assert.Equal(80, early.ViewTy, 3);

        var late = timeline.SceneAt(1500);
        Assert.Equal(2.0, late.ViewScale, 3);
        Assert.Equal(-30, late.ViewTx, 3);
        Assert.Equal(40, late.ViewTy, 3);
    }

    [Fact]
    public void SceneAt_ViewStateDefaultsAndIgnoresInvalidPayload()
    {
        var empty = new InkTimeline(Session()).SceneAt(0);
        Assert.Equal(1, empty.ViewScale);
        Assert.Equal(0, empty.ViewTx);
        Assert.Equal(0, empty.ViewTy);

        var invalid = new InkTimeline(Session(
            Ev(100, TimelineEventKind.ViewChanged, "not-json"))).SceneAt(150);
        Assert.Equal(1, invalid.ViewScale);
    }

    [Fact]
    public void EncodeViewChanged_RoundTripsAndUsesCamelCaseKeys()
    {
        string payload = InkTimeline.EncodeViewChanged(1.25, 10.5, -3.5);
        using var document = JsonDocument.Parse(payload);
        Assert.True(document.RootElement.TryGetProperty("scale", out _));
        Assert.True(document.RootElement.TryGetProperty("tx", out _));
        Assert.True(document.RootElement.TryGetProperty("ty", out _));

        var decoded = InkTimeline.DecodeViewChanged(payload);
        Assert.True(decoded.IsValid);
        Assert.Equal(1.25, decoded.Scale, 3);
        Assert.Equal(10.5, decoded.Tx, 3);
        Assert.Equal(-3.5, decoded.Ty, 3);
    }

    [Fact]
    public void InkController_RaisesViewChangedOnlyWhenTransformChanges()
    {
        using var controller = new InkController();
        var seen = new List<Matrix3x2>();
        controller.ViewChanged += seen.Add;

        var transform = Matrix3x2.CreateScale(2f) * Matrix3x2.CreateTranslation(40f, 60f);
        controller.ViewTransform = transform;
        controller.ViewTransform = transform;
        controller.SetView(Matrix3x2.Identity);

        Assert.Equal(2, seen.Count);
        Assert.Equal(transform, seen[0]);
        Assert.Equal(Matrix3x2.Identity, seen[1]);
    }

    // ---- 未完成日志检测 --------------------------------------------------

    [Fact]
    public void PendingJournalIds_DetectsUnfinishedRecording()
    {
        string dir = TempDir();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            using (var journal = SessionJournal.Open(id, dir, "未完成录制", DateTime.Now))
            {
                journal.Append(Ev(100, TimelineEventKind.RosterDrawn, "张三"));
                journal.Flush();
            }

            Assert.Contains(id, ClassroomSessionStore.PendingJournalIds(dir));
            Assert.True(ClassroomSessionStore.HasPendingJournal(id, dir));
            Assert.NotEmpty(ClassroomSessionStore.PendingJournalIds(dir));
        }
        finally { RemoveDir(dir); }
    }

    [Fact]
    public void PendingJournalIds_EmptyWhenNoJournal()
    {
        string dir = TempDir();
        try
        {
            Assert.Empty(ClassroomSessionStore.PendingJournalIds(dir));
            Assert.False(ClassroomSessionStore.HasPendingJournal("missing", dir));
        }
        finally { RemoveDir(dir); }
    }
}