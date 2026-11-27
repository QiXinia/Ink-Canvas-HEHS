using System.Globalization;
using System.Text.RegularExpressions;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

public sealed partial class ConstructionDocument
{
    private static readonly HashSet<string> DocumentEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnAdd", "OnRemove", "OnClear", "OnRename", "OnStoreUndo", "OnViewChanged", "OnClient",
        "OnUndo", "OnRedo", "OnUpdateStyle", "OnExport", "OnAddMacro", "OnRemoveMacro",
        "OnMouseDown", "OnDropdownOpened", "OnDropdownClosed", "OnSetMode",
        "OnAlgebraPanelSelected", "OnToolsPanelSelected", "OnSidePanelOpened", "OnSidePanelClosed",
        "OnClick", "OnUpdate", "OnChange", "OnMouseOver", "OnMouseOut", "OnSelect", "OnDeselect", "OnDragStart", "OnDragEnd",
    };

    public static string ExpandTool(string definition, ConstructionSnapshot state)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            if (!MathSyntax.Call(definition, out var command, out var args)) return definition;
            if (command.Equals("GgbToolOutput", StringComparison.OrdinalIgnoreCase) && args.Length == 2)
            {
                if (depth != 0 || !MathSyntax.Call(args[0], out string source, out _) || !state.Tools.Any(item => item.Name == source))
                    throw new FormatException("自定义工具输出未定义。");
                return $"ConstructionOutput({ExpandTool(args[0], state)},{args[1]})";
            }
            var tool = state.Tools.FirstOrDefault(tool => tool.Name == command);
            if (tool is null) return definition;
            if (tool.Inputs.Length != args.Length) throw new FormatException("自定义工具参数数量不匹配 · " + command);
            var inputs = tool.Inputs.Select((name, index) => (name, value: args[index])).ToDictionary(item => item.name, item => item.value);
            definition = Regex.Replace(tool.Definition, @"[\p{L}_][\p{L}\d_]*", match => inputs.GetValueOrDefault(match.Value, match.Value));
        }
        throw new FormatException("自定义工具存在递归或嵌套过深。");
    }

    public void RunScript(string script, string eventName = "", string target = "", string previousTarget = "", string argument = null)
    {
        if (script.Length > 4096) throw new FormatException("事件脚本最多 4096 个字符。");
        if (script.TrimStart().StartsWith("js:", StringComparison.OrdinalIgnoreCase))
        { RunJavaScript(script.TrimStart()[3..], eventName, target, previousTarget, argument: argument); return; }
        var transaction = new ConstructionDocument(); transaction.Load(State);
        var lines = script.Replace("\r", "").Split('\n').Where(line => !line.TrimStart().StartsWith('#'))
            .SelectMany(line => MathSyntax.Split(line, ';')).Where(line => line.Length > 0).ToArray();
        if (lines.Length > 64) throw new FormatException("单次事件脚本最多 64 条指令。");
        foreach (string line in lines)
        {
            if (!MathSyntax.Call(line, out var command, out var args)) { transaction.Execute(line); continue; }
            if (command.Equals("SetEvent", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 3) throw new FormatException("SetEvent 需要对象标签、事件名和脚本。");
                transaction.RegisterEvent(args[0], args[1], args[2].Trim().Trim('"'));
            }
            else if (command.ToLowerInvariant() is "setvalue" or "setcoords" or "setvisible" or "setfixed" or "showlabel" or "setcolor" or "setcontrolposition")
            {
                if (args.Length < 2 || !transaction.State.Objects.Any(item => item.Label == args[0])) throw new FormatException("脚本目标对象不存在或缺少参数。");
                if (command.Equals("SetControlPosition", StringComparison.OrdinalIgnoreCase) && args.Length != 3)
                    throw new FormatException("SetControlPosition 需要对象标签、X 和 Y 坐标。");
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
                    "setcontrolposition" => item with
                    {
                        ControlPositionSet = true,
                        ControlX = ComputerAlgebra.Real(args[1]),
                        ControlY = ComputerAlgebra.Real(args[2]),
                    },
                    _ => item with { Color = 0xFF000000 | uint.Parse(args[1].Trim('"', '#'), NumberStyles.HexNumber) },
                });
            }
            else transaction.Execute(line);
        }
        if (!ReferenceEquals(State, transaction.State)) Commit(transaction.State);
    }

    public void RegisterEvent(string label, string eventName, string script)
    {
        if (script.Length > 4096) throw new FormatException("事件脚本最多 4096 个字符。");
        string eventKey = eventName.Trim().Trim('"').ToLowerInvariant();
        label = label.Trim().Trim('"');
        if (label == "*")
        {
            if (!DocumentEventNames.Contains(eventKey)) throw new FormatException("未知构造事件 · " + eventName);
            var scripts = new Dictionary<string, string>(State.EventScripts, StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(script)) scripts.Remove(eventKey);
            else scripts[eventKey] = script;
            Commit(State with { EventScripts = scripts });
            return;
        }
        Update(label, item => eventKey switch
        {
            "onclick" => item with { OnClick = script },
            "onupdate" => item with { OnUpdate = script },
            "onchange" => item with { OnChange = script },
            "onmouseover" => item with { OnMouseOver = script },
            "onmouseout" => item with { OnMouseOut = script },
            "onselect" => item with { OnSelect = script },
            "ondeselect" => item with { OnDeselect = script },
            "ondragstart" => item with { OnDragStart = script },
            "ondragend" => item with { OnDragEnd = script },
            _ => throw new FormatException("未知构造事件 · " + eventName),
        });
    }

    internal static bool ValidEventScripts(Dictionary<string, string> scripts)
        => scripts is not null && scripts.Count <= DocumentEventNames.Count
            && scripts.All(pair => pair.Key == pair.Key.ToLowerInvariant()
                && DocumentEventNames.Contains(pair.Key) && pair.Value is not null && pair.Value.Length <= 4096);
}
