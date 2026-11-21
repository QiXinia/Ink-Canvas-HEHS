using System.Diagnostics;
using System.Runtime.InteropServices;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Classroom;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 真实导出耗时测量：合成 120 秒 / 600 笔画的板书会话，调用 <see cref="TimeMachineExporter"/>
/// 真实渲染并用 Media Foundation 编码 MP4（BoardOnly、无音轨），记录帧数、耗时与文件大小。
/// 不覆盖真实音轨、摄像头 / 麦克风内容，也不代表慢机器的表现。
/// </summary>
public sealed class ExportTimingTests : IDisposable
{
    private const long DurationMs = 120_000;
    private const int StrokeCount = 600;

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    public ExportTimingTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), $"inkcanvas-export-timing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    // ---- 合成会话 --------------------------------------------------------

    /// <summary>600 个 StrokeAdded（约每 200ms 一笔）+ 每 60 秒一次 PageChanged；Seq 从 1 递增。</summary>
    private static ClassroomSession SyntheticSession()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        var events = new List<TimelineEvent>(StrokeCount + 2);
        int seq = 1;

        events.Add(new TimelineEvent
        {
            SessionId = sessionId,
            Seq = seq++,
            TimeMs = 0,
            PageIndex = 0,
            Kind = TimelineEventKind.PageChanged,
        });

        bool pageSwitched = false;
        for (int i = 0; i < StrokeCount; i++)
        {
            long timeMs = (i + 1) * DurationMs / (StrokeCount + 1);

            // 每 60 秒翻一页：60s 处切到第 1 页（页码 0 → 1）。
            if (!pageSwitched && timeMs >= 60_000)
            {
                pageSwitched = true;
                events.Add(new TimelineEvent
                {
                    SessionId = sessionId,
                    Seq = seq++,
                    TimeMs = 60_000,
                    PageIndex = 1,
                    Kind = TimelineEventKind.PageChanged,
                });
            }

            InkStroke stroke = Stroke(i);
            events.Add(new TimelineEvent
            {
                SessionId = sessionId,
                Seq = seq++,
                TimeMs = timeMs,
                PageIndex = timeMs >= 60_000 ? 1 : 0,
                Kind = TimelineEventKind.StrokeAdded,
                StrokeId = stroke.Id.ToString(),
                StrokeJson = StrokeJsonCodec.Serialize(stroke),
            });
        }

        return new ClassroomSession
        {
            Id = sessionId,
            Name = "export-timing-120s",
            CreatedAt = DateTime.Now,
            Events = events,
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
        };
    }

    /// <summary>2–4 个点的短笔画，坐标落在 640×480 画面内。</summary>
    private static InkStroke Stroke(int index)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });

        float x = 12f + index % 40 * 15f;
        float y = 24f + index / 40 % 28 * 15f;
        int points = 2 + index % 3;
        for (int p = 0; p < points; p++)
            stroke.AddPoint(x + p * 4f, y + p * 3f);

        stroke.Complete();
        return stroke;
    }

    /// <summary>Media Foundation / 渲染设备等环境类失败（导出器会包成 InvalidOperationException）。</summary>
    private static bool IsEnvironmentFailure(Exception ex)
    {
        for (Exception e = ex; e is not null; e = e.InnerException)
        {
            if (e is PlatformNotSupportedException or COMException
                or DllNotFoundException or EntryPointNotFoundException)
                return true;

            if (e is InvalidOperationException
                && (e.Message.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains("H.264", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    // ---- 真实导出耗时 ----------------------------------------------------

    [Fact]
    public async Task Export120SecondSyntheticSession_MeasuresRealMp4Timing()
    {
        string outputPath = Path.Combine(_tempDir, "export-timing-120s.mp4");
        var options = new ExportOptions(
            outputPath,
            StartMs: 0,
            EndMs: DurationMs,
            Fps: 30,
            IncludeAudio: false,
            Width: 640,
            Height: 480,
            Speed: 1.0,
            BoardOnly: true);

        ClassroomSession session = SyntheticSession();
        _output.WriteLine($"合成会话：{DurationMs} ms，{StrokeCount} 个 StrokeAdded，每 60 秒 PageChanged。");

        var stopwatch = Stopwatch.StartNew();
        ExportResult result;
        try
        {
            result = await TimeMachineExporter.ExportWithResultAsync(
                session, options, (IProgress<ExportProgress>)null, CancellationToken.None);
        }
        catch (Exception ex) when (IsEnvironmentFailure(ex))
        {
            stopwatch.Stop();
            _output.WriteLine("本机 Media Foundation / 渲染设备不可用，未实测 MP4 导出耗时：" + ex.Message);
            _output.WriteLine("如实跳过，不伪造帧数 / 耗时 / 文件大小。");
            return;
        }
        stopwatch.Stop();

        Assert.NotNull(result);
        Assert.True(result.FrameCount > 0, "导出结果应包含视频帧。");
        Assert.True(File.Exists(outputPath), "应生成 MP4 文件。");

        long size = new FileInfo(outputPath).Length;
        Assert.True(size > 0, "MP4 文件不应为空。");
        Assert.False(result.HasAudio, "IncludeAudio=false 时不应包含音轨。");
        Assert.Equal(TimeMachineExporter.EstimateFrameCount(options), result.FrameCount);

        double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        double msPerFrame = elapsedMs / result.FrameCount;
        _output.WriteLine(
            $"真实 MP4 导出：{result.FrameCount} 帧，耗时 {elapsedMs:0} ms，"
            + $"平均 {msPerFrame:0.00} ms/帧，文件 {size} 字节，"
            + $"{options.Width}×{options.Height} @ {options.Fps}fps，{options.Speed:0.##}×，仅画面无音轨。");
    }
}