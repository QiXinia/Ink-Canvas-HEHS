using System.Numerics;

namespace InkCanvas.Core.Ink;

/// <summary>
/// Builds filled regions for variable-width ink. Each centerline segment becomes its own
/// consistently-wound capsule. The renderer groups those regions with the non-zero winding
/// rule, making self-crossings a union rather than an even/odd boolean hole.
/// </summary>
public static class StrokeGeometry
{
    /// <summary>
    /// Segment capsules whose Winding-rule union is the complete stroke.
    /// Adjacent capsules overlap at their round caps; they are grouped once, so translucent ink
    /// is composited once per stroke even when the centerline crosses itself.
    /// </summary>
    public static Vector2[][] BuildUnionContours(InkStroke stroke, float widthScale = 1)
    {
        if (stroke is null) throw new ArgumentNullException(nameof(stroke));
        var points = stroke.Points;
        if (points.Count == 0) return Array.Empty<Vector2[]>();
        var result = new Vector2[Math.Max(1, points.Count - 1)][];
        for (int i = 0; i < result.Length; i++)
        {
            var buffer = new Vector2[26];
            int count = WriteInkSegment(stroke, i, widthScale, buffer);
            result[i] = buffer.AsSpan(0, count).ToArray();
        }
        return result;
    }

    /// <summary>Shared tilt projection for live ink, committed ink, thumbnails and PDF vectors.</summary>
    public static int WriteInkSegment(InkStroke stroke, int segment, float widthScale, Span<Vector2> outline)
    {
        var first = stroke.Points[segment];
        var last = stroke.Points[Math.Min(segment + 1, stroke.Count - 1)];
        var nib = stroke.Attributes.NibTransform;
        float minimumWidth = first.HasPressure || last.HasPressure ? .05f : .5f;
        float firstRadius = MathF.Max(first.Width, minimumWidth) * .5f * widthScale;
        float lastRadius = MathF.Max(last.Width, minimumWidth) * .5f * widthScale;
        if (first.TiltX == 0 && first.TiltY == 0 && last.TiltX == 0 && last.TiltY == 0
            || stroke.Count == 1 || stroke.Attributes.IsHighlighter || stroke.IsRecognizedShape)
            return WriteSegmentOutline(first.Position, firstRadius, last.Position, lastRadius, outline, nib);
        var tilt = new Vector2(first.TiltX + last.TiltX, first.TiltY + last.TiltY) * .5f;
        float strength = Math.Clamp(tilt.Length() / 75, 0, 1);
        var offset = Vector2.Zero;
        if (strength > .001f)
        {
            float angle = MathF.Atan2(tilt.Y, tilt.X);
            nib *= Matrix3x2.CreateRotation(-angle) * Matrix3x2.CreateScale(1 + .45f * strength, 1 - .12f * strength)
                * Matrix3x2.CreateRotation(angle);
            // The narrow dark core follows the lean of the brush inside the translucent wash.
            offset = Vector2.Normalize(tilt) * (strength * stroke.Attributes.BaseWidth * .08f * (1 - widthScale));
        }
        return WriteSegmentOutline(first.Position + offset, firstRadius, last.Position + offset, lastRadius, outline, nib);
    }

    /// <summary>
    /// A round-ended, optionally variable-width segment. Its contour is always positive-winding,
    /// regardless of the direction the pointer travelled.
    /// </summary>
    public static Vector2[] BuildSegmentOutline(Vector2 start, float startRadius, Vector2 end, float endRadius, Matrix3x2? nib = null)
    {
        var buffer = new Vector2[26];
        int count = WriteSegmentOutline(start, startRadius, end, endRadius, buffer, nib);
        return count == buffer.Length ? buffer : buffer.AsSpan(0, count).ToArray();
    }

    public static int WriteSegmentOutline(Vector2 start, float startRadius, Vector2 end, float endRadius,
        Span<Vector2> outline, Matrix3x2? nib)
    {
        if (nib is null || nib == Matrix3x2.Identity) return WriteSegmentOutline(start, startRadius, end, endRadius, outline);
        Matrix3x2.Invert(nib.Value, out var inverse);
        int count = WriteSegmentOutline(Vector2.Zero, startRadius, Vector2.TransformNormal(end - start, inverse), endRadius, outline);
        for (int i = 0; i < count; i++) outline[i] = start + Vector2.TransformNormal(outline[i], nib.Value);
        return count;
    }

    /// <summary>Writes the same capsule into caller-owned scratch memory, avoiding per-sample arrays.</summary>
    public static int WriteSegmentOutline(Vector2 start, float startRadius, Vector2 end, float endRadius, Span<Vector2> outline)
    {
        if (outline.Length < 26) throw new ArgumentException("A capsule needs space for 26 vertices.", nameof(outline));
        var delta = end - start;
        float length = delta.Length();
        if (length < 1e-4f)
        {
            float radius = MathF.Max(startRadius, endRadius);
            for (int i = 0; i < 24; i++)
            {
                float angle = 2 * MathF.PI * i / 24;
                outline[i] = start + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            }
            return 24;
        }

        var tangent = delta / length;
        var normal = new Vector2(-tangent.Y, tangent.X);
        float a0 = MathF.Atan2(normal.Y, normal.X);
        int count = 0;
        outline[count++] = start + normal * startRadius;
        outline[count++] = end + normal * endRadius;
        AppendArc(outline, ref count, end, endRadius, a0);
        outline[count++] = end - normal * endRadius;
        outline[count++] = start - normal * startRadius;
        AppendArc(outline, ref count, start, startRadius, a0 + MathF.PI);
        double area = 0;
        for (int i = 0; i < count; i++)
        {
            var a = outline[i]; var b = outline[(i + 1) % count];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        if (area < 0) outline[..count].Reverse();
        return count;
    }

    public static float SignedArea(IReadOnlyList<Vector2> points)
    {
        if (points is null || points.Count < 3) return 0;
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return (float)(area * 0.5);
    }

    private static void AppendArc(Span<Vector2> points, ref int count, Vector2 center, float radius, float startAngle)
    {
        if (radius <= 0) return;
        for (int i = 1; i < 12; i++)
        {
            float angle = startAngle - MathF.PI * i / 12;
            points[count++] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
    }

    public static Vector2[] Circle(Vector2 center, float radius, int segments)
    {
        var result = new Vector2[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = 2 * MathF.PI * i / segments;
            result[i] = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
        }
        return result;
    }

    /// <summary>Legacy single-outline helper used by replay comparisons and compatibility tests.</summary>
    public static Vector2[] BuildOutline(InkStroke stroke)
    {
        if (stroke is null) throw new ArgumentNullException(nameof(stroke));
        var contours = BuildUnionContours(stroke);
        if (contours.Length == 0) return Array.Empty<Vector2>();
        if (contours.Length == 1) return contours[0];
        // Flattening is for diagnostics/equality only. Production rendering uses the separate
        // contours grouped with Winding; connecting this list would reintroduce self-intersections.
        return contours.SelectMany(c => c).ToArray();
    }
}
