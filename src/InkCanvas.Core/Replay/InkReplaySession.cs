using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;

namespace InkCanvas.Core.Replay;

/// <summary>
/// Ink replay ("time machine"): re-reveals committed strokes in drawing order.
/// The caller advances a clock and asks for the visible prefix; nothing here mutates the
/// source document, and stored widths are preserved so replay looks like the original drawing.
/// </summary>
public sealed class InkReplaySession
{
    // Generated shapes have arbitrary sampling densities, including two-point dashes.
    private const float ShapeUnitsPerPoint = 3f;
    private readonly List<InkStroke> _source;
    private readonly List<float> _cumulative;
    private readonly float[][] _shapeDistances;
    private readonly List<InkPoint> _partialPoints = new();
    private readonly float _totalMs;
    private readonly TimeSpan _duration;
    private InkStroke _partial;
    private int _partialIndex = -1;
    private float _partialElapsed = float.NaN;

    public InkReplaySession(IEnumerable<InkStroke> strokes, float msPerPoint = 12f)
    {
        _source = strokes.SelectMany(ShapeDefinition.RenderContours).ToList();
        _cumulative = new List<float>();
        _shapeDistances = new float[_source.Count][];
        float acc = 0;
        for (int i = 0; i < _source.Count; i++)
        {
            var s = _source[i];
            float duration = Math.Max(1, s.Count) * msPerPoint;
            if (s.IsRecognizedShape && s.Count > 1)
            {
                var distances = new float[s.Count];
                for (int p = 1; p < s.Count; p++)
                    distances[p] = distances[p - 1] + System.Numerics.Vector2.Distance(s.Points[p - 1].Position, s.Points[p].Position);
                if (distances[^1] > 0)
                {
                    _shapeDistances[i] = distances;
                    duration = MathF.Max(1, distances[^1] / ShapeUnitsPerPoint) * msPerPoint;
                }
            }
            acc += duration;
            _cumulative.Add(acc);
        }
        _totalMs = acc;
        // Round up to a clock tick so seeking to Duration always reveals the last dash.
        _duration = TimeSpan.FromTicks((long)Math.Ceiling((double)acc * TimeSpan.TicksPerMillisecond));
    }

    public TimeSpan Duration => _duration;

    public int StrokeCount => _source.Count;

    public bool IsComplete(float elapsedMs) => elapsedMs >= _totalMs;

    /// <summary>Reusable playback frame. Finished strokes retain their cached geometry and the partial stroke keeps its identity.</summary>
    public void WriteVisibleAt(float elapsedMs, List<InkStroke> result)
    {
        result.Clear();
        for (int i = 0; i < _source.Count; i++)
        {
            float start = i == 0 ? 0 : _cumulative[i - 1];
            if (elapsedMs < start) break;
            var source = _source[i];
            float end = _cumulative[i];
            if (elapsedMs >= end) { result.Add(source); continue; }
            if (elapsedMs <= 0) break;
            int take = Math.Clamp((int)(source.Count * (elapsedMs - start) / Math.Max(1, end - start)), Math.Min(1, source.Count), source.Count);
            if (_partialIndex != i)
            {
                _partial = new InkStroke(source.Attributes.Clone()) { IsRecognizedShape = source.IsRecognizedShape, ShapeSource = source.ShapeSource };
                // Interpolation can revise the final segment without adding a point.
                // Keep it out of the renderer's append-only live geometry cache.
                if (_shapeDistances[i] is not null) _partial.Complete();
                _partialIndex = i;
                _partialElapsed = float.NaN;
            }
            if (_shapeDistances[i] is not null)
            {
                if (elapsedMs <= start) break;
                if (_partialElapsed != elapsedMs)
                    RestoreShapePrefix(_partial, source, _shapeDistances[i], (elapsedMs - start) / (end - start));
                _partialElapsed = elapsedMs;
            }
            else if (_partial.Count != take) _partial.RestorePrefix(source.Points, take);
            result.Add(_partial); break;
        }
    }

    /// <summary>Strokes visible at <paramref name="elapsedMs"/>, the last one possibly partial.</summary>
    public List<InkStroke> VisibleAt(float elapsedMs)
    {
        var result = new List<InkStroke>();
        for (int i = 0; i < _source.Count; i++)
        {
            float start = i == 0 ? 0 : _cumulative[i - 1];
            if (elapsedMs < start) break;
            float end = _cumulative[i];
            var src = _source[i];
            if (elapsedMs >= end)
            {
                result.Add(src);
                continue;
            }

            float fraction = (elapsedMs - start) / Math.Max(1f, end - start);
            int take = Math.Max(1, (int)(src.Count * fraction));
            var partial = new InkStroke(src.Attributes.Clone()) { IsRecognizedShape = src.IsRecognizedShape, ShapeSource = src.ShapeSource };
            if (_shapeDistances[i] is not null)
            {
                if (elapsedMs <= start) break;
                RestoreShapePrefix(partial, src, _shapeDistances[i], fraction);
                partial.Complete();
            }
            else partial.RestorePoints(src.Points.Take(take));
            result.Add(partial);
        }
        return result;
    }

    private void RestoreShapePrefix(InkStroke partial, InkStroke source, float[] distances, float fraction)
    {
        float distance = distances[^1] * fraction;
        int low = 0, high = distances.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (distances[middle] <= distance) low = middle + 1;
            else high = middle;
        }

        _partialPoints.Clear();
        for (int i = 0; i < low; i++) _partialPoints.Add(source.Points[i]);
        if (low > 0 && low < source.Count && distance > distances[low - 1])
        {
            var from = source.Points[low - 1];
            var to = source.Points[low];
            float blend = (distance - distances[low - 1]) / (distances[low] - distances[low - 1]);
            _partialPoints.Add(new InkPoint(
                from.X + (to.X - from.X) * blend,
                from.Y + (to.Y - from.Y) * blend,
                from.Pressure + (to.Pressure - from.Pressure) * blend,
                from.Width + (to.Width - from.Width) * blend,
                from.TimeMs + (to.TimeMs - from.TimeMs) * blend,
                from.HasPressure || to.HasPressure,
                from.TiltX + (to.TiltX - from.TiltX) * blend,
                from.TiltY + (to.TiltY - from.TiltY) * blend));
        }
        partial.RestorePoints(_partialPoints);
    }
}
