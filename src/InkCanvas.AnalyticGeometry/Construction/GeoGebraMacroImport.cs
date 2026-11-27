using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

public static partial class GeoGebraImport
{
    private static (List<ConstructionTool> Tools, Dictionary<string, int> Outputs) ReadMacroTools(ZipArchive archive,
        XElement worksheet, ICollection<string> warnings, CancellationToken token)
    {
        var tools = new List<ConstructionTool>();
        var outputs = new Dictionary<string, int>(StringComparer.Ordinal);
        var macros = worksheet.Elements().Where(element => element.Name.LocalName == "macro");
        var entry = archive.GetEntry("geogebra_macro.xml");
        if (entry is not null)
        {
            try
            {
                if (entry.Length is <= 0 or > MaximumXmlBytes) throw new FormatException("工具 XML 大小无效。");
                if (ReadXml(entry).Root is { } root)
                    macros = macros.Concat(root.Elements().Where(element => element.Name.LocalName == "macro"));
            }
            catch (Exception ex) when (ex is XmlException or FormatException or InvalidDataException)
            {
                warnings.Add("自定义工具 XML 无法读取，工作表其余对象继续导入。");
            }
        }

        var definitions = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var macro in macros)
        {
            token.ThrowIfCancellationRequested();
            if (definitions.Count == 64)
            {
                warnings.Add("自定义工具数量超过 64。");
                break;
            }
            string name = Attribute(macro, "cmdName");
            if (!definitions.TryAdd(name, macro)) warnings.Add($"{name}：自定义工具名称重复。");
        }

        var active = new HashSet<string>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var context = new ConstructionSnapshot { Tools = tools };
        foreach (string name in definitions.Keys) ResolveMacro(name);
        return (tools, outputs);

        bool ResolveMacro(string name)
        {
            token.ThrowIfCancellationRequested();
            if (outputs.ContainsKey(name)) return true;
            if (failed.Contains(name) || !active.Add(name)) return false;
            try
            {
                var macro = definitions[name];
                var dependencies = macro.Elements().Where(node => node.Name.LocalName == "construction")
                    .SelectMany(node => node.Elements().Where(child => child.Name.LocalName == "command"))
                    .Select(node => Attribute(node, "name")).Where(definitions.ContainsKey).Distinct(StringComparer.Ordinal);
                foreach (string dependency in dependencies)
                    if (!ResolveMacro(dependency)) throw new FormatException($"依赖工具 {dependency} 未导入或存在循环引用。");

                var tool = ReadMacro(macro, name, context, outputs, out int count, token);
                outputs.Add(name, count);
                tools.Add(tool);
                return true;
            }
            catch (FormatException ex)
            {
                failed.Add(name);
                warnings.Add($"{name}：自定义工具未导入。{ex.Message}");
                return false;
            }
            finally { active.Remove(name); }
        }
    }

    private static ConstructionTool ReadMacro(XElement macro, string name, ConstructionSnapshot context,
        IReadOnlyDictionary<string, int> toolOutputs, out int outputCount, CancellationToken token)
    {
        if (!MathSyntax.Identifier(name) || name.Length > 40 || SupportedCommands.Contains(name) || MapCommand(name) != name)
            throw new FormatException("工具名称无效或与本地命令重名。");
        var inputs = ReadIndexedAttributes(macro, "macroInput");
        var outputs = ReadIndexedAttributes(macro, "macroOutput");
        if (inputs.Length is < 1 or > 16 || outputs.Length == 0
            || inputs.Any(input => !MathSyntax.Identifier(input)) || inputs.Distinct(StringComparer.Ordinal).Count() != inputs.Length)
            throw new FormatException("工具输入或输出格式尚未映射。");
        outputCount = outputs.Length;

        var construction = macro.Elements().FirstOrDefault(element => element.Name.LocalName == "construction")
            ?? throw new FormatException("工具缺少 construction 节点。");
        var elements = ReadLabeledNodes("element");
        var expressions = ReadLabeledNodes("expression");
        var commands = ReadCommands(construction, elements, toolOutputs);
        var definitions = inputs.ToDictionary(input => input, input => input, StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var localLabels = elements.Keys.Concat(expressions.Keys).Concat(commands.Keys).Distinct(StringComparer.Ordinal).ToArray();
        if (localLabels.Length > MaximumObjects || localLabels.Any(label => !MathSyntax.Identifier(label)))
            throw new FormatException("工具包含尚未映射的局部标签。");

        var labels = localLabels.ToHashSet(StringComparer.Ordinal);
        var parts = outputs.Select(Resolve).ToArray();
        string definition = parts.Length == 1 ? parts[0] : $"{{{string.Join(',', parts)}}}";
        if (definition.Length > 4096) throw new FormatException("展开后的工具定义过长。");
        return new(name, inputs, definition);

        Dictionary<string, XElement> ReadLabeledNodes(string type) => construction.Elements()
            .Where(element => element.Name.LocalName == type && Attribute(element, "label").Length != 0)
            .GroupBy(element => Attribute(element, "label"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        string Resolve(string label)
        {
            token.ThrowIfCancellationRequested();
            if (definitions.TryGetValue(label, out string resolved)) return resolved;
            if (active.Count >= 32 || !active.Add(label)) throw new FormatException("工具依赖循环或嵌套过深。");
            string expression;
            if (commands.TryGetValue(label, out var command))
            {
                if (!command.Supported) throw new FormatException($"命令 {command.Name} 尚未映射。");
                expression = toolOutputs.ContainsKey(command.Name)
                    ? ConstructionDocument.ExpandTool(command.Definition, context) : command.Definition;
            }
            else if (expressions.TryGetValue(label, out var node))
                expression = Attribute(node, "exp");
            else
                expression = ElementFallback(label, elements, []);
            if (!LooksLikeDefinition(expression) || expression.StartsWith("GeoGebraUnsupported(", StringComparison.Ordinal))
                throw new FormatException($"工具对象 {label} 缺少可用定义。");

            expression = Regex.Replace(expression, @"""(?:\\.|[^""\\])*""|(?<![\p{L}\d_])[\p{L}_][\p{L}\d_]*",
                match =>
                {
                    if (!labels.Contains(match.Value) || expression.AsSpan(match.Index + match.Length).TrimStart().StartsWith("("))
                        return match.Value;
                    string dependency = Resolve(match.Value);
                    return MathSyntax.Identifier(dependency) || MathSyntax.Call(dependency, out _, out _)
                        || dependency.StartsWith('(') || dependency.StartsWith('{') || dependency.StartsWith('"')
                        ? dependency : "(" + dependency + ")";
                });
            if (expression.Length > 4096) throw new FormatException("展开后的工具定义过长。");
            active.Remove(label);
            return definitions[label] = expression;
        }
    }
}
