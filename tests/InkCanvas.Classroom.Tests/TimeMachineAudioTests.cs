using System.Text;
using InkCanvas.Core.Classroom;
using InkCanvas.Features.Classroom;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 讲解音频：WAV 头合法性 / 时长对齐 / 无声与失败降级 / ExportOptions 校验 /
/// 合成 PCM 走 MF AAC 合并路径（本机编码器可用时验证 MP4 含音轨）。
/// </summary>
public sealed class TimeMachineAudioTests
{
    private readonly ITestOutputHelper _output;

    public TimeMachineAudioTests(ITestOutputHelper output) => _output = output;

    // ---- helpers ---------------------------------------------------------

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-audio-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static short[] Silence(int frames) => new short[frames * 2];

    private sealed class FakeCapture : IWasapiLoopbackCapture
    {
        private readonly Func<TimeSpan, Action<short[], int>, CancellationToken, (bool Ok, string Error)> _capture;

        public FakeCapture(Func<TimeSpan, Action<short[], int>, CancellationToken, (bool Ok, string Error)> capture)
            => _capture = capture;

        public bool Capture(TimeSpan duration, Action<short[], int> writePcm, CancellationToken ct, out string error)
        {
            (bool ok, string message) = _capture(duration, writePcm, ct);
            error = message;
            return ok;
        }
    }

    private static bool ContainsAscii(byte[] bytes, string text)
        => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    // ---- WAV 头 / 时长 ---------------------------------------------------

    [Fact]
    public void Pcm16Wav_WriteAndRead_ProducesLegalHeaderAndDuration()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "silence.wav");
            Pcm16Wav.Write(path, Silence(48_000));

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal("fmt ", Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.Equal("data", Encoding.ASCII.GetString(bytes, 36, 4));
            Assert.Equal(Pcm16Wav.HeaderSize + 48_000 * 4, bytes.Length);
            Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(48_000 * 4, BitConverter.ToInt32(bytes, 40));

            WavInfo info = Pcm16Wav.ReadInfo(path);
            Assert.Equal(1, info.AudioFormat);
            Assert.Equal(2, info.Channels);
            Assert.Equal(48_000, info.SampleRate);
            Assert.Equal(16, info.BitsPerSample);
            Assert.Equal(48_000 * 4, info.DataBytes);
            Assert.Equal(TimeSpan.FromSeconds(1), info.Duration);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AudioSampleFrames_MatchesVideoDurationAndOptions()
    {
        var options = new ExportOptions("a.mp4", 1_000, 3_500, 30);
        Assert.Equal(120_000, TimeMachineExporter.AudioSampleFrames(options));

        IReadOnlyList<long> frames = TimeMachineExporter.SampleFrameTimes(options);
        double videoSeconds = frames.Count / 30.0;
        double audioSeconds = TimeMachineExporter.AudioSampleFrames(options) / 48_000.0;
        Assert.Equal(2.5, videoSeconds, 3);
        Assert.Equal(videoSeconds, audioSeconds, 3);
    }

    // ---- 录制失败 / 无声 -------------------------------------------------

    [Fact]
    public async Task Recorder_NoAudioDevice_ReturnsNullWithChineseReason()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "no-device.wav");
            var recorder = new WasapiAudioRecorder(new FakeCapture((_, _, _) =>
                (false, "未找到可用的系统音频输出设备，无法录制系统声音。")));

            string result = await recorder.RecordAsync(path, TimeSpan.FromMilliseconds(100), CancellationToken.None);

            Assert.Null(result);
            Assert.False(string.IsNullOrWhiteSpace(recorder.LastError));
            Assert.Contains("设备", recorder.LastError);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recorder_BackendThrows_ReturnsNullWithoutThrowing()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "throw.wav");
            var recorder = new WasapiAudioRecorder(new FakeCapture((_, _, _) =>
                throw new InvalidOperationException("模拟音频设备错误。")));

            string result = await recorder.RecordAsync(path, TimeSpan.FromMilliseconds(100), CancellationToken.None);

            Assert.Null(result);
            Assert.Contains("录制讲解音频失败", recorder.LastError);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recorder_SilentCapture_StillWritesFullLengthWav()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "silent.wav");
            var recorder = new WasapiAudioRecorder(new FakeCapture((_, write, _) =>
            {
                short[] silence = Silence(48_000);
                write(silence, silence.Length);
                return (true, "");
            }));

            string result = await recorder.RecordAsync(path, TimeSpan.FromSeconds(1), CancellationToken.None);

            Assert.Equal(path, result);
            Assert.Equal("", recorder.LastError);
            WavInfo info = Pcm16Wav.ReadInfo(path);
            Assert.True(info.IsPcm16);
            Assert.Equal(48_000 * 4, info.DataBytes);
            Assert.Equal(TimeSpan.FromSeconds(1), info.Duration);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recorder_ShortCapture_PadsSilenceToRequestedDuration()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "short.wav");
            var recorder = new WasapiAudioRecorder(new FakeCapture((_, write, _) =>
            {
                short[] samples = Silence(12_000); // 0.25 秒
                write(samples, samples.Length);
                return (true, "");
            }));

            string result = await recorder.RecordAsync(path, TimeSpan.FromSeconds(1), CancellationToken.None);

            Assert.Equal(path, result);
            WavInfo info = Pcm16Wav.ReadInfo(path);
            Assert.Equal(48_000 * 4, info.DataBytes);
            Assert.Equal(TimeSpan.FromSeconds(1), info.Duration);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recorder_LongCapture_TrimsToRequestedDuration()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "long.wav");
            var recorder = new WasapiAudioRecorder(new FakeCapture((_, write, _) =>
            {
                short[] samples = Silence(144_000); // 3 秒
                write(samples, samples.Length);
                return (true, "");
            }));

            string result = await recorder.RecordAsync(path, TimeSpan.FromSeconds(1), CancellationToken.None);

            Assert.Equal(path, result);
            WavInfo info = Pcm16Wav.ReadInfo(path);
            Assert.Equal(48_000 * 4, info.DataBytes);
            Assert.Equal(TimeSpan.FromSeconds(1), info.Duration);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recorder_Cancelled_DeletesPartialWavAndThrows()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "cancel.wav");
            var recorder = new WasapiAudioRecorder(new FakeCapture((_, _, _) => (true, "")));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => recorder.RecordAsync(path, TimeSpan.FromSeconds(1), cts.Token));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recorder_RealWasapi_FailsGracefullyOrWritesLegalWav()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "real.wav");
            var recorder = new WasapiAudioRecorder();

            string result = await recorder.RecordAsync(path, TimeSpan.FromMilliseconds(80), CancellationToken.None);

            if (result is null)
            {
                Assert.False(string.IsNullOrWhiteSpace(recorder.LastError));
                _output.WriteLine("本机 WASAPI 不可用：" + recorder.LastError);
                return;
            }

            WavInfo info = Pcm16Wav.ReadInfo(result);
            Assert.True(info.IsPcm16);
            Assert.Equal(48_000, info.SampleRate);
            _output.WriteLine($"本机 WASAPI 录制成功：{info.DataBytes} 字节。");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- ExportOptions / 结果语义 ----------------------------------------

    [Fact]
    public void ExportOptions_Validate_RejectsInvalidValuesAndAcceptsAudio()
    {
        Assert.Throws<ArgumentNullException>(() => TimeMachineExporter.Validate(null));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(new ExportOptions("", 0, 1_000)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(new ExportOptions("a.mkv", 0, 1_000)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(new ExportOptions("a.mp4", -1, 1_000)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(new ExportOptions("a.mp4", 1_000, 1_000)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(new ExportOptions("a.mp4", 0, 1_000, 0)));
        Assert.Throws<ArgumentException>(() => TimeMachineExporter.Validate(new ExportOptions("a.mp4", 0, 1_000, 30, 1_000)));

        TimeMachineExporter.Validate(new ExportOptions("a.mp4", 0, 1_000, 30, 4_000_000, true));
    }

    [Fact]
    public void ExportResult_HasAudioSemantics_AreExplicit()
    {
        var videoOnly = new ExportResult("a.mp4", 60, false, "系统音频设备不可用，已仅导出视频。");
        Assert.False(videoOnly.HasAudio);
        Assert.Null(videoOnly.AudioPath);
        Assert.Contains("仅导出视频", videoOnly.AudioNote);

        var withAudio = new ExportResult("a.mp4", 60, true, "已同步录制系统讲解音频。", "a.wav");
        Assert.True(withAudio.HasAudio);
        Assert.Equal("a.wav", withAudio.AudioPath);
    }

    // ---- 合成 PCM -> MF AAC -> MP4 音轨 ----------------------------------

    [Fact]
    public async Task ExportWithSyntheticAudio_ProducesMp4WithAudioTrack()
    {
        string dir = TempDir();
        string mp4 = Path.Combine(dir, "with-audio.mp4");
        string audioPath = null;
        try
        {
            var session = new ClassroomSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "audio-export",
                CreatedAt = DateTime.Now,
                Events = new List<TimelineEvent>(),
                Checkpoints = new List<SessionCheckpoint>(),
            };
            var capture = new FakeCapture((duration, write, _) =>
            {
                long frames = Pcm16Wav.SampleFrames(duration, 48_000);
                short[] samples = new short[frames * 2];
                for (int i = 0; i < frames; i++)
                {
                    short value = (short)(Math.Sin(2 * Math.PI * 440 * i / 48_000.0) * 8_000);
                    samples[i * 2] = value;
                    samples[i * 2 + 1] = value;
                }
                write(samples, samples.Length);
                return (true, "");
            });
            var recorder = new WasapiAudioRecorder(capture);

            var options = new ExportOptions(mp4, 0, 2_000, 30, 4_000_000, IncludeAudio: true);
            ExportResult result;
            try
            {
                result = await TimeMachineExporter.ExportWithResultAsync(
                    session, options, (IProgress<ExportProgress>)null, CancellationToken.None, recorder);
            }
            catch (Exception ex)
            {
                _output.WriteLine("本机 Media Foundation 不可用，未实测带音轨 MP4：" + ex.Message);
                return;
            }

            audioPath = result.AudioPath;
            Assert.True(result.HasAudio, result.AudioNote);
            Assert.False(string.IsNullOrEmpty(result.AudioPath));
            Assert.True(File.Exists(result.AudioPath));
            Assert.True(File.Exists(mp4));
            Assert.True(new FileInfo(mp4).Length > 0);

            byte[] bytes = File.ReadAllBytes(mp4);
            Assert.True(ContainsAscii(bytes, "soun"), "MP4 应包含音频轨道（soun handler）。");
            Assert.True(ContainsAscii(bytes, "mp4a"), "MP4 应包含 AAC 采样描述（mp4a）。");
            _output.WriteLine($"MP4 {new FileInfo(mp4).Length} 字节，WAV {new FileInfo(result.AudioPath).Length} 字节。");
        }
        finally
        {
            if (audioPath != null && File.Exists(audioPath)) File.Delete(audioPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}