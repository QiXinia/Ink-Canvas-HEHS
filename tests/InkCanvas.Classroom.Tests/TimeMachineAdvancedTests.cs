using InkCanvas.Core.Classroom;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 时光机剩余能力：分辨率 / 倍速 / 区间校验、倍速采样、仅板书判定、磁盘空间预检、
/// 事件跳转边界、书签摘要与异步定位取消闸门。
/// </summary>
public sealed class TimeMachineAdvancedTests
{
    // ---- helpers ---------------------------------------------------------

    private static TimelineEvent Ev(long timeMs, TimelineEventKind kind, string payload = null, int page = 0)
        => new() { TimeMs = timeMs, PageIndex = page, Kind = kind, Payload = payload };

    private static TimelineEvent Page(long timeMs, int page)
        => Ev(timeMs, TimelineEventKind.PageChanged, page: page);

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "advanced",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
    };

    private static ExportOptions Options(
        long startMs = 0, long endMs = 2_000, int fps = 30,
        int width = 1920, int height = 1080, double speed = 1.0,
        bool boardOnly = false, bool audio = false)
        => new("export.mp4", startMs, endMs, Fps: fps, IncludeAudio: audio,
            Width: width, Height: height, Speed: speed, BoardOnly: boardOnly);

    /// <summary>复刻 TimeMachineWindow.StepEvent 的索引钳制逻辑。</summary>
    private static long StepTarget(InkTimeline timeline, int eventCount, long nowMs, int delta)
    {
        int cutoff = timeline.IndexAt(nowMs);
        int target = Math.Clamp(cutoff + delta, 0, Math.Max(0, eventCount - 1));
        return timeline.TimeAtEvent(target);
    }

    // ---- ExportOptions 校验 ---------------------------------------------

    [Fact]
    public void ExportOptions_Validate_RejectsWidthOutsideRange()
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(width: 319)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(width: 3841)));

        TimeMachineExporter.Validate(Options(width: 320));
        TimeMachineExporter.Validate(Options(width: 3840));
    }

    [Fact]
    public void ExportOptions_Validate_RejectsHeightOutsideRange()
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(height: 319)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(height: 3841)));

        TimeMachineExporter.Validate(Options(height: 720));
    }

    [Fact]
    public void ExportOptions_Validate_RejectsSpeedOutsideRange()
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(speed: 0.4)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(speed: 8.1)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(speed: double.NaN)));

        TimeMachineExporter.Validate(Options(speed: 0.5));
        TimeMachineExporter.Validate(Options(speed: 8));
    }

    [Fact]
    public void ExportOptions_Validate_RejectsInvalidRange()
    {
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(startMs: 500, endMs: 500)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(startMs: 900, endMs: 100)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(Options(startMs: -1, endMs: 1_000)));

        TimeMachineExporter.Validate(Options(startMs: 0, endMs: 1));
    }

    // ---- 倍速采样 --------------------------------------------------------

    [Fact]
    public void SampleFrameTimes_1x_UsesRealTimeAndFullDuration()
    {
        var options = Options(startMs: 0, endMs: 2_000, fps: 30, speed: 1.0);
        var times = TimeMachineExporter.SampleFrameTimes(options);

        Assert.Equal(60, times.Count);
        Assert.Equal(0, times[0]);
        Assert.Equal(33, times[1]);
        Assert.Equal(2000, TimeMachineExporter.EffectiveDurationMs(options));
        Assert.True(times[^1] <= 2000);
        for (int i = 1; i < times.Count; i++) Assert.True(times[i] > times[i - 1]);
    }

    [Fact]
    public void SampleFrameTimes_2x_HalvesOutputDuration()
    {
        var options = Options(startMs: 0, endMs: 2_000, fps: 30, speed: 2.0);
        var times = TimeMachineExporter.SampleFrameTimes(options);

        Assert.Equal(30, times.Count);
        Assert.Equal(1000, TimeMachineExporter.EffectiveDurationMs(options));
        Assert.Equal(0, times[0]);
        Assert.Equal(67, times[1]);
        Assert.Equal(1933, times[^1]);
        Assert.True(times[^1] <= 2000);
    }

    [Fact]
    public void SampleFrameTimes_4x_QuartersOutputDuration()
    {
        var options = Options(startMs: 0, endMs: 2_000, fps: 30, speed: 4.0);
        var times = TimeMachineExporter.SampleFrameTimes(options);

        Assert.Equal(15, times.Count);
        Assert.Equal(500, TimeMachineExporter.EffectiveDurationMs(options));
        Assert.Equal(133, times[1]);
        Assert.Equal(1867, times[^1]);
        Assert.Equal(times.Count, TimeMachineExporter.EstimateFrameCount(options));
    }

    [Fact]
    public void SampleFrameTimes_KeepsStartOffsetAndStaysWithinRange()
    {
        var options = Options(startMs: 1_000, endMs: 4_000, fps: 30, speed: 2.0);
        var times = TimeMachineExporter.SampleFrameTimes(options);

        Assert.Equal(1_000, times[0]);
        Assert.All(times, t => Assert.InRange(t, 1_000, 4_000));
        double outputSeconds = TimeMachineExporter.EffectiveDurationMs(options) / 1000.0;
        Assert.Equal(1.5, outputSeconds, 3);
        Assert.Equal(times.Count / 30.0, outputSeconds, 3);
    }

    // ---- 仅板书判定 ------------------------------------------------------

    [Fact]
    public void ShouldDrawPptBackground_FollowsBoardOnlyFlag()
    {
        Assert.True(TimeMachineExporter.ShouldDrawPptBackground(Options(boardOnly: false)));
        Assert.False(TimeMachineExporter.ShouldDrawPptBackground(Options(boardOnly: true)));
        Assert.False(TimeMachineExporter.ShouldDrawPptBackground(null));
    }

    // ---- 磁盘空间预检 ----------------------------------------------------

    [Fact]
    public void EstimateRequiredBytes_IncludesVideoAndOptionalAudio()
    {
        var videoOnly = Options(startMs: 0, endMs: 2_000, width: 1280, height: 720);
        long expectedVideo = 60L * 1280 * 720 * 4;
        Assert.Equal(expectedVideo, TimeMachineExporter.EstimateRequiredBytes(videoOnly));

        var withAudio = Options(startMs: 0, endMs: 2_000, width: 1280, height: 720, audio: true);
        long expectedAudio = 2L * TimeMachineExporter.AudioSampleRate * 4;
        Assert.Equal(expectedVideo + expectedAudio, TimeMachineExporter.EstimateRequiredBytes(withAudio));
    }

    [Fact]
    public void EnsureFreeDiskSpace_InsufficientSpace_ThrowsChineseMessage()
    {
        var options = Options();
        var ex = Assert.Throws<InvalidOperationException>(
            () => TimeMachineExporter.EnsureFreeDiskSpace(1_000_000, options));

        Assert.Contains("磁盘可用空间不足", ex.Message);
        Assert.Contains("MB", ex.Message);
    }

    [Fact]
    public void EnsureFreeDiskSpace_EnoughSpace_WithTwentyPercentMargin()
    {
        var options = Options();
        long required = TimeMachineExporter.EstimateRequiredBytes(options);

        var ex = Record.Exception(() => TimeMachineExporter.EnsureFreeDiskSpace(required, options));
        Assert.NotNull(ex);      // 恰好等于预估值，仍缺 20% 余量。
        Assert.Contains("磁盘可用空间不足", ex!.Message);

        Exception enough = Record.Exception(
            () => TimeMachineExporter.EnsureFreeDiskSpace((long)(required * 1.2) + 1, options));
        Assert.Null(enough);
    }

    // ---- 上一 / 下一事件边界 --------------------------------------------

    [Fact]
    public void StepTarget_ClampsAtBothEnds()
    {
        var timeline = new InkTimeline(Session(
            Ev(0, TimelineEventKind.PageChanged),
            Ev(100, TimelineEventKind.PageChanged),
            Ev(200, TimelineEventKind.PageChanged)));
        int count = timeline.Session.Events.Count;

        Assert.Equal(0, StepTarget(timeline, count, -50, -1));    // 首事件之前，上一事件钳到开头
        Assert.Equal(0, StepTarget(timeline, count, -50, +1));    // 首事件之前，下一事件仍是开头
        Assert.Equal(0, StepTarget(timeline, count, 100, -1));    // 正常后退一格
        Assert.Equal(200, StepTarget(timeline, count, 100, +1));  // 正常前进一格
        Assert.Equal(200, StepTarget(timeline, count, 999, +1));  // 末尾钳制
        Assert.Equal(100, StepTarget(timeline, count, 999, -1));  // 末尾后退一格
    }

    // ---- 书签 ------------------------------------------------------------

    [Fact]
    public void Bookmarks_AddRenameRemove_AtPlayhead()
    {
        var session = Session(Page(0, 0), Page(5_000, 1));
        var timeline = new InkTimeline(session);

        int index = timeline.AddBookmark(2_500, "书签 1", "manual");
        Assert.Equal(0, index);
        Assert.Single(session.Bookmarks);
        Assert.Equal(2_500, session.Bookmarks[index].TimeMs);
        Assert.Equal(0, session.Bookmarks[index].PageIndex);
        Assert.False(session.Bookmarks[index].Auto);

        Assert.True(timeline.RenameBookmark(index, "  重点讲解  "));
        Assert.Equal("重点讲解", session.Bookmarks[index].Name);
        Assert.Equal("manual", session.Bookmarks[index].Kind);

        Assert.True(timeline.RemoveBookmark(index));
        Assert.Empty(session.Bookmarks);
        Assert.False(timeline.RemoveBookmark(0));
    }

    [Fact]
    public void BookmarkSummary_UsesEventAndDescribeFormat()
    {
        var session = Session(
            Page(0, 0),
            Ev(1_000, TimelineEventKind.RosterDrawn, "张三"));
        var timeline = new InkTimeline(session);

        int index = timeline.AddBookmark(1_000, "书签 1", "manual");
        SessionBookmark bookmark = session.Bookmarks[index];

        Assert.Equal("点名 张三", timeline.BookmarkSummary(bookmark));
        string description = timeline.DescribeBookmark(bookmark);
        Assert.Contains("第 1 页", description);
        Assert.Contains("点名 张三", description);
        Assert.StartsWith("00:01", description);
    }

    [Fact]
    public void BookmarkSummary_TruncatesToTwentyCharacters()
    {
        string longName = "一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十";
        var session = Session(
            Page(0, 0),
            Ev(1_000, TimelineEventKind.RosterDrawn, longName));
        var timeline = new InkTimeline(session);

        int index = timeline.AddBookmark(1_000, "书签 1", "manual");
        string summary = timeline.BookmarkSummary(session.Bookmarks[index]);

        Assert.Equal(20, summary.Length);
        Assert.StartsWith("点名 ", summary);
    }

    // ---- 异步定位取消 ----------------------------------------------------

    [Fact]
    public void SeekGate_CancelledTokenOrStaleVersion_DoesNotApply()
    {
        using var cts = new CancellationTokenSource();

        Assert.True(InkTimeline.ShouldApplySeek(cts.Token, requestVersion: 3, latestVersion: 3));
        Assert.False(InkTimeline.ShouldApplySeek(cts.Token, requestVersion: 2, latestVersion: 3));

        cts.Cancel();
        Assert.False(InkTimeline.ShouldApplySeek(cts.Token, requestVersion: 3, latestVersion: 3));
    }
}