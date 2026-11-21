using InkCanvas.Core.Ink;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;

namespace InkCanvas.Ink.Rendering;

/// <summary>Caches stable water-ink chunks and rebuilds only the moving tail, including transparent feibai.</summary>
public sealed class CalligraphyStrokeRenderer : IDisposable
{
    private const int ChunkPoints = CalligraphyBrushGeometry.ChunkPointCount;
    private const int MaxCachedVertices = 400_000;
    private readonly record struct BrushKey(InkWashBrushOptions Options, int Seed, float Width)
    {
        public static BrushKey From(InkStroke stroke) => new(stroke.Attributes.InkWash, stroke.Attributes.InkWashSeed, stroke.Attributes.BaseWidth);
    }

    private sealed class Geometry : IDisposable
    {
        public readonly CanvasGeometry[] Layers = new CanvasGeometry[4];
        public int VertexCount;
        public void Draw(CanvasDrawingSession session, InkStroke stroke)
        {
            var layers = InkBrush.Layers(stroke);
            for (int i = 0; i < Layers.Length; i++)
            {
                if (Layers[i] is null) continue;
                var c = layers[i].Color(stroke.Attributes.EffectiveColor);
                session.FillGeometry(Layers[i], Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
            }
        }
        public void Dispose()
        {
            foreach (var layer in Layers) layer?.Dispose();
        }
    }

    private sealed record CommittedEntry(InkStroke Stroke, long Version, BrushKey Brush, int VertexCount,
        Geometry Geometry, LinkedListNode<Guid> Node);

    private sealed class LiveEntry : IDisposable
    {
        public required InkStroke Stroke;
        public required BrushKey Brush;
        public long Version = -1;
        public int Count, FrozenPointIndex;
        public readonly List<Geometry> Chunks = new();
        public Geometry Frozen, Composite;
        public void Dispose()
        {
            Composite?.Dispose();
            Frozen?.Dispose();
            foreach (var chunk in Chunks) chunk.Dispose();
            Chunks.Clear();
        }
    }

    private readonly Dictionary<Guid, CommittedEntry> _cache = new();
    private readonly LinkedList<Guid> _usage = new();
    private readonly Dictionary<Guid, LiveEntry> _liveCache = new();
    private readonly List<Guid> _expired = new();
    private readonly HashSet<InkStroke> _retained = new();
    private int _cachedVertices;
    public int CachedStrokeCount => _cache.Count;
    public long GeometryBuildCount { get; private set; }
    public long SegmentBuildCount { get; private set; }

    public void Draw(CanvasDrawingSession session, InkStroke stroke, Matrix3x2 transform)
    {
        if (!InkBrush.UsesInkWash(stroke)) return;
        var brush = BrushKey.From(stroke);
        var previous = session.Transform;
        try
        {
            session.Transform = transform;
            if (stroke.IsCompleted && _cache.TryGetValue(stroke.Id, out var cached)
                && ReferenceEquals(cached.Stroke, stroke) && cached.Version == stroke.GeometryVersion && cached.Brush == brush)
            {
                _usage.Remove(cached.Node);
                _usage.AddLast(cached.Node);
                cached.Geometry.Draw(session, stroke);
                return;
            }

            Remove(stroke.Id);
            if (!stroke.IsCompleted)
            {
                GetLiveGeometry(session, stroke, brush).Draw(session, stroke);
                return;
            }

            Geometry geometry;
            if (_liveCache.TryGetValue(stroke.Id, out var live)
                && ReferenceEquals(live.Stroke, stroke) && live.Brush == brush
                && live.Version == stroke.GeometryVersion && live.Count == stroke.Count && live.Composite is not null)
            {
                geometry = live.Composite;
                live.Composite = null;
                RemoveLive(stroke.Id);
            }
            else
            {
                RemoveLive(stroke.Id);
                var chunks = new List<Geometry>();
                try
                {
                    for (int start = 0; start < stroke.Count - 1; start += ChunkPoints)
                        chunks.Add(BuildChunk(session, stroke, start, Math.Min(start + ChunkPoints, stroke.Count - 1)));
                    geometry = Group(session, chunks);
                }
                finally { foreach (var chunk in chunks) chunk.Dispose(); }
            }
            bool stored = false;
            try
            {
                geometry.Draw(session, stroke);
                if (geometry.VertexCount <= MaxCachedVertices)
                {
                    while (_usage.First is not null && (_cachedVertices + geometry.VertexCount > MaxCachedVertices || _cache.Count >= 1024))
                        Remove(_usage.First.Value);
                    _cache[stroke.Id] = new(stroke, stroke.GeometryVersion, brush, geometry.VertexCount, geometry, _usage.AddLast(stroke.Id));
                    _cachedVertices += geometry.VertexCount;
                    stored = true;
                }
            }
            finally { if (!stored) geometry.Dispose(); }
        }
        finally { session.Transform = previous; }
    }

    private Geometry GetLiveGeometry(CanvasDrawingSession session, InkStroke stroke, BrushKey brush)
    {
        if (_liveCache.TryGetValue(stroke.Id, out var entry)
            && (!ReferenceEquals(entry.Stroke, stroke) || stroke.Count < entry.Count || entry.Brush != brush
                || stroke.GeometryVersion - entry.Version != stroke.Count - entry.Count))
        {
            RemoveLive(stroke.Id);
            entry = null;
        }
        if (entry is null)
        {
            if (_liveCache.Count >= 16) RemoveLive(_liveCache.Keys.First());
            _liveCache[stroke.Id] = entry = new() { Stroke = stroke, Brush = brush };
        }
        if (entry.Version == stroke.GeometryVersion && entry.Composite is not null) return entry.Composite;

        // Catmull-Rom needs one look-ahead sample. The exit envelope also needs an
        // arc-length safety margin, even on very dense or nearly stationary touch input.
        int stableEnd = Math.Max(0, Math.Min(stroke.StablePointCount - 1, stroke.Count - 3));
        float tailMargin = MathF.Max(24, stroke.Attributes.BaseWidth * 1.6f);
        bool frozenChanged = false;
        while (stableEnd - entry.FrozenPointIndex >= ChunkPoints)
        {
            int next = entry.FrozenPointIndex + ChunkPoints;
            if (stroke.ArcLength() - stroke.ArcLengthAt(next) < tailMargin) break;
            entry.Chunks.Add(BuildChunk(session, stroke, entry.FrozenPointIndex, next));
            entry.FrozenPointIndex = next;
            frozenChanged = true;
        }
        if (frozenChanged)
        {
            var frozen = Group(session, entry.Chunks);
            entry.Frozen?.Dispose();
            entry.Frozen = frozen;
        }
        var moving = new List<Geometry>();
        Geometry composite;
        try
        {
            if (entry.Frozen is not null) moving.Add(entry.Frozen);
            for (int start = entry.FrozenPointIndex; start < stroke.Count - 1; start += ChunkPoints)
                moving.Add(BuildChunk(session, stroke, start, Math.Min(start + ChunkPoints, stroke.Count - 1)));
            composite = Group(session, moving);
        }
        finally { foreach (var chunk in moving) if (!ReferenceEquals(chunk, entry.Frozen)) chunk.Dispose(); }
        entry.Composite?.Dispose();
        entry.Composite = composite;
        entry.Count = stroke.Count;
        entry.Version = stroke.GeometryVersion;
        return composite;
    }

    private Geometry BuildChunk(ICanvasResourceCreator creator, InkStroke stroke, int start, int end)
    {
        GeometryBuildCount++;
        SegmentBuildCount += end - start;
        return CreateGeometry(creator, CalligraphyBrushGeometry.BuildChunk(stroke, start, end,
            start == 0, end == stroke.Count - 1, stroke.IsCompleted));
    }

    /// <summary>Shared by native canvas rendering and XAML stroke previews.</summary>
    public static CanvasGeometry[] CreatePreviewGeometry(ICanvasResourceCreator creator, InkStroke stroke)
    {
        var chunks = new List<Geometry>();
        try
        {
            for (int start = 0; start < stroke.Count - 1; start += ChunkPoints)
            {
                int end = Math.Min(start + ChunkPoints, stroke.Count - 1);
                chunks.Add(CreateGeometry(creator, CalligraphyBrushGeometry.BuildChunk(stroke, start, end,
                    start == 0, end == stroke.Count - 1, stroke.IsCompleted)));
            }
            // Ownership of the four grouped native paths transfers to the caller.
            return Group(creator, chunks).Layers;
        }
        finally { foreach (var chunk in chunks) chunk.Dispose(); }
    }

    private static Geometry Group(ICanvasResourceCreator creator, IReadOnlyList<Geometry> chunks)
    {
        var result = new Geometry();
        try
        {
            result.VertexCount = chunks.Sum(chunk => chunk.VertexCount);
            for (int layer = 0; layer < result.Layers.Length; layer++)
            {
                var parts = chunks.Select(chunk => chunk.Layers[layer]).Where(path => path is not null).ToArray();
                if (parts.Length > 0) result.Layers[layer] = CanvasGeometry.CreateGroup(creator, parts, CanvasFilledRegionDetermination.Winding);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static Geometry CreateGeometry(ICanvasResourceCreator creator, CalligraphyChunkContours contours)
    {
        var result = new Geometry();
        if (contours is null) return result;
        try
        {
            result.VertexCount = contours.WetBody.Sum(path => path.Length) + contours.GreyDryDrag.Sum(path => path.Length)
                + contours.OuterClusters.Sum(path => path.Length) + contours.CoreClusters.Sum(path => path.Length)
                + contours.FeibaiCutouts.Sum(path => path.Length);
            result.Layers[0] = CreateWindingGeometry(creator, contours.WetBody);
            result.Layers[1] = CreateWindingGeometry(creator, contours.GreyDryDrag);
            result.Layers[2] = CreateWindingGeometry(creator, contours.OuterClusters);
            result.Layers[3] = CreateWindingGeometry(creator, contours.CoreClusters);
            using var feibai = CreateWindingGeometry(creator, contours.FeibaiCutouts);
            if (feibai is null) return result;
            foreach (int layer in new[] { 0, 2, 3 })
            {
                var source = result.Layers[layer];
                if (source is null) continue;
                result.Layers[layer] = null;
                try { result.Layers[layer] = source.CombineWith(feibai, Matrix3x2.Identity, CanvasGeometryCombine.Exclude); }
                finally { source.Dispose(); }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static CanvasGeometry CreateWindingGeometry(ICanvasResourceCreator creator, Vector2[][] polygons)
    {
        if (polygons.Length == 0) return null;
        using var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        foreach (var poly in polygons)
        {
            if (poly.Length < 3) continue;
            builder.BeginFigure(poly[0]);
            for (int i = 1; i < poly.Length; i++) builder.AddLine(poly[i]);
            builder.EndFigure(CanvasFigureLoop.Closed);
        }
        return CanvasGeometry.CreatePath(builder);
    }

    public void Retain(IReadOnlyList<InkStroke> strokes)
    {
        _retained.Clear();
        foreach (var stroke in strokes) _retained.Add(stroke);
        _expired.Clear();
        foreach (var (id, entry) in _cache)
            if (!_retained.Contains(entry.Stroke) || entry.Version != entry.Stroke.GeometryVersion || entry.Brush != BrushKey.From(entry.Stroke))
                _expired.Add(id);
        foreach (var id in _expired) Remove(id);
        _retained.Clear();
        _expired.Clear();
    }

    public void RetainLive(IEnumerable<InkStroke> strokes)
    {
        _retained.Clear();
        foreach (var stroke in strokes) _retained.Add(stroke);
        foreach (var id in _liveCache.Where(pair => !_retained.Contains(pair.Value.Stroke)).Select(pair => pair.Key).ToArray()) RemoveLive(id);
        _retained.Clear();
    }

    public void Invalidate(InkStroke stroke)
    {
        if (stroke is null) return;
        Remove(stroke.Id);
        RemoveLive(stroke.Id);
    }
    private void Remove(Guid id)
    {
        if (!_cache.Remove(id, out var entry)) return;
        _usage.Remove(entry.Node);
        _cachedVertices -= entry.VertexCount;
        entry.Geometry.Dispose();
    }
    private void RemoveLive(Guid id)
    {
        if (_liveCache.Remove(id, out var entry)) entry.Dispose();
    }
    public void InvalidateAll()
    {
        foreach (var entry in _cache.Values) entry.Geometry.Dispose();
        _cache.Clear();
        _usage.Clear();
        _cachedVertices = 0;
        foreach (var id in _liveCache.Keys.ToArray()) RemoveLive(id);
    }
    public void Dispose() => InvalidateAll();
}
