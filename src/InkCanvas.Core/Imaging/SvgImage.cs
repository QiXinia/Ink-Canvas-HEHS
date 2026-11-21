using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace InkCanvas.Core.Imaging;

public sealed record SvgImage(string Xml, Vector2 Size)
{
    public const string ContentType = "image/svg+xml";

    public static SvgImage Read(Stream source, bool compressed = false)
    {
        using var expanded = compressed ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : null;
        using var reader = XmlReader.Create(expanded ?? source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 32 * 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg"
            || root.Name.NamespaceName is not ("" or "http://www.w3.org/2000/svg"))
            throw new InvalidDataException("文件不包含有效的 SVG 图形。");
        if (root.Name.Namespace == XNamespace.None)
        {
            XNamespace svgNamespace = "http://www.w3.org/2000/svg";
            foreach (var element in root.DescendantsAndSelf().Where(element => element.Name.Namespace == XNamespace.None))
                element.Name = svgNamespace + element.Name.LocalName;
        }

        var viewBox = ((string)root.Attribute("viewBox") ?? "").Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        float viewWidth = 0, viewHeight = 0;
        if (viewBox.Length == 4 && viewBox.All(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && float.IsFinite(parsed)))
        {
            viewWidth = float.Parse(viewBox[2], CultureInfo.InvariantCulture);
            viewHeight = float.Parse(viewBox[3], CultureInfo.InvariantCulture);
        }
        float width = Length((string)root.Attribute("width"));
        float height = Length((string)root.Attribute("height"));
        if (viewWidth > 0 && viewHeight > 0)
        {
            if (width <= 0 && height > 0) width = height * viewWidth / viewHeight;
            if (height <= 0 && width > 0) height = width * viewHeight / viewWidth;
            if (width <= 0) width = viewWidth;
            if (height <= 0) height = viewHeight;
        }
        if (width <= 0) width = 300;
        if (height <= 0) height = 150;
        if (!float.IsFinite(width) || !float.IsFinite(height)) throw new InvalidDataException("SVG 图形的尺寸无效。");
        root.SetAttributeValue("width", width.ToString("R", CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", height.ToString("R", CultureInfo.InvariantCulture));
        return new SvgImage(document.ToString(SaveOptions.DisableFormatting), new Vector2(width, height));
    }

    private static float Length(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim().ToLowerInvariant();
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float pixels))
            return float.IsFinite(pixels) && pixels > 0 ? pixels : 0;
        if (value.Length < 3) return 0;
        string unit = value[^2..];
        if (!float.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
            || !float.IsFinite(number) || number <= 0) return 0;
        return unit switch
        {
            "px" => number,
            "pt" => number * 96 / 72,
            "pc" => number * 16,
            "in" => number * 96,
            "cm" => number * 96 / 2.54f,
            "mm" => number * 96 / 25.4f,
            _ => 0,
        };
    }
}
