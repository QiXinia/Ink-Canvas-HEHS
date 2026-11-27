using System.Buffers.Binary;

namespace InkCanvas.AnalyticGeometry.Construction;

/// <summary>Encodes opaque BGRA frames as a looping 3-3-2 palette GIF.</summary>
public static class AnimatedGifEncoder
{
    private const int MaximumFrames = 120;
    private const int MaximumPixels = 4 * 1024 * 1024;

    public static byte[] Encode(int width, int height, IReadOnlyList<byte[]> bgraFrames, TimeSpan frameDelay)
    {
        ArgumentNullException.ThrowIfNull(bgraFrames);
        ValidateSize(width, height, bgraFrames.Count);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        WriteHeader(writer, width, height);
        foreach (byte[] frame in bgraFrames) WriteFrame(writer, width, height, frame, frameDelay, default);
        writer.Write((byte)0x3B);
        writer.Flush();
        return output.ToArray();
    }

    public static async Task EncodeAsync(Stream output, int width, int height, int frameCount,
        IAsyncEnumerable<byte[]> bgraFrames, TimeSpan frameDelay, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(bgraFrames);
        ValidateSize(width, height, frameCount);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        WriteHeader(writer, width, height);
        int count = 0;
        await foreach (var frame in bgraFrames.WithCancellation(token))
        {
            if (++count > frameCount) throw new ArgumentException("GIF 帧数超出声明数量。", nameof(bgraFrames));
            await Task.Run(() => WriteFrame(writer, width, height, frame, frameDelay, token), token);
        }
        if (count != frameCount) throw new ArgumentException("GIF 帧数不足。", nameof(bgraFrames));
        writer.Write((byte)0x3B);
        await output.FlushAsync(token);
    }

    private static void ValidateSize(int width, int height, int frameCount)
    {
        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue || (long)width * height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "GIF 尺寸须不超过 4 百万像素。");
        if (frameCount is < 1 or > MaximumFrames)
            throw new ArgumentOutOfRangeException(nameof(frameCount), "GIF 帧数须为 1–120。");
    }

    private static void WriteHeader(BinaryWriter writer, int width, int height)
    {
        writer.Write("GIF89a"u8);
        WriteUInt16(writer, width);
        WriteUInt16(writer, height);
        writer.Write((byte)0xF7); // Global 256-color table, 8-bit color resolution.
        writer.Write((byte)0);
        writer.Write((byte)0);
        WritePalette(writer);
        WriteLoopExtension(writer);
    }

    private static void WriteFrame(BinaryWriter writer, int width, int height, byte[] frame, TimeSpan frameDelay, CancellationToken token)
    {
        int pixelCount = width * height;
        if (frame is null || frame.Length != pixelCount * 4)
            throw new ArgumentException("每帧必须包含完整的 BGRA 像素数据。", nameof(frame));
        int delay = (int)Math.Clamp(Math.Round(frameDelay.TotalMilliseconds / 10), 1, ushort.MaxValue);
        writer.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 });
        WriteUInt16(writer, delay);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0x2C);
        WriteUInt16(writer, 0);
        WriteUInt16(writer, 0);
        WriteUInt16(writer, width);
        WriteUInt16(writer, height);
        writer.Write((byte)0);

        byte[] compressed = Compress(Quantize(frame, pixelCount), token);
        writer.Write((byte)8);
        for (int offset = 0; offset < compressed.Length;)
        {
            int length = Math.Min(255, compressed.Length - offset);
            writer.Write((byte)length);
            writer.Write(compressed, offset, length);
            offset += length;
        }
        writer.Write((byte)0);
    }

    private static byte[] Quantize(byte[] bgra, int pixelCount)
    {
        var indices = new byte[pixelCount];
        for (int pixel = 0, source = 0; pixel < pixelCount; pixel++, source += 4)
            indices[pixel] = (byte)(bgra[source + 2] & 0xE0 | (bgra[source + 1] & 0xE0) >> 3 | bgra[source] >> 6);
        return indices;
    }

    private static void WritePalette(BinaryWriter writer)
    {
        for (int index = 0; index < 256; index++)
        {
            writer.Write((byte)(((index >> 5) & 7) * 255 / 7));
            writer.Write((byte)(((index >> 2) & 7) * 255 / 7));
            writer.Write((byte)((index & 3) * 255 / 3));
        }
    }

    private static void WriteLoopExtension(BinaryWriter writer)
    {
        writer.Write(new byte[] { 0x21, 0xFF, 0x0B });
        writer.Write("NETSCAPE2.0"u8);
        writer.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
    }

    private static byte[] Compress(byte[] indices, CancellationToken token)
    {
        using var output = new MemoryStream();
        var bits = new GifBitWriter(output);
        const int clearCode = 256;
        const int endCode = 257;
        var dictionary = new Dictionary<int, int>(4096);
        int codeSize = 9;
        int nextCode = 258;
        bits.Write(clearCode, codeSize);
        int prefix = indices[0];

        for (int index = 1; index < indices.Length; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            int symbol = indices[index];
            int key = (prefix << 8) | symbol;
            if (dictionary.TryGetValue(key, out int combined))
            {
                prefix = combined;
                continue;
            }

            bits.Write(prefix, codeSize);
            if (nextCode < 4096)
            {
                dictionary.Add(key, nextCode++);
                if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                bits.Write(clearCode, codeSize);
                dictionary.Clear();
                codeSize = 9;
                nextCode = 258;
            }
            prefix = symbol;
        }

        bits.Write(prefix, codeSize);
        bits.Write(endCode, codeSize);
        bits.Flush();
        return output.ToArray();
    }

    private static void WriteUInt16(BinaryWriter writer, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
        writer.Write(bytes);
    }

    private sealed class GifBitWriter(Stream output)
    {
        private uint _buffer;
        private int _bitCount;

        public void Write(int code, int size)
        {
            _buffer |= (uint)code << _bitCount;
            _bitCount += size;
            while (_bitCount >= 8)
            {
                output.WriteByte((byte)_buffer);
                _buffer >>= 8;
                _bitCount -= 8;
            }
        }

        public void Flush()
        {
            if (_bitCount > 0) output.WriteByte((byte)_buffer);
        }
    }
}
