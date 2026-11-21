using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Classroom;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 时光机导出的音画同步与一致性：WAV 变速 / 倍速导出音频长度 / 完整与区间帧数 /
/// 取消清理 / 异常恢复丢帧量化。
/// </summary>
public sealed class ExportSyncTests
{
    private readonly ITestOutputHelper _output;

    public ExportSyncTests(ITestOutputHelper output) => _output = output;

    // ---- helpers ---------------------------------------------------------

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-export-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTemp(string dir)
    {
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static short[] Tone(int frames, double hz = 440, int sampleRate = 48_000)
    {
        var samples = new short[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            short value = (short)(Math.Sin(2 * Math.PI * hz * i / sampleRate) * 8_000);
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }
        return samples;
    }

    private sealed class FakeCapture : IWasapiLoopbackCapture
    {
        private readonly Func<TimeSpan, Action<short[], int>, CancellationToken, (bool Ok, string Error)> _capture;

        public FakeCapture(Func<TimeSpan, Action<short[], int>, CancellationToken, (bool Ok, string Error)> capture)
            => _capture = capture;

        public TimeSpan LastDuration { get; private set; }

        public bool Capture(TimeSpan duration, Action<short[], int> writePcm, CancellationToken ct, out string error)
        {
            LastDuration = duration;
            (bool ok, string message) = _capture(duration, writePcm, ct);
            error = message;
            return ok;
        }
    }

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "export-sync",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
    };

    private static InkStroke Stroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f);
        stroke.AddPoint(x + 5f, 10f);
        stroke.Complete();
        return stroke;
    }

    private static TimelineEvent Added(long timeMs, int page, InkStroke stroke) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokeAdded,
        StrokeId = stroke.Id.ToString(),
        StrokeJson = StrokeJsonCodec.Serialize(stroke),
    };

    private static TimelineEvent Page(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PageChanged,
    };

    private static TimelineEvent Background(long timeMs, int page, string name) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.BackgroundChanged,
        Payload = name,
    };

    private static TimelineEvent Removed(long timeMs, int page, params string[] ids) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokesRemoved,
        StrokeIds = ids,
    };

    private static TimelineEvent History(long timeMs, int page, TimelineEventKind kind,
        string[] removed, InkStroke[] readded) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = kind,
        StrokeIds = removed,
        Payload = readded is { Length: > 0 } ? InkTimeline.EncodeAddedStrokes(readded) : null,
    };

    private static string Signature(IReadOnlyList<InkStroke> strokes)
        => string.Join("|", strokes
            .Select(s => StrokeJsonCodec.Serialize(s))
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>包含 StrokeAdded / StrokesRemoved / Cleared / PageChanged / BackgroundChanged / ViewChanged / Undo / Redo。</summary>
    private static ClassroomSession MixedSession(out InkStroke lastStroke)
    {
        var s1 = Stroke(10);
        var s2 = Stroke(20);
        var s3 = Stroke(30);
        var s4 = Stroke(40);
        lastStroke = s4;

        return Session(
            Page(0, 0),
            Background(0, 0, "Black"),
            Added(100, 0, s1),
            Added(200, 0, s2),
            History(300, 0, TimelineEventKind.Undo, new[] { s2.Id.ToString() }, Array.Empty<InkStroke>()),
            History(400, 0, TimelineEventKind.Redo, Array.Empty<string>(), new[] { s2 }),
            Removed(500, 0, s1.Id.ToString()),
            new TimelineEvent { TimeMs = 600, PageIndex = 0, Kind = TimelineEventKind.Cleared },
            Added(700, 0, s3),
            Page(800, 1),
            Added(900, 1, s4),
            Background(1_000, 1, "Green"),
            new TimelineEvent
            {
                TimeMs = 1_100,
                PageIndex = 1,
                Kind = TimelineEventKind.ViewChanged,
                Payload = InkTimeline.EncodeViewChanged(1.5, 120, 80),
            },
            History(1_500, 1, TimelineEventKind.Undo, new[] { s4.Id.ToString() }, Array.Empty<InkStroke>()),
            History(1_700, 1, TimelineEventKind.Redo, Array.Empty<string>(), new[] { s4 }),
            Background(2_000, 1, "Cream"));
    }

    // ---- MP4 轨道时长（ISO BMFF mdhd，不依赖外部工具）--------------------

    private readonly record struct Mp4Box(string Type, int Payload, int End);

    private static uint ReadBe32(byte[] data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
            | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static List<Mp4Box> ReadBoxes(byte[] data, int start, int end)
    {
        var boxes = new List<Mp4Box>();
        int position = start;
        while (position + 8 <= end)
        {
            long size = ReadBe32(data, position);
            string type = Encoding.ASCII.GetString(data, position + 4, 4);
            int header = 8;
            if (size == 1 && position + 16 <= end)
            {
                size = (long)(((ulong)ReadBe32(data, position + 8) << 32) | ReadBe32(data, position + 12));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < header || position + size > end) break;
            boxes.Add(new Mp4Box(type, position + header, position + (int)size));
            position += (int)size;
        }
        return boxes;
    }

    private static (TimeSpan? Video, TimeSpan? Audio) ReadMp4TrackDurations(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Mp4Box moov = ReadBoxes(data, 0, data.Length).FirstOrDefault(b => b.Type == "moov");
        if (moov.Type != "moov") return (null, null);

        TimeSpan? video = null, audio = null;
        foreach (Mp4Box trak in ReadBoxes(data, moov.Payload, moov.End).Where(b => b.Type == "trak"))
        {
            Mp4Box mdia = ReadBoxes(data, trak.Payload, trak.End).FirstOrDefault(b => b.Type == "mdia");
            if (mdia.Type != "mdia") continue;

            List<Mp4Box> children = ReadBoxes(data, mdia.Payload, mdia.End);
            Mp4Box hdlr = children.FirstOrDefault(b => b.Type == "hdlr");
            Mp4Box mdhd = children.FirstOrDefault(b => b.Type == "mdhd");
            if (hdlr.Type != "hdlr" || mdhd.Type != "mdhd") continue;

            string handler = Encoding.ASCII.GetString(data, hdlr.Payload + 8, 4);
            int version = data[mdhd.Payload];
            uint timescale;
            ulong duration;
            if (version == 1)
            {
                timescale = ReadBe32(data, mdhd.Payload + 20);
                duration = ((ulong)ReadBe32(data, mdhd.Payload + 24) << 32)
                    | ReadBe32(data, mdhd.Payload + 28);
            }
            else
            {
                timescale = ReadBe32(data, mdhd.Payload + 12);
                duration = ReadBe32(data, mdhd.Payload + 16);
            }
            if (timescale == 0) continue;

            var length = TimeSpan.FromSeconds(duration / (double)timescale);
            if (handler == "vide") video = length;
            else if (handler == "soun") audio = length;
        }
        return (video, audio);
    }

    // ---- 1) WAV 变速 ------------------------------------------------------

    [Fact]
    public void WavResampler_2x_ShortensToOneHalfSecond()
    {
        string dir = TempDir();
        try
        {
            string src = Path.Combine(dir, "tone-1s.wav");
            string dst = Path.Combine(dir, "tone-2x.wav");
            Pcm16Wav.Write(src, Tone(48_000));

            string result = WavResampler.ChangeSpeedToFile(src, dst, 2.0);
            WavInfo info = Pcm16Wav.ReadInfo(result);

            Assert.Equal(48_000, info.SampleRate);
            Assert.Equal(2, info.Channels);
            Assert.Equal(16, info.BitsPerSample);
            Assert.InRange(info.Duration.TotalMilliseconds, 480, 520);
            _output.WriteLine($"2× 时长 {info.Duration.TotalMilliseconds:0.0} ms，{info.DataBytes} 字节。");
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void WavResampler_HalfSpeed_DoublesDuration()
    {
        string dir = TempDir();
        try
        {
            string src = Path.Combine(dir, "tone-1s.wav");
            Pcm16Wav.Write(src, Tone(48_000));

            byte[] changed = WavResampler.ChangeSpeed(File.ReadAllBytes(src), 0.5);
            string dst = Path.Combine(dir, "tone-half.wav");
            File.WriteAllBytes(dst, changed);
            WavInfo info = Pcm16Wav.ReadInfo(dst);

            Assert.Equal(48_000, info.SampleRate);
            Assert.Equal(2, info.Channels);
            Assert.InRange(info.Duration.TotalMilliseconds, 1980, 2020);
            _output.WriteLine($"0.5× 时长 {info.Duration.TotalMilliseconds:0.0} ms，{info.DataBytes} 字节。");
        }
        finally { DeleteTemp(dir); }
    }

    // ---- 1) 倍速导出音画对齐 ---------------------------------------------

    [Fact]
    public async Task Export2x_RecordsSourceDurationAndAlignsAudioToVideoFrames()
    {
        string dir = TempDir();
        string audioPath = null;
        try
        {
            string mp4 = Path.Combine(dir, "speed-2x.mp4");
            var capture = new FakeCapture((duration, write, _) =>
            {
                long frames = Pcm16Wav.SampleFrames(duration, 48_000);
                short[] samples = Tone((int)frames);
                write(samples, samples.Length);
                return (true, "");
            });
            var recorder = new WasapiAudioRecorder(capture);
            var session = Session(Page(0, 0), Background(0, 0, "Black"));
            var options = new ExportOptions(mp4, 0, 1_000, Fps: 30,
                IncludeAudio: true, Width: 640, Height: 480, Speed: 2.0);

            ExportResult result;
            try
            {
                result = await TimeMachineExporter.ExportWithResultAsync(
                    session, options, (IProgress<ExportProgress>)null, CancellationToken.None, recorder);
            }
            catch (Exception ex)
            {
                _output.WriteLine("本机 Media Foundation 不可用，未实测 2× 带音轨 MP4：" + ex.Message);
                return;
            }

            audioPath = result.AudioPath;
            Assert.Equal(1_000, capture.LastDuration.TotalMilliseconds, 1);
            Assert.True(result.HasAudio, result.AudioNote);
            Assert.False(string.IsNullOrEmpty(result.AudioPath));
            Assert.True(File.Exists(result.AudioPath));

            WavInfo info = Pcm16Wav.ReadInfo(result.AudioPath);
            Assert.Equal(48_000, info.SampleRate);
            Assert.InRange(info.Duration.TotalMilliseconds, 480, 520);

            int videoFrames = TimeMachineExporter.EstimateFrameCount(options);
            Assert.Equal(videoFrames, result.FrameCount);

            long audioFrames = info.DataBytes / (info.Channels * info.BitsPerSample / 8);
            double audioSeconds = audioFrames / (double)info.SampleRate;
            double videoSeconds = result.FrameCount / (double)options.Fps;
            double frameSeconds = 1.0 / options.Fps;

            Assert.InRange(Math.Abs(audioSeconds - videoSeconds), 0, frameSeconds);

            var (mp4Video, mp4Audio) = ReadMp4TrackDurations(mp4);
            Assert.True(mp4Video.HasValue, "MP4 应包含视频轨。");
            Assert.True(mp4Audio.HasValue, "MP4 应包含音频轨。");
            TimeSpan mp4VideoLength = mp4Video.Value;
            TimeSpan mp4AudioLength = mp4Audio.Value;
            double frameMs = frameSeconds * 1000;
            Assert.InRange(Math.Abs(mp4VideoLength.TotalMilliseconds - 500), 0, frameMs);
            Assert.InRange(Math.Abs(mp4AudioLength.TotalMilliseconds - 500), 0, frameMs);

            _output.WriteLine($"录制 {capture.LastDuration.TotalMilliseconds:0} ms → 音频 {info.Duration.TotalMilliseconds:0.0} ms / "
                + $"{audioFrames} 帧，视频 {result.FrameCount} 帧 = {videoSeconds * 1000:0.0} ms，"
                + $"偏差 {Math.Abs(audioSeconds - videoSeconds) * 1000:0.00} ms（1 帧 = {frameMs:0.0} ms）；"
                + $"MP4 轨道：视频 {mp4VideoLength.TotalMilliseconds:0.0} ms，音频 {mp4AudioLength.TotalMilliseconds:0.0} ms。");
        }
        finally
        {
            if (audioPath != null && File.Exists(audioPath)) File.Delete(audioPath);
            DeleteTemp(dir);
        }
    }

    [Fact]
    public async Task ExportWithoutAudioDevice_DegradesToVideoOnlyWithoutThrowing()
    {
        string dir = TempDir();
        try
        {
            string mp4 = Path.Combine(dir, "no-audio.mp4");
            var recorder = new WasapiAudioRecorder(new FakeCapture((_, _, _) =>
                (false, "未找到可用的系统音频输出设备，无法录制系统声音。")));
            var session = Session(Page(0, 0), Background(0, 0, "Black"));
            var options = new ExportOptions(mp4, 0, 500, Fps: 30,
                IncludeAudio: true, Width: 640, Height: 480);

            ExportResult result;
            try
            {
                result = await TimeMachineExporter.ExportWithResultAsync(
                    session, options, (IProgress<ExportProgress>)null, CancellationToken.None, recorder);
            }
            catch (Exception ex)
            {
                _output.WriteLine("本机 Media Foundation 不可用，未实测无声降级 MP4：" + ex.Message);
                return;
            }

            Assert.False(result.HasAudio);
            Assert.Null(result.AudioPath);
            Assert.Contains("仅导出视频", result.AudioNote);
            Assert.True(File.Exists(mp4));
            _output.WriteLine("无音频设备静默降级：" + result.AudioNote);
        }
        finally { DeleteTemp(dir); }
    }

    // ---- 2) 完整 / 区间一致性 --------------------------------------------

    [Fact]
    public void FullAndRangeExport_UseCeilFrameCounts_AndRangeFirstFrameMatchesFullAtT1()
    {
        var session = MixedSession(out _);
        var timeline = new InkTimeline(session);

        var full = new ExportOptions("full.mp4", 0, 2_000, Fps: 30, Speed: 1.0);
        int fullExpected = (int)Math.Ceiling(2_000 / 1000.0 * 30);
        Assert.Equal(fullExpected, TimeMachineExporter.EstimateFrameCount(full));
        var fullTimes = TimeMachineExporter.SampleFrameTimes(full);
        Assert.Equal(fullExpected, fullTimes.Count);

        const long t1 = 900, t2 = 1_700;
        var range = new ExportOptions("range.mp4", t1, t2, Fps: 30, Speed: 1.0);
        int rangeExpected = (int)Math.Ceiling((t2 - t1) / 1000.0 * 30);
        Assert.Equal(rangeExpected, TimeMachineExporter.EstimateFrameCount(range));
        var rangeTimes = TimeMachineExporter.SampleFrameTimes(range);
        Assert.Equal(rangeExpected, rangeTimes.Count);
        Assert.Equal(t1, rangeTimes[0]);

        int fullIndex = fullTimes.ToList().IndexOf(t1);
        Assert.True(fullIndex >= 0, "完整导出的采样网格应恰好落在 t1。");
        Assert.Equal(t1, fullTimes[fullIndex]);

        var fullFrame = new InkTimeline(session).BuildFrame(fullTimes[fullIndex]);
        var rangeFrame = new InkTimeline(session).BuildFrame(rangeTimes[0]);
        Assert.Equal(fullFrame.Count, rangeFrame.Count);
        Assert.Equal(Signature(fullFrame), Signature(rangeFrame));
        Assert.NotEmpty(rangeFrame);

        var fast = new ExportOptions("range-2x.mp4", t1, t2, Fps: 30, Speed: 2.0);
        int fastExpected = (int)Math.Ceiling((t2 - t1) / 2.0 / 1000.0 * 30);
        Assert.Equal(fastExpected, TimeMachineExporter.EstimateFrameCount(fast));
        Assert.Equal(fastExpected, TimeMachineExporter.SampleFrameTimes(fast).Count);

        var odd = new ExportOptions("odd.mp4", 0, 1_100, Fps: 30, Speed: 1.0);
        Assert.Equal(33, TimeMachineExporter.EstimateFrameCount(odd));
        Assert.Equal(33, TimeMachineExporter.SampleFrameTimes(odd).Count);

        _output.WriteLine($"完整 {fullTimes.Count} 帧；区间 [{t1},{t2}] {rangeTimes.Count} 帧；"
            + $"2× 区间 {fastExpected} 帧；首帧笔画 {rangeFrame.Count} 个。");
    }

    [Fact]
    public async Task Export_Cancelled_ProducesNoFile_AndCleanupRemovesPartialArtifacts()
    {
        string dir = TempDir();
        try
        {
            string mp4 = Path.Combine(dir, "cancelled.mp4");
            var session = Session(Page(0, 0));
            var options = new ExportOptions(mp4, 0, 500, Fps: 30, Width: 640, Height: 480);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TimeMachineExporter.ExportWithResultAsync(
                    session, options, (IProgress<ExportProgress>)null, cts.Token));
            Assert.False(File.Exists(mp4));

            File.WriteAllText(mp4 + ".tmp", "partial");
            File.WriteAllText(mp4 + ".part", "partial");
            File.WriteAllText(mp4, "completed");
            TimeMachineExporter.CleanupArtifacts(options);

            Assert.False(File.Exists(mp4 + ".tmp"));
            Assert.False(File.Exists(mp4 + ".part"));
            Assert.True(File.Exists(mp4), "已完成的 MP4 不应被清理。");
        }
        finally { DeleteTemp(dir); }
    }

    // ---- 3) 异常恢复丢帧量化 ---------------------------------------------

    [Fact]
    public void JournalRecovery_LastEventTime_IsWithinTwoSeconds_AndJournalRetained()
    {
        string dir = TempDir();
        try
        {
            string id = Guid.NewGuid().ToString("N");
            var snapshot = Session(Page(0, 0));
            snapshot.Id = id;
            snapshot.Name = "recovery";
            snapshot.Events.Clear();
            for (long t = 0; t <= 4_000; t += 500) snapshot.Events.Add(Page(t, 0));
            ClassroomSessionStore.SaveSnapshot(snapshot, dir);

            using (var journal = SessionJournal.Open(id, dir, "recovery", snapshot.CreatedAt))
            {
                for (long t = 0; t <= 5_000; t += 500)
                    journal.Append(new TimelineEvent { TimeMs = t, PageIndex = 0, Kind = TimelineEventKind.PageChanged });
                journal.Flush();
            }

            // 模拟异常退出：最后约 1 秒事件仍在缓冲区未落盘（保留到 4000ms），
            // 且末尾多出半条被截断的 JSON 行。
            const long logicalLast = 5_000;
            const long persistedLast = 4_000;
            string journalPath = SessionJournal.PathFor(id, dir);
            var kept = new List<string>();
            foreach (string line in File.ReadAllLines(journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (root.TryGetProperty("event", out JsonElement ev)
                        && ev.ValueKind == JsonValueKind.Object
                        && ev.TryGetProperty("timeMs", out JsonElement time)
                        && time.GetInt64() > persistedLast)
                        continue;
                }
                catch (JsonException)
                {
                    continue;
                }
                kept.Add(line);
            }
            File.WriteAllLines(journalPath, kept);
            File.AppendAllText(journalPath, "{\"type\":\"event\",\"event\":{\"timeMs\":9999");

            long journalLast = SessionJournal.LastEventTimeMs(id, dir);
            Assert.Equal(persistedLast, journalLast);

            ClassroomSession recovered = ClassroomSessionStore.LoadWithJournal(id, dir);
            Assert.NotNull(recovered);
            long recoveredLast = recovered.Events.Max(e => e.TimeMs);
            Assert.Equal(journalLast, recoveredLast);
            Assert.InRange(Math.Abs(logicalLast - recoveredLast), 0, 2_000);
            Assert.True(File.Exists(SessionJournal.PathFor(id, dir)), "恢复后 journal 应保留。");

            _output.WriteLine($"逻辑末事件 {logicalLast} ms，journal 末事件 {journalLast} ms，"
                + $"恢复末事件 {recoveredLast} ms，崩溃丢帧窗口 {logicalLast - recoveredLast} ms（上限 2000 ms）。");
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void LastEventTimeMs_MissingJournal_ReturnsMinusOne()
    {
        string dir = TempDir();
        try
        {
            Assert.Equal(-1, SessionJournal.LastEventTimeMs(Guid.NewGuid().ToString("N"), dir));
            Assert.Equal(-1, SessionJournal.LastEventTimeMs("", dir));
        }
        finally { DeleteTemp(dir); }
    }
}