using InkCanvas.Core.Ink;

namespace InkCanvas.Ink;

/// <summary>A short visual transition to a tap's final, persisted width.</summary>
public sealed class InkDotAnimation
{
    private readonly InkPoint[] _initial;
    private readonly InkPoint[] _points;
    private float _elapsed;
    public const float DurationMs = 120;

    public InkStroke Source { get; }
    public InkStroke Preview { get; }
    public long SourceVersion { get; }

    internal InkDotAnimation(InkStroke source, InkStroke provisional)
    {
        Source = source;
        SourceVersion = source.GeometryVersion;
        _initial = provisional.Points.ToArray();
        _points = new InkPoint[_initial.Length];
        Preview = new InkStroke(source.Attributes.Clone()) { InkLayerIndex = source.InkLayerIndex };
        UpdateShape();
    }

    internal bool Advance(float milliseconds)
    {
        _elapsed = Math.Clamp(_elapsed + milliseconds, 0, DurationMs);
        UpdateShape();
        return _elapsed >= DurationMs;
    }

    private void UpdateShape()
    {
        float progress = _elapsed / DurationMs;
        float eased = progress * progress * (3 - 2 * progress);
        var target = Source.Points[0];
        for (int index = 0; index < _points.Length; index++)
        {
            var initial = _initial[index];
            _points[index] = new InkPoint(
                initial.X + (target.X - initial.X) * eased,
                initial.Y + (target.Y - initial.Y) * eased,
                initial.Pressure,
                initial.Width + (target.Width - initial.Width) * eased,
                initial.TimeMs, initial.HasPressure,
                initial.TiltX + (target.TiltX - initial.TiltX) * eased,
                initial.TiltY + (target.TiltY - initial.TiltY) * eased);
        }
        Preview.RestorePoints(_points);
    }
}
