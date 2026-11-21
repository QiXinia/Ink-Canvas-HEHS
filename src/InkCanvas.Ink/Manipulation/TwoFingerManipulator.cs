using System.Numerics;

namespace InkCanvas.Ink.Manipulation;

/// <summary>
/// Two-finger pan / pinch-zoom / rotate, derived from the movement of a contact pair.
/// Pure math so it can be unit-tested without a digitizer.
/// </summary>
public sealed class TwoFingerManipulator
{
    private Vector2 _prevA, _prevB;
    private bool _hasPrevious;

    public bool AllowZoom { get; set; } = true;
    public bool AllowTranslate { get; set; } = true;
    public bool AllowRotation { get; set; }

    public float MinScale { get; set; } = 0.2f;
    public float MaxScale { get; set; } = 8f;

    /// <summary>Fraction of logarithmic zoom; reversing a pinch must not introduce drift.</summary>
    public float ZoomDamping { get; set; } = 1f;
    /// <summary>Contact length below which zoom and rotation are ignored, in DIP.</summary>
    public float ZoomDeadZone { get; set; } = 1f;

    public void Begin(Vector2 a, Vector2 b)
    {
        _prevA = a; _prevB = b; _hasPrevious = true;
    }

    public void Reset() => _hasPrevious = false;

    /// <summary>Maps the contact pair captured by Begin to its current position.</summary>
    public Matrix3x2 TransformFromStart(Vector2 a, Vector2 b)
    {
        if (!_hasPrevious) return Matrix3x2.Identity;
        var previousMidpoint = (_prevA + _prevB) / 2;
        var midpoint = AllowTranslate ? (a + b) / 2 : previousMidpoint;
        var before = _prevB - _prevA;
        var after = b - a;
        float previousLength = before.Length();
        float length = after.Length();
        float scale = 1, angle = 0;
        if (previousLength > ZoomDeadZone && length > ZoomDeadZone)
        {
            if (AllowZoom) scale = Math.Clamp(MathF.Pow(length / previousLength, Math.Clamp(ZoomDamping, .05f, 1f)), MinScale, MaxScale);
            if (AllowRotation) angle = MathF.Atan2(after.Y, after.X) - MathF.Atan2(before.Y, before.X);
        }
        return Matrix3x2.CreateTranslation(-previousMidpoint) * Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(midpoint);
    }

    /// <summary>
    /// Accumulates the delta from the previous pair position into <paramref name="transform"/>.
    /// Returns false when there is nothing to apply.
    /// </summary>
    public bool Update(Vector2 a, Vector2 b, ref Matrix3x2 transform, Vector2 center, bool screenSpace = false)
    {
        if (!_hasPrevious)
        {
            Begin(a, b);
            return false;
        }

        var prevMid = (_prevA + _prevB) * 0.5f;
        var mid = (a + b) * 0.5f;
        var prevVec = _prevB - _prevA;
        var vec = b - a;

        float prevLen = prevVec.Length();
        float len = vec.Length();

        var delta = Matrix3x2.Identity;

        if (AllowTranslate && !screenSpace)
            delta *= Matrix3x2.CreateTranslation(mid - prevMid);

        var pivot = screenSpace && AllowTranslate && Vector2.DistanceSquared(center, mid) < .001f ? prevMid : center;

        if (AllowZoom && prevLen > ZoomDeadZone && len > ZoomDeadZone)
        {
            float scale = MathF.Pow(len / prevLen, Math.Clamp(ZoomDamping, .05f, 1f));
            float currentScale = MathF.Sqrt(MathF.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
            scale = Math.Clamp(scale, MinScale / MathF.Max(currentScale, 1e-3f), MaxScale / MathF.Max(currentScale, 1e-3f));
            delta *= Matrix3x2.CreateTranslation(-pivot) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(pivot);
        }

        if (AllowRotation && prevLen > ZoomDeadZone && len > ZoomDeadZone)
        {
            float angle = MathF.Atan2(vec.Y, vec.X) - MathF.Atan2(prevVec.Y, prevVec.X);
            while (angle > MathF.PI) angle -= 2 * MathF.PI;
            while (angle < -MathF.PI) angle += 2 * MathF.PI;
            delta *= Matrix3x2.CreateTranslation(-pivot) * Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(pivot);
        }

        _prevA = a; _prevB = b;
        if (AllowTranslate && screenSpace) delta *= Matrix3x2.CreateTranslation(mid - prevMid);

        if (delta == Matrix3x2.Identity) return false;
        transform = screenSpace ? transform * delta : delta * transform;
        return true;
    }
}
