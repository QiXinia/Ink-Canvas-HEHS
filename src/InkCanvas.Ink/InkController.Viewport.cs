using System.Numerics;
using InkCanvas.Ink.Manipulation;
using InkCanvas.Platform.Windows;

namespace InkCanvas.Ink;

public sealed partial class InkController
{
    private readonly ViewportPinch _viewportPinch = new();
    private (uint First, uint Second)? _viewportPair;
    private uint? _viewportRemaining;
    private long? _viewportSurface;
    private bool _viewportGesture, _viewportDeformed;
    private bool _viewportFramePending;
    private Matrix3x2? _pendingViewport;
    private Vector2 _viewportAnchor, _viewportScalePivot, _viewportVelocity;
    private Matrix3x2 _springStart;
    private float _springOffset, _springElapsed;
    private readonly (uint Time, Vector2 Center)[] _viewportSamples = new (uint, Vector2)[16];
    private int _viewportSampleNext, _viewportSampleCount;

    public event Action ViewportFrameRequested;
    public bool ViewportReleaseMotionEnabled { get; set; }
    public bool IsTouchViewManipulating => _viewportPair.HasValue || _viewportRemaining.HasValue;
    private bool IsViewportSettling => _viewportVelocity != Vector2.Zero || _springOffset != 0;
    private bool UseViewportMotion => ViewportReleaseMotionEnabled && ViewportFrameRequested is not null;
    private Matrix3x2 InputViewport => _pendingViewport ?? ViewTransform;

    private static Vector2 Position(in OverlayPointer pointer) => new(pointer.X, pointer.Y);

    private bool BeginViewportPair(long surface, uint time)
    {
        if (_viewportPair.HasValue) return true;
        if (!TryGetTouchPair(out var first, out var second, surface)) return false;
        _viewportPair = (first.Id, second.Id);
        _viewportSurface = surface;
        _viewportRemaining = _panPointerId = null;
        _viewportPinch.Begin(Position(first), Position(second), ZoomDeadZone);
        _viewportPinch.RebaseScale(InputViewport);
        _viewportAnchor = (Position(first) + Position(second)) * .5f;
        _viewportScalePivot = _manipulator.AllowTranslate ? _viewportAnchor : ResolvePivot(_viewportAnchor);
        _viewportGesture = true;
        ResetViewportSamples(time);
        return true;
    }

    private bool MoveViewportContact(in OverlayPointer sample, in OverlayPointer previous)
    {
        if (_viewportPair is { } pair)
        {
            if (sample.Id != pair.First && sample.Id != pair.Second) return true;
            var first = Position(_touches[pair.First]);
            var second = Position(_touches[pair.Second]);
            var before = InputViewport;
            var center = (first + second) * .5f;
            if (_manipulator.AllowTranslate
                || TransformCenterOption == Core.Settings.MatrixTransformCenterPointOptions.GestureOperationCenterPoint)
                _viewportScalePivot = center;
            var view = _viewportPinch.Update(first, second, _viewportScalePivot, before, _manipulator, UseViewportMotion);
            _viewportDeformed |= MathF.Abs(view.M11 - before.M11) + MathF.Abs(view.M12 - before.M12) > .002f * ViewportPinch.Scale(before);
            _viewportAnchor = center;
            RecordViewportSample(sample.TimeMs);
            QueueViewport(view);
            return true;
        }
        if (_viewportRemaining == sample.Id)
        {
            _viewportAnchor = Position(sample);
            var previousPosition = Position(previous);
            _viewportScalePivot += _viewportAnchor - previousPosition;
            RecordViewportSample(sample.TimeMs);
            QueueViewport(ViewportPinch.Around(InputViewport, previousPosition, _viewportAnchor, 1));
            return true;
        }
        return _viewportGesture;
    }

    private bool ReleaseViewportContact(in OverlayPointer sample, bool canceled)
    {
        if (!IsViewportContact(sample.Id) || !_touches.TryGetValue(sample.Id, out var previous)) return false;
        bool primary = _viewportRemaining == sample.Id
            || _viewportPair is { } pair && (pair.First == sample.Id || pair.Second == sample.Id);
        if (primary && !canceled)
        {
            _touches[sample.Id] = sample;
            MoveViewportContact(sample, previous);
        }
        CommitViewportFrame();
        _touches.Remove(sample.Id);
        if (canceled) _viewportDeformed = true;
        if (primary)
        {
            _viewportPair = null;
            _viewportRemaining = null;
            _panPointerId = null;
            if (BeginViewportPair(sample.InputSurface, sample.TimeMs)) return true;
            if (Tool == InkTool.Pan || UseViewportMotion && _manipulator.AllowTranslate)
            {
                foreach (var remaining in _touches.Values)
                {
                    if (remaining.InputSurface != sample.InputSurface) continue;
                    _viewportRemaining = remaining.Id;
                    _viewportAnchor = Position(remaining);
                    ResetViewportSamples(sample.TimeMs);
                    break;
                }
            }
        }
        if (!_touches.Values.Any(touch => touch.InputSurface == previous.InputSurface))
        {
            _viewportGesture = false;
            _viewportSurface = null;
            _panPointerId = null;
            if (UseViewportMotion) SettleViewport(sample.TimeMs, !_viewportDeformed && _manipulator.AllowTranslate);
            _viewportDeformed = false;
        }
        if (primary) Invalidated?.Invoke();
        return true;
    }

    private void QueueViewport(Matrix3x2 view)
    {
        if (view == InputViewport) return;
        _pendingViewport = view;
        if (_viewportFramePending) return;
        _viewportFramePending = true;
        if (ViewportFrameRequested is { } request) request();
        else CommitViewportFrame();
    }

    public void CommitViewportFrame()
    {
        if (_pendingViewport is not { } view) return;
        _pendingViewport = null;
        _viewportFramePending = false;
        SetViewport(view);
        Invalidated?.Invoke();
    }

    public bool AdvanceViewportFrame(float seconds)
    {
        CommitViewportFrame();
        if (seconds <= 0) return IsViewportSettling;
        if (_springOffset != 0)
        {
            _springElapsed += seconds;
            float t = 18 * _springElapsed;
            float residual = _springOffset * (1 + t) * MathF.Exp(-t);
            if (MathF.Abs(residual) < .001f) { residual = 0; }
            SetViewport(ViewportPinch.Around(_springStart, _viewportScalePivot, _viewportScalePivot, MathF.Exp(residual - _springOffset)));
            if (residual == 0) _springOffset = 0;
        }
        else if (_viewportVelocity != Vector2.Zero)
        {
            float decay = MathF.Exp(-7.5f * seconds);
            SetViewport(ViewportPinch.Around(ViewTransform, Vector2.Zero, _viewportVelocity * ((1 - decay) / 7.5f), 1));
            _viewportVelocity *= decay;
            if (_viewportVelocity.LengthSquared() < 15 * 15) _viewportVelocity = Vector2.Zero;
        }
        else return false;
        Invalidated?.Invoke();
        return IsViewportSettling;
    }

    private void SettleViewport(uint time, bool allowMomentum)
    {
        float scale = ViewportPinch.Scale(ViewTransform);
        float edge = Math.Clamp(scale, _manipulator.MinScale, _manipulator.MaxScale);
        if (MathF.Abs(scale - edge) > .0001f)
        {
            _springStart = ViewTransform;
            _springOffset = MathF.Log(scale / edge);
            _springElapsed = 0;
        }
        else if (allowMomentum)
        {
            for (int age = _viewportSampleCount - 1; age >= 0; age--)
            {
                var first = _viewportSamples[(_viewportSampleNext - 1 - age + _viewportSamples.Length) % _viewportSamples.Length];
                uint elapsed = unchecked(time - first.Time);
                if (elapsed is < 15 or > 80) continue;
                var velocity = (_viewportAnchor - first.Center) * (1000f / elapsed);
                float speed = velocity.Length();
                if (speed >= 100) _viewportVelocity = velocity * Math.Min(1, 2800 / speed);
                break;
            }
        }
        if (IsViewportSettling) ViewportFrameRequested?.Invoke();
    }

    private void ResetViewportSamples(uint time)
    {
        _viewportSampleCount = _viewportSampleNext = 0;
        RecordViewportSample(time);
    }

    private void RecordViewportSample(uint time)
    {
        if (!UseViewportMotion) return;
        int last = (_viewportSampleNext + _viewportSamples.Length - 1) % _viewportSamples.Length;
        if (_viewportSampleCount > 0 && unchecked(time - _viewportSamples[last].Time) < 5) return;
        _viewportSamples[_viewportSampleNext] = (time, _viewportAnchor);
        _viewportSampleNext = (_viewportSampleNext + 1) % _viewportSamples.Length;
        _viewportSampleCount = Math.Min(_viewportSampleCount + 1, _viewportSamples.Length);
    }

    public void StopViewportMotion()
    {
        if (!IsViewportSettling) return;
        _viewportVelocity = Vector2.Zero;
        _springOffset = 0;
        Invalidated?.Invoke();
    }

    private void ResetViewportContacts()
    {
        _viewportPair = null;
        _viewportRemaining = null;
        _viewportSurface = null;
        _viewportGesture = _viewportDeformed = false;
        _pendingViewport = null;
        _viewportFramePending = false;
        StopViewportMotion();
    }

    private bool IsViewportContact(uint id)
        => _touches.TryGetValue(id, out var pointer) && IsViewportSurface(pointer.InputSurface);

    private bool IsViewportSurface(long surface) => _viewportGesture && _viewportSurface == surface;

}
