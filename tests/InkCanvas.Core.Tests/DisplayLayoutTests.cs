using InkCanvas.Platform.System;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class DisplayLayoutTests
{
    [Theory]
    [InlineData(-1920, 0, 1920, 1040)]
    [InlineData(0, -1440, 2560, 1400)]
    [InlineData(1920, 300, 800, 1200)]
    public void DisconnectedWindowAndOversizedChromeFitTheNearestWorkArea(int x, int y, int width, int height)
    {
        var work = new ScreenRect(x, y, width, height);
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2 })
        foreach (int offset in new[] { -5000, 0, 5000 })
        {
            var fit = new ScreenRect(offset, offset, (int)(900 * dpi), (int)(700 * dpi)).FitInside(work);
            Assert.InRange(fit.X, work.X, work.X + work.Width - fit.Width);
            Assert.InRange(fit.Y, work.Y, work.Y + work.Height - fit.Height);
            Assert.Equal(Math.Min((int)(900 * dpi), width), fit.Width);
            Assert.Equal(Math.Min((int)(700 * dpi), height), fit.Height);
        }
    }

    [Fact]
    public void PlacementAlreadyInsideSecondaryMonitorStaysExact()
    {
        var window = new ScreenRect(-1853, -1170, 682, 64);
        Assert.Equal(window, window.FitInside(new(-1920, -1200, 1920, 1160)));
    }

    [Fact]
    public void EmptyWorkAreaDuringDisplayTransitionHasValidDimensions()
    {
        Assert.Equal(new ScreenRect(-50, 40, 1, 1), new ScreenRect(0, 0, 0, 0).FitInside(new(-50, 40, 0, 0)));
    }

    [Fact]
    public void VirtualBoundsKeepPhysicalPixelsAcrossMixedDpiAndNegativeOrigins()
    {
        ScreenInfo[] screens =
        {
            new(new(1), new(-2560, -400, 2560, 1440), default, 1.5),
            new(new(2), new(0, 0, 1920, 1080), default, 1),
            new(new(3), new(1920, -1200, 1200, 1920), default, 1.25),
        };
        Assert.Equal(new(-2560, -1200, 5680, 2280), ScreenService.GetVirtualBounds(screens));
        Assert.Equal(ScreenService.GetVirtualBounds(screens), ScreenService.GetVirtualBounds(screens.Reverse().ToArray()));
        Assert.Equal(new(0, 0, 1920, 1080), ScreenService.GetVirtualBounds(new[] { screens[1] }));
        Assert.Equal(default, ScreenService.GetVirtualBounds(Array.Empty<ScreenInfo>()));
    }
}
