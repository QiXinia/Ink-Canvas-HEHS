using InkCanvas.Core.Classroom;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// PPT 翻页截图进入时间轴：载荷解析、SceneAt 回放路径、旧载荷兼容与回放背景网格映射。
/// </summary>
public sealed class PptScreenshotTimelineTests
{
    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "ppt-screenshot",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
    };

    private static TimelineEvent Ev(long timeMs, TimelineEventKind kind, string payload = null) => new()
    {
        TimeMs = timeMs,
        PageIndex = 0,
        Kind = kind,
        Payload = payload,
    };

    private static TimelineEvent PptChanged(long timeMs, int slideId, int index, string screenshot)
        => Ev(timeMs, TimelineEventKind.PptSlideChanged,
            InkTimeline.EncodePptSlideChanged(slideId, index, screenshot));

    [Fact]
    public void SceneAt_PptSlideChangedWithScreenshot_ReturnsPath()
    {
        const string path = @"C:\Users\teacher\Pictures\slide-003.png";
        var timeline = new InkTimeline(Session(PptChanged(100, 123, 3, path)));

        ClassroomScene scene = timeline.SceneAt(150);

        Assert.Equal(3, scene.PptSlideIndex);
        Assert.Equal(path, scene.PptScreenshotPath);
    }

    [Fact]
    public void SceneAt_PptSlideChangedWithoutScreenshot_ReturnsEmpty()
    {
        var timeline = new InkTimeline(Session(
            PptChanged(100, 7, 2, ""),
            PptChanged(200, 8, 3, null)));

        Assert.Equal("", timeline.SceneAt(150).PptScreenshotPath);
        Assert.Equal("", timeline.SceneAt(250).PptScreenshotPath);
        Assert.Equal(3, timeline.SceneAt(250).PptSlideIndex);
    }

    [Fact]
    public void SceneAt_LegacyPlainIndexPayload_IsCompatiblyParsed()
    {
        var timeline = new InkTimeline(Session(
            Ev(100, TimelineEventKind.PptSlideChanged, "3")));

        ClassroomScene scene = timeline.SceneAt(150);

        Assert.Equal(3, scene.PptSlideIndex);
        Assert.Equal("", scene.PptScreenshotPath);
    }

    [Fact]
    public void SceneAt_PptSlideChangedAfterTargetTime_IsIgnored()
    {
        var timeline = new InkTimeline(Session(
            PptChanged(100, 1, 1, @"C:\shots\one.png"),
            PptChanged(300, 2, 2, @"C:\shots\two.png")));

        Assert.Equal(@"C:\shots\one.png", timeline.SceneAt(200).PptScreenshotPath);
        Assert.Equal(1, timeline.SceneAt(200).PptSlideIndex);
    }

    [Fact]
    public void SceneAt_NewSlideWithoutScreenshot_ClearsPreviousPath()
    {
        var timeline = new InkTimeline(Session(
            PptChanged(100, 1, 1, @"C:\shots\one.png"),
            PptChanged(200, 2, 2, "")));

        Assert.Equal(@"C:\shots\one.png", timeline.SceneAt(150).PptScreenshotPath);

        ClassroomScene later = timeline.SceneAt(250);
        Assert.Equal(2, later.PptSlideIndex);
        Assert.Equal("", later.PptScreenshotPath);
    }

    [Fact]
    public void DecodePptSlideChanged_ReadsCamelCaseJson()
    {
        PptSlideChange change = InkTimeline.DecodePptSlideChanged(
            "{\"slideId\":456,\"index\":5,\"screenshot\":\"D:\\\\shots\\\\five.png\"}");

        Assert.True(change.IsValid);
        Assert.Equal(456, change.SlideId);
        Assert.Equal(5, change.Index);
        Assert.Equal(@"D:\shots\five.png", change.Screenshot);
    }

    [Fact]
    public void DecodePptSlideChanged_MissingScreenshotField_FallsBackToEmpty()
    {
        PptSlideChange change = InkTimeline.DecodePptSlideChanged("{\"slideId\":9,\"index\":4}");

        Assert.True(change.IsValid);
        Assert.Equal(4, change.Index);
        Assert.Equal("", change.Screenshot);
    }

    [Fact]
    public void DecodePptSlideChanged_InvalidPayload_IsNotValid()
    {
        Assert.False(InkTimeline.DecodePptSlideChanged("not-json").IsValid);
        Assert.False(InkTimeline.DecodePptSlideChanged("").IsValid);
        Assert.False(InkTimeline.DecodePptSlideChanged("[1,2,3]").IsValid);
    }

    [Fact]
    public void ReplayBackgrounds_GridAndCoordinate_HaveGridAndAxes()
    {
        ReplayBackgroundStyle grid = ReplayBackgrounds.Resolve("Grid");
        Assert.Equal(ReplayGridStyle.Grid, grid.Grid);
        Assert.True(grid.HasGrid);
        Assert.False(grid.HasAxes);
        Assert.True(grid.IsLight);

        ReplayBackgroundStyle coordinate = ReplayBackgrounds.Resolve("Coordinate");
        Assert.Equal(ReplayGridStyle.Coordinate, coordinate.Grid);
        Assert.True(coordinate.HasGrid);
        Assert.True(coordinate.HasAxes);
        Assert.True(coordinate.IsLight);
    }

    [Fact]
    public void ReplayBackgrounds_GreenAndCream_HaveNoGrid()
    {
        ReplayBackgroundStyle green = ReplayBackgrounds.Resolve("Green");
        Assert.Equal(ReplayGridStyle.None, green.Grid);
        Assert.False(green.HasGrid);
        Assert.False(green.IsLight);

        ReplayBackgroundStyle cream = ReplayBackgrounds.Resolve("cream");
        Assert.Equal(ReplayGridStyle.None, cream.Grid);
        Assert.False(cream.HasGrid);
        Assert.True(cream.IsLight);
    }
}