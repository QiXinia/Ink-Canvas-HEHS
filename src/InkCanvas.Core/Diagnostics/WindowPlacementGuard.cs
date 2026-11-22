namespace InkCanvas.Core.Diagnostics;

/// <summary>
/// 窗口放置前的状态快照。全部为值类型输入，不依赖 UI 线程，便于单元测试
/// 覆盖「窗口已关闭 / AppWindow 为空 / HWND 为 0」这几类崩溃前状态。
/// </summary>
public readonly record struct WindowPlacementState(
    bool HasWindow,
    bool HasAppWindow,
    bool HasHwnd,
    bool IsHwndAlive)
{
    /// <summary>只有四项全部成立时才允许调用 AppWindow.MoveAndResize / Content 等原生接口。</summary>
    public bool CanPlace => HasWindow && HasAppWindow && HasHwnd && IsHwndAlive;

    /// <summary>不可放置原因的稳定代号（英文常量，写入日志便于检索与测试断言）。</summary>
    public string Reason => HasWindow
        ? HasAppWindow
            ? HasHwnd
                ? IsHwndAlive ? WindowPlacementReasons.Ready : WindowPlacementReasons.HwndClosed
                : WindowPlacementReasons.HwndMissing
            : WindowPlacementReasons.AppWindowMissing
        : WindowPlacementReasons.WindowMissing;
}

/// <summary>不可放置原因代号。使用英文常量以保持日志可检索、测试可断言。</summary>
public static class WindowPlacementReasons
{
    public const string Ready = "ready";
    public const string WindowMissing = "window-missing";
    public const string AppWindowMissing = "app-window-missing";
    public const string HwndMissing = "hwnd-missing";
    public const string HwndClosed = "hwnd-closed";
    public const string AppWindowUnavailable = "app-window-unavailable";
}

/// <summary>窗口放置守卫的纯逻辑部分：判定 + 原因归类。</summary>
public static class WindowPlacementGuard
{
    /// <summary>按调用方探测到的状态判定能否安全放置。</summary>
    public static WindowPlacementState Evaluate(bool hasWindow, bool hasAppWindow, bool hasHwnd, bool isHwndAlive)
        => new(hasWindow, hasAppWindow, hasHwnd, isHwndAlive);

    /// <summary>Win32 HWND 值直接换算成「存在 + 仍存活」两项。</summary>
    public static WindowPlacementState FromHwnd(bool hasWindow, bool hasAppWindow, nint hwnd)
        => Evaluate(hasWindow, hasAppWindow, hwnd != 0, hwnd != 0);

    /// <summary>覆盖 Win32 IsWindow 结果的重载：HWND 非 0 但窗口已销毁时仍不可放置。</summary>
    public static WindowPlacementState FromHwnd(bool hasWindow, bool hasAppWindow, nint hwnd, bool isWindowAlive)
        => Evaluate(hasWindow, hasAppWindow, hwnd != 0, hwnd != 0 && isWindowAlive);

    public static bool CanPlace(WindowPlacementState state) => state.CanPlace;
}