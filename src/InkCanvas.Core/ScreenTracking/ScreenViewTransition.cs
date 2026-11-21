using System.Numerics;

namespace InkCanvas.Core.ScreenTracking;

/// <summary>Interpolates the displayed view while the input view stays at the observed position.</summary>
public sealed class ScreenViewTransition
{
    public const double DurationMilliseconds = 100;
    private Matrix3x2 _from;
    private double _started;
    private double _duration = DurationMilliseconds;
    public Matrix3x2 Current { get; private set; } = Matrix3x2.Identity;
    public Matrix3x2 Target { get; private set; } = Matrix3x2.Identity;
    public bool IsActive { get; private set; }

    public void Retarget(Matrix3x2 previous, Matrix3x2 target, double now, bool animate, double durationMilliseconds = DurationMilliseconds)
    {
        if (!animate || IsActive && Target != previous)
        {
            Reset(target);
            return;
        }
        if (IsActive)
        {
            Sample(now);
            if (target == Target) return;
            previous = Current;
        }
        _from = Current = previous;
        Target = target;
        _started = now;
        _duration = Math.Max(1, durationMilliseconds);
        IsActive = previous != target;
    }

    public Matrix3x2 Sample(double now)
    {
        if (!IsActive) return Current;
        float progress = (float)Math.Clamp((now - _started) / _duration, 0, 1);
        float eased = 1 - MathF.Pow(1 - progress, 3);
        Current = Matrix3x2.Lerp(_from, Target, eased);
        if (progress >= 1) Reset(Target);
        return Current;
    }

    public void Reset(Matrix3x2 view)
    {
        Current = Target = view;
        IsActive = false;
    }
}
