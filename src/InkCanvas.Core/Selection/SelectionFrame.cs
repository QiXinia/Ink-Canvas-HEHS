using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Selection;

/// <summary>An oriented selection frame, independent of document zoom and native DPI.</summary>
public readonly record struct SelectionFrame(Rect Bounds, Matrix3x2 Transform)
{
    public static readonly SelectionHandle[] ResizeHandles =
    { SelectionHandle.TopLeft, SelectionHandle.Top, SelectionHandle.TopRight, SelectionHandle.Right,
        SelectionHandle.BottomRight, SelectionHandle.Bottom, SelectionHandle.BottomLeft, SelectionHandle.Left };

    public Vector2[] Corners(Matrix3x2 view) => new[]
    {
        Vector2.Transform(new Vector2(Bounds.Left, Bounds.Top), Transform * view),
        Vector2.Transform(new Vector2(Bounds.Right, Bounds.Top), Transform * view),
        Vector2.Transform(new Vector2(Bounds.Right, Bounds.Bottom), Transform * view),
        Vector2.Transform(new Vector2(Bounds.Left, Bounds.Bottom), Transform * view),
    };

    public Vector2 Center(Matrix3x2 view) => Vector2.Transform(Bounds.Center, Transform * view);

    public bool Contains(Vector2 point, Matrix3x2 view)
        => !Bounds.IsEmpty && Matrix3x2.Invert(Transform * view, out var inverse)
            && Bounds.Contains(Vector2.Transform(point, inverse));

    public bool Intersects(Rect rectangle, Matrix3x2 view)
    {
        if (Bounds.IsEmpty || rectangle.IsEmpty) return false;
        var corners = Corners(view);
        for (int i = 0; i < corners.Length; i++)
            if (InkDocument.SegmentIntersectsRect(corners[i], corners[(i + 1) % corners.Length], rectangle)) return true;
        return Contains(new Vector2(rectangle.Left, rectangle.Top), view);
    }

    /// <summary>Omit overlapping midpoint handles on short or thin objects so their body stays draggable.</summary>
    public IEnumerable<SelectionHandle> VisibleResizeHandles(Matrix3x2 view)
    {
        var transform = Transform * view;
        float width = Vector2.TransformNormal(new Vector2(Bounds.Width, 0), transform).Length();
        float height = Vector2.TransformNormal(new Vector2(0, Bounds.Height), transform).Length();
        foreach (var handle in ResizeHandles)
        {
            if (handle is SelectionHandle.Top or SelectionHandle.Bottom && (width < 48 || height < 24)) continue;
            if (handle is SelectionHandle.Left or SelectionHandle.Right && (height < 48 || width < 24)) continue;
            yield return handle;
        }
    }

    public Vector2 HandlePosition(SelectionHandle handle, Matrix3x2 view)
    {
        var transform = Transform * view;
        if (handle == SelectionHandle.Rotate)
        {
            var top = Vector2.Transform(new Vector2(Bounds.Center.X, Bounds.Top), transform);
            var direction = Vector2.TransformNormal(new Vector2(0, -Bounds.Height / 2), transform);
            if (direction.LengthSquared() < 0.001f) direction = new Vector2(0, -1);
            return top + Vector2.Normalize(direction) * SelectionGeometry.RotateOffset;
        }
        var local = handle switch
        {
            SelectionHandle.TopLeft => new Vector2(Bounds.Left, Bounds.Top),
            SelectionHandle.TopRight => new Vector2(Bounds.Right, Bounds.Top),
            SelectionHandle.BottomRight => new Vector2(Bounds.Right, Bounds.Bottom),
            SelectionHandle.BottomLeft => new Vector2(Bounds.Left, Bounds.Bottom),
            SelectionHandle.Top => new Vector2(Bounds.Center.X, Bounds.Top),
            SelectionHandle.Right => new Vector2(Bounds.Right, Bounds.Center.Y),
            SelectionHandle.Bottom => new Vector2(Bounds.Center.X, Bounds.Bottom),
            SelectionHandle.Left => new Vector2(Bounds.Left, Bounds.Center.Y),
            _ => Bounds.Center,
        };
        return Vector2.Transform(local, transform);
    }

    public SelectionHandle HitTest(Vector2 point, Matrix3x2 view)
    {
        if (Bounds.IsEmpty) return SelectionHandle.None;
        if (Vector2.Distance(point, HandlePosition(SelectionHandle.Rotate, view)) <= SelectionGeometry.HandleRadius)
            return SelectionHandle.Rotate;
        var nearest = SelectionHandle.None;
        float distance = SelectionGeometry.HandleRadius;
        foreach (var handle in VisibleResizeHandles(view))
        {
            var position = HandlePosition(handle, view);
            float radius = MathF.Min(SelectionGeometry.HandleRadius, Vector2.Distance(position, Center(view)) * 0.65f);
            float current = Vector2.Distance(point, position);
            if (current <= radius && current < distance) { nearest = handle; distance = current; }
        }
        return nearest != SelectionHandle.None ? nearest : Contains(point, view) ? SelectionHandle.Move : SelectionHandle.None;
    }

    public Matrix3x2 Drag(SelectionHandle handle, Vector2 from, Vector2 to, Matrix3x2 view,
        bool constrain, out float rotation)
    {
        rotation = 0;
        if (!Matrix3x2.Invert(view, out var inverseView)) return Matrix3x2.Identity;
        if (handle == SelectionHandle.Move) return Matrix3x2.CreateTranslation(Vector2.TransformNormal(to - from, inverseView));
        if (handle == SelectionHandle.Rotate)
        {
            var center = Center(view);
            var a = from - center; var b = to - center;
            if (a.LengthSquared() < 0.001f || b.LengthSquared() < 0.001f) return Matrix3x2.Identity;
            rotation = MathF.Atan2(b.Y, b.X) - MathF.Atan2(a.Y, a.X);
            if (constrain)
            {
                float initial = MathF.Atan2(Transform.M12, Transform.M11);
                float step = MathF.PI / 12;
                rotation = MathF.Round((initial + rotation) / step) * step - initial;
            }
            var delta = Matrix3x2.CreateTranslation(-center) * Matrix3x2.CreateRotation(rotation)
                * Matrix3x2.CreateTranslation(center);
            return view * delta * inverseView;
        }
        if (!Matrix3x2.Invert(Transform * view, out var inverse) || !Matrix3x2.Invert(Transform, out var inverseFrame))
            return Matrix3x2.Identity;
        var local = SelectionGeometry.DragMatrix(handle, Bounds, Vector2.Transform(from, inverse),
            Vector2.Transform(to, inverse), constrain);
        return inverseFrame * local * Transform;
    }
}
