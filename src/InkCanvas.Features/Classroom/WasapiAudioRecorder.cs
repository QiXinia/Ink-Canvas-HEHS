using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace InkCanvas.Features.Classroom;

/// <summary>系统声音采集后端（WASAPI loopback）。抽出接口便于在没有音频设备的环境里测试失败降级。</summary>
public interface IWasapiLoopbackCapture
{
    /// <summary>
    /// 按实时采集 <paramref name="duration"/> 时长的系统声音，把 48 kHz / 2 声道 / 16-bit PCM
    /// 以交错短整型写入 <paramref name="writePcm"/>（第二个参数是 short 个数）。
    /// 成功返回 true；失败返回 false 并在 <paramref name="error"/> 写入中文原因。
    /// </summary>
    bool Capture(TimeSpan duration, Action<short[], int> writePcm, CancellationToken ct, out string error);
}

/// <summary>PCM 16-bit WAV 读写辅助（RIFF 头 + PCM 数据），同时用于测试与音频编码回读。</summary>
public static class Pcm16Wav
{
    public const int HeaderSize = 44;

    /// <summary>按采样率计算指定时长对应的 PCM 帧数。</summary>
    public static long SampleFrames(TimeSpan duration, int sampleRate)
    {
        if (duration <= TimeSpan.Zero || sampleRate <= 0) return 0;
        return (long)Math.Round(duration.TotalSeconds * sampleRate, MidpointRounding.AwayFromZero);
    }

    /// <summary>写入标准 44 字节 PCM WAV 头（始终从文件开头写入，不改变已有数据）。</summary>
    public static void WriteHeader(Stream stream, int sampleRate, int channels, long dataBytes)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (dataBytes < 0) throw new ArgumentOutOfRangeException(nameof(dataBytes));

        int blockAlign = channels * 2;
        stream.Position = 0;
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * blockAlign));
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataBytes);
    }

    /// <summary>把交错 PCM 写成合法 WAV（16-bit、指定采样率与声道数）。</summary>
    public static void Write(string path, short[] samples, int sampleRate = 48_000, int channels = 2)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (samples is null) throw new ArgumentNullException(nameof(samples));

        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeader(stream, sampleRate, channels, (long)samples.Length * sizeof(short));
        stream.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
    }

    /// <summary>解析 WAV 头与 data 块，返回时长等关键信息。只支持 PCM/IEEE float 容器。</summary>
    public static WavInfo ReadInfo(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 12) throw new InvalidDataException("WAV 文件过短。");

        string riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
        reader.ReadUInt32();
        string wave = Encoding.ASCII.GetString(reader.ReadBytes(4));

        int audioFormat = 0;
        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        long dataBytes = 0;
        long dataOffset = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            uint size = reader.ReadUInt32();
            long payload = stream.Position;
            long end = payload + size;

            if (id == "fmt ")
            {
                audioFormat = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = checked((int)reader.ReadUInt32());
                reader.ReadUInt32();
                reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                if (audioFormat == 0xFFFE && size >= 40)
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    byte[] sub = reader.ReadBytes(16);
                    audioFormat = (int)BitConverter.ToUInt32(sub, 0);
                }
            }
            else if (id == "data")
            {
                dataBytes = Math.Min(size, stream.Length - payload);
                dataOffset = payload;
                break;
            }

            stream.Position = Math.Min(end + (size & 1), stream.Length);
        }

        int blockAlign = channels * (bitsPerSample / 8);
        double seconds = blockAlign > 0 && sampleRate > 0
            ? dataBytes / (double)(sampleRate * blockAlign)
            : 0;

        return new WavInfo(riff, wave, audioFormat, channels, sampleRate, bitsPerSample,
            dataBytes, dataOffset, TimeSpan.FromSeconds(seconds));
    }
}

/// <summary>WAV 头解析结果。</summary>
public sealed record WavInfo(
    string ChunkId,
    string Format,
    int AudioFormat,
    int Channels,
    int SampleRate,
    int BitsPerSample,
    long DataBytes,
    long DataOffset,
    TimeSpan Duration)
{
    public bool IsPcm16 => AudioFormat == 1 && BitsPerSample == 16;
}

/// <summary>
/// 用 WASAPI loopback 录制系统/讲解声，并写成 48 kHz / 2 声道 / 16-bit PCM 的 WAV。
/// <para>
/// 设备缺失、权限不足或初始化失败时返回 null 并把中文原因写入 <see cref="LastError"/>，
/// 不向调用方抛未处理异常；取消时抛 <see cref="OperationCanceledException"/> 并删除半成品。
/// 静音仍会生成时长完整的 WAV，保证可以合并进 MP4。
/// </para>
/// </summary>
public sealed class WasapiAudioRecorder
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int BlockAlign = Channels * BitsPerSample / 8;

    private readonly IWasapiLoopbackCapture _capture;

    public WasapiAudioRecorder()
        : this(new WasapiLoopbackCapture())
    {
    }

    public WasapiAudioRecorder(IWasapiLoopbackCapture capture)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    /// <summary>最近一次录制失败的中文原因；成功后为空字符串。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>录制指定时长并返回 WAV 路径；失败返回 null。</summary>
    public Task<string> RecordAsync(string wavPath, TimeSpan duration, CancellationToken ct)
        => RecordAsync(wavPath, duration, null, ct);

    /// <summary>录制并报告 0–1 进度（按已写入目标帧数计算）。</summary>
    public Task<string> RecordAsync(string wavPath, TimeSpan duration, IProgress<double> progress, CancellationToken ct)
    {
        // 取消令牌交给内部处理，保证半成品能被删除。
        return Task.Run(() => RecordCore(wavPath, duration, progress, ct), CancellationToken.None);
    }

    private string RecordCore(string wavPath, TimeSpan duration, IProgress<double> progress, CancellationToken ct)
    {
        LastError = "";
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(wavPath))
        {
            LastError = "录制讲解音频失败：输出路径为空。";
            return null;
        }

        if (duration <= TimeSpan.Zero)
        {
            LastError = "录制讲解音频失败：录制时长必须大于 0。";
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(wavPath);
        }
        catch (Exception ex)
        {
            LastError = "录制讲解音频失败：输出路径无效（" + ex.Message + "）。";
            return null;
        }

        long targetFrames = Pcm16Wav.SampleFrames(duration, SampleRate);
        if (targetFrames <= 0) targetFrames = 1;
        long framesWritten = 0;
        FileStream stream = null;

        try
        {
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.Write(new byte[Pcm16Wav.HeaderSize]);

            bool ok = _capture.Capture(duration, (buffer, count) =>
            {
                int frames = count / Channels;
                long remaining = targetFrames - framesWritten;
                if (frames <= 0 || remaining <= 0) return;

                int framesToWrite = (int)Math.Min(frames, remaining);
                framesToWrite = Math.Min(framesToWrite, buffer.Length / Channels);
                if (framesToWrite <= 0) return;
                stream.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, framesToWrite * Channels)));
                framesWritten += framesToWrite;
                progress?.Report(Math.Clamp(framesWritten / (double)targetFrames, 0, 1));
            }, ct, out string error);

            if (!ok)
            {
                DisposeQuietly(stream);
                stream = null;
                TryDelete(fullPath);
                LastError = string.IsNullOrWhiteSpace(error)
                    ? "录制讲解音频失败：系统音频设备不可用。"
                    : error;
                return null;
            }

            long silenceFrames = targetFrames - framesWritten;
            if (silenceFrames > 0) WriteSilence(stream, silenceFrames);

            Pcm16Wav.WriteHeader(stream, SampleRate, Channels, targetFrames * BlockAlign);
            stream.Flush();
            DisposeQuietly(stream);
            stream = null;

            progress?.Report(1);
            return fullPath;
        }
        catch (OperationCanceledException)
        {
            DisposeQuietly(stream);
            TryDelete(fullPath);
            throw;
        }
        catch (Exception ex)
        {
            DisposeQuietly(stream);
            TryDelete(fullPath);
            LastError = "录制讲解音频失败：" + ex.Message;
            return null;
        }
    }

    private static void WriteSilence(Stream stream, long frames)
    {
        long bytes = frames * BlockAlign;
        byte[] chunk = new byte[4096];
        while (bytes > 0)
        {
            int count = (int)Math.Min(chunk.Length, bytes);
            stream.Write(chunk, 0, count);
            bytes -= count;
        }
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try { disposable?.Dispose(); }
        catch { /* 清理失败不覆盖原始结果 */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 清理失败不覆盖原始结果 */ }
    }
}

/// <summary>
/// WASAPI loopback 采集实现（COM interop）。优先请求 48 kHz / 2 声道 / 16-bit PCM
/// 并启用系统重采样；若设备不支持，则回退到混音格式并自行转换。
/// </summary>
public sealed class WasapiLoopbackCapture : IWasapiLoopbackCapture
{
    private const int ERender = 0;
    private const int EConsole = 0;
    private const int ClsCtxAll = 23;
    private const int Shared = 0;
    private const int Loopback = 0x00020000;
    private const int AutoConvertPcm = unchecked((int)0x80000000);
    private const int SrcDefaultQuality = 0x00010000;
    private const long BufferDuration = 2_000_000;
    private const int BufferFlagSilent = 0x2;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    private static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private const int OutputRate = WasapiAudioRecorder.SampleRate;
    private const int OutputChannels = WasapiAudioRecorder.Channels;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        [In] ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext,
        [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    public bool Capture(TimeSpan duration, Action<short[], int> writePcm, CancellationToken ct, out string error)
    {
        error = "";
        if (duration <= TimeSpan.Zero || writePcm is null)
        {
            error = "录制讲解音频失败：录制时长或输出回调无效。";
            return false;
        }

        bool comInitialized = false;
        object enumerator = null;
        IMMDevice device = null;
        IAudioClient audioClient = null;
        IAudioCaptureClient captureClient = null;
        IntPtr customFormat = IntPtr.Zero;
        IntPtr mixFormat = IntPtr.Zero;

        try
        {
            int hr = CoInitializeEx(IntPtr.Zero, 0);
            if (hr >= 0) comInitialized = true;
            else if (hr != RpcChangedMode)
            {
                error = "初始化音频子系统失败（HRESULT " + FormatHr(hr) + "）。";
                return false;
            }

            Guid clsid = ClsidEnumerator;
            Guid iid = IidEnumerator;
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out enumerator);
            if (hr < 0 || enumerator is not IMMDeviceEnumerator devices)
            {
                error = "创建系统音频设备枚举器失败（HRESULT " + FormatHr(hr) + "）。";
                return false;
            }

            hr = devices.GetDefaultAudioEndpoint(ERender, EConsole, out device);
            if (hr < 0 || device is null)
            {
                error = "未找到可用的系统音频输出设备，无法录制系统声音。";
                return false;
            }

            Guid iidAudioClient = IidAudioClient;
            hr = device.Activate(ref iidAudioClient, ClsCtxAll, IntPtr.Zero, out object audioClientObject);
            if (hr < 0 || audioClientObject is not IAudioClient client)
            {
                error = "激活 WASAPI 音频客户端失败（HRESULT " + FormatHr(hr) + "）。";
                return false;
            }
            audioClient = client;

            CaptureFormat format;
            customFormat = AllocPcmFormat(WasapiAudioRecorder.SampleRate, OutputChannels, WasapiAudioRecorder.BitsPerSample);
            int flags = Loopback | AutoConvertPcm | SrcDefaultQuality;
            hr = audioClient.Initialize(Shared, flags, BufferDuration, 0, customFormat, IntPtr.Zero);

            if (hr >= 0)
            {
                format = new CaptureFormat(WasapiAudioRecorder.SampleRate, OutputChannels,
                    WasapiAudioRecorder.BitsPerSample, false, WasapiAudioRecorder.BlockAlign);
            }
            else
            {
                hr = audioClient.GetMixFormat(out mixFormat);
                if (hr < 0 || mixFormat == IntPtr.Zero)
                {
                    error = "获取系统音频混音格式失败（HRESULT " + FormatHr(hr) + "）。";
                    return false;
                }

                format = ReadFormat(mixFormat);
                hr = audioClient.Initialize(Shared, Loopback, BufferDuration, 0, mixFormat, IntPtr.Zero);
                if (hr < 0)
                {
                    error = "初始化 WASAPI 录音失败（HRESULT " + FormatHr(hr) + "）。";
                    return false;
                }
            }

            Guid iidCapture = IidCaptureClient;
            hr = audioClient.GetService(ref iidCapture, out object captureObject);
            if (hr < 0 || captureObject is not IAudioCaptureClient capture)
            {
                error = "获取 WASAPI 采集客户端失败（HRESULT " + FormatHr(hr) + "）。";
                return false;
            }
            captureClient = capture;

            hr = audioClient.Start();
            if (hr < 0)
            {
                error = "启动 WASAPI 录音失败（HRESULT " + FormatHr(hr) + "）。";
                return false;
            }

            var converter = new PcmConverter(format, writePcm);
            var clock = Stopwatch.StartNew();
            uint packetFrames = 0;

            while (clock.Elapsed < duration)
            {
                ct.ThrowIfCancellationRequested();

                hr = captureClient.GetNextPacketSize(out packetFrames);
                if (hr < 0)
                {
                    error = "读取 WASAPI 音频包失败（HRESULT " + FormatHr(hr) + "）。";
                    return false;
                }

                while (packetFrames > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    hr = captureClient.GetBuffer(out IntPtr data, out uint frames,
                        out uint packetFlags, out _, out _);
                    if (hr < 0)
                    {
                        error = "读取 WASAPI 音频缓冲失败（HRESULT " + FormatHr(hr) + "）。";
                        return false;
                    }

                    bool silent = (packetFlags & BufferFlagSilent) != 0;
                    int byteCount = checked((int)frames * format.BlockAlign);
                    if (silent)
                    {
                        converter.AppendSilence((int)frames);
                    }
                    else if (byteCount > 0)
                    {
                        byte[] bytes = new byte[byteCount];
                        Marshal.Copy(data, bytes, 0, byteCount);
                        converter.Append(bytes, (int)frames);
                    }

                    captureClient.ReleaseBuffer(frames);

                    hr = captureClient.GetNextPacketSize(out packetFrames);
                    if (hr < 0)
                    {
                        error = "读取 WASAPI 音频包失败（HRESULT " + FormatHr(hr) + "）。";
                        return false;
                    }
                }

                Thread.Sleep(10);
            }

            audioClient.Stop();
            converter.Flush();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = "录制讲解音频失败：" + ex.Message;
            return false;
        }
        finally
        {
            try { audioClient?.Stop(); } catch { /* 停止失败不影响结果 */ }
            Release(captureClient);
            Release(audioClient);
            Release(device);
            Release(enumerator);
            if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
            if (customFormat != IntPtr.Zero) Marshal.FreeHGlobal(customFormat);
            if (comInitialized) CoUninitialize();
        }
    }

    private static string FormatHr(int hr) => "0x" + hr.ToString("X8");

    private static void Release(object comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject)) return;
        try { Marshal.ReleaseComObject(comObject); }
        catch { /* 释放失败不影响结果 */ }
    }

    private static IntPtr AllocPcmFormat(int sampleRate, int channels, int bitsPerSample)
    {
        int blockAlign = channels * (bitsPerSample / 8);
        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = (ushort)channels,
            SamplesPerSec = (uint)sampleRate,
            AvgBytesPerSec = (uint)(sampleRate * blockAlign),
            BlockAlign = (ushort)blockAlign,
            BitsPerSample = (ushort)bitsPerSample,
        };

        int size = Marshal.SizeOf<WaveFormatEx>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(format, pointer, false);
        return pointer;
    }

    private static CaptureFormat ReadFormat(IntPtr pointer)
    {
        var format = Marshal.PtrToStructure<WaveFormatEx>(pointer);
        bool isFloat = format.FormatTag == 3;
        if (format.FormatTag == 0xFFFE)
        {
            // WAVEFORMATEXTENSIBLE：SubFormat GUID 从结构偏移 24 开始。
            Guid subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(pointer, 24));
            isFloat = subFormat == SubFormatIeeeFloat;
        }

        return new CaptureFormat((int)format.SamplesPerSec, format.Channels,
            format.BitsPerSample, isFloat, format.BlockAlign);
    }

    private readonly record struct CaptureFormat(
        int SampleRate, int Channels, int BitsPerSample, bool IsFloat, int BlockAlign);

    /// <summary>把任意 mix format 转成 48 kHz / 2 声道 / 16-bit 交错 PCM。</summary>
    private sealed class PcmConverter
    {
        private readonly CaptureFormat _format;
        private readonly Action<short[], int> _write;
        private readonly short[] _batch = new short[4096];
        private readonly LinearResampler _resampler;
        private int _batchCount;

        public PcmConverter(CaptureFormat format, Action<short[], int> write)
        {
            _format = format;
            _write = write;
            if (format.SampleRate != OutputRate) _resampler = new LinearResampler(format.SampleRate, OutputRate);
        }

        public void Append(byte[] bytes, int frames)
        {
            int sampleBytes = Math.Max(1, _format.BitsPerSample / 8);
            int channelBytes = _format.Channels * sampleBytes;
            for (int i = 0; i < frames; i++)
            {
                int offset = i * channelBytes;
                double left = ReadSample(bytes, offset);
                double right = _format.Channels >= 2 ? ReadSample(bytes, offset + sampleBytes) : left;
                Emit(left, right);
            }
        }

        public void AppendSilence(int frames)
        {
            for (int i = 0; i < frames; i++) Emit(0, 0);
        }

        private void Emit(double left, double right)
        {
            if (_resampler is null)
            {
                EmitDirect(left, right);
                return;
            }
            _resampler.Feed(left, right, EmitDirect);
        }

        private void EmitDirect(double left, double right)
        {
            _batch[_batchCount++] = ToPcm16(left);
            _batch[_batchCount++] = ToPcm16(right);
            if (_batchCount >= _batch.Length) Flush();
        }

        public void Flush()
        {
            if (_batchCount <= 0) return;
            _write(_batch, _batchCount);
            _batchCount = 0;
        }

        private double ReadSample(byte[] bytes, int offset)
        {
            int bytesPerSample = Math.Max(1, _format.BitsPerSample / 8);
            if (_format.IsFloat)
            {
                if (bytesPerSample == 4) return BitConverter.ToSingle(bytes, offset);
                if (bytesPerSample == 8) return BitConverter.ToDouble(bytes, offset);
                return 0;
            }

            switch (bytesPerSample)
            {
                case 1:
                    return (bytes[offset] - 128) / 128.0;
                case 2:
                    return BitConverter.ToInt16(bytes, offset) / 32768.0;
                case 3:
                    int value = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
                    if ((value & 0x800000) != 0) value -= 0x1000000;
                    return value / 8388608.0;
                case 4:
                    return BitConverter.ToInt32(bytes, offset) / 2147483648.0;
                default:
                    return 0;
            }
        }

        private static short ToPcm16(double value)
        {
            if (value > 1) value = 1;
            else if (value < -1) value = -1;
            return (short)Math.Round(value * 32767, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>流式线性插值重采样，跨采集块保持相位连续。</summary>
    private sealed class LinearResampler
    {
        private readonly double _step;
        private double _position;
        private double _previousLeft;
        private double _previousRight;
        private int _frameIndex = -1;
        private bool _started;

        public LinearResampler(int inputRate, int outputRate)
        {
            _step = inputRate / (double)outputRate;
        }

        public void Feed(double left, double right, Action<double, double> emit)
        {
            if (!_started)
            {
                _started = true;
                _frameIndex = 0;
                _position = _step;
                _previousLeft = left;
                _previousRight = right;
                emit(left, right);
                return;
            }

            _frameIndex++;
            while (_position < _frameIndex)
            {
                double fraction = _position - (_frameIndex - 1);
                emit(_previousLeft + (left - _previousLeft) * fraction,
                     _previousRight + (right - _previousRight) * fraction);
                _position += _step;
            }

            _previousLeft = left;
            _previousRight = right;

        }
    }

    // ---- COM interop ------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration,
            long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}