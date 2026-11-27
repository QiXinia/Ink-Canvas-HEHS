using System.Globalization;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry.Construction;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static InkCanvas.Modules.AnalyticGeometry.GeometryToolInput;

namespace InkCanvas.Modules.AnalyticGeometry;

[Flags]
internal enum GeometryToolInput
{
    Point = 1 << 0,
    PathPoint = 1 << 1,
    Segment = 1 << 2,
    InfiniteLine = 1 << 3,
    Vector = 1 << 4,
    Circle = 1 << 5,
    Ellipse = 1 << 6,
    Hyperbola = 1 << 7,
    OtherConic = 1 << 8,
    PolygonRegion = 1 << 9,
    Function = 1 << 10,
    Curve = 1 << 11,
    SizedRegion = 1 << 12,
    Plane = 1 << 13,
    Surface = 1 << 14,
    Solid = 1 << 15,
    Linear = Segment | InfiniteLine,
    CenteredConic = Circle | Ellipse | Hyperbola,
    Conic = CenteredConic | OtherConic,
    FiniteLength = Segment | Vector,
    Region = Circle | Ellipse | PolygonRegion | Plane,
    AreaRegion = Circle | Ellipse | PolygonRegion | SizedRegion,
    ConstraintPath = Linear | Conic | PolygonRegion | Function | Curve | SizedRegion,
    Transformable = Point | ConstraintPath | Vector | Plane | Surface | Solid,
}

public sealed partial class GeometryWorkbenchWindow
{
    private sealed record ToolSelection(ConstructionView.ObjectPick Object, GeoVector Position, GeometryToolInput Kind);
    private readonly List<ToolSelection> _toolSelections = new();
    private Button _finishTool;
    private ContentDialog _toolDialog;
    private GeometryToolInput _toolInput;

    private static GeometryToolInput GetInputKind(GeometryValue value) => value.Kind switch
    {
        GeometryKind.Point => Point | (value.AttachmentTarget is { } path && ConstructionDocument.IsAttachablePath(path)
            ? PathPoint : 0),
        GeometryKind.Segment => Segment,
        GeometryKind.Line or GeometryKind.Ray => InfiniteLine,
        GeometryKind.Vector => Vector,
        GeometryKind.Circle => Circle,
        GeometryKind.Conic => value.Expression switch
        {
            "ellipse" => Ellipse,
            "hyperbola" => Hyperbola,
            _ => OtherConic,
        },
        GeometryKind.Polygon => PolygonRegion,
        GeometryKind.Function => Function,
        GeometryKind.Curve => Curve,
        GeometryKind.Region => SizedRegion,
        GeometryKind.Plane => Plane,
        GeometryKind.Surface => Surface,
        GeometryKind.Solid => Solid,
        _ => 0,
    };

    private IEnumerable<ToolRecipe> MatchingRecipes(DrawingTool tool)
        => tool.Recipes.Where(recipe => _toolSelections.Count < recipe.Picks.Length
            && _toolSelections.Select((item, index) => (recipe.Picks[index] & item.Kind) != 0).All(matches => matches));

    private GeometryToolInput NextToolInput(DrawingTool tool)
        => tool.MinimumPoints > 0 ? Point : MatchingRecipes(tool)
            .Aggregate((GeometryToolInput)0, (accepted, recipe) => accepted | recipe.Picks[_toolSelections.Count]);

    private bool AcceptToolObject(ConstructionView.ObjectPick pick)
    {
        var kind = GetInputKind(pick.Value);
        if ((_toolInput & kind) == 0) return false;
        var tool = DrawingTools[_activeToolIndex];
        if ((tool.Command is AttachDetachTool or "Locus") && pick.Output != 0) return false;
        if (pick.Inside && (tool.Command is "PointOnObject" or AttachDetachTool || (kind & AreaRegion) != 0))
            return GeometryPath.IsAttachableRegion(pick.Value);
        return !pick.Inside;
    }

    private void GraphClicked(GeoVector position, ConstructionView.ObjectPick pick)
    {
        if (_activeToolIndex == 0)
        {
            if (pick is null) return;
            SelectAlgebraItem(pick.Label);
            DispatchObjectEvent(pick.Label, "OnClick");
            return;
        }
        if (_toolDialog is not null) return;
        Guard(() => PickToolInput(position, pick));
    }

    private void PickToolInput(GeoVector position, ConstructionView.ObjectPick pick)
    {
        if (Evaluation is null || _renderedRevision != Document.EvaluationRevision)
            throw new InvalidOperationException("构造正在更新，请稍后重试。");
        var tool = DrawingTools[_activeToolIndex];
        if (tool.Command == AttachDetachTool)
        {
            PickAttachment(pick);
            return;
        }
        if (tool.Command == "PointOnObject")
        {
            if (pick is null) throw new FormatException(tool.Help);
            string definition;
            if (pick.Inside && GeometryPath.TryGetRegionDefinition(pick.Reference, pick.Value, position, out definition))
                Document.Execute(definition);
            else if (!pick.Inside && GeometryPath.TryGetParameter(pick.Value, position, out double parameter))
                Document.Execute($"PointOnObject({pick.Reference},{ComputerAlgebra.Number(parameter)})");
            else throw new FormatException("无法在此位置建立约束点。");
            return;
        }
        if (pick is null && ((_toolInput & Point) == 0 || tool.Command == "Locus")) throw new FormatException(tool.Help);
        if (tool.Command == "Locus" && _toolSelections.Count > 0
            && (Document.State.Objects.Any(item => item.Label == pick.Label && ConstructionDocument.IsRegionPoint(item))
                || !ConstructionKernel.DependsOn(Document.State, _toolSelections[0].Object.Label, pick.Label)))
            throw new FormatException("请选择驱动第一个点的路径点。");
        var kind = pick is null ? Point : GetInputKind(pick.Value);
        var point = (kind & Point) != 0 && pick is not null ? pick.Value.Origin : position;

        if (tool.Command == "Point")
        {
            if (pick is null) Document.Execute(Coordinates(point));
            return;
        }
        if (tool.CloseAtFirst && _toolSelections.Count >= tool.MinimumPoints
            && SamePick(_toolSelections[0], pick, point))
        {
            CompleteTool(tool);
            return;
        }
        if (_toolSelections.Any(item => SamePick(item, pick, point)))
        {
            var alternative = pick is null ? null : _view.PickObject(_view.Project(position),
                candidate => AcceptToolObject(candidate) && !_toolSelections.Any(item => item.Object?.Reference == candidate.Reference));
            if (alternative is null) throw new FormatException("请选择其他点或对象。");
            pick = alternative;
            kind = GetInputKind(pick.Value);
            point = (kind & Point) != 0 ? pick.Value.Origin : position;
        }
        if (tool.Command == "Intersect" && _toolSelections.Count == 0 && pick is not null)
        {
            var secondInputs = tool.Recipes.Where(recipe => (recipe.Picks[0] & kind) != 0)
                .Aggregate((GeometryToolInput)0, (valid, recipe) => valid | recipe.Picks[1]);
            var second = _view.PickObject(_view.Project(position), candidate => candidate.Reference != pick.Reference
                && (GetInputKind(candidate.Value) & secondInputs) != 0, includePoints: false);
            if (second is not null)
            {
                Document.Execute($"Intersect({pick.Reference},{second.Reference},{Coordinates(position)})");
                return;
            }
        }

        var recipe = MatchingRecipes(tool).FirstOrDefault(candidate => candidate.Picks.Length == _toolSelections.Count + 1
            && (candidate.Picks[_toolSelections.Count] & kind) != 0);
        _toolSelections.Add(new(pick, point, kind));
        RefreshToolBanner();
        if (recipe?.Number is { } input) _ = CompleteNumberToolAsync(tool, recipe, input);
        else if (recipe is not null) CompleteTool(tool, recipe);
    }

    private bool SamePick(ToolSelection previous, ConstructionView.ObjectPick current, GeoVector position)
    {
        if (previous.Object is not null) return previous.Object.Reference == current?.Reference;
        return current is null && System.Numerics.Vector2.DistanceSquared(_view.Project(previous.Position), _view.Project(position)) < 144;
    }

    private void ClearToolSelection()
    {
        _toolDialog?.Hide();
        _toolSelections.Clear();
        RefreshToolBanner();
    }

    private void RefreshToolBanner()
    {
        var tool = DrawingTools[_activeToolIndex];
        _toolInput = tool.Command == AttachDetachTool && _toolSelections.Count > 0
            ? ConstraintPath | Region : NextToolInput(tool);
        _view.ShowToolSelection(_toolSelections.Where(item => item.Object is not null).Select(item => item.Object.Label),
            _toolSelections.Where(item => item.Object is null).Select(item => item.Position).ToArray());
        string progress = _toolSelections.Count > 0 ? $" · 已选择 {_toolSelections.Count} 项" : "";
        string help = tool.Command == AttachDetachTool && _toolSelections.Count > 0 ? "请选择对象的路径或区域内部。" : tool.Help;
        if (_toolBannerText is not null) _toolBannerText.Text = $"{tool.Name} · {help}{progress}";
        if (_finishTool is not null)
        {
            _finishTool.Visibility = tool.MinimumPoints > 0 ? Visibility.Visible : Visibility.Collapsed;
            _finishTool.IsEnabled = _toolSelections.Count >= tool.MinimumPoints;
        }
        SetStatus(help + progress);
    }

    private async Task CompleteNumberToolAsync(DrawingTool tool, ToolRecipe recipe, NumberInput input)
    {
        try
        {
            if (_root.XamlRoot is null) throw new InvalidOperationException("工作区尚未就绪。");
            var box = new NumberBox
            {
                Header = input.Name, Value = input.Default, Minimum = input.Minimum, Maximum = input.Maximum,
                MinWidth = 240, MinHeight = 42, ValidationMode = NumberBoxValidationMode.Disabled,
            };
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme, Title = tool.Name, Content = box,
                PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            };
            void UpdateEnabled() => dialog.IsPrimaryButtonEnabled = double.IsFinite(box.Value)
                && box.Value >= input.Minimum && box.Value <= input.Maximum
                && (!input.Integer || box.Value == Math.Truncate(box.Value))
                && (tool.Command != "Dilate" || box.Value != 0);
            box.ValueChanged += (_, _) => UpdateEnabled();
            dialog.PrimaryButtonClick += (_, args) =>
            {
                UpdateEnabled();
                args.Cancel = !dialog.IsPrimaryButtonEnabled;
            };
            UpdateEnabled();
            _toolDialog = dialog;
            int revision = Document.EvaluationRevision;
            var result = await dialog.ShowAsync();
            _toolDialog = null;
            if (!_closed && result == ContentDialogResult.Primary && _toolSelections.Count == recipe.Picks.Length
                && revision == Document.EvaluationRevision && ReferenceEquals(tool, DrawingTools[_activeToolIndex]))
                CompleteTool(tool, recipe, box.Value);
            else if (!_closed) ClearToolSelection();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ClearToolSelection();
                SetStatus(ex.GetBaseException().Message);
            }
        }
        finally { _toolDialog = null; }
    }

    private void CompleteTool(DrawingTool tool, ToolRecipe recipe = null, double? number = null)
    {
        if (tool.Command == "Angle" && _toolSelections[0].Object is { Value.Kind: GeometryKind.Polygon } polygon)
        {
            var angles = Enumerable.Range(1, polygon.Value.Points.Count).Select(index => Measurement(
                $"Angle({polygon.Reference},{index})", [$"PolygonVertex({polygon.Reference},{index})"], "角度"));
            ClearToolSelection();
            Document.Execute(string.Join(';', angles));
            return;
        }
        var definitions = new List<string>();
        var objects = Document.State.Objects.ToList();
        var args = new List<string>();
        int livePoints = tool.Command switch
        {
            "RigidPolygon" => 2,
            "VectorPolygon" => 1,
            _ => _toolSelections.Count,
        };
        for (int i = 0; i < livePoints; i++)
        {
            var item = _toolSelections[i];
            if (item.Object is { } existing) args.Add(existing.Reference);
            else
            {
                string coordinates = Coordinates(item.Position);
                string label = ConstructionDocument.NextLabel(objects, "P");
                objects.Add(new() { Label = label, Definition = coordinates });
                definitions.Add($"{label}={coordinates}");
                args.Add(label);
            }
        }
        if (number is { } parameter) args.Add(ComputerAlgebra.Number(parameter));

        string command = recipe?.Format is { } format
            ? string.Format(CultureInfo.InvariantCulture, format, args.Cast<object>().ToArray())
            : $"{tool.Command}({string.Join(',', args)})";
        command = tool.Command switch
        {
            "FitLine" => $"FitLine({{{string.Join(',', args)}}})",
            "RigidPolygon" => $"RigidPolygon({{{string.Join(',', _toolSelections.Select(item => Coordinates(item.Position)))}}},{args[0]},{args[1]})",
            "VectorPolygon" => $"VectorPolygon({args[0]},{{{string.Join(',', _toolSelections.Skip(1).Select((item, index) =>
                $"Vector({Coordinates(item.Position - _toolSelections[index].Position)})"))}}})",
            "Angle" when number.HasValue => FixedAngle(args, objects, definitions),
            "Angle" => Measurement(command, args, "角度"),
            "Distance" when command.StartsWith("Length(", StringComparison.Ordinal) => Measurement(command, args, "长度"),
            "Distance" when command.StartsWith("Perimeter(", StringComparison.Ordinal) => Measurement(command, args, "周长"),
            "Distance" => Measurement(command, args, "距离"),
            "Area" => Measurement(command, args, "面积"),
            "Slope" => Measurement(command, args, "斜率"),
            _ => command,
        };

        definitions.Add(command);
        ClearToolSelection();
        Document.Execute(string.Join(';', definitions));
    }

    private static string FixedAngle(List<string> args, List<ConstructionObject> objects, List<string> definitions)
    {
        string rotated = ConstructionDocument.NextLabel(objects, "P");
        string radians = $"({args[2]})*pi/180";
        definitions.Add($"{rotated}=Rotate({args[0]},{radians},{args[1]})");
        return Measurement(radians, [args[0], args[1], rotated], "角度");
    }

    private static string Measurement(string expression, IEnumerable<string> sources, string kind)
        => $"Measure({expression},{{{string.Join(',', sources)}}},\"{kind}\")";

    private void PickAttachment(ConstructionView.ObjectPick pick)
    {
        if (pick is null || pick.Output != 0) throw new FormatException("请选择单个可附着的对象。");
        if (_toolSelections.Count > 0)
        {
            Document.AttachPoint(Evaluation, _toolSelections[0].Object.Label, pick.Label, pick.Inside);
            ClearToolSelection();
            return;
        }

        var item = Document.State.Objects.FirstOrDefault(candidate => candidate.Label == pick.Label);
        if (item is null || item.Fixed || pick.Value.Kind != GeometryKind.Point)
            throw new FormatException("请选择可移动的自由点或附着点。");
        if (ConstructionDocument.AttachedPath(item, pick.Value) is not null)
        {
            Document.DetachPoint(Evaluation, pick.Label);
            return;
        }
        if (!ConstructionDocument.IsFreePoint(item)) throw new FormatException("请选择可移动的自由点。");
        _toolSelections.Add(new(pick, pick.Value.Origin, Point));
        RefreshToolBanner();
    }
}
