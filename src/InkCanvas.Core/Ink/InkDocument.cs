using System.Numerics;

namespace InkCanvas.Core.Ink;

/// <summary>
/// The stroke collection for one surface (a board page, or the screen overlay).
/// Raises <see cref="Changed"/> so the renderer knows when to rebuild its cached layer.
/// </summary>
public sealed class InkDocument
{
    private readonly List<InkStroke> _strokes = new();

    public IReadOnlyList<InkStroke> Strokes => _strokes;

    public int Count => _strokes.Count;

    public event Action Changed;
    private int _updateDepth;
    private bool _changedDuringUpdate;

    /// <summary>Observers see the final stroke set when a recognition replaces several source strokes.</summary>
    public IDisposable DeferNotifications()
    {
        _updateDepth++;
        return new UpdateScope(this);
    }

    private void NotifyChanged()
    {
        if (_updateDepth > 0) { _changedDuringUpdate = true; return; }
        Changed?.Invoke();
    }

    private sealed class UpdateScope(InkDocument owner) : IDisposable
    {
        private InkDocument _owner = owner;
        public void Dispose()
        {
            var document = _owner;
            if (document is null) return;
            _owner = null;
            if (--document._updateDepth != 0 || !document._changedDuringUpdate) return;
            document._changedDuringUpdate = false;
            document.Changed?.Invoke();
        }
    }

    public void Add(InkStroke stroke)
    {
        if (stroke is null) return;
        _strokes.Add(stroke);
        NotifyChanged();
    }

    public void AddRange(IEnumerable<InkStroke> strokes)
    {
        bool any = false;
        foreach (var s in strokes)
        {
            if (s is null) continue;
            _strokes.Add(s);
            any = true;
        }
        if (any) NotifyChanged();
    }

    public bool Remove(InkStroke stroke)
    {
        bool removed = _strokes.Remove(stroke);
        if (removed) NotifyChanged();
        return removed;
    }

    public int RemoveAll(IEnumerable<InkStroke> strokes)
    {
        var set = new HashSet<InkStroke>(strokes);
        int n = _strokes.RemoveAll(set.Contains);
        if (n > 0) NotifyChanged();
        return n;
    }

    public void Insert(int index, InkStroke stroke)
    {
        _strokes.Insert(Math.Clamp(index, 0, _strokes.Count), stroke);
        NotifyChanged();
    }

    /// <summary>Restore removed strokes in ascending original-index order, notifying once.</summary>
    public void InsertRange(IEnumerable<(int Index, InkStroke Stroke)> strokes)
    {
        bool any = false;
        foreach (var (index, stroke) in strokes)
        {
            _strokes.Insert(Math.Clamp(index, 0, _strokes.Count), stroke);
            any = true;
        }
        if (any) NotifyChanged();
    }

    /// <summary>Replace one stroke by its surviving fragments without reordering other ink.</summary>
    public bool Replace(InkStroke stroke, IReadOnlyList<InkStroke> replacement)
    {
        int index = _strokes.IndexOf(stroke);
        if (index < 0) return false;
        _strokes.RemoveAt(index);
        _strokes.InsertRange(index, replacement);
        // Notify once, so renderers never observe a temporarily empty document mid-erase.
        NotifyChanged();
        return true;
    }

    public void Clear()
    {
        if (_strokes.Count == 0) return;
        _strokes.Clear();
        NotifyChanged();
    }

    /// <summary>Exchange a screen page atomically while retaining stroke identity for undo.</summary>
    public void ReplaceAll(IEnumerable<InkStroke> strokes)
    {
        var incoming = strokes.Where(stroke => stroke is not null).ToArray();
        _strokes.Clear();
        _strokes.AddRange(incoming);
        NotifyChanged();
    }

    /// <summary>Notify that stroke geometry mutated in place (e.g. a transform).</summary>
    public void Touch() => NotifyChanged();

    public Rect Bounds
    {
        get
        {
            var r = Rect.Empty;
            foreach (var s in _strokes) r = r.Union(s.Bounds);
            return r;
        }
    }

    /// <summary>Strokes whose centreline comes within <paramref name="radius"/> of a point.</summary>
    public List<InkStroke> HitTest(Vector2 point, float radius)
    {
        var hits = new List<InkStroke>();
        foreach (var s in _strokes)
        {
            if (!s.Bounds.Inflate(radius).Contains(point)) continue;
            if (s.DistanceTo(point) <= radius + s.HitRadius) hits.Add(s);
        }
        return hits;
    }

    /// <summary>Strokes whose centreline intersects the selection rectangle.</summary>
    public List<InkStroke> HitTest(Rect rect) => HitTest(rect, Matrix3x2.Identity);

    /// <summary>Hit-test in view space, including a rotated or zoomed canvas.</summary>
    public List<InkStroke> HitTest(Rect rect, Matrix3x2 worldToView)
    {
        var hits = new List<InkStroke>();
        Span<Vector2> corners = stackalloc Vector2[4];
        foreach (var s in _strokes)
        {
            var bounds = s.Bounds;
            corners[0] = new Vector2(bounds.Left, bounds.Top); corners[1] = new Vector2(bounds.Right, bounds.Top);
            corners[2] = new Vector2(bounds.Right, bounds.Bottom); corners[3] = new Vector2(bounds.Left, bounds.Bottom);
            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            foreach (var corner in corners)
            {
                var point = Vector2.Transform(corner, worldToView);
                left = MathF.Min(left, point.X); top = MathF.Min(top, point.Y);
                right = MathF.Max(right, point.X); bottom = MathF.Max(bottom, point.Y);
            }
            var viewBounds = new Rect(left, top, right, bottom);
            if (!viewBounds.Intersects(rect)) continue;
            if (s.Count == 0) continue;
            var previous = Vector2.Transform(s.Points[0].Position, worldToView);
            bool hit = rect.Contains(previous);
            for (int i = 1; !hit && i < s.Count; i++)
            {
                var current = Vector2.Transform(s.Points[i].Position, worldToView);
                hit = SegmentIntersectsRect(previous, current, rect);
                previous = current;
            }
            if (!hit && s.ShapeSource?.Geometry?.IsFill == true && Matrix3x2.Invert(worldToView, out var inverse))
                hit = s.DistanceTo(Vector2.Transform(rect.Center, inverse)) == 0;
            if (hit) hits.Add(s);
        }
        return hits;
    }

    internal static bool SegmentIntersectsRect(Vector2 a, Vector2 b, Rect rect)
    {
        float low = 0, high = 1;
        var delta = b - a;
        return Clip(-delta.X, a.X - rect.Left, ref low, ref high)
            && Clip(delta.X, rect.Right - a.X, ref low, ref high)
            && Clip(-delta.Y, a.Y - rect.Top, ref low, ref high)
            && Clip(delta.Y, rect.Bottom - a.Y, ref low, ref high);

        static bool Clip(float direction, float distance, ref float low, ref float high)
        {
            if (MathF.Abs(direction) < 1e-6f) return distance >= 0;
            float t = distance / direction;
            if (direction < 0) low = MathF.Max(low, t); else high = MathF.Min(high, t);
            return low <= high;
        }
    }

    /// <summary>
    /// Erase by area: removes whole strokes touched by the eraser disc. Matches the
    /// original's stroke-level (not pixel-level) eraser behaviour.
    /// </summary>
    public List<InkStroke> EraseAt(Vector2 point, float radius)
    {
        var hits = HitTest(point, radius);
        if (hits.Count > 0) RemoveAll(hits);
        return hits;
    }

    public static void Transform(IEnumerable<InkStroke> strokes, Matrix3x2 m)
    {
        foreach (var s in strokes) s.Transform(m);
    }
}
