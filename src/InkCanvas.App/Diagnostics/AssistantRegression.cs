using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

/// <summary>
/// AI 助教浮窗状态机诊断：在真实 App、真实 UI 线程与真实 <see cref="AssistantWindow"/> 中，
/// 用本地构造的 <see cref="AssistantSession"/> 走完「加载 → 出错 → 重试 → 成功 → 关闭」，
/// 不发起任何 AI HTTP 请求，只验证窗口对状态的渲染与按钮交互。
/// </summary>
internal static class AssistantRegression
{
    private const string LogPrefix = "[AI][AiCheck]";

    private sealed record CheckItem(string Check, bool Passed, bool Optional, double ElapsedMs,
        Dictionary<string, object> Details);

    private sealed class CheckLog
    {
        private readonly List<CheckItem> _items = new();
        private readonly Stopwatch _phase = Stopwatch.StartNew();

        public void Add(string check, bool passed, params (string Key, object Value)[] details)
        {
            var values = new Dictionary<string, object>();
            foreach (var (key, value) in details) values[key] = value;
            _items.Add(new CheckItem(check, passed, false, Math.Round(_phase.Elapsed.TotalMilliseconds, 1), values));
            _phase.Restart();
            FileLog.WriteLogToFile(
                $"{LogPrefix} {(passed ? "PASS" : "FAIL")} {check}",
                passed ? LogType.Info : LogType.Error);
        }

        public IReadOnlyList<CheckItem> Items => _items;

        public bool Passed => _items.All(item => item.Passed || item.Optional);
    }

    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var log = new CheckLog();
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        string textPath = Path.Combine(directory, "ai-check.txt");
        string jsonPath = Path.Combine(directory, "ai-check.json");
        FileLog.WriteLogToFile($"{LogPrefix} 开始，输出目录：{directory}", LogType.Info);

        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        using var shell = new ShellController(services);
        AssistantWindow window = null;
        AssistantSession session = null;
        int closedCount = 0;
        int retryClicks = 0;
        AssistantState stateAtClose = AssistantState.Idle;
        try
        {
            shell.Start();
            shell.StartInClickThrough();
            await Task.Delay(400);

            var screenArea = InkCanvas.Platform.System.ScreenService.WorkArea;
            var anchor = new Windows.Graphics.RectInt32(screenArea.X, screenArea.Y, screenArea.Width, screenArea.Height);
            var work = WindowChrome.GetWorkArea(anchor);
            double dpi = WindowChrome.GetDpiScale(anchor);
            if (!(dpi > 0)) dpi = 1.0;
            double workHeightDip = work.Height / dpi;

            window = new AssistantWindow(services.Settings.Appearance.Theme, services.Settings.Features);
            session = AssistantMachine.CreateSession(AssistantMode.Auto, "aicheck-" + DateTime.UtcNow.Ticks);
            window.RetryRequested += () =>
            {
                retryClicks++;
                FileLog.WriteLogToFile(
                    $"{LogPrefix} 重试按钮被触发：retryClicks={retryClicks}，state={session?.State}",
                    LogType.Info);
                if (session is null) return;
                // 重试走真实的状态机入口（Capture → Recognize），不发起 HTTP 请求。
                AssistantMachine.BeginCapture(session, Environment.TickCount64);
                AssistantMachine.BeginRecognize(session);
                session.Text = "";
                session.Error = "";
                window.Apply(session, "");
            };
            window.Closed += () =>
            {
                closedCount++;
                if (session is not null)
                {
                    stateAtClose = session.State;
                    // 模拟 ShellController.OnAiWindowClosed 的会话清理语义：窗口模型不再持有活动流。
                    session.State = AssistantState.Idle;
                }
                FileLog.WriteLogToFile(
                    $"{LogPrefix} 窗口关闭回调：closedCount={closedCount}，stateAtClose={stateAtClose}",
                    LogType.Info);
            };

            window.ShowAt(
                new AssistantPlacement(work.X / dpi + 24, work.Y / dpi + 24, false, false, 0),
                dpi, workHeightDip);
            await Task.Delay(250);
            bool opened = window.AppWindow.IsVisible && window.Content is FrameworkElement;
            log.Add("assistant window opens on the ui thread", opened,
                ("isVisible", window.AppWindow.IsVisible), ("dpi", dpi),
                ("position", $"{window.AppWindow.Position.X},{window.AppWindow.Position.Y}"),
                ("size", $"{window.AppWindow.Size.Width}x{window.AppWindow.Size.Height}"));
            if (!opened) throw new InvalidOperationException("AI 浮窗未打开。");

            // 1) 加载态：Capturing/Recognizing 时状态文本为「识别中」并显示进度环。
            AssistantMachine.BeginCapture(session, Environment.TickCount64);
            AssistantMachine.BeginRecognize(session);
            window.Apply(session, "");
            await Task.Delay(80);
            string loadingStatus = StatusText(window);
            bool loadingRing = ProgressRings(window).Any(ring => ring.IsActive && IsShownInTree(ring));
            log.Add("loading state shows recognizing text and a progress ring",
                loadingStatus == "识别中" && loadingRing,
                ("state", session.State.ToString()), ("statusText", loadingStatus),
                ("progressRingActive", loadingRing), ("visibleTexts", VisibleTexts(window)));

            // 2) 错误态：「模拟错误」文本可见，且「重试」按钮存在并 Enabled。
            string errorMessage = "模拟错误（aicheck）";
            AssistantMachine.Fail(session, errorMessage);
            window.Apply(session, "");
            await Task.Delay(80);
            string errorStatus = StatusText(window);
            bool errorVisible = VisibleTexts(window).Contains(errorMessage);
            var retryButtons = RetryButtons(window);
            bool retryVisible = retryButtons.Count > 0;
            bool retryEnabled = retryVisible && retryButtons.All(button => button.IsEnabled);
            log.Add("error state shows the message and an enabled retry button",
                session.State == AssistantState.Error && errorStatus == "出错"
                    && errorVisible && retryVisible && retryEnabled,
                ("state", session.State.ToString()), ("statusText", errorStatus),
                ("errorText", errorMessage), ("errorTextVisible", errorVisible),
                ("retryButtons", retryButtons.Count), ("retryEnabled", retryEnabled),
                ("visibleTexts", VisibleTexts(window)));

            // 3) 重试：UIA Invoke 真实按钮，状态必须从 Error 变为非 Error（本地 Capture/Recognize）。
            bool invoked = retryVisible && Invoke(retryButtons[0]);
            await Task.Delay(200);
            string retryStatus = StatusText(window);
            bool leftError = session.State != AssistantState.Error && retryStatus != "出错";
            bool retryHidden = RetryButtons(window).Count == 0;
            log.Add("retry moves the window out of the error state",
                invoked && retryClicks == 1 && leftError,
                ("invokedViaUia", invoked), ("retryClicks", retryClicks),
                ("state", session.State.ToString()), ("statusText", retryStatus),
                ("errorTextVisible", VisibleTexts(window).Contains(errorMessage)),
                ("retryButtonsAfter", RetryButtons(window).Count), ("retryHidden", retryHidden));

            // 4) 本地成功流：Token + Done，窗口进入「已完成」，错误文本与重试按钮消失。
            long now = Environment.TickCount64;
            AssistantMachine.ApplyEvent(session, new AssistantEvent
            {
                Kind = AssistantEventKind.Token,
                Text = "本地成功回答（aicheck）",
            }, now);
            AssistantMachine.ApplyEvent(session, new AssistantEvent
            {
                Kind = AssistantEventKind.Done,
            }, now);
            window.Apply(session, "");
            await Task.Delay(80);
            string doneStatus = StatusText(window);
            bool retryGone = RetryButtons(window).Count == 0;
            bool errorGone = !VisibleTexts(window).Contains(errorMessage);
            log.Add("local success stream reaches the done state and clears the error",
                session.State == AssistantState.Done && doneStatus == "已完成" && retryGone && errorGone,
                ("state", session.State.ToString()), ("statusText", doneStatus),
                ("tokenCount", session.TokenCount), ("errorTextVisible", !errorGone),
                ("retryButtonsAfter", RetryButtons(window).Count), ("visibleTexts", VisibleTexts(window)));

            // 5) 关闭：Closed 回调触发一次，窗口隐藏，会话被清理出活动状态。
            window.Close();
            await Task.Delay(250);
            // 关闭后 AppWindow 可能已释放；访问异常本身就是「窗口已销毁」的证据。
            bool appWindowVisible = false;
            bool appWindowReleased = false;
            try { appWindowVisible = window.AppWindow.IsVisible; }
            catch (Exception) { appWindowReleased = true; }
            bool hidden = !appWindowVisible;
            bool cleaned = closedCount == 1 && session.State == AssistantState.Idle;
            log.Add("closing the window fires the cleanup callback and hides it",
                hidden && cleaned,
                ("closedCount", closedCount), ("isVisible", appWindowVisible),
                ("appWindowReleased", appWindowReleased),
                ("stateAtClose", stateAtClose.ToString()), ("stateAfterClose", session.State.ToString()));
        }
        catch (Exception ex)
        {
            log.Add("assistant regression exception", false, ("error", ex.ToString()));
        }
        finally
        {
            try
            {
                if (window is not null && closedCount == 0) window.Close();
            }
            catch (Exception)
            {
                // 清理阶段的异常不影响已记录结果。
            }

            try { shell.Dispose(); } catch { }
            WriteReport(textPath, jsonPath, log, jsonOptions);
            bool passed = log.Passed;
            FileLog.WriteLogToFile(
                $"{LogPrefix} 完成：{log.Items.Count(item => item.Passed)}/{log.Items.Count} 通过，退出码 {(passed ? 0 : 1)}",
                passed ? LogType.Info : LogType.Error);
            Environment.ExitCode = passed ? 0 : 1;
            Application.Current.Exit();
        }
    }

    private static string StatusText(AssistantWindow window)
        => VisibleTexts(window).FirstOrDefault(text => text is "就绪" or "识别中" or "生成中" or "已完成" or "出错")
            ?? "";

    private static List<string> VisibleTexts(AssistantWindow window)
    {
        var texts = new List<string>();
        foreach (var block in Descendants(window).OfType<TextBlock>())
        {
            if (!IsShownInTree(block) || string.IsNullOrWhiteSpace(block.Text)) continue;
            if (!texts.Contains(block.Text)) texts.Add(block.Text);
        }
        return texts;
    }

    private static List<Button> RetryButtons(AssistantWindow window)
        => Descendants(window).OfType<Button>()
            .Where(button => IsShownInTree(button)
                && string.Equals(ButtonLabel(button), "重试", StringComparison.Ordinal))
            .ToList();

    private static IEnumerable<ProgressRing> ProgressRings(AssistantWindow window)
        => Descendants(window).OfType<ProgressRing>();

    private static string ButtonLabel(Button button) => button.Content switch
    {
        string text => text,
        TextBlock block => block.Text,
        _ => "",
    };

    private static bool Invoke(Button button)
    {
        try
        {
            var peer = new ButtonAutomationPeer(button);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider provider) return false;
            provider.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"{LogPrefix} UIA Invoke 失败：{ex.Message}", LogType.Error);
            return false;
        }
    }

    private static bool IsShownInTree(DependencyObject element)
    {
        DependencyObject current = element;
        while (current is not null)
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            current = VisualTreeHelper.GetParent(current);
        }
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(AssistantWindow window)
    {
        if (window.Content is not DependencyObject root) yield break;
        foreach (var child in Descendants(root)) yield return child;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void WriteReport(string textPath, string jsonPath, CheckLog log, JsonSerializerOptions options)
    {
        var builder = new StringBuilder();
        foreach (var item in log.Items)
        {
            builder.Append(item.Passed ? "[PASS] " : "[FAIL] ");
            if (item.Optional) builder.Append("(optional) ");
            builder.Append(item.Check)
                .Append(" (").Append(item.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms)");
            foreach (var pair in item.Details)
                builder.Append(' ').Append(pair.Key).Append('=').Append(FormatValue(pair.Value));
            builder.AppendLine();
        }
        int passed = log.Items.Count(item => item.Passed);
        int failed = log.Items.Count(item => !item.Passed && !item.Optional);
        int optional = log.Items.Count(item => item.Optional);
        double totalElapsed = log.Items.Sum(item => item.ElapsedMs);
        builder.AppendLine();
        builder.AppendLine($"summary: {passed}/{log.Items.Count} passed, {failed} failed, {optional} optional, "
            + $"total={totalElapsed.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        builder.AppendLine("note: no AI HTTP request is issued; sessions are constructed locally.");
        File.WriteAllText(textPath, builder.ToString());

        var document = new
        {
            generatedAt = DateTimeOffset.Now,
            passed = log.Passed,
            totalElapsedMs = Math.Round(totalElapsed, 1),
            networkUsed = false,
            checks = log.Items,
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(document, options));
    }

    private static string FormatValue(object value) => value switch
    {
        null => "null",
        string text => text.ReplaceLineEndings(" "),
        bool flag => flag ? "true" : "false",
        IReadOnlyList<string> list => string.Join(" | ", list),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };
}