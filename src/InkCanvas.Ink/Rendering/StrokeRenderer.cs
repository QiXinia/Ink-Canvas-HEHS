using InkCanvas.Core.Ink;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;

namespace InkCanvas.Ink.Rendering;

/// <summary>
/// Draws strokes with Win2D. Each stroke is a Winding-rule union of round-ended segment
/// capsules. This prevents a single stroke that crosses itself from becoming an even/odd
/// boolean hole, and ensures its translucent color is composited only once at overlaps.
/// </summary>
public sealed class StrokeRenderer : IDisposable
{
    private sealed record Entry(InkStroke Stroke, long Version, Matrix3x2 Nib, int PointCount, CanvasGeometry[] Geometry, LinkedListNode<Guid> Node);
    private readonly Dictionary<Guid, Entry> _cache = new();
    private readonly LinkedList<Guid> _usage = new();
    private const int MaxCachedPoints = 100_000;
    private int _cachedPoints;
    private readonly List<Guid> _expired = new();
    private readonly HashSet<InkStroke> _retained = new();
    private readonly CalligraphyStrokeRenderer _calligraphy = new();
    private sealed class LiveEntry
    {
        public InkStroke Stroke;
        public long Version;
        public Matrix3x2 Nib;
        public int Count, StableSegments;
        public List<CanvasGeometry>[] Chunks;
    }
    private readonly Dictionary<Guid, LiveEntry> _liveCache = new();
    public int CachedGeometryCount => _cache.Count + _calligraphy.CachedStrokeCount;
    private long _geometryBuildCount, _segmentBuildCount;
    public long GeometryBuildCount => _geometryBuildCount + _calligraphy.GeometryBuildCount;
    public long SegmentBuildCount => _segmentBuildCount + _calligraphy.SegmentBuildCount;

    /// <summary>Geometry for finished strokes is immutable, so it is safe to cache.</summary>
    public void Draw(CanvasDrawingSession session, InkStroke stroke, Matrix3x2 transform)
    {
        if (stroke is null || stroke.Count == 0) return;
        if (InkBrush.UsesInkWash(stroke))
        {
            _calligraphy.Draw(session, stroke, transform);
            return;
        }

        var color = stroke.Attributes.EffectiveColor;
        var layers = InkBrush.Layers(stroke);

        bool fromCache = stroke.IsCompleted && _cache.TryGetValue(stroke.Id, out var cached)
            && ReferenceEquals(cached.Stroke, stroke) && cached.Version == stroke.GeometryVersion && cached.Nib == stroke.Attributes.NibTransform
            && cached.Geometry.Length == layers.Length;
        CanvasGeometry[] geometry;
        if (fromCache)
        {
            var entry = _cache[stroke.Id];
            _usage.Remove(entry.Node);
            _usage.AddLast(entry.Node);
            geometry = entry.Geometry;
        }
        else
        {
            Invalidate(stroke);
            geometry = !stroke.IsCompleted || _liveCache.ContainsKey(stroke.Id)
                ? BuildLiveGeometry(session, stroke) : BuildGeometry(session, stroke);
            _geometryBuildCount++;
        }
        if (geometry.Length == 0) return;
        if (stroke.IsCompleted) RemoveLive(stroke.Id);

        bool storedInCache = false;
        int cachedPoints = stroke.Count * layers.Length;
        if (!fromCache && stroke.IsCompleted && cachedPoints <= MaxCachedPoints)
        {
            while (_usage.First is not null && (_cachedPoints + cachedPoints > MaxCachedPoints || _cache.Count >= 2048))
                Remove(_usage.First.Value);
            _cache[stroke.Id] = new Entry(stroke, stroke.GeometryVersion, stroke.Attributes.NibTransform, cachedPoints, geometry, _usage.AddLast(stroke.Id));
            _cachedPoints += cachedPoints;
            storedInCache = true;
        }

        var previous = session.Transform;
        try
        {
            session.Transform = transform;
            for (int layer = 0; layer < geometry.Length; layer++)
            {
                var tint = layers[layer].Color(color);
                if (geometry[layer] is not null)
                    session.FillGeometry(geometry[layer], Windows.UI.Color.FromArgb(tint.A, tint.R, tint.G, tint.B));
            }
        }
        finally
        {
            session.Transform = previous;
            if (!fromCache && !storedInCache) foreach (var path in geometry) path.Dispose();
        }
    }

    private CanvasGeometry[] BuildLiveGeometry(CanvasDrawingSession session, InkStroke stroke)
    {
        var layers = InkBrush.Layers(stroke);
        if (_liveCache.TryGetValue(stroke.Id, out var entry)
            && (!ReferenceEquals(entry.Stroke, stroke) || stroke.Count < entry.Count
                || entry.Nib != stroke.Attributes.NibTransform || entry.Chunks.Length != layers.Length
                || stroke.GeometryVersion - entry.Version != stroke.Count - entry.Count))
        { RemoveLive(stroke.Id); entry = null; }
        if (entry is null)
        {
            if (_liveCache.Count >= 16) RemoveLive(_liveCache.Keys.First());
            _liveCache[stroke.Id] = entry = new LiveEntry { Stroke = stroke, Nib = stroke.Attributes.NibTransform,
                Chunks = Enumerable.Range(0, layers.Length).Select(_ => new List<CanvasGeometry>()).ToArray() };
        }
        // Tail taper can revise the last ten points; only freeze complete chunks behind that window.
        int stable = Math.Max(0, stroke.StablePointCount - 1);
        while (stable - entry.StableSegments >= 64)
        {
            for (int layer = 0; layer < layers.Length; layer++)
                entry.Chunks[layer].Add(BuildLayerGeometry(session, stroke, layers[layer].WidthScale, entry.StableSegments, 64));
            entry.StableSegments += 64;
        }
        entry.Count = stroke.Count; entry.Version = stroke.GeometryVersion;
        var geometry = new CanvasGeometry[layers.Length];
        for (int layer = 0; layer < layers.Length; layer++)
        {
            var tail = BuildLayerGeometry(session, stroke, layers[layer].WidthScale, entry.StableSegments);
            var chunks = entry.Chunks[layer];
            if (chunks.Count == 0) { geometry[layer] = tail; continue; }
            var parts = new CanvasGeometry[chunks.Count + (tail is null ? 0 : 1)];
            chunks.CopyTo(parts);
            if (tail is not null) parts[^1] = tail;
            // Composite each layer once, including self-crossings and chunk boundaries.
            try { geometry[layer] = CanvasGeometry.CreateGroup(session, parts, CanvasFilledRegionDetermination.Winding); }
            finally { tail?.Dispose(); }
        }
        return geometry;
    }

    private CanvasGeometry[] BuildGeometry(CanvasDrawingSession session, InkStroke stroke)
    {
        var layers = InkBrush.Layers(stroke);
        var result = new CanvasGeometry[layers.Length];
        for (int layer = 0; layer < layers.Length; layer++) result[layer] = BuildLayerGeometry(session, stroke, layers[layer].WidthScale);
        return result;
    }

    private CanvasGeometry BuildLayerGeometry(CanvasDrawingSession session, InkStroke stroke, float widthScale, int firstSegment = 0, int segmentCount = int.MaxValue)
    {
        if (stroke.Count == 0) return null;
        int endSegment = Math.Max(1, stroke.Count - 1);
        if (firstSegment >= endSegment) return null;
        int lastSegment = (int)Math.Min(endSegment, (long)firstSegment + segmentCount);
        _segmentBuildCount += lastSegment - firstSegment;

        // All capsule contours share positive winding, so one Winding path is their union.
        // One native path per stroke avoids allocating a COM geometry for every sample pair.
        using var builder = new CanvasPathBuilder(session);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        if (stroke.ShapeSource?.Geometry?.IsFill == true)
        {
            builder.BeginFigure(stroke.Points[0].Position);
            foreach (var point in stroke.Points.Skip(1)) builder.AddLine(point.Position);
            builder.EndFigure(CanvasFigureLoop.Closed);
            return CanvasGeometry.CreatePath(builder);
        }
        if (stroke.ShapeSource?.Geometry is { Construction: InkCanvas.Core.Shapes.ShapeConstruction.Connector,
            LineStyle: InkCanvas.Core.Shapes.ShapeLineStyle.Dashed or InkCanvas.Core.Shapes.ShapeLineStyle.Dotted })
        {
            Span<Vector2> points = stackalloc Vector2[26];
            foreach (var path in InkCanvas.Core.Shapes.ShapeDefinition.RenderContours(stroke))
                for (int segment = 0; segment < Math.Max(1, path.Count - 1); segment++)
                {
                    int count = StrokeGeometry.WriteInkSegment(path, segment, widthScale, points);
                    if (count < 3) continue;
                    builder.BeginFigure(points[0]);
                    for (int i = 1; i < count; i++) builder.AddLine(points[i]);
                    builder.EndFigure(CanvasFigureLoop.Closed);
                }
            return CanvasGeometry.CreatePath(builder);
        }
        Span<Vector2> scratch = stackalloc Vector2[26];
        for (int segment = firstSegment; segment < lastSegment; segment++)
        {
            int count = StrokeGeometry.WriteInkSegment(stroke, segment, widthScale, scratch);
            var outline = scratch[..count];
            if (outline.Length < 3) continue;
            builder.BeginFigure(outline[0]);
            for (int i = 1; i < outline.Length; i++) builder.AddLine(outline[i]);
            builder.EndFigure(CanvasFigureLoop.Closed);
        }
        return CanvasGeometry.CreatePath(builder);
    }

    /// <summary>Evict removed or edited strokes while keeping native geometry for unchanged ink.</summary>
    public void Retain(IReadOnlyList<InkStroke> strokes)
    {
        _calligraphy.Retain(strokes);
        if (_cache.Count == 0) return;
        _retained.Clear();
        foreach (var stroke in strokes) _retained.Add(stroke);
        _expired.Clear();
        foreach (var (id, entry) in _cache)
            if (!_retained.Contains(entry.Stroke) || entry.Version != entry.Stroke.GeometryVersion) _expired.Add(id);
        foreach (var id in _expired)
        {
            Remove(id);
        }
        _retained.Clear();
        _expired.Clear();
    }

    public void Invalidate(InkStroke stroke)
    {
        _calligraphy.Invalidate(stroke);
        if (stroke is not null) Remove(stroke.Id);
    }

    private void Remove(Guid id)
    {
        if (!_cache.Remove(id, out var entry)) return;
        _usage.Remove(entry.Node);
        _cachedPoints -= entry.PointCount;
        foreach (var geometry in entry.Geometry) geometry.Dispose();
    }

    public void InvalidateAll()
    {
        _calligraphy.InvalidateAll();
        foreach (var entry in _cache.Values) foreach (var geometry in entry.Geometry) geometry.Dispose();
        _cache.Clear();
        _usage.Clear();
        _cachedPoints = 0;
        foreach (var id in _liveCache.Keys.ToArray()) RemoveLive(id);
    }

    public void RetainLive(IEnumerable<InkStroke> strokes)
    {
        _calligraphy.RetainLive(strokes);
        if (_liveCache.Count == 0) return;
        _retained.Clear();
        foreach (var stroke in strokes) _retained.Add(stroke);
        foreach (var id in _liveCache.Where(p => !_retained.Contains(p.Value.Stroke)).Select(p => p.Key).ToArray()) RemoveLive(id);
        _retained.Clear();
    }
    private void RemoveLive(Guid id)
    {
        if (!_liveCache.Remove(id, out var entry)) return;
        foreach (var chunks in entry.Chunks) foreach (var geometry in chunks) geometry.Dispose();
    }

    public void Dispose() => InvalidateAll();
}
