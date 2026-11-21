using System.Globalization;
using System.Text.RegularExpressions;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

public sealed partial class ConstructionDocument
{
    public static string ExpandTool(string definition, ConstructionSnapshot state)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            if (!MathSyntax.Call(definition, out var command, out var args)) return definition;
            var tool = state.Tools.FirstOrDefault(tool => tool.Name == command);
            if (tool is null) return definition;
            if (tool.Inputs.Length != args.Length) throw new FormatException("自定义工具参数数量不匹配 · " + command);
            var inputs = tool.Inputs.Select((name, index) => (name, value: args[index])).ToDictionary(item => item.name, item => item.value);
            definition = Regex.Replace(tool.Definition, @"[\p{L}_][\p{L}\d_]*", match => inputs.GetValueOrDefault(match.Value, match.Value));
        }
        throw new FormatException("自定义工具存在递归或嵌套过深。");
    }

    public void RunScript(string script)
    {
        if (script.Length > 4096) throw new FormatException("事件脚本最多 4096 个字符。");
        if (script.TrimStart().StartsWith("js:", StringComparison.OrdinalIgnoreCase))
        { RunJavaScript(script.TrimStart()[3..]); return; }
        var transaction = new ConstructionDocument(); transaction.Load(State);
        var lines = script.Replace("\r", "").Split('\n').SelectMany(line => MathSyntax.Split(line, ';')).Where(line => line.Length > 0).ToArray();
        if (lines.Length > 64) throw new FormatException("单次事件脚本最多 64 条指令。");
        foreach (string line in lines)
        {
            if (!MathSyntax.Call(line, out var command, out var args)) { transaction.Execute(line); continue; }
            if (command.ToLowerInvariant() is "setvalue" or "setcoords" or "setvisible" or "setfixed" or "showlabel" or "setcolor")
            {
                if (args.Length < 2 || !transaction.State.Objects.Any(item => item.Label == args[0])) throw new FormatException("脚本目标对象不存在或缺少参数。");
                string resolved = args[1];
                if (command.Equals("SetValue", StringComparison.OrdinalIgnoreCase))
                {
                    string temporary = NextLabel(transaction.State.Objects, "scriptValue");
                    var state = transaction.State with { Objects = transaction.State.Objects.Append(new ConstructionObject { Label = temporary, Definition = args[1] }).ToList() };
                    var value = new ConstructionKernel().Evaluate(state).Values[temporary];
                    if (!value.Defined) throw new FormatException(value.Error);
                    resolved = value.Kind is GeometryKind.Scalar or GeometryKind.Boolean ? ComputerAlgebra.Number(value.Number) : args[1];
                }
                bool Flag(string text) => text.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || !text.Equals("false", StringComparison.OrdinalIgnoreCase) && ComputerAlgebra.Real(text) != 0;
                transaction.Update(args[0], item => command.ToLowerInvariant() switch
                {
                    "setvalue" => item with { Definition = resolved },
                    "setcoords" => item with { Definition = "(" + string.Join(',', args.Skip(1)) + ")" },
                    "setvisible" => item with { Visible = Flag(args[1]) },
                    "setfixed" => item with { Fixed = Flag(args[1]) },
                    "showlabel" => item with { ShowLabel = Flag(args[1]) },
                    _ => item with { Color = 0xFF000000 | uint.Parse(args[1].Trim('"', '#'), NumberStyles.HexNumber) },
                });
            }
            else transaction.Execute(line);
        }
        Commit(transaction.State);
    }
}
