using InkCanvas.Core.Input;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class WindowDragSessionTests
{
    [Theory]
    [InlineData(1001)]
    [InlineData(30000)]
    [InlineData(3600000)]
    public void InterruptedDispatchCannotMoveTheWindowFromAnOldPress(long gap)
    {
        var drag = new WindowDragSession();
        drag.Begin(100, 200, 110, 210, 100);
        Assert.True(drag.TryMove(140, 230, 116, 8, out var position));
        Assert.Equal((130, 220), position);
        Assert.False(drag.TryMove(900, 600, 116 + gap, 8, out _));
        Assert.False(drag.IsPressed);
        Assert.False(drag.TryMove(920, 650, 132 + gap, 8, out _));
    }

    [Fact]
    public void CancelAndResumeRequireAFreshPressWithANewOrigin()
    {
        var drag = new WindowDragSession();
        drag.Begin(-1200, 200, -1180, 220, 10);
        Assert.True(drag.TryMove(-1150, 240, 26, 8, out _));
        Assert.True(drag.End());
        Assert.False(drag.TryMove(600, 700, 100, 8, out _));
        drag.Begin(300, 400, 320, 420, 120);
        Assert.True(drag.TryMove(350, 440, 136, 8, out var position));
        Assert.Equal((330, 420), position);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void TapJitterDoesNotBecomeADragAtDifferentDpiScales(double threshold)
    {
        var drag = new WindowDragSession();
        drag.Begin(100, 200, 110, 210, 0);
        Assert.False(drag.TryMove(113, 213, 16, threshold, out _));
        Assert.True(drag.IsPressed);
        Assert.False(drag.End());
        Assert.False(drag.TryMove(500, 600, 32, threshold, out _));
    }

    [Fact]
    public void SlowContinuousDragRemainsActiveWithoutATotalDurationLimit()
    {
        var drag = new WindowDragSession();
        drag.Begin(100, 200, 110, 210, 0);
        for (int step = 1; step <= 600; step++)
        {
            Assert.True(drag.TryMove(110 + step * 10, 210, step * 250, 8, out var position));
            Assert.Equal((100 + step * 10, 200), position);
        }
    }
}
