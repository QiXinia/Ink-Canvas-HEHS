using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Platform.Windows;

namespace InkCanvas.Ink;

public enum AdvancedSelectionMode { Rotate, PointReflection, AxisReflection }

public readonly record struct AdvancedSelectionTransform(AdvancedSelectionMode Mode, Vector2? First, Vector2? Second, float Angle)
{
    public bool IsReady => First is { } first && (Mode != AdvancedSelectionMode.AxisReflection
        || Second is { } second && Vector2.DistanceSquared(first, second) > 1e-12f);

    public Matrix3x2 Matrix
    {
        get
        {
            if (!IsReady) return Matrix3x2.Identity;
            var center = First.Value;
            if (Mode == AdvancedSelectionMode.Rotate) return Matrix3x2.CreateRotation(Angle * (MathF.PI / 180), center);
            if (Mode == AdvancedSelectionMode.PointReflection) return Matrix3x2.CreateScale(-1, center);
            var direction = Vector2.Normalize(Second.Value - center);
            float diagonal = direction.X * direction.X - direction.Y * direction.Y;
            float offDiagonal = 2 * direction.X * direction.Y;
            var matrix = new Matrix3x2(diagonal, offDiagonal, offDiagonal, -diagonal, 0, 0);
            matrix.Translation = center - Vector2.TransformNormal(center, matrix);
            return matrix;
        }
    }
}

public sealed partial class InkController
{
    private enum ReferenceDrag { First, Second, MoveFirst, MoveSecond, NewAxis, TranslateAxis, Angle }
    private sealed record AdvancedDrag(uint Pointer, long Surface, ReferenceDrag Part, Vector2 Start, AdvancedSelectionTransform Before);
    private AdvancedDrag _advancedDrag;
    private Rect _advancedSourceBounds;
    internal Rect TransformAdvancedSourceBounds(Matrix3x2 transform) =>
        Rendering.CommittedInkLayer.TransformBounds(_advancedSourceBounds, transform).Inflate(4);
    public AdvancedSelectionTransform? AdvancedSelection { get; private set; }
    public bool CanUseAdvancedSelection => Tool == InkTool.Select && HasSelection && Playback is null
        && PolygonExtension?.SupportsAdvancedSelectionTransforms == true;
    public bool CanCommitAdvancedSelection => CanUseAdvancedSelection && _advancedDrag is null
        && AdvancedSelection is { IsReady: true } state && (state.Mode != AdvancedSelectionMode.Rotate || MathF.Abs(state.Angle) >= .001f);

    public (Vector2? First, Vector2? Second) AdvancedReferencePositions(Matrix3x2? screenToHost = null)
    {
        if ((_advancedDrag?.Before ?? AdvancedSelection) is not { } state) return default;
        var view = ViewTransform * (screenToHost ?? Matrix3x2.Identity);
        return (state.First is { } first ? Vector2.Transform(first, view) : null,
            state.Second is { } second ? Vector2.Transform(second, view) : null);
    }

    public bool BeginAdvancedSelection(AdvancedSelectionMode mode)
    {
        if (!CanUseAdvancedSelection || HasActiveInteraction || !Enum.IsDefined(mode)) return false;
        StopViewportMotion();
        ShapeEditEnabled = false;
        _advancedSourceBounds = SelectionBounds;
        UpdateAdvancedSelection(new(mode, null, null, 0));
        return true;
    }

    public void SetAdvancedRotationCenterToSelection()
    {
        if (AdvancedSelection is { Mode: AdvancedSelectionMode.Rotate } state && _advancedDrag is null)
            UpdateAdvancedSelection(state with { First = _advancedSourceBounds.Center });
    }

    public void SetAdvancedRotationAngle(float degrees)
    {
        if (AdvancedSelection is { Mode: AdvancedSelectionMode.Rotate } state && _advancedDrag is null && float.IsFinite(degrees))
            UpdateAdvancedSelection(state with { Angle = NormalizeRotation(degrees) });
    }

    public bool CommitAdvancedSelection()
    {
        if (!CanCommitAdvancedSelection || AdvancedSelection is not { } state) return false;
        AdvancedSelection = null;
        TransformSelectionUndoable(state.Matrix, state.Mode switch
        {
            AdvancedSelectionMode.Rotate => "rotate selection about point",
            AdvancedSelectionMode.PointReflection => "reflect selection about point",
            _ => "reflect selection across axis",
        }, scaleWidth: false);
        return true;
    }

    public bool CancelAdvancedSelection()
    {
        if (AdvancedSelection is null) return false;
        _advancedDrag = null;
        UpdateAdvancedSelection(null);
        return true;
    }

    public Vector2? AdvancedRotationHandle
    {
        get
        {
            if (AdvancedSelection is not { Mode: AdvancedSelectionMode.Rotate, First: { } pivot } state) return null;
            float scale = MathF.Sqrt(MathF.Abs(ViewTransform.GetDeterminant()));
            if (scale < .0001f) return null;
            var radial = _advancedSourceBounds.Center - pivot;
            if (radial.LengthSquared() * scale * scale < 24 * 24)
                radial = Matrix3x2.Invert(ViewTransform, out var inverse) ? Vector2.TransformNormal(new(0, -56), inverse) : new(0, -56);
            var direction = Vector2.Normalize(radial);
            float length = Math.Clamp(radial.Length() * scale, 48, 104) / scale;
            return Vector2.Transform(pivot + direction * length, state.Matrix);
        }
    }

    internal Rect AdvancedSelectionVisualBounds
    {
        get
        {
            if (AdvancedSelection is not { } state) return Rect.Empty;
            var bounds = state.IsReady ? TransformAdvancedSourceBounds(state.Matrix * ViewTransform) : Rect.Empty;
            if (state.First is { } first) bounds = bounds.Union(Marker(first));
            if (state.Second is { } second) bounds = bounds.Union(Marker(second));
            if (AdvancedRotationHandle is { } handle) bounds = bounds.Union(Marker(handle));
            return bounds;
        }
    }

    private Rect Marker(Vector2 world)
    {
        var point = Vector2.Transform(world, ViewTransform);
        return new(point.X - 16, point.Y - 16, point.X + 16, point.Y + 16);
    }

    private void BeginAdvancedReference(in OverlayPointer pointer)
    {
        if (_advancedDrag is not null || AdvancedSelection is not { } state || !CanUseAdvancedSelection) return;
        var screen = new Vector2(pointer.X, pointer.Y);
        var world = ToWorld(pointer);
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y)) return;

        bool Near(Vector2 point) => Vector2.DistanceSquared(Vector2.Transform(point, ViewTransform), screen) <= 16 * 16;
        var part = ReferenceDrag.First;
        if (state.Mode == AdvancedSelectionMode.AxisReflection)
        {
            if (state.First is { } first && state.Second is { } second)
            {
                if (Near(first) || Near(second))
                    part = Vector2.DistanceSquared(Vector2.Transform(first, ViewTransform), screen)
                        <= Vector2.DistanceSquared(Vector2.Transform(second, ViewTransform), screen) ? ReferenceDrag.MoveFirst : ReferenceDrag.MoveSecond;
                else if (InkStroke.DistanceToSegment(screen, Vector2.Transform(first, ViewTransform), Vector2.Transform(second, ViewTransform)) <= 10)
                    part = ReferenceDrag.TranslateAxis;
                else part = ReferenceDrag.NewAxis;
            }
            else part = state.First is { } anchor ? Near(anchor) ? ReferenceDrag.MoveFirst : ReferenceDrag.Second : ReferenceDrag.NewAxis;
        }
        else if (AdvancedRotationHandle is { } handle && Near(handle)) part = ReferenceDrag.Angle;
        else if (state.First is { } pivot && Near(pivot)) part = ReferenceDrag.MoveFirst;
        _advancedDrag = new(pointer.Id, pointer.InputSurface, part, world, state);
        MoveAdvancedReference(pointer);
    }

    private void MoveAdvancedReference(in OverlayPointer pointer)
    {
        if (_advancedDrag is not { } drag || drag.Pointer != pointer.Id || drag.Surface != pointer.InputSurface) return;
        var world = ToWorld(pointer);
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y)) return;
        var original = drag.Before;
        var state = drag.Part switch
        {
            ReferenceDrag.First => original with { First = world },
            ReferenceDrag.Second => original with { Second = world },
            ReferenceDrag.MoveFirst => original with { First = original.First + world - drag.Start },
            ReferenceDrag.MoveSecond => original with { Second = original.Second + world - drag.Start },
            ReferenceDrag.NewAxis => original with { First = drag.Start, Second = world },
            ReferenceDrag.TranslateAxis => original with { First = original.First + world - drag.Start, Second = original.Second + world - drag.Start },
            _ => original with { Angle = DragAngle(drag, world, pointer.Shift) },
        };
        if (state is { Mode: AdvancedSelectionMode.AxisReflection, First: { } a, Second: { } b })
        {
            if (Vector2.TransformNormal(b - a, ViewTransform).LengthSquared() < 8 * 8)
                state = drag.Part == ReferenceDrag.NewAxis ? state with { Second = null } : original;
        }
        UpdateAdvancedSelection(state);
    }

    private void FinishAdvancedReference(in OverlayPointer pointer, bool canceled)
    {
        if (_advancedDrag is not { } drag || drag.Pointer != pointer.Id || drag.Surface != pointer.InputSurface) return;
        if (!canceled) MoveAdvancedReference(pointer);
        _advancedDrag = null;
        if (canceled) UpdateAdvancedSelection(drag.Before);
        Invalidated?.Invoke();
    }

    private static float DragAngle(AdvancedDrag drag, Vector2 world, bool snap)
    {
        var center = drag.Before.First.Value;
        var before = drag.Start - center;
        var after = world - center;
        if (after.LengthSquared() < .0001f || before.LengthSquared() < .0001f) return drag.Before.Angle;
        float angle = drag.Before.Angle + MathF.Atan2(before.X * after.Y - before.Y * after.X, Vector2.Dot(before, after)) * (180 / MathF.PI);
        return NormalizeRotation(snap ? MathF.Round(angle / 15) * 15 : angle);
    }

    private static float NormalizeRotation(float angle) => MathF.IEEERemainder(angle, 360);

    private void UpdateAdvancedSelection(AdvancedSelectionTransform? state)
    {
        if (AdvancedSelection == state) return;
        AdvancedSelection = state;
        Invalidated?.Invoke();
    }
}
