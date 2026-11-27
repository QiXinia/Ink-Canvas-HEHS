using System.Numerics;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Recognition;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Shapes;

namespace InkCanvas.Ink;

public sealed partial class InkController
{
    private sealed record HeldShape(RecognitionResult Result, List<InkStroke> Sources, Vector2 Anchor,
        StrokeAttributes Attributes, List<InkStroke> Preview);
    private readonly Dictionary<uint, HeldShape> _heldShapes = new();
    private readonly Dictionary<uint, float> _shapeHoldElapsed = new();
    private readonly HashSet<uint> _rejectedHolds = new();
    public InkToShapeSettings RecognitionOptions { get; set; } = new();
    public IReadOnlyList<InkStroke> RecognitionPreview { get; private set; } = Array.Empty<InkStroke>();
    public long RecognitionSourcesVersion { get; private set; }
    public event Action ShapeHoldChanged;
    public bool HasPendingShapeHold => RecognizeShapes && Tool == InkTool.Pen
        && _live.Keys.Any(id => !_heldShapes.ContainsKey(id) && !_rejectedHolds.Contains(id));

    public bool IsRecognitionPreviewSource(InkStroke stroke)
    {
        foreach (var held in _heldShapes.Values) if (held.Sources.Contains(stroke)) return true;
        return false;
    }

    public void AdvanceShapeHold(float milliseconds)
    {
        if (!HasPendingShapeHold || !float.IsFinite(milliseconds) || milliseconds <= 0) return;
        foreach (var pair in _live)
        {
            if (_heldShapes.ContainsKey(pair.Key) || _rejectedHolds.Contains(pair.Key)) continue;
            float elapsed = _shapeHoldElapsed.GetValueOrDefault(pair.Key) + milliseconds;
            _shapeHoldElapsed[pair.Key] = elapsed;
            if (elapsed < Math.Clamp(RecognitionOptions.HoldMilliseconds, 300, 1500)) continue;
            if (TryFitHeldShape(pair.Value, out var result, out var sources))
            {
                var attrs = pair.Value.Attributes.Clone(); attrs.TaperKind = TaperKind.None;
                var anchor = pair.Value.Points[^1].Position;
                _heldShapes[pair.Key] = new(result, sources, anchor, attrs, GenerateRecognized(result, attrs));
                RecognitionSourcesVersion++;
                RefreshRecognitionPreview();
            }
            else _rejectedHolds.Add(pair.Key);
        }
        ShapeHoldChanged?.Invoke();
    }

    private void ResetShapeHold(uint id)
    {
        bool starting = !_shapeHoldElapsed.ContainsKey(id);
        bool retry = _rejectedHolds.Remove(id);
        _shapeHoldElapsed[id] = 0;
        if (starting || retry) ShapeHoldChanged?.Invoke();
    }

    private HeldShape RemoveShapeHold(uint id)
    {
        _shapeHoldElapsed.Remove(id); _rejectedHolds.Remove(id);
        _heldShapes.Remove(id, out var held);
        if (held is not null)
        {
            RecognitionSourcesVersion++;
            RecognitionPreview = _heldShapes.Values.SelectMany(item => item.Preview).ToArray();
        }
        ShapeHoldChanged?.Invoke();
        return held;
    }

    private void RefreshRecognitionPreview()
    {
        RecognitionPreview = _heldShapes.Values.SelectMany(held => held.Preview).ToArray();
        Invalidated?.Invoke();
    }

    private void ClearShapeHolds()
    {
        bool hadPreview = _heldShapes.Count > 0;
        _heldShapes.Clear(); _shapeHoldElapsed.Clear(); _rejectedHolds.Clear();
        RecognitionPreview = Array.Empty<InkStroke>();
        if (hadPreview) RecognitionSourcesVersion++;
        ShapeHoldChanged?.Invoke();
    }

    private void RefineHeldShape(uint id, Vector2 position)
    {
        var held = _heldShapes[id];
        var result = held.Result;
        var corners = result.Corners.ToArray();
        var offset = position - held.Anchor;
        if (result.Shape == RecognizedShape.Line)
        {
            int end = Vector2.DistanceSquared(corners[0], held.Anchor) < Vector2.DistanceSquared(corners[1], held.Anchor) ? 0 : 1;
            corners[end] += offset;
            result = result with { Corners = corners };
        }
        else if (result.Shape is RecognizedShape.Circle or RecognizedShape.Ellipse)
        {
            float original = Vector2.Distance(held.Anchor, result.Center);
            float ratio = Math.Clamp(Vector2.Distance(position, result.Center) / MathF.Max(1, original), .1f, 10);
            result = result with { RadiusX = result.RadiusX * ratio, RadiusY = result.RadiusY * ratio };
        }
        else if (result.Shape is RecognizedShape.Triangle or RecognizedShape.Polygon)
        {
            int nearest = Enumerable.Range(0, corners.Length).MinBy(i => Vector2.DistanceSquared(corners[i], held.Anchor));
            corners[nearest] += offset;
            if (result.Shape == RecognizedShape.Polygon && !PolygonGeometry.IsSimple(corners)) return;
            result = result with { Corners = corners };
        }
        else if (result.Shape is RecognizedShape.Rectangle or RecognizedShape.Parallelogram)
        {
            int nearest = Enumerable.Range(0, 4).MinBy(i => Vector2.DistanceSquared(corners[i], held.Anchor));
            int opposite = (nearest + 2) % 4;
            var pivot = corners[opposite];
            var u = corners[(opposite + 1) % 4] - pivot;
            var v = corners[(opposite + 3) % 4] - pivot;
            var target = corners[nearest] + offset - pivot;
            float determinant = u.X * v.Y - u.Y * v.X;
            float x = Math.Clamp((target.X * v.Y - target.Y * v.X) / determinant, .1f, 10);
            float y = Math.Clamp((u.X * target.Y - u.Y * target.X) / determinant, .1f, 10);
            corners[(opposite + 1) % 4] = pivot + u * x;
            corners[(opposite + 3) % 4] = pivot + v * y;
            corners[nearest] = pivot + u * x + v * y;
            result = result with { Corners = corners, Center = (pivot + corners[nearest]) / 2 };
        }
        else
        {
            // Resize the fitted construction around its opposite corner while preserving its constraints.
            var bounds = corners.Aggregate(Rect.Empty, (rect, p) => rect.Union(new(p.X - .01f, p.Y - .01f, p.X + .01f, p.Y + .01f)));
            var pivot = new Vector2(held.Anchor.X > bounds.Center.X ? bounds.Left : bounds.Right,
                held.Anchor.Y > bounds.Center.Y ? bounds.Top : bounds.Bottom);
            var before = held.Anchor - pivot; var after = position - pivot;
            float scale = Math.Clamp(Vector2.Dot(after, before) / MathF.Max(1, before.LengthSquared()), .1f, 10);
            for (int i = 0; i < corners.Length; i++) corners[i] = pivot + (corners[i] - pivot) * scale;
            result = result with { Corners = corners, Center = pivot + (result.Center - pivot) * scale };
        }
        _heldShapes[id] = held with { Preview = GenerateRecognized(result, held.Attributes) };
        RefreshRecognitionPreview();
    }

    private bool TryFitHeldShape(InkStroke stroke, out RecognitionResult result, out List<InkStroke> sources)
    {
        sources = new() { stroke };
        result = ShapeRecognizer.RecognizeHeld(sources);
        if (result.Shape is RecognizedShape.None or RecognizedShape.Line)
        {
            var recent = _recentForRecognition.Where(s => s.InkLayerIndex == stroke.InkLayerIndex && Document.Strokes.Contains(s) && !s.IsRecognizedShape
                && s.Attributes.EffectiveColor == stroke.Attributes.EffectiveColor).TakeLast(12).Append(stroke).ToList();
            for (int count = 2; count <= recent.Count; count++)
            {
                var candidates = recent.TakeLast(count).ToList();
                var combined = ShapeRecognizer.RecognizeHeld(candidates);
                if (combined.Shape is not (RecognizedShape.Cube or RecognizedShape.CoordinateAxes)) continue;
                result = combined; sources = candidates; break;
            }
        }
        if (result.Shape is RecognizedShape.None or RecognizedShape.Circle or RecognizedShape.Ellipse
            && FindComplexPolygon(stroke) is { Length: >= 4 } polygon)
        {
            result = new(RecognizedShape.Polygon, Vector2.Zero, 0, 0, .9f, polygon);
        }
        return result.Shape switch
        {
            RecognizedShape.Line => !stroke.Attributes.IsHighlighter,
            RecognizedShape.Circle or RecognizedShape.Ellipse => RecognitionOptions.RecognizeRoundShapes,
            RecognizedShape.Triangle or RecognizedShape.Rectangle or RecognizedShape.Parallelogram or RecognizedShape.Polygon => RecognitionOptions.RecognizePolygons,
            RecognizedShape.Cube => RecognitionOptions.RecognizeCube,
            RecognizedShape.CoordinateAxes => RecognitionOptions.RecognizeAxes,
            _ => false,
        };
    }

    private Vector2[] FindComplexPolygon(InkStroke stroke) => RecognitionOptions.RecognizePolygons && !stroke.Attributes.IsHighlighter
        ? PolygonExtension?.RecognizeClosedStroke(stroke, MathF.Sqrt(MathF.Abs(ViewTransform.GetDeterminant()))) : null;

    private Vector2 AlignDirection(Vector2 start, Vector2 end)
    {
        if (!RecognitionOptions.AutoAlign) return end;
        var delta = end - start;
        var screenDelta = Vector2.TransformNormal(delta, ViewTransform);
        float angle = MathF.Atan2(screenDelta.Y, screenDelta.X);
        float aligned = MathF.Round(angle / (MathF.PI / 4)) * MathF.PI / 4;
        if (MathF.Abs(angle - aligned) > Math.Clamp(RecognitionOptions.AlignmentAngle, 1, 12) * MathF.PI / 180) return end;
        var snapped = new Vector2(MathF.Cos(aligned), MathF.Sin(aligned)) * screenDelta.Length();
        return Matrix3x2.Invert(ViewTransform, out var inverse) ? start + Vector2.TransformNormal(snapped, inverse) : end;
    }

    private List<InkStroke> GenerateRecognized(RecognitionResult result, StrokeAttributes attrs)
    {
        var corners = result.Corners;
        List<InkStroke> shapes;
        if (result.Shape == RecognizedShape.Circle)
            shapes = ShapeGenerator.Generate(ShapeKind.CircleFromCenter, result.Center,
                result.Center + new Vector2(MathF.Max(result.RadiusX, result.RadiusY), 0), attrs);
        else if (result.Shape == RecognizedShape.Ellipse)
            shapes = ShapeGenerator.Generate(ShapeKind.Ellipse, result.Center - new Vector2(result.RadiusX, result.RadiusY),
                result.Center + new Vector2(result.RadiusX, result.RadiusY), attrs);
        else if (result.Shape == RecognizedShape.Line)
            shapes = ShapeGenerator.Generate(ShapeKind.Line, corners[0], AlignDirection(corners[0], corners[1]), attrs);
        else if (result.Shape == RecognizedShape.Cube)
        {
            shapes = new();
            for (int mask = 0; mask < 8; mask++)
            for (int axis = 0; axis < 3; axis++)
                if ((mask & (1 << axis)) == 0) shapes.Add(ShapeGenerator.Stroke(attrs, corners[mask], corners[mask | (1 << axis)]));
        }
        else if (result.Shape == RecognizedShape.CoordinateAxes)
        {
            shapes = new();
            for (int i = 0; i < 4; i += 2)
                shapes.AddRange(ShapeGenerator.Generate(ShapeKind.Arrow, corners[i], AlignDirection(corners[i], corners[i + 1]), attrs));
        }
        else
        {
            if (result.Shape is RecognizedShape.Rectangle or RecognizedShape.Parallelogram && RecognitionOptions.AutoAlign)
            {
                var edge = corners[1] - corners[0];
                var aligned = AlignDirection(Vector2.Zero, edge);
                float angle = MathF.Atan2(aligned.Y, aligned.X) - MathF.Atan2(edge.Y, edge.X);
                var rotate = Matrix3x2.CreateRotation(angle, result.Center);
                corners = corners.Select(p => Vector2.Transform(p, rotate)).ToArray();
            }
            shapes = new() { ShapeGenerator.Stroke(attrs, corners.Append(corners[0]).ToArray()) };
        }
        ShapeSource group;
        if (result.Shape is RecognizedShape.Triangle or RecognizedShape.Rectangle or RecognizedShape.Parallelogram or RecognizedShape.Polygon)
        {
            var kind = result.Shape == RecognizedShape.Triangle ? ShapeKind.Triangle
                : result.Shape == RecognizedShape.Parallelogram ? ShapeKind.Parallelogram
                : result.Shape == RecognizedShape.Polygon ? ShapeKind.Polygon : ShapeKind.Rectangle;
            group = ShapeSource.FromGeometry(Guid.NewGuid(), ShapeDefinition.FromPolygon(kind, corners));
        }
        else group = shapes.FirstOrDefault()?.ShapeSource?.Geometry is not null && shapes.Select(s => s.ShapeSource?.GroupId).Distinct().Count() == 1
            ? shapes[0].ShapeSource : new ShapeSource(Guid.NewGuid(), "ink-to-shape", result.Shape.ToString());
        foreach (var shape in shapes)
        {
            shape.IsRecognizedShape = true; shape.ShapeSource = group; shape.Complete();
        }
        return shapes;
    }

    private void TryRecognize(List<InkStroke> committed, bool endpointHeld, HeldShape held)
    {
        var stroke = committed[0];
        if (held is null && endpointHeld && !stroke.Attributes.IsHighlighter
            && TryFitHeldShape(stroke, out var result, out var sources))
        {
            var attrs = stroke.Attributes.Clone(); attrs.TaperKind = TaperKind.None;
            held = new(result, sources, stroke.Points[^1].Position, attrs, GenerateRecognized(result, attrs));
        }
        if (held is null && !endpointHeld && !stroke.Attributes.IsHighlighter)
        {
            // Preserve the existing immediate recognition of closed round shapes.
            var round = ShapeRecognizer.Recognize(committed);
            bool enabled = RecognitionOptions.RecognizeRoundShapes && round.Shape is RecognizedShape.Circle or RecognizedShape.Ellipse;
            if (enabled && FindComplexPolygon(stroke) is not { Length: >= 4 })
            {
                var attrs = stroke.Attributes.Clone(); attrs.TaperKind = TaperKind.None;
                held = new(round, committed, stroke.Points[^1].Position, attrs, GenerateRecognized(round, attrs));
            }
        }
        if (held is not null && held.Sources.All(Document.Strokes.Contains))
        {
            var removed = held.Sources; var replacement = held.Preview;
            long layer = NextLayerOrder();
            foreach (var shape in replacement)
            {
                shape.InkLayerIndex = stroke.InkLayerIndex;
                shape.LayerOrder = layer++;
            }
            Replace(removed, replacement);
            History.Push(new DelegateAction("recognize",
                () => Replace(removed, replacement), () => Replace(replacement, removed)));
            _recentForRecognition.Clear();

            void Replace(List<InkStroke> before, List<InkStroke> after)
            {
                using (Document.DeferNotifications())
                {
                    Document.RemoveAll(before); Document.AddRange(after);
                }
            }
        }
        else
        {
            _recentForRecognition.AddRange(committed);
            while (_recentForRecognition.Count > 12) _recentForRecognition.RemoveAt(0);
        }
    }
}
