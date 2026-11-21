using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Ink;
using System.Xml;
using System.Xml.Linq;

namespace InkCanvas.Plugins.LegacyImport;

internal sealed class LegacyPage
{
    internal static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const int MiB = 1024 * 1024;
    private readonly List<object> _strokes = new(), _images = new(), _media = new();
    private long _nextLayer, _mediaBytes;

    internal static LegacyPage Read(string path)
    {
        using var file = File.OpenRead(path);
        var page = new LegacyPage();
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".isf":
            case ".icstk":
                if (file.Length > 16 * MiB) throw new InvalidDataException("墨迹数据超过 16 MiB。");
                page.ReadStrokes(file);
                break;
            case ".icart":
                if (file.Length > 128 * MiB) throw new InvalidDataException("白板包超过 128 MiB。");
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read)) page.ReadArchive(archive);
                break;
            default: throw new InvalidDataException("不支持此文件扩展名。");
        }
        return page;
    }

    internal void Write(Stream output) => JsonSerializer.Serialize(output, new
    {
        Format = "inkcanvas-board", Version = 1,
        Pages = new[] { new { Strokes = _strokes, Images = _images, Media = _media } }
    });

    private void ReadStrokes(Stream input)
    {
        var strokes = new StrokeCollection(input);
        int points = 0;
        foreach (var stroke in strokes)
        {
            if ((points += stroke.StylusPoints.Count) > 500_000 || _strokes.Count >= 32_768)
                throw new InvalidDataException("旧版笔迹超出导入上限。");
            var a = stroke.DrawingAttributes;
            var c = a.Color;
            _strokes.Add(new
            {
                Color = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B,
                Width = a.Width, Highlighter = a.IsHighlighter, HighlighterAlpha = (byte)(c.A == 255 ? 128 : c.A),
                LayerOrder = _nextLayer++,
                Points = stroke.StylusPoints.Select(p => new[] { p.X, p.Y, p.PressureFactor,
                    a.Width * (a.IgnorePressure ? 1 : .25 + 1.5 * p.PressureFactor), 0d }).ToArray()
            });
        }
    }

    private void ReadArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 2048) throw new InvalidDataException("白板包条目过多。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
            if (!entries.TryAdd(entry.FullName.Replace('\\', '/'), entry))
                throw new InvalidDataException("白板包内有重名条目。");
        bool hasInk = entries.TryGetValue("strokes.icstk", out var ink);
        bool hasLayout = entries.TryGetValue("elements.xaml", out var layout);
        if (!hasInk && !hasLayout) throw new InvalidDataException("白板包缺少墨迹和布局。");
        if (hasLayout)
        {
            using var stream = new MemoryStream(ReadEntry(layout, 32 * MiB));
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * MiB
            });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != Wpf + "InkCanvas") throw new InvalidDataException("无法识别旧版画布布局。");
            foreach (var child in root.Elements())
            {
                if (child.Name == Wpf + "InkCanvas.Children")
                    foreach (var element in child.Elements()) ReadElement(element, entries);
                else if (!child.Name.LocalName.Contains('.')) ReadElement(child, entries);
            }
        }
        if (hasInk)
        {
            using var stream = new MemoryStream(ReadEntry(ink, 16 * MiB));
            ReadStrokes(stream);
        }
    }

    private void ReadElement(XElement element, Dictionary<string, ZipArchiveEntry> entries)
    {
        bool video = element.Name == Wpf + "MediaElement";
        if (!video && element.Name != Wpf + "Image")
            throw new InvalidDataException($"不支持旧版画布对象 {element.Name.LocalName}。");
        string source = (string)element.Attribute("Source")
            ?? (string)element.Element(Wpf + "Image.Source")?.Element(Wpf + "BitmapImage")?.Attribute("UriSource");
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("旧对象缺少可读取的媒体来源。");
        // Only match the leaf name inside the archive, even when WPF recorded an absolute file URI.
        string name = Path.GetFileName(Uri.UnescapeDataString(source).Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || !entries.TryGetValue("File Dependency/" + name, out var data))
            throw new InvalidDataException($"缺少随存档打包的媒体文件：{name}");
        if (data.Length <= 0 || data.Length > 96 * MiB - _mediaBytes)
            throw new InvalidDataException("媒体为空或总大小超过 96 MiB。");
        _mediaBytes += data.Length;
        string mime = (video, Path.GetExtension(name).ToLowerInvariant()) switch
        {
            (true, ".mp4") => "video/mp4", (true, ".avi") => "video/x-msvideo", (true, ".wmv") => "video/x-ms-wmv",
            (false, ".jpg" or ".jpeg") => "image/jpeg", (false, ".png") => "image/png", (false, ".bmp") => "image/bmp",
            _ => throw new InvalidDataException($"不支持旧版媒体格式：{name}")
        };
        double width = LegacyLayout.Number(element, "Width", 0), height = LegacyLayout.Number(element, "Height", 0);
        if (width <= 0 || height <= 0) throw new InvalidDataException($"对象缺少有效尺寸：{name}");
        double left = LegacyLayout.Number(element, "InkCanvas.Left", 0), top = LegacyLayout.Number(element, "InkCanvas.Top", 0);
        var asset = new
        {
            Name = name, ContentType = mime, Data = ReadEntry(data, 96 * MiB), LayerOrder = _nextLayer++,
            Width = width, Height = height, Left = left, Top = top,
            Opacity = LegacyLayout.Number(element, "Opacity", 1),
            Transform = LegacyLayout.Transform(element, left, top, width, height)
        };
        (video ? _media : _images).Add(asset);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit) throw new InvalidDataException($"包内文件过大：{entry.Name}");
        using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("白板包条目长度有误。");
        return bytes;
    }
}
