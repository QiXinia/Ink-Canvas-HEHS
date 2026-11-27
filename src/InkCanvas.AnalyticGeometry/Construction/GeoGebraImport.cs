using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

public sealed record GeoGebraImportResult(ConstructionSnapshot Snapshot, IReadOnlyList<string> Warnings);

/// <summary>Imports the public XML construction stored inside a GeoGebra .ggb archive.</summary>
public static partial class GeoGebraImport
{
    private const long MaximumArchiveBytes = 64L * 1024 * 1024;
    private const long MaximumXmlBytes = 32L * 1024 * 1024;
    private const int MaximumObjects = 10_000;

    private static readonly HashSet<string> SupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Point", "PointIn", "PointOnObject", "IsInRegion", "Midpoint", "Center", "Line", "Segment", "Ray", "Vector", "VectorFromPoint",
        "UnitVector", "PerpendicularVector", "UnitPerpendicularVector", "Dot", "Cross",
        "Polygon", "Polyline", "RegularPolygon", "Centroid", "Circle", "Radius", "Compass", "Circumcircle", "Semicircle", "CellRange",
        "Arc", "Sector", "Circumarc", "Circumsector", "Intersect", "IntersectPath", "Element", "Tangent", "IsTangent", "Polar", "Diameter", "FitLine",
        "Parallel", "ParallelLine", "Perpendicular", "PerpendicularLine", "OrthogonalLine", "PerpendicularBisector",
        "AngleBisector", "Translate", "Rotate", "Dilate", "Enlarge", "Homothety", "Reflect", "CommonTangents", "Distance", "Length",
        "Area", "Circumference", "Perimeter", "Slope", "Angle", "Relation", "Sum", "Mean", "Function", "Curve", "PolarCurve", "ParametricDerivative", "Derivative", "Diff", "Integral",
        "Integrate", "Limit", "Root", "Extremum", "InflectionPoint", "Ellipse", "Hyperbola", "Parabola", "Conic",
        "Focus", "Directrix", "Eccentricity", "Asymptote", "Vertex", "If", "Sequence", "Zip", "Locus", "Envelope",
        "Plane", "ParallelPlane", "PerpendicularPlane", "Surface", "Sphere", "Cylinder", "Cone", "Prism", "Pyramid", "Cube", "Tetrahedron", "Octahedron",
        "Icosahedron", "Dodecahedron", "Volume", "SurfaceArea", "Net",
        "First", "Last", "Take", "Reverse", "Append", "Join", "Flatten", "Sort", "Unique",
        "Variance", "SD", "SampleVariance", "SampleSD", "Median", "Product", "RigidPolygon", "VectorPolygon",
        "Curvature", "OsculatingCircle", "Normal", "IntegralBetween", "SlopeField",
        "Checkbox", "Button", "InputBox", "Dropdown", "Text", "Name", "Simplify", "Expand", "Factor",
        "Prove", "ProveDetails", "AreParallel", "ArePerpendicular", "AreCollinear", "AreCoplanar", "AreConcyclic", "AreConcurrent", "AreCongruent", "AreEqual",
        "Factorize", "Solve", "Solutions", "Substitute", "Determinant", "Inverse", "Transpose", "Rank", "Real", "Imaginary"
    };

    public static GeoGebraImportResult Load(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("找不到 GeoGebra 文件。", path);
        if (file.Length <= 0 || file.Length > MaximumArchiveBytes)
            throw new FormatException("GeoGebra 文件大小须为 1 byte 至 64 MiB。 ");

        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("geogebra.xml") ?? archive.GetEntry("construction.xml");
        if (entry is null) throw new FormatException("归档内没有 geogebra.xml 构造文件。");
        if (entry.Length <= 0 || entry.Length > MaximumXmlBytes)
            throw new FormatException("GeoGebra 构造 XML 超过 32 MiB。 ");

        cancellationToken.ThrowIfCancellationRequested();
        var xml = ReadXml(entry);
        var construction = xml.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "construction")
            ?? throw new FormatException("GeoGebra XML 不包含 construction 节点。");
        var warnings = new List<string>();
        var expressions = construction.Elements().Where(element => element.Name.LocalName == "expression")
            .Select(element => (Label: Attribute(element, "label"), Definition: Attribute(element, "exp")))
            .Where(item => item.Label.Length > 0 && item.Definition.Length > 0)
            .GroupBy(item => item.Label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Definition, StringComparer.Ordinal);
        var elements = construction.Elements().Where(element => element.Name.LocalName == "element")
            .Where(element => Attribute(element, "label").Length > 0)
            .GroupBy(element => Attribute(element, "label"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var (tools, toolOutputs) = ReadMacroTools(archive, xml.Root, warnings, cancellationToken);
        var commands = ReadCommands(construction, elements, toolOutputs);

        var labels = new List<string>();
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in construction.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Name.LocalName is "expression" or "element") AddLabel(Attribute(node, "label"));
            else if (node.Name.LocalName == "command")
                foreach (var output in node.Elements().FirstOrDefault(element => element.Name.LocalName == "output")?.Attributes() ?? Enumerable.Empty<XAttribute>())
                    AddLabel(output.Value);
        }
        foreach (string label in expressions.Keys.Concat(commands.Keys).Concat(elements.Keys)) AddLabel(label);
        if (labels.Count > MaximumObjects) throw new FormatException("单个 GeoGebra 文件最多导入 10000 个对象。 ");

        var labelMap = MakeLabelMap(labels);
        var renamedLabels = labelMap.Where(pair => pair.Key != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Regex renamedLabelPattern = renamedLabels.Count == 0 ? null : new Regex(
            @"""(?:\\.|[^""\\])*""|(?<![\p{L}\d_])(?:" + string.Join("|", renamedLabels.Keys
                .OrderByDescending(label => label.Length).Select(Regex.Escape)) + @")(?![\p{L}\d_])");
        string RewriteLabels(string expression) => renamedLabelPattern is null ? expression
            : renamedLabelPattern.Replace(expression, match => renamedLabels.GetValueOrDefault(match.Value, match.Value));
        var pointLabels = elements.Where(pair => Attribute(pair.Value, "type") is "point" or "point3d")
            .Select(pair => labelMap[pair.Key]).ToHashSet(StringComparer.Ordinal);
        var objects = new List<ConstructionObject>(labels.Count);
        foreach (string sourceLabel in labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string label = labelMap[sourceLabel];
            var style = elements.GetValueOrDefault(sourceLabel);
            string type = style is null ? "" : Attribute(style, "type");
            string definition;
            if (expressions.TryGetValue(sourceLabel, out var expression) && !commands.ContainsKey(sourceLabel))
            {
                definition = RewriteLabels(expression);
                if (type is "function" or "functionNVar" or "")
                    definition = RewriteFunctionDefinition(label, definition);
                if (!LooksLikeDefinition(definition)) definition = ElementFallback(sourceLabel, elements, warnings);
                else
                {
                    // The GeoGebra argument order is LambertW(value, branch), including in lowercase XML expressions.
                    definition = Regex.Replace(definition, @"\blambertw(?=\s*\()", "LambertW", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    definition = RewriteSquaredDistances(definition, pointLabels);
                    if (pointLabels.Contains(label))
                        definition = RewritePointExpression(definition, pointLabels, type == "point3d");
                    else if (type == "text")
                        definition = ReadTextDefinition(definition, style, RewriteLabels);
                    else if (type == "conic" && definition.Contains('='))
                        definition = $"ConicEquation({definition})";
                    else if (type == "line" && definition.Contains('='))
                        definition = $"LineEquation({definition})";
                    else if (type is "vector" or "vector3d")
                        definition = $"Vector({RewritePointExpression(definition, pointLabels, type == "vector3d")})";
                }
            }
            else if (commands.TryGetValue(sourceLabel, out var command))
            {
                definition = RewriteLabels(command.Definition);
                if (!command.Supported)
                {
                    string fallback = ElementFallback(sourceLabel, elements, new List<string>());
                    if (!fallback.StartsWith("GeoGebraUnsupported(", StringComparison.Ordinal))
                    {
                        warnings.Add($"{sourceLabel}：命令 {command.Name} 未映射，已从 XML 坐标静态导入。");
                    }
                    else
                    {
                        warnings.Add($"{sourceLabel}：未映射 GeoGebra 命令 {command.Name}。");
                    }
                    definition = fallback;
                }
            }
            else definition = ElementFallback(sourceLabel, elements, warnings);

            if (definition.Length == 0) definition = "GeoGebraUnsupported(\"" + sourceLabel.Replace("\"", "") + "\")";
            objects.Add(ReadStyle(label, definition, style, RewriteLabels, warnings));
        }

        var view = ReadView(xml.Root);
        var listeners = new List<ConstructionListener>();
        for (int index = 0; index < objects.Count; index++)
        {
            if (!elements.TryGetValue(labels[index], out var element)) continue;
            foreach (var listener in element.Elements().Where(child => child.Name.LocalName == "listener"))
            {
                string eventName = Attribute(listener, "type") switch
                {
                    "objectClick" => "OnClick",
                    "objectUpdate" => "OnUpdate",
                    _ => null,
                };
                string callback = Attribute(listener, "val");
                if (eventName is null || !MathSyntax.Identifier(callback) || callback.Length > 4096)
                    warnings.Add($"{labels[index]}：Applet 监听器格式暂未映射。");
                else if (listeners.Count < ConstructionDocument.MaximumAppletListeners)
                    listeners.Add(new(eventName, objects[index].Id, callback));
                else
                    warnings.Add("Applet 监听器超过可导入的数量。");
            }
        }
        var snapshot = view with { Objects = objects, Tools = tools, AppletScript = ReadAppletScript(archive, warnings), AppletListeners = listeners };
        ConstructionDocument.Validate(snapshot);
        if (objects.Any(item => item.Definition.Contains(",PathParameter(", StringComparison.Ordinal)
            || ConstructionDocument.IsRegionPoint(item)))
        {
            var evaluated = new ConstructionKernel().Evaluate(snapshot, cancellationToken);
            for (int index = 0; index < objects.Count; index++)
            {
                var item = objects[index];
                if (item.Definition.Contains(",PathParameter(", StringComparison.Ordinal)
                    && evaluated.Values[item.Label].PathParameter is double parameter
                    && ConstructionDocument.AttachedPath(item, evaluated.Values[item.Label]) is string target)
                    objects[index] = item with { Definition = $"PointOnObject({target},{N(parameter)})" };
                if (!ConstructionDocument.IsRegionPoint(item) || !MathSyntax.Call(item.Definition, out _, out var arguments)
                    || arguments.Length != 2) continue;
                var point = evaluated.Values[item.Label];
                if (point.RegionPosition is { } region)
                    objects[index] = item with { Definition = GeometryPath.RegionDefinition(arguments[0], region.Face, region.U, region.V) };
                else if (elements.TryGetValue(labels[index], out var element) && TryStoredPoint(element, out string stored))
                {
                    warnings.Add($"{labels[index]}：区域类型尚未映射，已从 XML 坐标静态导入。");
                    objects[index] = item with { Definition = stored };
                }
            }
        }
        return new(snapshot, warnings.Distinct(StringComparer.Ordinal).ToArray());

        void AddLabel(string value)
        {
            if (value.Length > 0 && seenLabels.Add(value)) labels.Add(value);
        }
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlBytes,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private sealed record ImportedCommand(string Name, string Definition, bool Supported);

    private static string ReadAppletScript(ZipArchive archive, ICollection<string> warnings)
    {
        var entry = archive.GetEntry("geogebra_javascript.js");
        if (entry is null || entry.Length == 0) return "";
        int limit = ConstructionDocument.MaximumAppletScriptLength;
        if (entry.Length <= limit * 4L)
        {
            using var reader = new StreamReader(entry.Open());
            var source = new char[limit + 1];
            int length = reader.ReadBlock(source, 0, source.Length);
            if (length <= limit)
            {
                string script = new string(source, 0, length);
                // Unmodified GeoGebra files ship with an empty ggbOnInit handler.
                return Regex.IsMatch(script, @"^\s*(?:function\s+ggbOnInit\s*\(\s*\)\s*\{\s*\})?\s*$") ? "" : script;
            }
        }
        warnings.Add("全局 JavaScript 超过工作台支持的长度。");
        return "";
    }

    private static Dictionary<string, ImportedCommand> ReadCommands(XElement construction, IReadOnlyDictionary<string, XElement> elements,
        IReadOnlyDictionary<string, int> toolOutputs = null)
    {
        var result = new Dictionary<string, ImportedCommand>(StringComparer.Ordinal);
        foreach (var command in construction.Elements().Where(element => element.Name.LocalName == "command"))
        {
            string name = Attribute(command, "name");
            var inputs = ReadIndexedAttributes(command, "input");
            var outputs = ReadIndexedAttributes(command, "output");
            if (name.Length == 0 || outputs.Length == 0) continue;
            if (toolOutputs?.TryGetValue(name, out int toolOutputCount) == true)
            {
                string toolCall = $"{name}({string.Join(',', inputs)})";
                for (int index = 0; index < outputs.Length; index++)
                {
                    string definition = toolOutputCount == 1 ? toolCall : $"GgbToolOutput({toolCall},{index + 1})";
                    result[outputs[index]] = new(name, definition, index < toolOutputCount);
                }
                continue;
            }

            string mapped = MapCommand(name);
            bool supported = SupportedCommands.Contains(name) || SupportedCommands.Contains(mapped);
            if (mapped.Equals("Button", StringComparison.OrdinalIgnoreCase) && inputs.Length == 0)
                inputs = [ButtonCaption(elements.GetValueOrDefault(outputs[0]), outputs[0])];
            if (mapped.Equals("Checkbox", StringComparison.OrdinalIgnoreCase))
                inputs = [CheckboxValue(elements.GetValueOrDefault(outputs[0]))];
            if (mapped.Equals("Function", StringComparison.OrdinalIgnoreCase) && inputs.Length == 3)
                inputs = [inputs[0], "x", inputs[1], inputs[2]];
            if (mapped.Equals("Polygon", StringComparison.OrdinalIgnoreCase) && inputs.Length == 3
                && (outputs.Length > 4 || double.TryParse(inputs[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    || elements.TryGetValue(inputs[2], out var count) && Attribute(count, "type") == "numeric"))
                mapped = "RegularPolygon";
            if (GeometryEvaluator.FindRegularSolid(mapped) is { } regularSolid && inputs.Length > 0)
            {
                ReadRegularSolid(result, name, mapped, regularSolid, inputs, outputs, elements);
                continue;
            }
            if ((mapped.Equals("Derivative", StringComparison.OrdinalIgnoreCase) || mapped.Equals("Diff", StringComparison.OrdinalIgnoreCase))
                && inputs.Length is 1 or 2 && elements.TryGetValue(outputs[0], out var derivative)
                && Attribute(derivative, "type") is "curvecartesian" or "curvecartesian3d")
                mapped = "CurveDerivative";
            string call = mapped + "(" + string.Join(',', inputs) + ")";
            bool prism = mapped.Equals("Prism", StringComparison.OrdinalIgnoreCase);
            bool pyramid = mapped.Equals("Pyramid", StringComparison.OrdinalIgnoreCase);
            bool regularPolygon = mapped.Equals("RegularPolygon", StringComparison.OrdinalIgnoreCase);
            int pointOutput = 0, faceOutput = 0, edgeOutput = 0;
            int baseVertexCount = inputs.Length >= 4 ? inputs.Length - 1 : 0;
            bool topPointIsInput = baseVertexCount > 0 || inputs.Length == 2 &&
                elements.TryGetValue(inputs[1], out var top) && Attribute(top, "type") is "point" or "point3d";
            var namedEdges = pyramid && baseVertexCount > 0 ? ReadNamedPyramidEdges(inputs) : null;
            for (int index = 0; index < outputs.Length; index++)
            {
                string definition = call;
                bool mappedOutput = supported;
                if (index > 0 && (prism || pyramid || regularPolygon))
                {
                    string type = elements.TryGetValue(outputs[index], out var element) ? Attribute(element, "type") : "";
                    string parent = outputs[0];
                    definition = type switch
                    {
                        "point" or "point3d" when regularPolygon => $"PolygonVertex({parent},{++pointOutput + 2})",
                        "point" or "point3d" when prism => $"PrismTopVertex({parent},{++pointOutput + (topPointIsInput ? 1 : 0)})",
                        "point" or "point3d" when !topPointIsInput => $"SolidVertex({parent},-1)",
                        "polygon" or "polygon3d" when pyramid =>
                            $"SolidFace({parent},{(++faceOutput == baseVertexCount + 1 && baseVertexCount > 0 ? 1 : faceOutput + 1)})",
                        "polygon" or "polygon3d" when prism => $"SolidFace({parent},{++faceOutput + (baseVertexCount == 0 ? 1 : 0)})",
                        "segment" or "segment3d" when regularPolygon => $"PolygonSide({parent},{++edgeOutput})",
                        "segment" or "segment3d" => ReadSolidEdge(parent, outputs[index], ++edgeOutput, namedEdges),
                        _ => null,
                    };
                    mappedOutput &= definition is not null;
                    definition ??= call;
                }
                else if (mapped.Equals("Polygon", StringComparison.OrdinalIgnoreCase) && index > 0)
                    definition = $"PolygonSide({outputs[0]},{index})";
                else if (outputs.Length > 1 && !prism && !pyramid && !regularPolygon)
                    definition = $"ConstructionOutput({call},{index + 1})";
                if ((mapped.Equals("Point", StringComparison.OrdinalIgnoreCase) || mapped.Equals("PointOnObject", StringComparison.OrdinalIgnoreCase)
                    || mapped.Equals("PointIn", StringComparison.OrdinalIgnoreCase))
                    && inputs.Length == 1 && elements.TryGetValue(outputs[index], out var stored) && TryStoredPoint(stored, out string point))
                {
                    if (inputs[0] == "xOyPlane") definition = point;
                    else if (mapped.Equals("PointIn", StringComparison.OrdinalIgnoreCase))
                        definition = $"PointIn({inputs[0]},{point})";
                    else if (!elements.TryGetValue(inputs[0], out var source) || Attribute(source, "type") != "list")
                        definition = $"PointOnObject({inputs[0]},PathParameter({inputs[0]},{point}))";
                }
                result[outputs[index]] = new(name, definition, mappedOutput);
            }
        }
        return result;
    }

    private static void ReadRegularSolid(Dictionary<string, ImportedCommand> result, string name, string mapped,
        GeometryEvaluator.RegularSolidTemplate template, string[] inputs, string[] outputs, IReadOnlyDictionary<string, XElement> elements)
    {
        string parent = outputs[0];
        string TypeOf(string label) => elements.TryGetValue(label, out var element) ? Attribute(element, "type") : "";
        bool linkedBase = TypeOf(inputs[0]) is "polygon" or "polygon3d";
        int inputVertices = linkedBase ? template.Faces[0].Length
            : inputs.Length >= 3 && (TypeOf(inputs[2]) is "point" or "point3d" || inputs[2].StartsWith('(')) ? 3 : 2;
        string call = $"GgbRegularSolid(\"{mapped}\",{string.Join(',', inputs)})";
        result[parent] = new(name, call, true);
        var faces = linkedBase ? template.Faces.Skip(1) : template.Faces;
        var baseEdges = linkedBase ? GeometryEvaluator.SolidEdges([template.Faces[0]]).ToHashSet() : [];
        var edges = GeometryEvaluator.SolidEdges(faces).Where(edge => !baseEdges.Contains(edge)).ToArray();
        var vertexLabels = new string[template.Vertices.Length];
        if (!linkedBase && inputs.Length >= inputVertices) Array.Copy(inputs, vertexLabels, inputVertices);
        int vertexIndex = inputVertices;
        foreach (string output in outputs.Skip(1))
            if ((TypeOf(output) is "point" or "point3d") && vertexIndex < vertexLabels.Length)
                vertexLabels[vertexIndex++] = output;

        // Older files can serialize generated edges in the order their default names were assigned.
        var namedEdges = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < edges.Length; index++)
        {
            string first = vertexLabels[edges[index].First], second = vertexLabels[edges[index].Second];
            if (first is null || second is null) continue;
            AddEdge("edge" + first + second, index + 1);
            AddEdge("edge" + second + first, index + 1);
        }

        int point = inputVertices, face = linkedBase ? 1 : 0, edge = 0;
        foreach (string output in outputs.Skip(1))
        {
            string definition = TypeOf(output) switch
            {
                "point" or "point3d" when point < vertexLabels.Length => $"SolidVertex({parent},{++point})",
                "polygon" or "polygon3d" when face < template.Faces.Length => $"SolidFace({parent},{++face})",
                "segment" or "segment3d" when edge < edges.Length => $"SolidEdge({parent},{NamedEdgeIndex(output, ++edge)})",
                _ => null,
            };
            result[output] = new(name, definition ?? call, definition is not null);
        }

        void AddEdge(string label, int index)
        {
            if (!namedEdges.TryAdd(label, index) && namedEdges[label] != index) namedEdges[label] = 0;
        }

        int NamedEdgeIndex(string label, int fallback)
        {
            if (namedEdges.TryGetValue(label, out int index) && index > 0) return index;
            int separator = label.LastIndexOf('_');
            return separator >= 0 && int.TryParse(label.AsSpan(separator + 1), out _)
                && namedEdges.TryGetValue(label[..separator], out index) && index > 0 ? index : fallback;
        }
    }

    private static Dictionary<string, string> ReadNamedPyramidEdges(string[] inputs)
    {
        // Legacy GeoGebra pyramids may serialize edges in the order their labels were assigned.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < inputs.Length - 1; index++)
        {
            AddEdge(inputs[index], inputs[(index + 1) % (inputs.Length - 1)]);
            AddEdge(inputs[index], inputs[^1]);
        }
        return result;

        void AddEdge(string first, string second)
        {
            string segment = $"Segment({first},{second})";
            foreach (string name in new[] { "edge" + first + second, "edge" + second + first })
                if (!result.TryAdd(name, segment)) result[name] = null;
        }
    }

    private static string ReadSolidEdge(string parent, string label, int index, Dictionary<string, string> namedEdges)
    {
        if (namedEdges is not null)
        {
            if (namedEdges.TryGetValue(label, out string definition) && definition is not null) return definition;
            int separator = label.LastIndexOf('_');
            if (separator >= 0 && int.TryParse(label.AsSpan(separator + 1), out _)
                && namedEdges.TryGetValue(label[..separator], out definition) && definition is not null) return definition;
        }
        return $"SolidEdge({parent},{index})";
    }

    private static string[] ReadIndexedAttributes(XElement parent, string node)
        => parent.Elements().FirstOrDefault(element => element.Name.LocalName == node)?.Attributes()
            .Where(attribute => Index(attribute.Name.LocalName) < int.MaxValue)
            .OrderBy(attribute => Index(attribute.Name.LocalName)).Select(attribute => attribute.Value).ToArray() ?? [];

    private static int Index(string name)
        => int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? index : int.MaxValue;

    private static string MapCommand(string name) => name.ToLowerInvariant() switch
    {
        "orthogonalline" => "PerpendicularLine",
        "orthogonalvector" => "PerpendicularVector",
        "unitorthogonalvector" => "UnitPerpendicularVector",
        "linebisector" => "PerpendicularBisector",
        "angularbisector" => "AngleBisector",
        "enlarge" => "Dilate",
        "intersection" or "intersections" or "intersectconic" => "Intersect",
        "circlearc" or "circulararc" => "Arc",
        "circularsector" => "Sector",
        "circumcirculararc" => "Circumarc",
        "circumcircularsector" => "Circumsector",
        "matrixrank" => "Rank",
        "perpendicularline" => "PerpendicularLine",
        "curvecartesian" => "Curve",
        "stdevp" => "SD",
        "stdev" => "SampleSD",
        _ => name,
    };

    private static Dictionary<string, string> MakeLabelMap(IEnumerable<string> labels)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string label in labels)
        {
            string normalized = MathSyntax.Normalize(label);
            string safe = MathSyntax.Identifier(normalized) ? normalized : Regex.Replace(normalized, @"[^\p{L}\d_]", "_");
            if (safe.Length == 0 || char.IsDigit(safe[0])) safe = "g_" + safe;
            if (safe.Length > 40) safe = safe[..40];
            string candidate = safe;
            for (int suffix = 2; !used.Add(candidate); suffix++)
            {
                string tail = "_" + suffix.ToString(CultureInfo.InvariantCulture);
                candidate = safe[..Math.Min(safe.Length, 40 - tail.Length)] + tail;
            }
            result[label] = candidate;
        }
        return result;
    }

    private static string RewriteSquaredDistances(string definition, IReadOnlySet<string> points) =>
        Regex.Replace(definition, @"\(\s*([\p{L}_][\p{L}\d_]*)\s*-\s*([\p{L}_][\p{L}\d_]*)\s*\)\s*\^\s*(?:\(\s*2(?:\.0+)?\s*\)|2\b)",
            match => points.Contains(match.Groups[1].Value) && points.Contains(match.Groups[2].Value)
                ? $"Distance({match.Groups[1].Value},{match.Groups[2].Value})^2" : match.Value);

    private static string RewritePointExpression(string definition, IReadOnlySet<string> points, bool spatial)
    {
        string expression = definition.Trim();
        if (points.Contains(expression) || expression.StartsWith('"') || MathSyntax.Call(expression, out _, out _)) return definition;
        if (expression.StartsWith('(') && expression.EndsWith(')'))
        {
            try { if (MathSyntax.Split(expression[1..^1]).Length is 2 or 3) return definition; }
            catch (FormatException) { }
        }
        if (!Regex.Matches(expression, @"[\p{L}_][\p{L}\d_]*").Any(match => points.Contains(match.Value))) return definition;
        string Coordinate(char axis) => Regex.Replace(expression, @"\b[xyz]\s*\(\s*[\p{L}_][\p{L}\d_]*\s*\)|[\p{L}_][\p{L}\d_]*",
            match => points.Contains(match.Value) ? $"{axis}({match.Value})" : match.Value);
        return "(" + Coordinate('x') + "," + Coordinate('y') + (spatial ? "," + Coordinate('z') : "") + ")";
    }

    private static string RewriteFunctionDefinition(string label, string definition)
    {
        var match = Regex.Match(definition.Trim(),
            @"^(?<name>[\p{L}_][\p{L}\d_]*)\s*\(\s*(?<variables>[\p{L}_][\p{L}\d_]*(?:\s*,\s*[\p{L}_][\p{L}\d_]*)?)\s*\)\s*=(?!=)\s*(?<body>.+)$");
        if (!match.Success || match.Groups["name"].Value != label) return definition;
        string[] variables = MathSyntax.Split(match.Groups["variables"].Value);
        string body = match.Groups["body"].Value.Trim();
        return variables.Length == 1 ? $"Function({body},{variables[0]})" : $"SurfaceFunction({body},{variables[0]},{variables[1]})";
    }

    private static string ElementFallback(string label, IReadOnlyDictionary<string, XElement> elements, ICollection<string> warnings)
    {
        if (!elements.TryGetValue(label, out var element)) return "GeoGebraUnsupported(\"" + label.Replace("\"", "") + "\")";
        string type = Attribute(element, "type");
        var coords = element.Elements().FirstOrDefault(child => child.Name.LocalName == "coords");
        if (TryStoredPoint(element, out string point))
        {
            if (type == "point" && coords.Attribute("z") is null) warnings.Add($"{label}：点的齐次权重缺失，按 1 导入。");
            return point;
        }
        if (type is "vector" or "vector3d" && coords is not null
            && NumberAttribute(coords, "x", out double vectorX) && NumberAttribute(coords, "y", out double vectorY))
        {
            NumberAttribute(coords, "z", out double vectorZ);
            return $"Vector(({N(vectorX)},{N(vectorY)}{(type == "vector3d" ? "," + N(vectorZ) : "")}))";
        }
        var storedValue = element.Elements().FirstOrDefault(child => child.Name.LocalName == "value");
        if (storedValue is not null && (type is "numeric" or "angle") && NumberAttribute(storedValue, "val", out double number))
            return N(number);
        if (storedValue is not null && type == "boolean")
            return "Checkbox(" + (BooleanAttribute(storedValue, "val", false) ? "true" : "false") + ")";
        if (type == "button") return "Button(" + ButtonCaption(element, label) + ")";
        if (type == "conic" && element.Elements().FirstOrDefault(child => child.Name.LocalName == "matrix") is { } matrix)
        {
            var coefficients = new double[6];
            bool valid = true;
            for (int index = 0; index < coefficients.Length; index++)
                valid &= NumberAttribute(matrix, $"A{index}", out coefficients[index]);
            if (valid)
            {
                warnings.Add($"{label}：使用 GeoGebra XML 中的圆锥曲线矩阵作为静态对象。");
                return $"Conic({N(coefficients[0])},{N(coefficients[1])},{N(coefficients[2])},"
                    + $"{N(2 * coefficients[3])},{N(2 * coefficients[4])},{N(2 * coefficients[5])})";
            }
        }
        var line = coords ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "line");
        if (type.Equals("line", StringComparison.OrdinalIgnoreCase) && line is not null
            && NumberAttribute(line, "x", out double a) && NumberAttribute(line, "y", out double b)
            && NumberAttribute(line, "z", out double c) && a * a + b * b > 1e-20)
        {
            double x0 = -a * c / (a * a + b * b), y0 = -b * c / (a * a + b * b);
            warnings.Add($"{label}：使用 GeoGebra XML 中的直线方程作为静态对象。");
            return $"Line(({N(x0)},{N(y0)}),({N(x0 - b)},{N(y0 + a)}))";
        }
        warnings.Add($"{label}：对象类型 {type} 无可映射的定义，已保留为未定义对象。");
        return "GeoGebraUnsupported(\"" + label.Replace("\"", "") + "\")";
    }

    private static bool TryStoredPoint(XElement element, out string definition)
    {
        definition = "";
        string type = Attribute(element, "type");
        var coordinates = element.Elements().FirstOrDefault(child => child.Name.LocalName == "coords");
        if (type is not ("point" or "point3d") || coordinates is null
            || !NumberAttribute(coordinates, "x", out double x) || !NumberAttribute(coordinates, "y", out double y)) return false;
        bool spatial = type == "point3d";
        NumberAttribute(coordinates, "z", out double z);
        double weight = NumberAttribute(coordinates, spatial ? "w" : "z", out double parsedWeight) ? parsedWeight : 1;
        if (Math.Abs(weight) <= 1e-12) return false;
        definition = "(" + N(x / weight) + "," + N(y / weight) + (spatial ? "," + N(z / weight) : "") + ")";
        return true;
    }

    private static string ReadTextDefinition(string definition, XElement element, Func<string, string> rewriteLabels)
    {
        var location = element.Elements().FirstOrDefault(child => child.Name.LocalName == "startPoint");
        if (location is null) return $"Text({definition})";
        string point = Attribute(location, "exp");
        if (point.Length != 0) return $"Text({definition},{rewriteLabels(point)})";
        if (!NumberAttribute(location, "x", out double x) || !NumberAttribute(location, "y", out double y))
            return $"Text({definition})";
        if (!NumberAttribute(location, "z", out double weight) || weight == 0) weight = 1;
        return $"Text({definition},({N(x / weight)},{N(y / weight)}))";
    }

    private static string ButtonCaption(XElement element, string label)
    {
        string caption = element?.Elements().FirstOrDefault(child => child.Name.LocalName == "caption")?.Attribute("val")?.Value;
        return "\"" + (string.IsNullOrEmpty(caption) ? label : caption).Replace('"', '\u201D') + "\"";
    }

    private static string CheckboxValue(XElement element)
    {
        var value = element?.Elements().FirstOrDefault(child => child.Name.LocalName == "value");
        return value is not null && BooleanAttribute(value, "val", false) ? "true" : "false";
    }

    private static ConstructionObject ReadStyle(string label, string definition, XElement element,
        Func<string, string> rewriteLabels, ICollection<string> warnings)
    {
        if (element is null) return new() { Label = label, Definition = definition };
        var scripts = element.Elements().Where(child => child.Name.LocalName is "ggbscript" or "javascript").ToArray();
        var show = element.Elements().FirstOrDefault(child => child.Name.LocalName == "show");
        var condition = element.Elements().FirstOrDefault(child => child.Name.LocalName == "condition");
        string visibilityCondition = condition is null ? "" : rewriteLabels(Attribute(condition, "showObject")).Trim();
        if (visibilityCondition.Length > 4096)
        {
            warnings.Add($"{label}：可见性条件超过 4096 字符。");
            visibilityCondition = "";
        }
        var color = element.Elements().FirstOrDefault(child => child.Name.LocalName is "objColor" or "objectColor");
        var line = element.Elements().FirstOrDefault(child => child.Name.LocalName == "lineStyle");
        var fixedNode = element.Elements().FirstOrDefault(child => child.Name.LocalName == "fixed");
        var trace = element.Elements().FirstOrDefault(child => child.Name.LocalName == "trace");
        var slider = element.Elements().FirstOrDefault(child => child.Name.LocalName == "slider");
        double sliderMin = slider is not null && NumberAttribute(slider, "min", out double minimum) ? minimum : -10;
        double sliderMax = slider is not null && NumberAttribute(slider, "max", out double maximum) ? maximum : 10;
        if (sliderMax <= sliderMin) (sliderMin, sliderMax) = (-10, 10);
        var animation = element.Elements().FirstOrDefault(child => child.Name.LocalName == "animation");
        var screenLocation = element.Elements().FirstOrDefault(child => child.Name.LocalName is "absoluteScreenLocation" or "screenLocation")
            ?? (slider is not null && BooleanAttribute(slider, "absoluteScreenLocation", false) ? slider : null);
        uint packed = 0xFF2269DE;
        if (color is not null && NumberAttribute(color, "r", out double red)
            && NumberAttribute(color, "g", out double green) && NumberAttribute(color, "b", out double blue))
        {
            bool normalized = red <= 1 && green <= 1 && blue <= 1
                && new[] { red, green, blue }.Any(component => component != Math.Truncate(component));
            byte alpha = NumberAttribute(color, "alpha", out double a) ? Component(a, a <= 1) : (byte)255;
            packed = (uint)(alpha << 24 | Component(red, normalized) << 16 | Component(green, normalized) << 8 | Component(blue, normalized));
        }
        double width = line is not null && NumberAttribute(line, "thickness", out double thickness) ? Math.Clamp(thickness, .1, 30) : 2;
        return new()
        {
            Label = label,
            Definition = definition,
            Visible = show is null || BooleanAttribute(show, "object", true),
            VisibilityCondition = visibilityCondition,
            ShowLabel = show is not null && BooleanAttribute(show, "label", false),
            Fixed = fixedNode is not null && BooleanAttribute(fixedNode, "val", false),
            Trace = trace is not null && BooleanAttribute(trace, "val", false),
            Color = packed | 0xFF000000,
            LineWidth = width,
            Opacity = (packed >> 24) / 255d,
            SliderMin = sliderMin,
            SliderMax = sliderMax,
            SliderStep = animation is not null && NumberAttribute(animation, "step", out double step) && step > 0 ? step : .1,
            Animate = animation is not null && BooleanAttribute(animation, "playing", false),
            ShowSlider = slider is not null,
            ControlPositionSet = screenLocation is not null,
            ControlX = screenLocation is not null && NumberAttribute(screenLocation, "x", out double controlX) ? Math.Clamp(controlX, 0, 100_000) : 16,
            ControlY = screenLocation is not null && NumberAttribute(screenLocation, "y", out double controlY) ? Math.Clamp(controlY, 0, 100_000) : 12,
            OnClick = ReadScript("val"),
            OnUpdate = ReadScript("onUpdate"),
            OnDragEnd = ReadScript("onDragEnd"),
            OnChange = ReadScript("onChange"),
        };

        string ReadScript(string attribute)
        {
            var node = scripts.LastOrDefault(child => child.Attribute(attribute) is not null);
            if (node is null) return "";
            string script = Attribute(node, attribute);
            if (string.IsNullOrWhiteSpace(script)) return "";
            script = node.Name.LocalName == "javascript" ? "js: " + script : rewriteLabels(script);
            if (script.Length <= 4096) return script;
            warnings.Add($"{label}：事件脚本 {attribute} 超过 4096 字符。");
            return "";
        }
    }

    private static ConstructionSnapshot ReadView(XElement root)
    {
        var view = root.Elements().FirstOrDefault(element => element.Name.LocalName == "euclidianView");
        var coordinates = view?.Elements().FirstOrDefault(element => element.Name.LocalName == "coordSystem");
        var spatialView = root.Elements().FirstOrDefault(element => element.Name.LocalName == "euclidianView3D");
        var spatialCamera = spatialView?.Elements().FirstOrDefault(element => element.Name.LocalName == "coordSystem");
        if (spatialCamera is not null && NumberAttribute(spatialCamera, "scale", out double spatialScale) && spatialScale > 0)
        {
            NumberAttribute(spatialCamera, "xAngle", out double pitch);
            NumberAttribute(spatialCamera, "zAngle", out double yaw);
            double span = 500 / spatialScale;
            var spatialGrid = spatialView.Elements().FirstOrDefault(element => element.Name.LocalName == "grid");
            return new()
            {
                ThreeDimensional = true, Yaw = yaw * Math.PI / 180, Pitch = pitch * Math.PI / 180,
                XMin = -span, XMax = span, YMin = -.75 * span, YMax = .75 * span,
                Axes = spatialView.Elements().Where(element => element.Name.LocalName == "axis").Any(axis => BooleanAttribute(axis, "show", true)),
                Grid = spatialGrid is not null && BooleanAttribute(spatialGrid, "show", false),
            };
        }
        if (coordinates is null) return new();
        NumberAttribute(coordinates, "xZero", out double xZero);
        NumberAttribute(coordinates, "yZero", out double yZero);
        if (!NumberAttribute(coordinates, "scale", out double scale) || scale <= 0) return new();
        double yScale = NumberAttribute(coordinates, "yscale", out double parsedYScale) && parsedYScale > 0 ? parsedYScale : scale;
        var size = view.Elements().FirstOrDefault(element => element.Name.LocalName == "size");
        double width = size is not null && NumberAttribute(size, "width", out double parsedWidth) ? parsedWidth : 1000;
        double height = size is not null && NumberAttribute(size, "height", out double parsedHeight) ? parsedHeight : 750;
        var settings = view.Elements().FirstOrDefault(element => element.Name.LocalName == "evSettings");
        var grid = settings is not null && BooleanAttribute(settings, "grid", false);
        var axes = settings is null || BooleanAttribute(settings, "axes", true);
        var polar = settings is not null && Attribute(settings, "gridType").Equals("polar", StringComparison.OrdinalIgnoreCase);
        return new()
        {
            XMin = -xZero / scale,
            XMax = (width - xZero) / scale,
            YMin = (yZero - height) / yScale,
            YMax = yZero / yScale,
            Axes = axes,
            Grid = grid,
            PolarGrid = polar,
        };
    }

    private static bool LooksLikeDefinition(string text) => text.Length > 0 && !text.Contains("undefined", StringComparison.OrdinalIgnoreCase);
    private static string Attribute(XElement element, string name) => element.Attribute(name)?.Value ?? "";
    private static bool BooleanAttribute(XElement element, string name, bool fallback)
        => bool.TryParse(Attribute(element, name), out bool value) ? value
            : int.TryParse(Attribute(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric) ? numeric != 0 : fallback;
    private static bool NumberAttribute(XElement element, string name, out double value)
        => double.TryParse(Attribute(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static byte Component(double value, bool normalized)
    {
        if (normalized) value *= 255;
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
    private static string N(double value) => value.ToString("G15", CultureInfo.InvariantCulture);
}
