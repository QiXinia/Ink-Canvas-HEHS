using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Construction;

/// <summary>Persistent definitions and undo transactions. Evaluations operate on immutable snapshots.</summary>
public sealed partial class ConstructionDocument
{
    private readonly Stack<ConstructionSnapshot> _undo = new(), _redo = new();
    private ConstructionSnapshot _gesture;
    public ConstructionSnapshot State { get; private set; } = new();
    public int Revision { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action Changed;

    public string Execute(string input)
    {
        var objects = State.Objects.ToList();
        var tools = State.Tools.ToList();
        string last = "";
        foreach (var source in input.Replace("\r", "").Split('\n').SelectMany(line => MathSyntax.Split(line, ';')).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            string text = source.Trim();
            if (MathSyntax.Call(text, out var toolCommand, out var toolArguments) && toolCommand.Equals("DefineTool", StringComparison.OrdinalIgnoreCase))
            {
                if (toolArguments.Length != 3) throw new FormatException("格式为 DefineTool(名称,{输入名,...},构造表达式)。");
                string name = toolArguments[0].Trim();
                var inputs = MathSyntax.Split(toolArguments[1].Trim().Trim('{', '}'));
                var tool = new ConstructionTool(name, inputs, toolArguments[2]);
                tools.RemoveAll(item => item.Name == name); tools.Add(tool);
                continue;
            }
            if (MathSyntax.Call(text, out var command, out var arguments) && command.Equals("Delete", StringComparison.OrdinalIgnoreCase))
            {
                objects.RemoveAll(item => arguments.Contains(item.Label));
                continue;
            }
            string label = "", definition = text;
            var function = Regex.Match(text, @"^([\p{L}_][\p{L}\d_]*)\s*\(([xyztuv](?:\s*,\s*[xyztuv])?)\)\s*:?=(.+)$");
            var assignment = Regex.Match(text, @"^([\p{L}_][\p{L}\d_]*)\s*:?=(?!=)(.+)$");
            if (function.Success)
            {
                label = function.Groups[1].Value;
                definition = function.Groups[2].Value.Contains(',')
                    ? $"Surface({function.Groups[3].Value})" : $"Function({function.Groups[3].Value},{function.Groups[2].Value})";
            }
            else if (assignment.Success && assignment.Groups[1].Value is not ("x" or "y" or "z"))
            { label = assignment.Groups[1].Value; definition = assignment.Groups[2].Value; }
            if (label.Length == 0) label = NextLabel(objects, text.TrimStart().StartsWith('(') ? "P" : "g");
            if (!MathSyntax.Identifier(label) || label.Length > 40 || definition.Length > 4096) throw new FormatException("名称或定义过长。");
            int index = objects.FindIndex(item => item.Label == label);
            if (index >= 0) objects[index] = objects[index] with { Definition = definition.Trim() };
            else objects.Add(new() { Label = label, Definition = definition.Trim(), Color = Colors[objects.Count % Colors.Length] });
            last = label;
        }
        if (objects.Count > 512) throw new InvalidOperationException("单个工作区最多 512 个对象。");
        Commit(State with { Objects = objects, Tools = tools });
        return last;
    }

    private static readonly uint[] Colors = { 0xFF2269DE, 0xFFBA3847, 0xFF15936F, 0xFF8E4CBD, 0xFFCE7918, 0xFF287E99 };
    public static string NextLabel(IEnumerable<ConstructionObject> objects, string prefix)
    {
        var names = objects.Select(item => item.Label).ToHashSet();
        for (int index = 1; ; index++) if (!names.Contains(prefix + index)) return prefix + index;
    }
    public void Update(string label, Func<ConstructionObject, ConstructionObject> update)
    {
        var objects = State.Objects.Select(item => item.Label == label ? update(item) : item).ToList();
        Commit(State with { Objects = objects });
    }
    public void SetView(ConstructionSnapshot view) => Commit(view with { Objects = State.Objects });
    public void BeginGesture() => _gesture ??= State;
    public void EndGesture()
    {
        if (_gesture is null) return;
        if (!ReferenceEquals(State, _gesture)) { _undo.Push(_gesture); _redo.Clear(); }
        _gesture = null;
    }
    private void Commit(ConstructionSnapshot state)
    {
        Validate(state);
        if (_gesture is null) { _undo.Push(State); _redo.Clear(); }
        State = state; Revision++; Changed?.Invoke();
    }
    public void Undo() { EndGesture(); if (_undo.TryPop(out var state)) { _redo.Push(State); State = state; Revision++; Changed?.Invoke(); } }
    public void Redo() { if (_redo.TryPop(out var state)) { _undo.Push(State); State = state; Revision++; Changed?.Invoke(); } }
    public void Load(ConstructionSnapshot state)
    {
        Validate(state); Commit(state);
    }
    public static void Validate(ConstructionSnapshot state)
    {
        if (state.FormatVersion != 2 || state.Objects is null || state.Objects.Count > 512) throw new FormatException("不支持的构造文件版本或对象过多。");
        if (state.Tools is null || state.Tools.Count > 64 || state.Tools.Any(tool => !MathSyntax.Identifier(tool.Name)
            || tool.Inputs is null || tool.Inputs.Length is < 1 or > 16 || tool.Inputs.Any(input => !MathSyntax.Identifier(input))
            || tool.Inputs.Distinct().Count() != tool.Inputs.Length || tool.Definition is null || tool.Definition.Length > 4096))
            throw new FormatException("自定义工具定义无效。");
        if (state.Objects.Any(item => item is null || !MathSyntax.Identifier(item.Label ?? "") || item.Label.Length > 40 || item.Definition is null || item.Definition.Length > 4096)
            || state.Objects.Select(item => item.Label).Distinct().Count() != state.Objects.Count) throw new FormatException("对象定义或名称无效。");
        if (!double.IsFinite(state.XMin + state.XMax + state.YMin + state.YMax + state.Yaw + state.Pitch)
            || state.XMax - state.XMin < 1e-7 || state.YMax - state.YMin < 1e-7) throw new FormatException("视图坐标无效。");
        if (state.Objects.Any(item => !double.IsFinite(item.LineWidth + item.Opacity + item.SliderMin + item.SliderMax + item.SliderStep)
            || item.LineWidth is < .1 or > 30 || item.Opacity is < 0 or > 1 || item.SliderMax <= item.SliderMin || item.SliderStep <= 0
            || item.OnClick is null || item.OnUpdate is null || item.OnClick.Length > 4096 || item.OnUpdate.Length > 4096)) throw new FormatException("对象样式、参数范围或脚本长度无效。");
    }
    public static bool IsFreePoint(ConstructionObject item)
    {
        string text = item.Definition.Trim();
        if (!text.StartsWith('(') || !text.EndsWith(')')) return false;
        try { return MathSyntax.Split(text[1..^1]).All(value => ComputerAlgebra.Parse(value).Vars.Count == 0); }
        catch (Exception) { return false; }
    }
    public static async Task SaveAsync(string path, ConstructionSnapshot state, CancellationToken token = default)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<ConstructionSnapshot> OpenAsync(string path, CancellationToken token = default)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new FormatException("构造文件超过 8 MiB。");
        var state = JsonSerializer.Deserialize<ConstructionSnapshot>(await File.ReadAllTextAsync(path, token)) ?? throw new FormatException("构造文件为空。");
        Validate(state); return state;
    }
}

public sealed class ConstructionKernel
{
    private static string DependencyText(string definition)
    {
        definition = Regex.Replace(definition, "\"(?:\\\\.|[^\"\\\\])*\"", "\"\"");
        if (!MathSyntax.Call(definition, out var command, out var arguments)) return definition;
        var args = arguments.ToArray();
        void Bind(int variableIndex, int expressions)
        {
            if (variableIndex >= args.Length) return;
            string variable = args[variableIndex].Trim();
            if (!MathSyntax.Identifier(variable)) return;
            for (int i = 0; i < Math.Min(expressions, args.Length); i++)
                args[i] = Regex.Replace(args[i], @"\b" + Regex.Escape(variable) + @"\b(?!\s*\()", "0");
            args[variableIndex] = "";
        }
        switch (command.ToLowerInvariant())
        {
            case "function": Bind(1,1); break;
            case "derivative": case "diff": case "integral": case "integrate": case "limit": Bind(1,1); break;
            case "curve": Bind(args.Length >= 6 ? 3 : 2,args.Length >= 6 ? 3 : 2); break;
            case "surface" when args.Length >= 9: Bind(3,3); Bind(6,3); break;
            case "sequence": Bind(1,1); break;
            case "zip": for (int i = 1; i < args.Length; i += 2) Bind(i,1); break;
        }
        return command + "(" + string.Join(',',args) + ")";
    }
    public static string ResolveExpression(ConstructionSnapshot state, ConstructionEvaluation evaluation, string expression, CancellationToken token = default)
    {
        AngouriMath.MathS.Multithreading.SetLocalCancellationToken(token);
        return new GeometryEvaluator(state, evaluation.Values, token).ExpandAlgebra(expression);
    }
    private sealed record Cache(string Signature, GeometryValue Value);
    private Dictionary<string, Cache> _cache = new();

    public ConstructionEvaluation Evaluate(ConstructionSnapshot snapshot, CancellationToken token = default)
    {
        AngouriMath.MathS.Multithreading.SetLocalCancellationToken(token);
        ConstructionDocument.Validate(snapshot);
        var watch = Stopwatch.StartNew();
        var definitions = snapshot.Objects.ToDictionary(item => item.Label);
        var values = new Dictionary<string, GeometryValue>();
        var visiting = new HashSet<string>();
        var nextCache = new Dictionary<string, Cache>();
        int evaluated = 0;
        var evaluator = new GeometryEvaluator(snapshot, values, token);
        foreach (var item in snapshot.Objects) Visit(item.Label);
        _cache = nextCache;
        return new(values, evaluated, watch.Elapsed);

        void Visit(string label)
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new OperationCanceledException("构造计算超时，请减小采样范围。", token);
            if (values.ContainsKey(label)) return;
            var item = definitions[label];
            if (!visiting.Add(label)) throw new FormatException("构造依赖存在循环 · " + label);
            try
            {
                string definition = ConstructionDocument.ExpandTool(item.Definition, snapshot);
                var dependencies = Regex.Matches(DependencyText(definition), @"[\p{L}_][\p{L}\d_]*").Select(match => match.Value)
                    .Where(name => definitions.ContainsKey(name)).Distinct().ToArray();
                foreach (string dependency in dependencies) Visit(dependency);
                var invalid = dependencies.FirstOrDefault(name => !values[name].Defined);
                if (invalid is not null) throw new FormatException("依赖对象 " + invalid + " 未定义 · " + values[invalid].Error);
                string signature = definition + "\n" + snapshot.XMin + "," + snapshot.XMax + "," + snapshot.YMin + "," + snapshot.YMax
                    + string.Join("\n", dependencies.Select(name => nextCache.GetValueOrDefault(name)?.Signature));
                signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
                GeometryValue value;
                if (_cache.TryGetValue(label, out var cached) && cached.Signature == signature) value = cached.Value;
                else { value = evaluator.Evaluate(definition); evaluated++; }
                values[label] = value;
                nextCache[label] = new(signature, value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                values[label] = new() { Kind = GeometryKind.Text, Error = ex.GetBaseException().Message, Display = "未定义" };
                nextCache[label] = new(item.Definition + "!" + ex.Message, values[label]);
            }
            finally { visiting.Remove(label); }
        }
    }
}
