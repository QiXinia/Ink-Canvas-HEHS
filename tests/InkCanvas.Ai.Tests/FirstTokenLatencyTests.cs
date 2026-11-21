using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Platform.Ai;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Ai.Tests;

/// <summary>
/// 「AI 首字延迟」测量：用现有本地 mock 流式客户端记录从发出请求到首个文本 / 公式内容事件
/// 到达的时间（毫秒），连续 10 次输出每次耗时与均值、最大值、最小值。
/// 说明：被测流为 <see cref="AiMockScript"/> 本地剧本（Endpoint 为空），不访问真实网络、不启动 UI，
/// 因此本测试数据仅代表 mock 流的首内容事件延迟，不代表真实 AI 端点的首字延迟。
/// </summary>
public sealed class FirstTokenLatencyTests
{
    /// <summary>连续测量次数。</summary>
    private const int Rounds = 10;

    /// <summary>宽松的首字延迟上限，避免 CI 调度抖动导致误报。</summary>
    private const int LatencyBudgetMs = 2000;

    /// <summary>单次等待 mock 流的超时，仅用于兜底，不使用 sleep 轮询。</summary>
    private const int WaitTimeoutMs = 5000;

    /// <summary>10 次测量总时长上限。</summary>
    private const int TotalBudgetMs = 5000;

    private readonly ITestOutputHelper _output;

    public FirstTokenLatencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 单次测量结果：首个内容事件耗时（毫秒）、累计内容片段数、首片段内容。
    /// </summary>
    private sealed record LatencySample(long FirstContentMs, int FragmentCount, string FirstContent);

    /// <summary>
    /// 首内容事件定义：mock 流中第一个「非空」的
    /// <see cref="AssistantEventKind.Token"/>（文本）或 <see cref="AssistantEventKind.Formula"/>（公式）事件。
    /// 计时从调用 <see cref="AiClient.Start"/> 之前启动 <see cref="Stopwatch"/>，在首个内容回调内读取
    /// <c>ElapsedMilliseconds</c>；量到首个内容事件后立即 Abort，剩余片段不再等待。
    /// </summary>
    private static LatencySample MeasureFirstContent()
    {
        using var client = new AiClient(new AiConfig());
        using var done = new ManualResetEventSlim();

        var request = new AssistantRequest
        {
            Mode = AssistantMode.Solve,
            Teaching = AssistantTeachingMode.StepByStep,
            SessionId = "first-token-latency",
        };

        var gate = new object();
        long? firstContentMs = null;
        int contentCount = 0;
        string firstContent = "";
        string error = "";

        var stopwatch = Stopwatch.StartNew();
        AiStreamHandle handle = client.Start(
            request,
            ev =>
            {
                bool first = false;
                lock (gate)
                {
                    if (ev is null) return;
                    if (ev.Kind is not (AssistantEventKind.Token or AssistantEventKind.Formula)) return;

                    string content = ev.Kind == AssistantEventKind.Formula
                        ? ev.Latex ?? ""
                        : ev.Text ?? "";
                    if (content.Length == 0) return;

                    firstContentMs ??= stopwatch.ElapsedMilliseconds;
                    contentCount++;
                    if (firstContent.Length == 0) firstContent = content;
                    first = contentCount == 1;
                }

                if (first) done.Set();
            },
            message =>
            {
                lock (gate)
                {
                    error = message ?? "";
                }

                done.Set();
            },
            () =>
            {
                lock (gate)
                {
                    // 完整流结束时也视为本轮结束（正常情况下首个内容事件已先行放行）。
                }

                done.Set();
            });

        try
        {
            Assert.True(
                done.Wait(WaitTimeoutMs),
                $"mock 流在 {WaitTimeoutMs}ms 内未产生首个内容事件，可能中断或卡死");
        }
        finally
        {
            handle.Abort();
        }

        lock (gate)
        {
            Assert.True(string.IsNullOrEmpty(error), "mock 流不应报错，实际错误：" + error);
            Assert.True(
                firstContentMs.HasValue,
                "首个内容事件的时间戳应可被 Stopwatch 测量到（不得为 null / 伪造值）");
            Assert.True(
                contentCount >= 1,
                "每次请求都应至少收到 1 个非空内容片段，实际：" + contentCount);
            Assert.False(
                string.IsNullOrWhiteSpace(firstContent),
                "首个内容片段内容不应为空，实际：「" + firstContent + "」");

            return new LatencySample(firstContentMs.GetValueOrDefault(), contentCount, firstContent);
        }
    }

    /// <summary>
    /// 完整回放一次 Solve 剧本，统计运行时的内容片段总数与 Done 次数，
    /// 用于证明 mock 流不是空流，且片段数量与剧本一致。
    /// </summary>
    private static (int Count, string First, int DoneCount, string Error) ReplayFullSolveStream()
    {
        using var client = new AiClient(new AiConfig());
        using var done = new ManualResetEventSlim();

        var request = new AssistantRequest
        {
            Mode = AssistantMode.Solve,
            Teaching = AssistantTeachingMode.StepByStep,
            SessionId = "first-token-latency-full",
        };

        var gate = new object();
        int contentCount = 0;
        string firstContent = "";
        int doneCount = 0;
        string error = "";

        AiStreamHandle handle = client.Start(
            request,
            ev =>
            {
                lock (gate)
                {
                    if (ev is null) return;

                    if (ev.Kind is AssistantEventKind.Token or AssistantEventKind.Formula)
                    {
                        string content = ev.Kind == AssistantEventKind.Formula
                            ? ev.Latex ?? ""
                            : ev.Text ?? "";
                        if (content.Length > 0)
                        {
                            contentCount++;
                            if (firstContent.Length == 0) firstContent = content;
                        }
                    }

                    if (ev.Kind == AssistantEventKind.Done) doneCount++;
                }
            },
            message =>
            {
                lock (gate)
                {
                    error = message ?? "";
                }

                done.Set();
            },
            done.Set);

        try
        {
            Assert.True(done.Wait(WaitTimeoutMs), $"mock 完整流在 {WaitTimeoutMs}ms 内未结束");
        }
        finally
        {
            handle.Abort();
        }

        lock (gate)
        {
            return (contentCount, firstContent, doneCount, error);
        }
    }

    /// <summary>
    /// 连续 10 次发起 Solve 请求，记录首个内容事件耗时并输出统计；
    /// 断言均值 / 最大值低于 2000ms，且每次都至少收到 1 个内容片段。
    /// </summary>
    [Fact]
    public void FirstTokenLatency_MockStream_StaysWithinBudget()
    {
        var samples = new List<LatencySample>(Rounds);
        var total = Stopwatch.StartNew();

        for (int i = 0; i < Rounds; i++)
        {
            LatencySample sample = MeasureFirstContent();
            samples.Add(sample);
            _output.WriteLine(
                $"第 {i + 1:00} 次：首字延迟 {sample.FirstContentMs} ms，内容片段 {sample.FragmentCount} 个，"
                + $"首片段「{sample.FirstContent.Trim()}」");
        }

        total.Stop();

        double average = samples.Average(s => s.FirstContentMs);
        double maximum = samples.Max(s => s.FirstContentMs);
        double minimum = samples.Min(s => s.FirstContentMs);
        _output.WriteLine(
            $"10 次统计：均值 {average:F1} ms，最大值 {maximum} ms，最小值 {minimum} ms，总耗时 {total.ElapsedMilliseconds} ms");

        // 每次都必须收到至少 1 个非空内容片段，确保不是空流。
        Assert.All(
            samples,
            s => Assert.True(
                s.FragmentCount >= 1,
                "每次请求都应至少收到 1 个内容片段，实际：" + s.FragmentCount));
        Assert.All(
            samples,
            s => Assert.False(
                string.IsNullOrWhiteSpace(s.FirstContent),
                "每次请求的首个内容片段都不应为空"));
        Assert.All(
            samples,
            s => Assert.True(
                s.FirstContentMs > 0,
                "首内容事件耗时必须是 Stopwatch 实测值且大于 0ms，实际：" + s.FirstContentMs));

        Assert.True(
            average < LatencyBudgetMs,
            $"首字延迟均值 {average:F1}ms 应小于 {LatencyBudgetMs}ms（宽松上限）");
        Assert.True(
            maximum < LatencyBudgetMs,
            $"首字延迟最大值 {maximum}ms 应小于 {LatencyBudgetMs}ms（单次也不应超预算）");
        Assert.True(
            total.ElapsedMilliseconds < TotalBudgetMs,
            $"10 次测量总耗时 {total.ElapsedMilliseconds}ms 应小于 {TotalBudgetMs}ms");

        // 同一内容重复测量：第 1、2 次为同一请求内容的两次测量，输出差值供复核稳定性。
        long delta = Math.Abs(samples[1].FirstContentMs - samples[0].FirstContentMs);
        _output.WriteLine(
            $"同一内容两次测量：{samples[0].FirstContentMs} ms / {samples[1].FirstContentMs} ms，差值 {delta} ms");
    }

    /// <summary>
    /// 对 mock 剧本与运行时片段做断言：内容片段数与剧本一致、首个片段非空、
    /// Done 恰好一次，确保测量对象是有内容的真实 mock 流而非空流。
    /// </summary>
    [Fact]
    public void FirstTokenLatency_MockScript_FragmentsAreNonEmpty()
    {
        IReadOnlyList<AssistantEvent> script = AiMockScript.Build(
            AssistantMode.Solve,
            AssistantTeachingMode.StepByStep);

        int expected = script.Count(ev =>
            ev.Kind is AssistantEventKind.Token or AssistantEventKind.Formula
            && !string.IsNullOrWhiteSpace(
                ev.Kind == AssistantEventKind.Formula ? ev.Latex : ev.Text));

        Assert.True(expected >= 1, "mock 剧本应至少包含 1 个非空内容片段，实际：" + expected);
        Assert.All(
            script.Where(ev => ev.Kind is AssistantEventKind.Token or AssistantEventKind.Formula),
            ev => Assert.False(
                string.IsNullOrWhiteSpace(
                    ev.Kind == AssistantEventKind.Formula ? ev.Latex : ev.Text),
                "mock 剧本中的内容事件都不应为空"));
        Assert.Equal(AssistantEventKind.Done, script[^1].Kind);

        var (count, first, doneCount, error) = ReplayFullSolveStream();

        Assert.True(string.IsNullOrEmpty(error), "mock 完整流不应报错，实际错误：" + error);
        Assert.Equal(expected, count);
        Assert.Equal(1, doneCount);
        Assert.False(string.IsNullOrWhiteSpace(first), "运行时首个内容片段不应为空");
        Assert.Contains("求解", first);

        _output.WriteLine(
            $"mock 剧本内容片段总数：{expected}；运行时片段总数：{count}；Done 次数：{doneCount}；"
            + $"首片段「{first.Trim()}」");
    }
}