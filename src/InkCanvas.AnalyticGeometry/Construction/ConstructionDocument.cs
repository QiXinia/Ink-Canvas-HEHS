using System.Diagnostics;
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
    public int EvaluationRevision { get; private set; }
    public bool IsGestureActive => _gesture is not null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action Changed;
    public event Action EvaluationChanged;
    public event Action ObjectsChanged;
    public event Action ViewChanged;
    public event Action UndoPointStored;
    public event Action<string> HistoryRestored;

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
            if (command.Equals("Rename", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Length != 2 || !MathSyntax.Identifier(arguments[1]) || arguments[1].Length > 40
                    || objects.Any(item => item.Label == arguments[1]))
                    throw new FormatException("Rename 需要已有对象和未被使用的新名称。");
                string oldName = arguments[0], newName = arguments[1];
                var renamed = objects.FirstOrDefault(item => item.Label == oldName)
                    ?? throw new FormatException("未找到对象 · " + oldName);
                bool callable = MathSyntax.Call(renamed.Definition, out var renamedCommand, out _)
                    && renamedCommand.Equals("Function", StringComparison.OrdinalIgnoreCase);
                string pattern = @"""(?:\\.|[^""\\])*""|(?<![\p{L}\d_])" + Regex.Escape(oldName)
                    + @"(?![\p{L}\d_])" + (callable ? "" : @"(?!\s*[\(\[])");
                objects = objects.Select(item => item with
                {
                    Label = item.Label == oldName ? newName : item.Label,
                    Definition = Regex.Replace(item.Definition, pattern, match => match.Value == oldName ? newName : match.Value),
                    VisibilityCondition = Regex.Replace(item.VisibilityCondition, pattern, match => match.Value == oldName ? newName : match.Value),
                }).ToList();
                last = newName;
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
        if (objects.Count > 10_000) throw new InvalidOperationException("单个工作区最多 10000 个对象。");
        var listeners = State.AppletListeners;
        if (listeners.Any(listener => listener.ObjectId is not null))
        {
            var liveIds = objects.Select(item => item.Id).ToHashSet();
            listeners = listeners.Where(listener => listener.ObjectId is not Guid id || liveIds.Contains(id)).ToList();
        }
        Commit(State with { Objects = objects, Tools = tools, AppletListeners = listeners });
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
        int index = State.Objects.FindIndex(item => item.Label == label);
        if (index < 0) throw new FormatException("未找到对象 · " + label);
        var previous = State.Objects[index];
        var updated = update(previous);
        if (previous == updated) return;
        var objects = State.Objects.ToList();
        objects[index] = updated;
        Commit(State with { Objects = objects });
    }
    public void SetView(ConstructionSnapshot view)
        => Commit(view with
        {
            Objects = State.Objects, Tools = State.Tools, EventScripts = State.EventScripts,
            AppletScript = State.AppletScript, AppletListeners = State.AppletListeners,
        });
    public void BeginGesture() => _gesture ??= State;
    public void EndGesture()
    {
        if (_gesture is null) return;
        bool changed = !ReferenceEquals(State, _gesture);
        if (changed) { _undo.Push(_gesture); _redo.Clear(); }
        _gesture = null;
        if (changed) UndoPointStored?.Invoke();
    }
    private void Commit(ConstructionSnapshot state)
    {
        Validate(state);
        bool storeUndo = _gesture is null;
        if (storeUndo) { _undo.Push(State); _redo.Clear(); }
        var previous = State;
        bool evaluationChanged = DefinitionsChanged(previous, state);
        bool objectsChanged = !ReferenceEquals(previous.Objects, state.Objects) && !evaluationChanged;
        bool viewChanged = ViewStateChanged(previous, state);
        State = state;
        Revision++;
        if (evaluationChanged) EvaluationRevision++;
        Changed?.Invoke();
        if (evaluationChanged) EvaluationChanged?.Invoke();
        else if (objectsChanged) ObjectsChanged?.Invoke();
        if (viewChanged && !evaluationChanged) ViewChanged?.Invoke();
        if (storeUndo) UndoPointStored?.Invoke();
    }
    public void Undo()
    {
        EndGesture();
        if (!_undo.TryPop(out var state)) return;
        _redo.Push(State);
        CommitRestoredState(state);
        HistoryRestored?.Invoke("OnUndo");
    }
    public void Redo()
    {
        if (!_redo.TryPop(out var state)) return;
        _undo.Push(State);
        CommitRestoredState(state);
        HistoryRestored?.Invoke("OnRedo");
    }
    private void CommitRestoredState(ConstructionSnapshot state)
    {
        var previous = State;
        bool evaluationChanged = DefinitionsChanged(previous, state);
        bool objectsChanged = !ReferenceEquals(previous.Objects, state.Objects) && !evaluationChanged;
        bool viewChanged = ViewStateChanged(previous, state);
        State = state;
        Revision++;
        if (evaluationChanged) EvaluationRevision++;
        Changed?.Invoke();
        if (evaluationChanged) EvaluationChanged?.Invoke();
        else if (objectsChanged) ObjectsChanged?.Invoke();
        if (viewChanged && !evaluationChanged) ViewChanged?.Invoke();
    }
    private static bool DefinitionsChanged(ConstructionSnapshot first, ConstructionSnapshot second)
    {
        if (first.Objects.Count != second.Objects.Count || first.Tools.Count != second.Tools.Count) return true;
        if (!ReferenceEquals(first.Objects, second.Objects))
            for (int index = 0; index < first.Objects.Count; index++)
            {
                var before = first.Objects[index]; var after = second.Objects[index];
                if (ReferenceEquals(before, after)) continue;
                if (before.Label != after.Label || before.Definition != after.Definition || before.Fixed != after.Fixed
                    || before.VisibilityCondition != after.VisibilityCondition) return true;
            }
        if (ReferenceEquals(first.Tools, second.Tools)) return false;
        var tools = first.Tools.ToDictionary(item => item.Name, StringComparer.Ordinal);
        return second.Tools.Any(item => !tools.TryGetValue(item.Name, out var previous)
            || !previous.Inputs.SequenceEqual(item.Inputs, StringComparer.Ordinal)
            || !string.Equals(previous.Definition, item.Definition, StringComparison.Ordinal));
    }
    private static bool ViewStateChanged(ConstructionSnapshot first, ConstructionSnapshot second)
        => first.XMin != second.XMin || first.XMax != second.XMax || first.YMin != second.YMin || first.YMax != second.YMax
            || first.Yaw != second.Yaw || first.Pitch != second.Pitch || first.ThreeDimensional != second.ThreeDimensional
            || first.Perspective != second.Perspective || first.Oblique != second.Oblique || first.Anaglyph != second.Anaglyph
            || first.AnaglyphBlue != second.AnaglyphBlue
            || first.PolarGrid != second.PolarGrid || first.Axes != second.Axes || first.Grid != second.Grid;
    public void Load(ConstructionSnapshot state)
    {
        Validate(state); Commit(state);
    }
    public void ApplyEventResult(ConstructionSnapshot state)
    {
        Validate(state);
        CommitRestoredState(state);
    }

    public static void Validate(ConstructionSnapshot state)
    {
        if (state.FormatVersion != 2 || state.Objects is null || state.Objects.Count > 10_000) throw new FormatException("不支持的构造文件版本或对象过多。");
        if (!ValidEventScripts(state.EventScripts)) throw new FormatException("构造事件配置无效。");
        if (!ValidAppletScripts(state)) throw new FormatException("Applet 事件配置无效。");
        if (state.Tools is null || state.Tools.Count > 64 || state.Tools.Any(tool => !MathSyntax.Identifier(tool.Name)
            || tool.Inputs is null || tool.Inputs.Length is < 1 or > 16 || tool.Inputs.Any(input => !MathSyntax.Identifier(input))
            || tool.Inputs.Distinct().Count() != tool.Inputs.Length || tool.Definition is null || tool.Definition.Length > 4096))
            throw new FormatException("自定义工具定义无效。");
        if (state.Objects.Any(item => item is null || !MathSyntax.Identifier(item.Label ?? "") || item.Label.Length > 40
                || item.Definition is null || item.Definition.Length > 4096 || item.VisibilityCondition is null || item.VisibilityCondition.Length > 4096)
            || state.Objects.Select(item => item.Label).Distinct().Count() != state.Objects.Count) throw new FormatException("对象定义或名称无效。");
        if (!double.IsFinite(state.XMin + state.XMax + state.YMin + state.YMax + state.Yaw + state.Pitch)
            || state.XMax - state.XMin < 1e-7 || state.YMax - state.YMin < 1e-7) throw new FormatException("视图坐标无效。");
        if (state.Objects.Any(item => !double.IsFinite(item.LineWidth + item.Opacity + item.SliderMin + item.SliderMax + item.SliderStep + item.ControlX + item.ControlY)
            || item.LineWidth is < .1 or > 30 || item.Opacity is < 0 or > 1 || item.SliderMax <= item.SliderMin || item.SliderStep <= 0
            || item.ControlX is < 0 or > 100_000 || item.ControlY is < 0 or > 100_000
            || item.OnClick is null || item.OnUpdate is null || item.OnChange is null || item.OnMouseOver is null || item.OnMouseOut is null
            || item.OnSelect is null || item.OnDeselect is null || item.OnDragStart is null || item.OnDragEnd is null
            || item.OnClick.Length > 4096 || item.OnUpdate.Length > 4096 || item.OnChange.Length > 4096
            || item.OnMouseOver.Length > 4096 || item.OnMouseOut.Length > 4096 || item.OnSelect.Length > 4096
            || item.OnDeselect.Length > 4096 || item.OnDragStart.Length > 4096 || item.OnDragEnd.Length > 4096))
            throw new FormatException("对象样式、控件位置或事件脚本无效。");
    }
    public static bool IsFreePoint(ConstructionObject item)
    {
        string text = item.Definition.Trim();
        if (!text.StartsWith('(') || !text.EndsWith(')')) return false;
        try { return MathSyntax.Split(text[1..^1]).All(value => ComputerAlgebra.Parse(value).Vars.Count == 0); }
        catch (Exception) { return false; }
    }
    public static string AttachedPath(ConstructionObject item, GeometryValue value)
    {
        if (!value.Defined || value.Kind != GeometryKind.Point || value.AttachmentTarget is null)
            return null;
        if (MathSyntax.Call(item.Definition, out string command, out var arguments)
            && (command.Equals("Point", StringComparison.OrdinalIgnoreCase)
                || command.Equals("PointOnObject", StringComparison.OrdinalIgnoreCase)
                || command.Equals("PointIn", StringComparison.OrdinalIgnoreCase))
            && arguments.Length > 0)
            return arguments[0].Trim();
        return null;
    }

    public static bool IsRegionPoint(ConstructionObject item)
        => MathSyntax.Call(item.Definition, out string command, out _) && command.Equals("PointIn", StringComparison.OrdinalIgnoreCase);
    public static bool IsAttachablePath(GeometryValue value)
        => value.Kind is GeometryKind.Line or GeometryKind.Ray or GeometryKind.Segment or GeometryKind.Circle
            or GeometryKind.Function or GeometryKind.Curve or GeometryKind.Conic or GeometryKind.Polygon or GeometryKind.Region;

    public void AttachPoint(ConstructionEvaluation evaluation, string label, string target, bool inside)
    {
        var item = State.Objects.FirstOrDefault(candidate => candidate.Label == label);
        if (item is null || item.Fixed || !IsFreePoint(item) || !evaluation.Values.TryGetValue(label, out var point)
            || !point.Defined || point.Kind != GeometryKind.Point)
            throw new FormatException("请选择可移动的自由点。");
        if (!evaluation.Values.TryGetValue(target, out var constraint) || !constraint.Defined)
            throw new FormatException("未找到附着对象。");
        if (ConstructionKernel.DependsOn(State, target, label))
            throw new FormatException("此对象依赖当前点，无法建立附着。");

        string definition;
        if (inside)
        {
            if (!GeometryPath.TryGetRegionDefinition(target, constraint, point.Origin, out definition))
                throw new FormatException("请选择圆、椭圆、多边形或平面。");
        }
        else
        {
            if (!IsAttachablePath(constraint) || !GeometryPath.TryGetParameter(constraint, point.Origin, out double parameter))
                throw new FormatException("无法计算该点在对象路径上的位置。");
            definition = $"PointOnObject({target},{ComputerAlgebra.Number(parameter)})";
        }
        Update(label, current => current with { Definition = definition });
    }

    public void DetachPoint(ConstructionEvaluation evaluation, string label)
    {
        var item = State.Objects.FirstOrDefault(candidate => candidate.Label == label);
        if (item is null || item.Fixed || !evaluation.Values.TryGetValue(label, out var point) || AttachedPath(item, point) is null)
            throw new FormatException("请选择可移动的附着点。");

        var position = point.Origin;
        string coordinates = ComputerAlgebra.Number(position.X) + "," + ComputerAlgebra.Number(position.Y);
        if (position.Z != 0) coordinates += "," + ComputerAlgebra.Number(position.Z);
        Update(label, current => current with { Definition = $"({coordinates})" });
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
    public static bool DependsOn(ConstructionSnapshot snapshot, string label, string target)
    {
        var definitions = snapshot.Objects.ToDictionary(item => item.Label, item => item.Definition, StringComparer.Ordinal);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(label);
        while (pending.TryPop(out string current))
        {
            if (current == target) return true;
            if (!visited.Add(current) || !definitions.TryGetValue(current, out string definition)) continue;
            string expanded = SpreadsheetReferences.Expand(ConstructionDocument.ExpandTool(definition, snapshot), definitions.Keys);
            foreach (Match match in Regex.Matches(DependencyText(expanded), @"[\p{L}_][\p{L}\d_]*"))
                if (definitions.ContainsKey(match.Value)) pending.Push(match.Value);
        }
        return false;
    }

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
            case "lineequation": case "conicequation" when args.Length > 0: args[0] = Regex.Replace(args[0], @"\b[xy]\b", "0"); break;
            case "derivative": case "diff": case "limit": Bind(1,1); break;
            case "integral": case "integrate":
                if (args.Length == 2 || args.Length == 4 && args[3] is not ("true" or "false")) Bind(1,1);
                break;
            case "curve": Bind(args.Length >= 6 ? 3 : 2,args.Length >= 6 ? 3 : 2); break;
            case "surface" when args.Length >= 9: Bind(3,3); Bind(6,3); break;
            case "surfacefunction" when args.Length == 3:
                Bind(1,1); Bind(2,1); break;
            case "sequence" when args.Length >= 4: Bind(1,1); break;
            case "product" when args.Length == 4: Bind(1,1); break;
            case "zip": for (int i = 1; i < args.Length; i += 2) Bind(i,1); break;
        }
        return "(" + string.Join(',', args) + ")";
    }
    public static string ResolveExpression(ConstructionSnapshot state, ConstructionEvaluation evaluation, string expression, CancellationToken token = default)
    {
        AngouriMath.MathS.Multithreading.SetLocalCancellationToken(token);
        return new GeometryEvaluator(state, evaluation.Values, token).ExpandAlgebra(expression);
    }
    private sealed record Plan(string SourceDefinition, string Definition, string[] Dependencies, bool ViewDependent, bool SamplingDependent);
    private sealed record Cache(Plan Plan, (double XMin, double XMax, double YMin, double YMax)? ViewBounds,
        bool Interactive, bool Fixed, GeometryValue[] Dependencies, GeometryValue Value);
    private Dictionary<string, Plan> _plans = new(StringComparer.Ordinal);
    private Dictionary<string, Cache> _cache = new(StringComparer.Ordinal);
    private string _toolSignature;
    private List<string> _order;
    private HashSet<string> _cyclic;

    public static bool HasViewDependentObjects(ConstructionSnapshot snapshot)
        => snapshot.Objects.Any(item => UsesView(ConstructionDocument.ExpandTool(item.Definition, snapshot)));

    public ConstructionEvaluation Evaluate(ConstructionSnapshot snapshot, CancellationToken token = default, bool interactive = false)
    {
        AngouriMath.MathS.Multithreading.SetLocalCancellationToken(token);
        ConstructionDocument.Validate(snapshot);
        var watch = Stopwatch.StartNew();
        var definitions = snapshot.Objects.ToDictionary(item => item.Label);
        var toolSignature = string.Join("\n", snapshot.Tools.Select(tool =>
            tool.Name + "|" + string.Join(",", tool.Inputs) + "|" + tool.Definition));
        bool toolsChanged = !string.Equals(toolSignature, _toolSignature, StringComparison.Ordinal);
        bool labelsChanged = definitions.Count != _plans.Count || definitions.Keys.Any(label => !_plans.ContainsKey(label));
        bool graphChanged = labelsChanged;
        var plans = new Dictionary<string, Plan>(definitions.Count, StringComparer.Ordinal);
        foreach (var item in snapshot.Objects)
        {
            token.ThrowIfCancellationRequested();
            _plans.TryGetValue(item.Label, out var cachedPlan);
            if (!toolsChanged && !labelsChanged && cachedPlan is not null
                && string.Equals(cachedPlan.SourceDefinition, item.Definition, StringComparison.Ordinal))
            {
                plans.Add(item.Label, cachedPlan);
                continue;
            }

            string definition = SpreadsheetReferences.Expand(ConstructionDocument.ExpandTool(item.Definition, snapshot), definitions.Keys);
            var dependencies = Regex.Matches(DependencyText(definition), @"[\p{L}_][\p{L}\d_]*")
                .Select(match => match.Value)
                .Where(name => definitions.ContainsKey(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            graphChanged |= cachedPlan is null || !dependencies.SequenceEqual(cachedPlan.Dependencies, StringComparer.Ordinal);
            plans.Add(item.Label, new(item.Definition, definition, dependencies, UsesView(definition), UsesSampling(definition)));
        }

        var order = graphChanged || _order is null ? TopologicalOrder(snapshot.Objects, plans, token) : _order;
        var values = new Dictionary<string, GeometryValue>(definitions.Count, StringComparer.Ordinal);
        var nextCache = new Dictionary<string, Cache>(definitions.Count, StringComparer.Ordinal);
        var cyclic = graphChanged || _cyclic is null ? FindCycles(order, plans) : _cyclic;
        var viewBounds = (snapshot.XMin, snapshot.XMax, snapshot.YMin, snapshot.YMax);
        int evaluated = 0;
        var evaluator = new GeometryEvaluator(snapshot, values, token, interactive ? 128 : 512);
        foreach (string label in order)
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(8))
                throw new OperationCanceledException("构造计算超时，请减小采样范围。", token);
            var plan = plans[label];
            (double, double, double, double)? relevantView = plan.ViewDependent ? viewBounds : null;
            bool coarse = interactive && plan.SamplingDependent;
            GeometryValue[] dependencyValues;
            GeometryValue value;
            if (cyclic.Contains(label))
            {
                dependencyValues = Array.Empty<GeometryValue>();
                value = new() { Kind = GeometryKind.Text, Error = "构造依赖存在循环 · " + label, Display = "未定义" };
            }
            else if (_cache.TryGetValue(label, out var cached)
                && (ReferenceEquals(cached.Plan, plan) || (cached.Plan.Definition == plan.Definition
                    && cached.Plan.Dependencies.SequenceEqual(plan.Dependencies, StringComparer.Ordinal)))
                && cached.Fixed == definitions[label].Fixed
                && cached.ViewBounds == relevantView
                && (!cached.Interactive || coarse)
                && SameReferences(cached.Dependencies, plan.Dependencies, values))
            {
                values[label] = cached.Value;
                nextCache[label] = cached;
                continue;
            }
            else
            {
                dependencyValues = plan.Dependencies.Select(name => values[name]).ToArray();
                var invalid = dependencyValues.FirstOrDefault(dependency => !dependency.Defined);
                if (invalid is not null)
                    value = new() { Kind = GeometryKind.Text, Error = "依赖对象未定义 · " + invalid.Error, Display = "未定义" };
                else
                {
                    try { value = evaluator.Evaluate(plan.Definition); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { value = new() { Kind = GeometryKind.Text, Error = ex.GetBaseException().Message, Display = "未定义" }; }
                    evaluated++;
                }
            }
            values[label] = value;
            nextCache[label] = new(plan, relevantView, coarse, definitions[label].Fixed, dependencyValues, value);
        }
        Dictionary<string, bool> visibility = null;
        HashSet<string> hidden = null;
        foreach (var item in snapshot.Objects)
        {
            if (item.VisibilityCondition.Length == 0) continue;
            token.ThrowIfCancellationRequested();
            visibility ??= new(StringComparer.Ordinal);
            if (!visibility.TryGetValue(item.VisibilityCondition, out bool visible))
            {
                try { visible = evaluator.Condition(item.VisibilityCondition); }
                catch (Exception ex) when (ex is not OperationCanceledException) { visible = false; }
                visibility.Add(item.VisibilityCondition, visible);
            }
            if (!visible) (hidden ??= new(StringComparer.Ordinal)).Add(item.Label);
        }

        _cache = nextCache;
        _plans = plans;
        _toolSignature = toolSignature;
        _order = order;
        _cyclic = cyclic;
        return new(values, evaluated, watch.Elapsed) { ConditionallyHidden = hidden };
    }

    private static bool SameReferences(
        IReadOnlyList<GeometryValue> cached, IReadOnlyList<string> dependencies, IReadOnlyDictionary<string, GeometryValue> values)
    {
        if (cached.Count != dependencies.Count) return false;
        for (int index = 0; index < dependencies.Count; index++)
            if (!ReferenceEquals(cached[index], values[dependencies[index]])) return false;
        return true;
    }

    private static bool UsesView(string definition)
    {
        if (MathSyntax.Call(definition, out string command, out _))
            return command.ToLowerInvariant() is "function" or "slopefield" or "root" or "extremum"
                or "inflectionpoint" or "surface" or "surfacefunction" or "conic" or "conicequation" or "sum" or "locus";
        return definition.IndexOfAny(['=', '<', '>']) >= 0
            || Regex.IsMatch(definition, @"(?<![\p{L}\d_])[xy](?![\p{L}\d_])");
    }

    private static bool UsesSampling(string definition)
    {
        if (MathSyntax.Call(definition, out string command, out _))
            return command.ToLowerInvariant() is "function" or "curve" or "polarcurve" or "slopefield" or "surface" or "surfacefunction"
                or "solveode" or "locus" or "envelope" or "revolve" or "sphere" or "cylinder" or "cone"
                or "conic" or "conicequation" or "ellipse" or "hyperbola" or "parabola" or "sum"
                or "length" or "root" or "extremum" or "inflectionpoint" or "constructionoutput" or "intersect" or "intersectpath"
                or "integral" or "integrate" or "integralarea" or "integralbetween" or "derivative" or "diff" or "curvederivative" or "parametricderivative";
        return definition.IndexOfAny(['=', '<', '>']) >= 0
            || Regex.IsMatch(definition, @"(?<![\p{L}\d_])[xy](?![\p{L}\d_])");
    }

    private static List<string> TopologicalOrder(
        IReadOnlyList<ConstructionObject> objects, IReadOnlyDictionary<string, Plan> plans, CancellationToken token)
    {
        var state = new Dictionary<string, byte>(objects.Count, StringComparer.Ordinal);
        var order = new List<string>(objects.Count);
        foreach (var item in objects)
        {
            if (state.ContainsKey(item.Label)) continue;
            var stack = new List<(string Label, int NextDependency)> { (item.Label, 0) };
            state[item.Label] = 1;
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                int last = stack.Count - 1;
                var frame = stack[last];
                var dependencies = plans[frame.Label].Dependencies;
                if (frame.NextDependency < dependencies.Length)
                {
                    string dependency = dependencies[frame.NextDependency];
                    stack[last] = (frame.Label, frame.NextDependency + 1);
                    if (!state.TryGetValue(dependency, out byte dependencyState))
                    {
                        state[dependency] = 1;
                        stack.Add((dependency, 0));
                    }
                    else if (dependencyState == 1)
                    {
                        // Keep the cycle in the dependency graph so each member can surface an error.
                    }
                    continue;
                }
                state[frame.Label] = 2;
                order.Add(frame.Label);
                stack.RemoveAt(last);
            }
        }
        return order;
    }

    private static HashSet<string> FindCycles(IReadOnlyList<string> order, IReadOnlyDictionary<string, Plan> plans)
    {
        var state = new Dictionary<string, byte>(order.Count, StringComparer.Ordinal);
        var cycles = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in order)
        {
            if (state.ContainsKey(root)) continue;
            var stack = new List<(string Label, int NextDependency)> { (root, 0) };
            state[root] = 1;
            while (stack.Count > 0)
            {
                int last = stack.Count - 1;
                var frame = stack[last];
                var dependencies = plans[frame.Label].Dependencies;
                if (frame.NextDependency < dependencies.Length)
                {
                    string dependency = dependencies[frame.NextDependency];
                    stack[last] = (frame.Label, frame.NextDependency + 1);
                    if (!state.TryGetValue(dependency, out byte dependencyState))
                    {
                        state[dependency] = 1;
                        stack.Add((dependency, 0));
                    }
                    else if (dependencyState == 1)
                    {
                        int cycleStart = stack.FindIndex(entry => entry.Label == dependency);
                        for (int index = Math.Max(0, cycleStart); index < stack.Count; index++)
                            cycles.Add(stack[index].Label);
                    }
                    continue;
                }
                state[frame.Label] = 2;
                stack.RemoveAt(last);
            }
        }
        return cycles;
    }
}
