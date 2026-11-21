using System.Diagnostics;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Classroom;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 90 分钟录制落盘压力测试：用 <see cref="SessionJournal"/> 在临时目录真实写盘 54_000 个事件
/// （约每 100ms 一条），再走 <see cref="ClassroomSessionStore.Save"/> 与会话恢复链路，
/// 验证写入吞吐、文件规模与恢复一致性。
/// <para>
/// 纯落盘路径：不经过真实 UI 书写帧率、触控 / 压感输入与 Win2D 栅格化；
/// 数字只代表本机这份合成负载，不代表慢磁盘或机械盘表现。
/// </para>
/// </summary>
public sealed class RecordingThroughputTests : IDisposable
{
    private const int EventCount = 54_000;          // 90 分钟 / 100ms
    private const long EventIntervalMs = 100;
    private const int EventsPerPage = 6_000;
    private const long LastEventTimeMs = (EventCount - 1) * EventIntervalMs;
    private const string SessionName = "录制压测";

    private readonly ITestOutputHelper _output;
    private readonly string _directory;

    public RecordingThroughputTests(ITestOutputHelper output)
    {
        _output = output;
        _directory = Path.Combine(Path.GetTempPath(), "ink-record-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void JournalAndStore_90Minutes_54000Events_WritesAndRecovers()
    {
        string id = Guid.NewGuid().ToString("N");
        var createdAt = new DateTime(2024, 9, 1, 8, 0, 0, DateTimeKind.Local);

        // 复用同一份笔画 JSON，避免把序列化开销混进录制吞吐测量；StrokeId 仍然唯一。
        string strokeJson = SampleStrokeJson();
        var session = new ClassroomSession
        {
            Id = id,
            Name = SessionName,
            CreatedAt = createdAt,
            Events = new List<TimelineEvent>(EventCount),
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
        };

        var totalWatch = Stopwatch.StartNew();
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        // ---- 1. 逐事件真实写盘（不手动 Flush，沿用 journal 自带的批量刷盘策略）----
        var writeWatch = Stopwatch.StartNew();
        using (var journal = SessionJournal.Open(id, _directory, SessionName, createdAt))
        {
            for (int i = 0; i < EventCount; i++)
            {
                var ev = BuildEvent(i, id, strokeJson);
                session.Events.Add(ev);
                journal.Append(ev);
            }
            journal.Close();
        }
        writeWatch.Stop();
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

        string journalPath = SessionJournal.PathFor(id, _directory);
        long journalBytes = new FileInfo(journalPath).Length;

        // ---- 2. 仅凭 journal 恢复：验证写进去的每条事件都能读回 ----
        var recoverWatch = Stopwatch.StartNew();
        ClassroomSession journalRecovered = SessionJournal.Recover(id, _directory);
        recoverWatch.Stop();

        // ---- 3. 正式保存：生成会话 JSON 与 SHA-256 旁车文件 ----
        var saveWatch = Stopwatch.StartNew();
        string jsonPath = ClassroomSessionStore.Save(session, _directory);
        saveWatch.Stop();
        long sessionJsonBytes = new FileInfo(jsonPath).Length;
        string checksumPath = ClassroomSessionStore.PathForChecksum(jsonPath);

        // ---- 4. 合并恢复（正式 JSON + journal）再校验一次 ----
        var mergedWatch = Stopwatch.StartNew();
        ClassroomSession merged = ClassroomSessionStore.LoadWithJournal(id, _directory);
        mergedWatch.Stop();

        totalWatch.Stop();

        // 只做约定的恢复中间产物清理；临时目录由 Dispose 递归删除。
        ClassroomSessionStore.DeleteRecoveryArtifacts(id, _directory);

        double writeMs = writeWatch.Elapsed.TotalMilliseconds;
        double saveMs = saveWatch.Elapsed.TotalMilliseconds;
        double recoverMs = recoverWatch.Elapsed.TotalMilliseconds;
        long memoryDelta = memoryAfter - memoryBefore;

        _output.WriteLine($"事件数={EventCount}；笔画事件={CountStrokeEvents()}；页切换=9");
        _output.WriteLine($"写入耗时={writeMs:F1} ms；每事件平均={writeMs / EventCount:F4} ms（上界 60000 ms）");
        _output.WriteLine($"保存耗时={saveMs:F1} ms；恢复耗时={recoverMs:F1} ms；合并恢复耗时={mergedWatch.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine($"总耗时={totalWatch.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine($"journal 大小={journalBytes / 1024.0 / 1024.0:F2} MB（{journalBytes} 字节）");
        _output.WriteLine($"会话 JSON 大小={sessionJsonBytes / 1024.0 / 1024.0:F2} MB（{sessionJsonBytes} 字节）");
        _output.WriteLine($"托管内存增量={memoryDelta / 1024.0 / 1024.0:F2} MB（{memoryDelta} 字节）");

        Assert.True(writeMs < 60_000, $"写入 {EventCount} 事件耗时 {writeMs:F1} ms 超过 60 秒上界");

        Assert.NotNull(journalRecovered);
        Assert.Equal(EventCount, journalRecovered.Events.Count);
        Assert.Equal(LastEventTimeMs, journalRecovered.Events[^1].TimeMs);
        Assert.Equal(EventCount, journalRecovered.Events[^1].Seq);

        Assert.NotNull(merged);
        Assert.Equal(EventCount, merged.Events.Count);
        Assert.Equal(LastEventTimeMs, merged.Events[^1].TimeMs);

        Assert.True(File.Exists(jsonPath), "会话 JSON 未生成");
        Assert.True(File.Exists(checksumPath), "会话 SHA-256 旁车文件未生成");
        Assert.Equal(EventCount, session.Events.Count);
    }

    /// <summary>合成第 <paramref name="i"/> 条事件：每 6000 条切一页，其余为笔画。</summary>
    private static TimelineEvent BuildEvent(int i, string sessionId, string strokeJson)
    {
        bool pageChanged = i % EventsPerPage == 0;
        return new TimelineEvent
        {
            SessionId = sessionId,
            Seq = i + 1,
            TimeMs = i * EventIntervalMs,
            PageIndex = i / EventsPerPage,
            Kind = pageChanged ? TimelineEventKind.PageChanged : TimelineEventKind.StrokeAdded,
            StrokeId = pageChanged ? null : $"stroke-{i:D6}",
            StrokeJson = pageChanged ? null : strokeJson,
        };
    }

    private static int CountStrokeEvents()
    {
        int pageEvents = (EventCount + EventsPerPage - 1) / EventsPerPage;
        return EventCount - pageEvents;
    }

    /// <summary>生成一份固定的小笔画 JSON，全部笔画事件共享该字符串。</summary>
    private static string SampleStrokeJson()
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(12f, 24f);
        stroke.AddPoint(13f, 24.5f);
        stroke.Complete();
        return StrokeJsonCodec.Serialize(stroke);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}