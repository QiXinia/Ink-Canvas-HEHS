using InkCanvas.Core.Diagnostics;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>
/// WindowChrome.Place 历史 NRE 的纯逻辑回归：关闭板 → 浮条重排时
/// window/AppWindow/HWND 任一失效都必须判定为不可放置。
/// </summary>
public sealed class WindowPlacementGuardTests
{
    [Fact]
    public void LiveWindowCanBePlaced()
    {
        var state = WindowPlacementGuard.FromHwnd(true, true, (nint)0x1234, isWindowAlive: true);

        Assert.True(state.CanPlace);
        Assert.Equal(WindowPlacementReasons.Ready, state.Reason);
    }

    [Fact]
    public void NullWindowIsRejected()
    {
        var state = WindowPlacementGuard.Evaluate(false, false, false, false);

        Assert.False(state.CanPlace);
        Assert.Equal(WindowPlacementReasons.WindowMissing, state.Reason);
    }

    [Fact]
    public void ClosedWindowWithReleasedAppWindowIsRejected()
    {
        // WinUI 在窗口关闭后访问 Window.AppWindow 可能返回 null 或抛 COMException (0x800710DD)。
        var state = WindowPlacementGuard.Evaluate(true, false, false, false);

        Assert.False(state.CanPlace);
        Assert.Equal(WindowPlacementReasons.AppWindowMissing, state.Reason);
    }

    [Fact]
    public void ZeroHwndIsRejected()
    {
        var state = WindowPlacementGuard.FromHwnd(true, true, 0);

        Assert.False(state.CanPlace);
        Assert.Equal(WindowPlacementReasons.HwndMissing, state.Reason);
    }

    [Fact]
    public void DestroyedHwndIsRejectedEvenWhenHandleValueIsNonZero()
    {
        var state = WindowPlacementGuard.FromHwnd(true, true, (nint)0x1234, isWindowAlive: false);

        Assert.False(state.CanPlace);
        Assert.Equal(WindowPlacementReasons.HwndClosed, state.Reason);
    }

    [Fact]
    public void GuardOnlyAllowsTheFullyLiveCombination()
    {
        foreach (bool hasWindow in new[] { false, true })
        foreach (bool hasAppWindow in new[] { false, true })
        foreach (bool hasHwnd in new[] { false, true })
        foreach (bool alive in new[] { false, true })
        {
            var state = WindowPlacementGuard.Evaluate(hasWindow, hasAppWindow, hasHwnd, alive);
            Assert.Equal(hasWindow && hasAppWindow && hasHwnd && alive, state.CanPlace);
        }
    }
}