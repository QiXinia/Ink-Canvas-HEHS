using System.Numerics;

namespace InkCanvas.Core.Ink;

/// <summary>Adaptive low-pass filtering in DIPs and seconds, independently owned by each stroke.</summary>
internal sealed class OneEuroFilter
{
    private Vector2 _previous, _filtered, _derivative;
    private float _time;
    private bool _initialized;

    public Vector2 Update(Vector2 point, float timeMs)
    {
        if (!_initialized)
        {
            _initialized = true;
            _previous = _filtered = point;
            _time = timeMs;
            return point;
        }

        float elapsed = timeMs - _time;
        // A missing/repeated timestamp cannot describe velocity. Keep the sample usable
        // without assuming a sampling interval or moving an already accepted point.
        if (!float.IsFinite(elapsed) || elapsed <= 0 || elapsed > 200)
        {
            _previous = _filtered = point;
            _derivative = Vector2.Zero;
            _time = timeMs;
            return point;
        }

        float seconds = elapsed / 1000;
        var derivative = (point - _previous) / seconds;
        _derivative = Vector2.Lerp(_derivative, derivative, Alpha(4, seconds));
        float cutoff = 3 + .04f * _derivative.Length();
        _filtered = Vector2.Lerp(_filtered, point, Alpha(cutoff, seconds));
        // Bound the visible lag at high speed and at sharp changes of direction.
        var error = point - _filtered;
        const float maximumLag = 2.5f;
        if (error.LengthSquared() > maximumLag * maximumLag)
            _filtered = point - Vector2.Normalize(error) * maximumLag;
        _previous = point;
        _time = timeMs;
        return _filtered;
    }

    private static float Alpha(float cutoff, float seconds) => 1 / (1 + 1 / (2 * MathF.PI * cutoff * seconds));
}
