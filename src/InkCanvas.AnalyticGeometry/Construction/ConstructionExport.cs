using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security;
using System.Text;
using System.Text.Json;

namespace InkCanvas.AnalyticGeometry.Construction;

public static class ConstructionExport
{
    public const double Width = 1000, Height = 750;
    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string XY(Vector2 point) => N(point.X) + "," + N(point.Y);

    public static string Encode(ConstructionSnapshot state)
    {
        ConstructionDocument.Validate(state);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, true))
            JsonSerializer.Serialize(gzip, state);
        string encoded = "construction-v2:" + Convert.ToBase64String(buffer.ToArray());
        if (encoded.Length > 16000) throw new FormatException("构造数据超过画布可保存的大小，请使用构造文件保存，或减少对象数量。");
        return encoded;
    }

    public static ConstructionSnapshot Decode(string text)
    {
        if (!text.StartsWith("construction-v2:", StringComparison.Ordinal) || text.Length > 16000)
            throw new FormatException("构造数据格式无效。");
        using var buffer = new MemoryStream(Convert.FromBase64String(text[16..]));
        using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
        using var result = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = gzip.Read(chunk)) > 0)
        {
            if (result.Length + count > 8 * 1024 * 1024) throw new FormatException("构造解压数据过大。");
            result.Write(chunk, 0, count);
        }
        var state = JsonSerializer.Deserialize<ConstructionSnapshot>(result.ToArray()) ?? throw new FormatException("构造为空。");
        ConstructionDocument.Validate(state);
        return state;
    }

    public static string Svg(ConstructionSnapshot state, ConstructionEvaluation evaluation)
    {
        var projection = new GeometryProjection(state, Width, Height);
        var text = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{Height}\" viewBox=\"0 0 {Width} {Height}\"><defs><clipPath id=\"view\"><rect width=\"1000\" height=\"750\"/></clipPath></defs><g clip-path=\"url(#view)\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
        foreach (var item in state.Objects.Where(item => item.Visible))
        {
            if (!evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            string color = "#" + (item.Color & 0xFFFFFF).ToString("X6");
            var fill = new StringBuilder();
            foreach (var face in value.Faces)
            {
                var a = projection.Project(face.A); var b = projection.Project(face.B); var c = projection.Project(face.C);
                if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) (b, c) = (c, b);
                fill.Append($"M{XY(a)}L{XY(b)}L{XY(c)}Z");
            }
            if (fill.Length > 0) text.Append($"<path d=\"{fill}\" fill=\"{color}\" fill-rule=\"nonzero\" fill-opacity=\"{N(item.Opacity)}\"/>");
            else if (value.Kind == GeometryKind.Region)
                foreach (var path in projection.Paths(value))
                    text.Append($"<polygon points=\"{string.Join(' ', path.Select(XY))}\" fill=\"{color}\" fill-opacity=\"{N(item.Opacity)}\"/>");
            foreach (var path in projection.Paths(value))
                text.Append($"<polyline points=\"{string.Join(' ', path.Select(XY))}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{N(item.LineWidth)}\"/>");
            foreach (var point in value.Points)
            {
                var p = projection.Project(point);
                text.Append($"<circle cx=\"{N(p.X)}\" cy=\"{N(p.Y)}\" r=\"4\" fill=\"{color}\"/>");
            }
            if (item.ShowLabel && value.Points.Count > 0)
            {
                var p = projection.Project(value.Points[0]);
                text.Append($"<text x=\"{N(p.X + 8)}\" y=\"{N(p.Y - 8)}\" font-family=\"Segoe UI\" font-size=\"14\" fill=\"{color}\">{SecurityElement.Escape(item.Label)}</text>");
            }
        }
        return text.Append("</g></svg>").ToString();
    }

    public static string Stl(ConstructionSnapshot state, ConstructionEvaluation evaluation)
    {
        var text = new StringBuilder("solid InkCanvas\n");
        int facets = 0;
        foreach (var item in state.Objects.Where(item => item.Visible))
        {
            if (!evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            foreach (var face in value.Faces)
            {
                var cross = (face.B - face.A).Cross(face.C - face.A);
                if (cross.Length < 1e-12) continue;
                var normal = cross.Unit();
                text.Append($"facet normal {XYZ(normal)}\nouter loop\nvertex {XYZ(face.A)}\nvertex {XYZ(face.B)}\nvertex {XYZ(face.C)}\nendloop\nendfacet\n");
                facets++;
            }
        }
        if (facets == 0) throw new FormatException("当前构造没有可导出的三维面。");
        return text.Append("endsolid InkCanvas\n").ToString();
    }

    private static string XYZ(GeoVector p) => N(p.X) + " " + N(p.Y) + " " + N(p.Z);

    public static string Tikz(ConstructionSnapshot state, ConstructionEvaluation evaluation)
    {
        var projection = new GeometryProjection(state, Width, Height);
        var text = new StringBuilder("\\begin{tikzpicture}[x=0.01cm,y=-0.01cm,line cap=round,line join=round]\n\\clip (0,0) rectangle (1000,750);\n");
        foreach (var item in state.Objects.Where(item => item.Visible))
        {
            if (!evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            string color = "c" + item.Id.ToString("N");
            text.Append($"\\definecolor{{{color}}}{{HTML}}{{{item.Color & 0xFFFFFF:X6}}}\n");
            foreach (var path in projection.Paths(value))
                text.Append($"\\draw[color={color},line width={N(item.LineWidth * .6)}pt] {string.Join(" -- ", path.Select(point => "(" + XY(point) + ")"))};\n");
        }
        return text.Append("\\end{tikzpicture}\n").ToString();
    }
}
