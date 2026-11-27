using System.Buffers.Binary;
using System.IO.Compression;
using InkCanvas.Core.Ink;

namespace InkCanvas.Pdf;

/// <summary>Lossless, bounded binary ink state stored inside each PDF annotation.</summary>
public static class PdfInkCodec
{
    private const int LegacyMagic = 0x314B4349;
    private const int ShapeMagic = 0x324B4349;
    private const int PressureMagic = 0x334B4349;
    private const int Magic = 0x344B4349;
    public const int MaximumPoints = 2_000_000;
    public static byte[] Encode(IReadOnlyList<InkStroke> strokes)
    {
        if (strokes is null || strokes.Count > 100_000 || strokes.Any(stroke => stroke is null || stroke.Count == 0)
            || strokes.Sum(stroke => (long)stroke.Count) > MaximumPoints) throw new InvalidDataException("Invalid PDF ink count.");
        using var output = new MemoryStream();
        int sourceBytes = 0;
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, true))
        using (var writer = new BinaryWriter(zlib))
        {
            writer.Write(Magic); writer.Write(strokes.Count);
            foreach (var stroke in strokes)
            {
                var a = stroke.Attributes;
                writer.Write(stroke.Id.ToByteArray()); writer.Write(stroke.LayerOrder.HasValue);
                if (stroke.LayerOrder.HasValue) writer.Write(stroke.LayerOrder.Value);
                writer.Write(stroke.IsRecognizedShape); writer.Write(a.Color.PackedArgb); writer.Write(a.BaseWidth);
                writer.Write(a.IsHighlighter); writer.Write(a.HighlighterAlpha); writer.Write((int)a.TaperKind);
                writer.Write((int)a.Nib); writer.Write(a.NibRotation);
                var wash = a.InkWash.Normalize();
                writer.Write(wash.Dryness); writer.Write(wash.Angle); writer.Write(wash.Flatness); writer.Write(a.InkWashSeed);
                var frame = stroke.ExportObjectFrame(); writer.Write(frame is not null);
                if (frame is not null) foreach (float value in frame) writer.Write(value);
                writer.Write(stroke.ShapeSource is not null);
                if (stroke.ShapeSource is { } source)
                {
                    writer.Write(source.GroupId.ToByteArray()); WriteText(writer, source.Provider, 128, ref sourceBytes); WriteText(writer, source.Definition, 16384, ref sourceBytes);
                }
                writer.Write(stroke.Count);
                foreach (var point in stroke.Points)
                {
                    writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Pressure); writer.Write(point.Width); writer.Write(point.TimeMs);
                    writer.Write(point.HasPressure); writer.Write(point.TiltX); writer.Write(point.TiltY);
                }
            }
        }
        if (output.Length > 48 * 1024 * 1024) throw new InvalidDataException("PDF ink metadata is too large.");
        return output.ToArray();
    }
    public static IReadOnlyList<InkStroke> Decode(byte[] compressed, CancellationToken token = default)
    {
        if (compressed is null || compressed.Length > 48 * 1024 * 1024) throw new InvalidDataException("PDF ink metadata is too large.");
        using var input = new MemoryStream(compressed, false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(zlib);
        int magic = reader.ReadInt32();
        if (magic != Magic && magic != PressureMagic && magic != ShapeMagic && magic != LegacyMagic) throw new InvalidDataException("Unsupported PDF ink metadata.");
        int count = reader.ReadInt32();
        if (count < 0 || count > 100_000) throw new InvalidDataException("Invalid PDF ink count.");
        var result = new List<InkStroke>(count); int total = 0, sourceBytes = 0;
        bool tabletData = magic is Magic or PressureMagic;
        Span<byte> pointBuffer = stackalloc byte[29 * 256];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var idBytes = reader.ReadBytes(16); if (idBytes.Length != 16) throw new EndOfStreamException();
            var id = new Guid(idBytes); long? layer = reader.ReadBoolean() ? reader.ReadInt64() : null;
            bool shape = reader.ReadBoolean();
            var attributes = new StrokeAttributes { Color = InkColor.FromPackedArgb(reader.ReadUInt32()), BaseWidth = reader.ReadSingle(),
                IsHighlighter = reader.ReadBoolean(), HighlighterAlpha = reader.ReadByte(), TaperKind = (TaperKind)reader.ReadInt32(),
                Nib = (PenNib)reader.ReadInt32(), NibRotation = reader.ReadSingle() };
            if (magic == Magic)
            {
                var wash = new InkWashBrushOptions(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (!float.IsFinite(wash.Dryness) || !float.IsFinite(wash.Angle) || !float.IsFinite(wash.Flatness))
                    throw new InvalidDataException("Invalid water-ink brush metadata.");
                attributes.InkWash = wash.Normalize();
                attributes.InkWashSeed = reader.ReadInt32();
            }
            if (!float.IsFinite(attributes.BaseWidth) || attributes.BaseWidth is <= 0 or > 1_000_000 || !float.IsFinite(attributes.NibRotation)
                || !Enum.IsDefined(attributes.TaperKind) || !Enum.IsDefined(attributes.Nib)) throw new InvalidDataException("Invalid PDF brush metadata.");
            float[] frame = null;
            if (reader.ReadBoolean())
            {
                frame = new float[10];
                for (int j = 0; j < frame.Length; j++)
                { frame[j] = reader.ReadSingle(); if (!float.IsFinite(frame[j])) throw new InvalidDataException("Invalid PDF object frame."); }
            }
            ShapeSource source = null;
            if (magic != LegacyMagic && reader.ReadBoolean())
            {
                var group = reader.ReadBytes(16); if (group.Length != 16) throw new EndOfStreamException();
                source = new ShapeSource(new Guid(group), ReadText(reader, 128, ref sourceBytes), ReadText(reader, 16384, ref sourceBytes));
            }
            int pointCount = reader.ReadInt32();
            if (pointCount <= 0 || pointCount > MaximumPoints || (total += pointCount) > MaximumPoints) throw new InvalidDataException("PDF ink point limit exceeded.");
            var points = new InkPoint[pointCount];
            ReadPoints(zlib, points, pointBuffer, tabletData, token);
            var stroke = new InkStroke(attributes) { Id = id, LayerOrder = layer, IsRecognizedShape = shape, ShapeSource = source };
            stroke.RestorePoints(points); stroke.ImportObjectFrame(frame); stroke.Complete(); result.Add(stroke);
        }
        if (zlib.ReadByte() != -1) throw new InvalidDataException("Unexpected PDF ink data.");
        ShapeSource.ShareDefinitions(result);
        return result;
    }

    private static void ReadPoints(Stream stream, Span<InkPoint> points, Span<byte> buffer, bool tabletData, CancellationToken token)
    {
        int pointSize = tabletData ? 29 : 20;
        while (!points.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(256, points.Length);
            var bytes = buffer[..(count * pointSize)];
            stream.ReadExactly(bytes);
            for (int index = 0; index < count; index++, bytes = bytes[pointSize..])
            {
                float x = BinaryPrimitives.ReadSingleLittleEndian(bytes);
                float y = BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]);
                float pressure = BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]);
                float width = BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]);
                float time = BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(pressure) || !float.IsFinite(width) || !float.IsFinite(time)
                    || Math.Abs(x) > 10_000_000 || Math.Abs(y) > 10_000_000 || width is < 0 or > 1_000_000) throw new InvalidDataException("Invalid PDF ink point.");
                var point = new InkPoint(x, y, pressure, width, time);
                if (tabletData)
                {
                    point.HasPressure = bytes[20] != 0;
                    point.TiltX = BinaryPrimitives.ReadSingleLittleEndian(bytes[21..]);
                    point.TiltY = BinaryPrimitives.ReadSingleLittleEndian(bytes[25..]);
                    if (!float.IsFinite(point.TiltX) || !float.IsFinite(point.TiltY)
                        || Math.Abs(point.TiltX) > 180 || Math.Abs(point.TiltY) > 180)
                        throw new InvalidDataException("Invalid pen tilt.");
                }
                points[index] = point;
            }
            points = points[count..];
        }
    }
    private static void WriteText(BinaryWriter writer, string value, int limit, ref int total)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
        if (bytes.Length > limit || (total += bytes.Length) > 16 * 1024 * 1024) throw new InvalidDataException("PDF shape definition is too large.");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string ReadText(BinaryReader reader, int limit, ref int total)
    {
        int length = reader.ReadInt32(); if (length < 0 || length > limit || (total += length) > 16 * 1024 * 1024) throw new InvalidDataException("Invalid PDF shape definition.");
        byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
