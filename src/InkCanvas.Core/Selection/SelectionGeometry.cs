using InkCanvas.Core.Ink;
using System.Numerics;

namespace InkCanvas.Core.Selection;

public enum SelectionHandle { None, Move, TopLeft, TopRight, BottomRight, BottomLeft, Rotate, Top, Right, Bottom, Left }

/// <summary>Hit-testing and drag maths for the selection box. Pure, so it is unit-testable.</summary>
public static class SelectionGeometry
{
    /// <summary>Grab radius in view pixels; generous so a finger can hit a corner.</summary>
    public const float HandleRadius = 12f;

    /// <summary>Distance of the rotate handle above the top edge.</summary>
    public const float RotateOffset = 28f;

    private const float MinScale = 0.05f;
    private const float MaxScale = 20f;

    public static Vector2 RotateHandlePosition(Rect bounds)
        => new(bounds.Center.X, bounds.Top - RotateOffset);

    public static SelectionHandle HitTest(Rect bounds, Vector2 point)
    {
        if (bounds.IsEmpty) return SelectionHandle.None;

        if (Vector2.Distance(point, RotateHandlePosition(bounds)) <= HandleRadius)
            return SelectionHandle.Rotate;

        if (Vector2.Distance(point, new Vector2(bounds.Left, bounds.Top)) <= HandleRadius)
            return SelectionHandle.TopLeft;
        if (Vector2.Distance(point, new Vector2(bounds.Right, bounds.Top)) <= HandleRadius)
            return SelectionHandle.TopRight;
        if (Vector2.Distance(point, new Vector2(bounds.Right, bounds.Bottom)) <= HandleRadius)
            return SelectionHandle.BottomRight;
        if (Vector2.Distance(point, new Vector2(bounds.Left, bounds.Bottom)) <= HandleRadius)
            return SelectionHandle.BottomLeft;

        return bounds.Contains(point) ? SelectionHandle.Move : SelectionHandle.None;
    }

    /// <summary>
    /// Matrix for one drag step. Corners scale about the opposite corner, rotate turns about
    /// the centre, and <see cref="SelectionHandle.Move"/> translates.
    /// </summary>
    public static Matrix3x2 DragMatrix(SelectionHandle handle, Rect bounds, Vector2 from, Vector2 to, bool keepAspect)
    {
        switch (handle)
        {
            case SelectionHandle.None:
                return Matrix3x2.Identity;

            case SelectionHandle.Move:
                return Matrix3x2.CreateTranslation(to - from);

            case SelectionHandle.Rotate:
                {
                    var center = bounds.Center;
                    var a = from - center;
                    var b = to - center;
                    if (a.LengthSquared() < 1e-4f || b.LengthSquared() < 1e-4f)
                        return Matrix3x2.Identity;
                    float angle = MathF.Atan2(b.Y, b.X) - MathF.Atan2(a.Y, a.X);
                    return About(center, Matrix3x2.CreateRotation(angle));
                }

            default:
                {
                    var anchor = OppositeCorner(handle, bounds);
                    float sx = Ratio(from.X - anchor.X, to.X - anchor.X);
                    float sy = Ratio(from.Y - anchor.Y, to.Y - anchor.Y);
                    if (handle is SelectionHandle.Top or SelectionHandle.Bottom) sx = 1;
                    if (handle is SelectionHandle.Left or SelectionHandle.Right) sy = 1;
                    if (keepAspect && handle is not (SelectionHandle.Top or SelectionHandle.Bottom or SelectionHandle.Left or SelectionHandle.Right))
                    {
                        float uniform = MathF.Max(sx, sy);
                        sx = sy = uniform;
                    }
                    return About(anchor, Matrix3x2.CreateScale(sx, sy));
                }
        }
    }

    private static float Ratio(float from, float to)
    {
        // A drag that starts on the anchor has no meaningful ratio; leave that axis alone.
        if (MathF.Abs(from) < 1e-3f) return 1f;
        return Math.Clamp(to / from, MinScale, MaxScale);
    }

    private static Matrix3x2 About(Vector2 pivot, Matrix3x2 m)
        => Matrix3x2.CreateTranslation(-pivot) * m * Matrix3x2.CreateTranslation(pivot);

    private static Vector2 OppositeCorner(SelectionHandle handle, Rect b) => handle switch
    {
        SelectionHandle.TopLeft => new Vector2(b.Right, b.Bottom),
        SelectionHandle.TopRight => new Vector2(b.Left, b.Bottom),
        SelectionHandle.BottomRight => new Vector2(b.Left, b.Top),
        SelectionHandle.BottomLeft => new Vector2(b.Right, b.Top),
        SelectionHandle.Top => new Vector2(b.Center.X, b.Bottom),
        SelectionHandle.Right => new Vector2(b.Left, b.Center.Y),
        SelectionHandle.Bottom => new Vector2(b.Center.X, b.Top),
        SelectionHandle.Left => new Vector2(b.Right, b.Center.Y),
        _ => b.Center,
    };
}
