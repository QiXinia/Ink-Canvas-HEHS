using System.Text;

namespace InkCanvas.Features.Classroom;

/// <summary>
/// 16-bit PCM WAV 变速（线性重采样）。输出采样率 / 声道数 / 位深保持不变，
/// 时长 = 原时长 / speed：speed &gt; 1 缩短（快放），speed &lt; 1 拉长（慢放）。
/// 仅支持标准 PCM 容器，不依赖任何第三方库。
/// </summary>
public static class WavResampler
{
    /// <summary>
    /// 对内存中的 WAV 做变速，返回新的合法 WAV（44 字节标准头）。
    /// 非 16-bit PCM、无 data 块或 <paramref name="speed"/> 非正数时抛出中文异常。
    /// </summary>
    public static byte[] ChangeSpeed(byte[] wav, double speed)
    {
        if (wav is null) throw new ArgumentNullException(nameof(wav), "WAV 数据不能为空。");
        if (!double.IsFinite(speed) || speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed), "变速倍率必须是大于 0 的有限数。");

        WavLayout layout = Parse(wav);
        int blockAlign = layout.Channels * 2;
        int inputFrames = layout.DataBytes / blockAlign;
        if (inputFrames <= 0)
            throw new InvalidDataException("WAV 没有可重采样的 PCM 数据。");

        long outputFrames = (long)Math.Round(inputFrames / speed, MidpointRounding.AwayFromZero);
        if (outputFrames < 1) outputFrames = 1;

        long outputBytes = outputFrames * blockAlign;
        if (outputBytes > int.MaxValue - Pcm16Wav.HeaderSize)
            throw new InvalidOperationException("变速后的 WAV 过大，无法在内存中生成。");

        var input = new short[inputFrames * layout.Channels];
        Buffer.BlockCopy(wav, layout.DataOffset, input, 0, inputFrames * blockAlign);

        int outFrames = checked((int)outputFrames);
        var output = new short[outFrames * layout.Channels];
        int channels = layout.Channels;

        for (int frame = 0; frame < outFrames; frame++)
        {
            double source = frame * speed;
            long left = (long)Math.Floor(source);
            if (left >= inputFrames - 1)
            {
                left = inputFrames - 1;
                source = left;
            }
            long right = Math.Min(left + 1, inputFrames - 1);
            double fraction = source - left;

            int baseIndex = frame * channels;
            int leftBase = (int)left * channels;
            int rightBase = (int)right * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                double a = input[leftBase + channel];
                double b = input[rightBase + channel];
                output[baseIndex + channel] = ToPcm16(a + (b - a) * fraction);
            }
        }

        var result = new byte[Pcm16Wav.HeaderSize + (int)outputBytes];
        using (var header = new MemoryStream(result))
            Pcm16Wav.WriteHeader(header, layout.SampleRate, layout.Channels, outputBytes);
        Buffer.BlockCopy(output, 0, result, Pcm16Wav.HeaderSize, (int)outputBytes);
        return result;
    }

    /// <summary>读取 <paramref name="src"/> 变速后写入 <paramref name="dst"/>，返回输出绝对路径。</summary>
    public static string ChangeSpeedToFile(string src, string dst, double speed)
    {
        if (string.IsNullOrWhiteSpace(src)) throw new ArgumentException("源 WAV 路径不能为空。", nameof(src));
        if (string.IsNullOrWhiteSpace(dst)) throw new ArgumentException("目标 WAV 路径不能为空。", nameof(dst));

        string fullPath = Path.GetFullPath(dst);
        string directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        byte[] changed = ChangeSpeed(File.ReadAllBytes(src), speed);
        File.WriteAllBytes(fullPath, changed);
        return fullPath;
    }

    private static short ToPcm16(double value)
    {
        if (value >= short.MaxValue) return short.MaxValue;
        if (value <= short.MinValue) return short.MinValue;
        return (short)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static WavLayout Parse(byte[] wav)
    {
        if (wav.Length < 12)
            throw new InvalidDataException("WAV 文件过短，缺少 RIFF/WAVE 头。");

        if (!Matches(wav, 0, "RIFF") || !Matches(wav, 8, "WAVE"))
            throw new InvalidDataException("不是合法的 RIFF/WAVE 文件。");

        int formatTag = 0;
        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        int dataOffset = -1;
        int dataBytes = 0;

        int position = 12;
        while (position + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, position, 4);
            uint size = BitConverter.ToUInt32(wav, position + 4);
            int payload = position + 8;
            long end = (long)payload + size;

            if (id == "fmt " && size >= 16 && payload + 16 <= wav.Length)
            {
                formatTag = BitConverter.ToUInt16(wav, payload);
                channels = BitConverter.ToUInt16(wav, payload + 2);
                sampleRate = BitConverter.ToInt32(wav, payload + 4);
                bitsPerSample = BitConverter.ToUInt16(wav, payload + 14);
            }
            else if (id == "data")
            {
                dataOffset = payload;
                dataBytes = (int)Math.Min(size, (long)wav.Length - payload);
                break;
            }

            if (end > wav.Length) break;
            position = (int)end + (int)(size & 1);
        }

        if (formatTag != 1)
            throw new InvalidDataException("仅支持 16-bit PCM WAV（format=1）。");
        if (bitsPerSample != 16)
            throw new InvalidDataException("仅支持 16-bit PCM WAV（BitsPerSample=16）。");
        if (channels <= 0 || sampleRate <= 0)
            throw new InvalidDataException("WAV 头中的声道数或采样率无效。");
        if (dataOffset < 0 || dataBytes <= 0)
            throw new InvalidDataException("WAV 缺少 data 块。");

        return new WavLayout(channels, sampleRate, dataOffset, dataBytes);
    }

    private static bool Matches(byte[] bytes, int offset, string ascii)
        => offset >= 0 && offset + ascii.Length <= bytes.Length
            && Encoding.ASCII.GetString(bytes, offset, ascii.Length) == ascii;

    private readonly record struct WavLayout(int Channels, int SampleRate, int DataOffset, int DataBytes);
}