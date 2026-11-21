using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace InkCanvas.Plugins.LegacyImport;

internal static class LegacyLayout
{
    private static readonly XNamespace Wpf = LegacyPage.Wpf;

    internal static double Number(XElement node, string name, double fallback)
    {
        var value = (string)node.Attribute(name);
        if (value is null) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            || !float.IsFinite((float)result)) throw new InvalidDataException($"布局数值无效：{name}");
        return result;
    }

    internal static double[] Transform(XElement element, double left, double top, double width, double height)
    {
        var transform = element.Elements().SingleOrDefault(e => e.Name.Namespace == Wpf
            && e.Name.LocalName.EndsWith(".RenderTransform", StringComparison.Ordinal))?.Elements().SingleOrDefault();
        Matrix matrix = transform is not null ? ReadTransform(transform, 0)
            : Matrix.Parse((string)element.Attribute("RenderTransform") ?? "Identity");
        if (matrix.IsIdentity) return null;
        var origin = Point.Parse((string)element.Attribute("RenderTransformOrigin") ?? "0,0");
        double x = left + origin.X * width, y = top + origin.Y * height;
        var canvas = Matrix.Identity;
        canvas.Translate(-x, -y);
        canvas.Append(matrix);
        canvas.Translate(x, y);
        var values = new[] { canvas.M11, canvas.M12, canvas.M21, canvas.M22, canvas.OffsetX, canvas.OffsetY };
        if (values.Any(v => !float.IsFinite((float)v))) throw new InvalidDataException("对象变换超出范围。");
        return values;
    }

    private static Matrix ReadTransform(XElement node, int depth)
    {
        if (depth > 16 || node.Name.Namespace != Wpf) throw new InvalidDataException("无法识别对象变换。");
        var m = Matrix.Identity;
        double N(string name, double fallback = 0) => Number(node, name, fallback);
        switch (node.Name.LocalName)
        {
            case "TransformGroup":
            case "TransformGroup.Children":
                foreach (var item in node.Elements()) m.Append(ReadTransform(item, depth + 1));
                break;
            case "MatrixTransform": return Matrix.Parse((string)node.Attribute("Matrix") ?? "Identity");
            case "ScaleTransform": m.ScaleAt(N("ScaleX", 1), N("ScaleY", 1), N("CenterX"), N("CenterY")); break;
            case "RotateTransform": m.RotateAt(N("Angle"), N("CenterX"), N("CenterY")); break;
            case "TranslateTransform": m.Translate(N("X"), N("Y")); break;
            case "SkewTransform":
                return new SkewTransform(N("AngleX"), N("AngleY"), N("CenterX"), N("CenterY")).Value;
            default: throw new InvalidDataException($"不支持对象变换 {node.Name.LocalName}。");
        }
        return m;
    }
}
