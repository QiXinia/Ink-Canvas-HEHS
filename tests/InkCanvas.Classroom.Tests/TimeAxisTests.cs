using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Features.Classroom;
using System.Text.Json;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 批次 C 时间轴统一：跨源事件合并、场景恢复、自动章节书签与会话 v2 书签持久化。
/// </summary>
public sealed class TimeAxisTests
{
    // ---- helpers ---------------------------------------------------------

    private static TimelineEvent Ev(long timeMs, TimelineEventKind kind, string payload = null, int page = 0)
        => new() { TimeMs = timeMs, PageIndex = page, Kind = kind, Payload = payload };

    private static TimelineEvent Page(long timeMs, int page) => Ev(timeMs, TimelineEventKind.PageChanged, page: page);

    private static TimelineEvent Background(long timeMs, int page, string name)
        => Ev(timeMs, TimelineEventKind.BackgroundChanged, name, page);

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "test",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
    };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-timeaxis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- 跨源事件顺序 ----------------------------------------------------

    [Fact]
    public void RecorderAppend_MergesCrossSourceEventsInTimeOrder()
    {
        var board = new BoardService();
        using var recorder = new InkRecorder(board);
        recorder.Start("统一时间轴");

        recorder.Append(Ev(500, TimelineEventKind.PptSlideChanged, "2"));
        recorder.Append(Ev(100, TimelineEventKind.RosterDrawn, "张三"));
        recorder.Append(Ev(300, TimelineEventKind.TimerStarted, "60000"));
        recorder.Append(Ev(900, TimelineEventKind.TimerEnded));

        var kinds = recorder.Session.Events.Select(e => e.Kind).ToList();
        Assert.Contains(TimelineEventKind.PptSlideChanged, kinds);
        Assert.Contains(TimelineEventKind.RosterDrawn, kinds);
        Assert.Contains(TimelineEventKind.TimerStarted, kinds);
        Assert.Contains(TimelineEventKind.TimerEnded, kinds);

        var times = recorder.Session.Events.Select(e => e.TimeMs).ToList();
        Assert.Equal(times.OrderBy(t => t), times);
        Assert.Equal(100, times[times.IndexOf(100)]);
    }

    [Fact]
    public void RecorderAppend_WhenNotRecording_IsIgnored()
    {
        var recorder = new InkRecorder(new BoardService());
        Exception exception = Record.Exception(() =>
            recorder.Append(Ev(10, TimelineEventKind.RosterDrawn, "李四")));
        Assert.Null(exception);
        Assert.Null(recorder.Session);
    }

    [Fact]
    public void RecorderAppend_ConcurrentDuringRecording_DoesNotThrow()
    {
        var board = new BoardService();
        using var recorder = new InkRecorder(board);
        recorder.Start("并发");

        Exception exception = Record.Exception(() => Parallel.For(0, 400, i =>
        {
            var kind = (i % 4) switch
            {
                0 => TimelineEventKind.PptSlideChanged,
                1 => TimelineEventKind.RosterDrawn,
                2 => TimelineEventKind.TimerStarted,
                _ => TimelineEventKind.PptInkChanged,
            };
            recorder.Append(Ev(i * 10L, kind, "1", i % 3));
        }));

        Assert.Null(exception);
        var times = recorder.Session.Events.Select(e => e.TimeMs).ToList();
        Assert.Equal(times.OrderBy(t => t), times);
    }

    // ---- SceneAt 场景恢复 ------------------------------------------------

    [Fact]
    public void SceneAt_RestoresPageAndBackgroundAtAnyTime()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0), Background(0, 0, "Black"),
            Page(100, 2), Background(100, 2, "White")));

        var early = timeline.SceneAt(50);
        Assert.Equal(0, early.PageIndex);
        Assert.Equal("Black", early.Background);

        var late = timeline.SceneAt(150);
        Assert.Equal(2, late.PageIndex);
        Assert.Equal("White", late.Background);
    }

    [Fact]
    public void SceneAt_RestoresPptSlideRosterAndTimer()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Ev(100, TimelineEventKind.PptSlideChanged, "3"),
            Ev(200, TimelineEventKind.RosterDrawn, "王五"),
            Ev(300, TimelineEventKind.TimerStarted, "600000")));

        var atPpt = timeline.SceneAt(150);
        Assert.Equal(3, atPpt.PptSlideIndex);
        Assert.Equal("", atPpt.RosterName);
        Assert.Equal(0, atPpt.TimerRemainingMs);

        var atRoster = timeline.SceneAt(250);
        Assert.Equal(3, atRoster.PptSlideIndex);
        Assert.Equal("王五", atRoster.RosterName);

        var atTimer = timeline.SceneAt(350);
        Assert.Equal(600_000, atTimer.TimerRemainingMs);

        var stopped = new InkTimeline(Session(
            Ev(100, TimelineEventKind.TimerStarted, "600000"),
            Ev(400, TimelineEventKind.TimerStopped, "120000"),
            Ev(700, TimelineEventKind.TimerEnded)));
        Assert.Equal(600_000, stopped.SceneAt(200).TimerRemainingMs);
        Assert.Equal(120_000, stopped.SceneAt(500).TimerRemainingMs);
        Assert.Equal(0, stopped.SceneAt(800).TimerRemainingMs);
    }

    [Fact]
    public void SceneAt_NoEvents_UsesDefaults()
    {
        var scene = new InkTimeline(Session()).SceneAt(0);
        Assert.Equal(0, scene.PageIndex);
        Assert.Equal("Black", scene.Background);
        Assert.Equal(0, scene.PptSlideIndex);
        Assert.Equal("", scene.RosterName);
        Assert.Equal(0, scene.TimerRemainingMs);
    }

    // ---- 自动书签规则 ----------------------------------------------------

    [Fact]
    public void AutoBookmarks_PageAndClearNamedByPageNumber()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Ev(5000, TimelineEventKind.Cleared, page: 0),
            Page(10000, 1)));

        var bookmarks = timeline.AutoBookmarks();
        Assert.Equal(3, bookmarks.Count);
        Assert.Equal("第 1 页", bookmarks[0].Name);
        Assert.Equal("清空第 1 页", bookmarks[1].Name);
        Assert.Equal("第 2 页", bookmarks[2].Name);
        Assert.All(bookmarks, b => Assert.True(b.Auto));
    }

    [Fact]
    public void AutoBookmarks_PptRosterAndTimerEnded()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Ev(5000, TimelineEventKind.PptSlideChanged, "2"),
            Ev(9000, TimelineEventKind.RosterDrawn, "张三"),
            Ev(14000, TimelineEventKind.TimerEnded)));

        var bookmarks = timeline.AutoBookmarks();
        Assert.Equal(4, bookmarks.Count);
        Assert.Contains(bookmarks, b => b.Name == "PPT 第 2 页" && b.Kind == "ppt");
        Assert.Contains(bookmarks, b => b.Name == "点名：张三" && b.Kind == "roster");
        Assert.Equal("倒计时结束", bookmarks[^1].Name);
        Assert.Equal(14000, bookmarks[^1].TimeMs);
    }

    [Fact]
    public void AutoBookmarks_MergesEventsWithinTwoSeconds()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Ev(1000, TimelineEventKind.Cleared, page: 0),
            Ev(1500, TimelineEventKind.RosterDrawn, "赵六"),
            Page(3000, 1)));

        var bookmarks = timeline.AutoBookmarks();
        Assert.Equal(2, bookmarks.Count);
        Assert.Equal("点名：赵六", bookmarks[0].Name);
        Assert.Equal(0, bookmarks[0].TimeMs);
        Assert.Equal("第 2 页", bookmarks[1].Name);
    }

    [Fact]
    public void AutoBookmarks_KeepsEventsTwoSecondsOrMoreApart()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Page(2000, 1)));

        var bookmarks = timeline.AutoBookmarks();
        Assert.Equal(2, bookmarks.Count);
        Assert.Equal(0, bookmarks[0].TimeMs);
        Assert.Equal(2000, bookmarks[1].TimeMs);
    }

    [Fact]
    public void AutoBookmarks_ApplyWritesIntoSession_AndAddBookmarkIsSorted()
    {
        var session = Session(Page(0, 0), Page(5000, 1));
        var timeline = new InkTimeline(session);
        timeline.ApplyAutoBookmarks();

        Assert.Equal(2, session.Bookmarks.Count);
        Assert.Equal(2, session.Version);

        int index = timeline.AddBookmark(2500, "手写要点", "manual");
        Assert.Equal(1, index);
        Assert.Equal("手写要点", session.Bookmarks[1].Name);
        Assert.Equal(new long[] { 0, 2500, 5000 },
            session.Bookmarks.Select(b => b.TimeMs).ToArray());
    }

    // ---- 书签重命名 / 删除 ----------------------------------------------

    [Fact]
    public void Bookmarks_RenameAndRemove()
    {
        var session = Session(Page(0, 0), Page(5000, 1));
        var timeline = new InkTimeline(session);
        timeline.ApplyAutoBookmarks();
        Assert.Equal(2, session.Bookmarks.Count);

        Assert.True(timeline.RenameBookmark(1, " 例题讲解 "));
        Assert.Equal("例题讲解", session.Bookmarks[1].Name);
        Assert.False(session.Bookmarks[1].Auto);

        Assert.True(timeline.RemoveBookmark(1));
        Assert.Single(session.Bookmarks);
        Assert.False(timeline.RemoveBookmark(9));
    }

    [Fact]
    public void Bookmarks_RenamedAutoBookmark_IsNotOverwrittenByAutoRules()
    {
        var session = Session(Page(0, 0), Page(5000, 1));
        var timeline = new InkTimeline(session);
        timeline.ApplyAutoBookmarks();

        timeline.RenameBookmark(1, "自定义名称");
        timeline.ApplyAutoBookmarks();

        Assert.Equal(2, session.Bookmarks.Count);
        Assert.Equal("自定义名称", session.Bookmarks[1].Name);
        Assert.False(session.Bookmarks[1].Auto);
    }

    // ---- 会话 v2 持久化 --------------------------------------------------

    [Fact]
    public void SessionV2_SaveLoad_RoundTripsBookmarks()
    {
        string dir = TempDir();
        try
        {
            var session = Session(Page(0, 0), Page(4000, 1));
            session.Version = 2;
            session.Bookmarks.Add(new SessionBookmark
            {
                TimeMs = 4000,
                PageIndex = 1,
                Name = "章节二",
                Auto = false,
                Kind = "manual",
            });

            string path = ClassroomSessionStore.Save(session, dir);
            var loaded = ClassroomSessionStore.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Version);
            Assert.Single(loaded.Bookmarks);
            Assert.Equal("章节二", loaded.Bookmarks[0].Name);
            Assert.Equal(4000, loaded.Bookmarks[0].TimeMs);
            Assert.Equal(1, loaded.Bookmarks[0].PageIndex);
            Assert.False(loaded.Bookmarks[0].Auto);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LegacyV1Session_WithoutVersionOrBookmarks_Loads()
    {
        string dir = TempDir();
        try
        {
            string json = """
            {
              "id": "legacy-v1",
              "name": "旧会话",
              "createdAt": "2025-01-01T00:00:00",
              "events": [
                { "timeMs": 0, "pageIndex": 0, "kind": 3 },
                { "timeMs": 5000, "pageIndex": 1, "kind": 3 }
              ],
              "checkpoints": []
            }
            """;
            string path = Path.Combine(dir, "legacy-v1.json");
            File.WriteAllText(path, json);

            var loaded = ClassroomSessionStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal("旧会话", loaded!.Name);
            Assert.Equal(2, loaded.Events.Count);
            Assert.NotNull(loaded.Bookmarks);
            Assert.Empty(loaded.Bookmarks);

            var timeline = new InkTimeline(loaded);
            timeline.ApplyAutoBookmarks();
            Assert.Equal(2, loaded.Bookmarks.Count);
            Assert.Equal("第 2 页", loaded.Bookmarks[1].Name);
            Assert.Equal(2, loaded.Version);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SessionV2_JsonUsesCamelCaseKeys()
    {
        string dir = TempDir();
        try
        {
            var session = Session(Page(0, 0));
            session.Bookmarks.Add(new SessionBookmark
            {
                TimeMs = 100,
                PageIndex = 0,
                Name = "书签",
                Auto = true,
                Kind = "page",
            });

            string path = ClassroomSessionStore.Save(session, dir);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("version", out _));
            Assert.True(root.TryGetProperty("bookmarks", out var bookmarks));
            Assert.Equal("page", bookmarks[0].GetProperty("kind").GetString());
            Assert.True(bookmarks[0].GetProperty("auto").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}