using System.Numerics;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Platform.Windows;

namespace InkCanvas.Ink;

public sealed partial class InkController
{
    public bool ShapeEditEnabled { get; private set; }
    private uint? _shapeEditPointer;
    private int _shapeControlIndex;
    private ShapeSource _shapeEditSource;
    private List<InkStroke> _shapeEditBefore;
    private bool _shapeControlMoved;
    private bool _hasShapeConnections;

    private ShapeSource SingleSelectedShape
    {
        get
        {
            if (Selection.Count == 0 || SelectedElements.Count != 0) return null;
            var source = Selection[0].ShapeSource;
            return source?.Geometry is not null && Selection.All(s => s.ShapeSource?.GroupId == source.GroupId) ? source : null;
        }
    }

    public bool CanEditShape => SingleSelectedShape is not null;
    public bool CanFillShapes => Selection.Any(s => s.ShapeSource?.Geometry?.CanFill == true);
    public bool HasEditableShapes => Selection.Any(s => s.ShapeSource?.Geometry is not null);
    public bool HasConnectedShapes => Selection.Any(s => s.ShapeSource?.Geometry is { Construction: ShapeConstruction.Connector, StartNode: not null }
        or { Construction: ShapeConstruction.Connector, EndNode: not null });
    public IReadOnlyList<Vector2> ShapeControlPoints => ShapeEditEnabled ? SingleSelectedShape?.Geometry.ControlPoints() ?? Array.Empty<Vector2>() : Array.Empty<Vector2>();

    private ShapeSource[] SelectedShapeNodes() => Selection.Where(s => s.ShapeSource?.Geometry is { Construction: not ShapeConstruction.Connector })
        .Select(s => s.ShapeSource).DistinctBy(source => source.GroupId).ToArray();
    public bool CanConnectShapes => SelectedElements.Count == 0 && SelectedShapeNodes().Length == 2;

    public void ToggleShapeEditing()
    {
        ShapeEditEnabled = !ShapeEditEnabled && CanEditShape;
        SelectionChanged?.Invoke(); Invalidated?.Invoke();
    }

    public void SetShapeFill(InkColor? color) => ChangeShapeDefinitions("shape fill", data => data.CanFill ? data with { FillColor = color?.PackedArgb } : data);
    public void SetShapeLineStyle(ShapeLineStyle style)
    {
        if (!Enum.IsDefined(style)) return;
        ChangeShapeDefinitions("shape line style", data => data with { LineStyle = style });
    }
    public void DetachShapeConnections() => ChangeShapeDefinitions("detach connector", data => data with { StartNode = null, EndNode = null });

    private void ChangeShapeDefinitions(string name, Func<ShapeDefinition, ShapeDefinition> update)
    {
        var changes = Selection.Where(s => s.ShapeSource?.Geometry is not null).GroupBy(s => s.ShapeSource.GroupId)
            .Select(group =>
            {
                var before = Document.Strokes.Where(s => s.ShapeSource?.GroupId == group.Key).ToList();
                var outline = before.FirstOrDefault(s => s.ShapeSource.Geometry.IsFill == false) ?? before[0];
                var data = update(outline.ShapeSource.Geometry);
                var after = data.Generate(outline.Attributes, group.Key);
                foreach (var stroke in after) stroke.LayerOrder = before.Min(s => s.LayerOrder);
                return (Id: group.Key, Before: before, After: after);
            }).Where(change => change.After.Count > 0).ToArray();
        if (changes.Length == 0) return;
        Apply(true);
        History.Push(new DelegateAction(name, () => Apply(true), () => Apply(false)));
        void Apply(bool after)
        {
            using (Document.DeferNotifications())
                foreach (var change in changes) ReplaceShapeMembers(change.Id, after ? change.After : change.Before);
            SelectionChanged?.Invoke(); Invalidated?.Invoke();
        }
    }

    private void ReplaceShapeMembers(Guid groupId, IReadOnlyList<InkStroke> replacement)
    {
        var result = new List<InkStroke>(); bool inserted = false;
        foreach (var stroke in Document.Strokes)
        {
            if (stroke.ShapeSource?.GroupId != groupId) { result.Add(stroke); continue; }
            if (!inserted) { result.AddRange(replacement); inserted = true; }
        }
        bool selected = Selection.RemoveAll(s => s.ShapeSource?.GroupId == groupId) > 0;
        if (selected) Selection.AddRange(replacement);
        Document.ReplaceAll(result);
    }

    private bool BeginShapeControlDrag(in OverlayPointer pointer)
    {
        var points = ShapeControlPoints;
        int index = -1;
        float nearest = 14 * 14;
        for (int i = 0; i < points.Count; i++)
        {
            float distance = Vector2.DistanceSquared(Vector2.Transform(points[i], ViewTransform), new(pointer.X, pointer.Y));
            if (distance > nearest) continue;
            nearest = distance; index = i;
        }
        if (index < 0) return false;
        _shapeEditPointer = pointer.Id; _shapeControlIndex = index; _shapeEditSource = SingleSelectedShape;
        _shapeControlMoved = false;
        _shapeEditBefore = Document.Strokes.Where(s => s.ShapeSource?.GroupId == _shapeEditSource.GroupId).ToList();
        return true;
    }

    private void UpdateShapeControlDrag(in OverlayPointer pointer, bool finish, bool canceled = false)
    {
        if (_shapeEditPointer != pointer.Id) return;
        var source = _shapeEditSource;
        var before = _shapeEditBefore;
        var original = source.Geometry.ControlPoints()[_shapeControlIndex];
        _shapeControlMoved |= Vector2.DistanceSquared(Vector2.Transform(original, ViewTransform), new(pointer.X, pointer.Y)) >= 1;
        if (!_shapeControlMoved && !canceled)
        {
            if (finish) { _shapeEditPointer = null; _shapeEditBefore = null; _shapeEditSource = null; }
            return;
        }
        var data = source.Geometry.MoveControlPoint(_shapeControlIndex, ToWorld(pointer));
        if (data.Construction == ShapeConstruction.Connector)
            data = _shapeControlIndex == 0 ? data with { StartNode = null } : data with { EndNode = null };
        if (canceled) ReplaceShapeMembers(source.GroupId, before);
        else
        {
            var outline = before.FirstOrDefault(s => !s.ShapeSource.Geometry.IsFill) ?? before[0];
            var after = data.Generate(outline.Attributes, source.GroupId);
            foreach (var stroke in after) stroke.LayerOrder = before.Min(s => s.LayerOrder);
            if (after.Count > 0) ReplaceShapeMembers(source.GroupId, after);
        }
        if (finish)
        {
            var after = Document.Strokes.Where(s => s.ShapeSource?.GroupId == source.GroupId).ToList();
            _shapeEditPointer = null; _shapeEditBefore = null; _shapeEditSource = null;
            if (!canceled && !before.SequenceEqual(after))
                History.Push(new DelegateAction("shape control point", () => Restore(after), () => Restore(before)));
        }
        SelectionChanged?.Invoke(); Invalidated?.Invoke();
        void Restore(List<InkStroke> strokes)
        {
            ReplaceShapeMembers(source.GroupId, strokes);
            SelectionChanged?.Invoke(); Invalidated?.Invoke();
        }
    }

    public bool ConnectSelectedShapes()
    {
        if (!CanConnectShapes) return false;
        var nodes = SelectedShapeNodes();
        var bounds = nodes.Select(node => NodeBounds(node.GroupId)).ToArray();
        var definition = new ShapeDefinition
        {
            Kind = ShapeKind.Arrow, Construction = ShapeConstruction.Connector,
            Parameters = new[] { bounds[0].Center.X, bounds[0].Center.Y, bounds[1].Center.X, bounds[1].Center.Y },
            StartNode = nodes[0].GroupId, EndNode = nodes[1].GroupId,
        };
        var strokes = definition.Generate(ShapeAttributes(), Guid.NewGuid());
        long layer = NextLayerOrder(); foreach (var stroke in strokes) stroke.LayerOrder = layer;
        Document.AddRange(strokes);
        History.Push(new DelegateAction("connect shapes", () => Document.AddRange(strokes), () => Document.RemoveAll(strokes)));
        SelectionChanged?.Invoke(); Invalidated?.Invoke(); return true;
    }

    private Rect NodeBounds(Guid id)
    {
        var bounds = Rect.Empty;
        foreach (var stroke in Document.Strokes)
            if (stroke.ShapeSource?.GroupId == id) bounds = bounds.Union(stroke.Bounds);
        return bounds;
    }

    // Called before document observers render. Existing connector strokes retain their identity for undo.
    private void RefreshShapeConnections()
    {
        var connectors = Document.Strokes.Where(s => s.ShapeSource?.Geometry?.Construction == ShapeConstruction.Connector).ToArray();
        _hasShapeConnections = connectors.Any(s => s.ShapeSource.Geometry.StartNode.HasValue || s.ShapeSource.Geometry.EndNode.HasValue);
        if (connectors.Length == 0) return;
        var bounds = new Dictionary<Guid, Rect>();
        var outlines = new Dictionary<Guid, List<InkStroke>>();
        foreach (var stroke in Document.Strokes)
            if (stroke.ShapeSource is { } source && source.Geometry is { Construction: not ShapeConstruction.Connector })
            {
                bounds[source.GroupId] = bounds.GetValueOrDefault(source.GroupId, Rect.Empty).Union(stroke.Bounds);
                if (!outlines.TryGetValue(source.GroupId, out var members)) outlines[source.GroupId] = members = new();
                if (!source.Geometry.IsFill) members.Add(stroke);
            }
        foreach (var connector in connectors)
        {
            var data = connector.ShapeSource.Geometry;
            var points = data.ControlPoints();
            var start = data.StartNode is { } startId ? bounds.GetValueOrDefault(startId, Rect.Empty) : Rect.Empty;
            var end = data.EndNode is { } endId ? bounds.GetValueOrDefault(endId, Rect.Empty) : Rect.Empty;
            var from = start.IsEmpty ? points[0] : start.Center;
            var to = end.IsEmpty ? points[1] : end.Center;
            var a = start.IsEmpty ? from : Port(data.StartNode.Value, start, to);
            var b = end.IsEmpty ? to : Port(data.EndNode.Value, end, from);
            if (Vector2.DistanceSquared(a, points[0]) < .0001f && Vector2.DistanceSquared(b, points[1]) < .0001f) continue;
            var next = data with { Parameters = new[] { a.X, a.Y, b.X, b.Y }, Transform = ShapeDefinition.Write(Matrix3x2.Identity) };
            var regenerated = next.Generate(connector.Attributes, connector.ShapeSource.GroupId);
            if (regenerated.Count != 1) continue;
            connector.RestorePoints(regenerated[0].Points); connector.ShapeSource = regenerated[0].ShapeSource;
        }
        Vector2 Port(Guid id, Rect bounds, Vector2 toward)
        {
            var delta = toward - bounds.Center;
            float furthest = 0;
            foreach (var stroke in outlines[id])
                for (int i = 1; i < stroke.Count; i++)
                {
                    var edge = stroke.Points[i].Position - stroke.Points[i - 1].Position;
                    float denominator = Cross(delta, edge);
                    if (MathF.Abs(denominator) < .00001f) continue;
                    var offset = stroke.Points[i - 1].Position - bounds.Center;
                    float t = Cross(offset, edge) / denominator, u = Cross(offset, delta) / denominator;
                    if (t > furthest && t <= 1 && u >= 0 && u <= 1) furthest = t;
                }
            if (furthest > 0) return bounds.Center + delta * furthest;
            float factor = MathF.Max(MathF.Abs(delta.X) / MathF.Max(.01f, bounds.Width / 2), MathF.Abs(delta.Y) / MathF.Max(.01f, bounds.Height / 2));
            return factor > .0001f ? bounds.Center + delta / factor : bounds.Center;
        }
        static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    }
}
