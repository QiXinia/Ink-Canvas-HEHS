using System.Numerics;

namespace InkCanvas.Ink.Manipulation;

internal sealed class ViewportPinch
{
    private const float RotationMinimumSpan = 24;
    private const float RotationSlop = 10 * MathF.PI / 180;
    private Vector2 _center, _direction;
    private float _startSpan, _zoom, _angle, _rotation;

    public void Begin(Vector2 first, Vector2 second, float minimumSpan)
    {
        _center = (first + second) * .5f;
        _direction = second - first;
        float span = _direction.Length();
        _startSpan = span >= Math.Max(.1f, minimumSpan) ? span : 0;
        _zoom = _angle = _rotation = 0;
    }

    public Matrix3x2 Update(Vector2 first, Vector2 second, Vector2 fixedPivot, Matrix3x2 view,
        TwoFingerManipulator settings, bool elastic)
    {
        var center = (first + second) * .5f;
        var pivot = settings.AllowTranslate ? _center : fixedPivot;
        var target = settings.AllowTranslate ? center : pivot;
        var direction = second - first;
        float span = direction.Length();
        float zoom = 0, rotation = 0;
        if (span < Math.Max(.1f, settings.ZoomDeadZone))
        {
            // Coincident contacts do not provide a stable pinch scale.
            _startSpan = 0;
            _zoom = 0;
            _angle = _rotation = 0;
        }
        else if (_startSpan == 0)
        {
            _startSpan = span;
            _direction = direction;
            _angle = _rotation = 0;
        }
        else
        {
            if (settings.AllowZoom)
            {
                float next = MathF.Log(span / _startSpan) * Math.Clamp(settings.ZoomDamping, .05f, 1f);
                zoom = next - _zoom;
                _zoom = next;
            }
            if (settings.AllowRotation && span >= RotationMinimumSpan && _direction.LengthSquared() >= RotationMinimumSpan * RotationMinimumSpan)
            {
                float cross = _direction.X * direction.Y - _direction.Y * direction.X;
                _angle += MathF.Atan2(cross, Vector2.Dot(_direction, direction));
                float next = MathF.CopySign(Math.Max(0, MathF.Abs(_angle) - RotationSlop), _angle);
                rotation = next - _rotation;
                _rotation = next;
            }
            _direction = direction;
        }

        float factor = zoom == 0 ? 1 : ScaleFactor(view, zoom, settings, elastic);
        var result = Around(view, pivot, target, factor, rotation);
        _center = center;
        return result;
    }

    private float _rawLogScale;
    public void RebaseScale(Matrix3x2 view) => _rawLogScale = MathF.Log(Scale(view));

    private float ScaleFactor(Matrix3x2 view, float zoom, TwoFingerManipulator settings, bool elastic)
    {
        float current = Scale(view);
        float minimum = MathF.Log(settings.MinScale), maximum = MathF.Log(settings.MaxScale);
        if (!elastic) return Math.Clamp(current * MathF.Exp(zoom), settings.MinScale, settings.MaxScale) / current;

        // Keep raw zoom separate from displayed zoom so resistance is sample-rate independent.
        float displayed = MathF.Log(current);
        _rawLogScale += zoom;
        float edge = Math.Clamp(_rawLogScale, minimum, maximum);
        float excess = _rawLogScale - edge;
        const float elasticRange = .22f;
        _rawLogScale = Math.Clamp(_rawLogScale, minimum - 1, maximum + 1);
        float next = edge + elasticRange * excess / (elasticRange + MathF.Abs(excess));
        return MathF.Exp(next - displayed);
    }

    public static float Scale(Matrix3x2 view) => MathF.Sqrt(MathF.Abs(view.GetDeterminant()));

    public static Matrix3x2 Around(Matrix3x2 view, Vector2 from, Vector2 to, float factor, float rotation = 0)
    {
        if (factor == 1 && rotation == 0)
        {
            view.Translation += to - from;
            return view;
        }
        var linear = Matrix3x2.CreateScale(factor) * Matrix3x2.CreateRotation(rotation);
        linear.Translation = to - Vector2.TransformNormal(from, linear);
        return view * linear;
    }
}
