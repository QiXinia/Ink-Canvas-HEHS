using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Core.Shapes;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using System.Numerics;

namespace InkCanvas.Ink.Rendering;

/// <summary>
/// Draws a whole scene (committed ink, live ink, shape preview, selection) into any Win2D
/// drawing session. Shared by the transparent overlay and the WinUI 3 board surface so both
/// render identically.
/// <para>
/// The committed/live split lets the overlay cache the expensive part and only re-read a
/// small live region from the GPU each frame.
/// </para>
/// </summary>
public sealed class InkSceneRenderer : IDisposable
{
    public StrokeRenderer Strokes { get; } = new();
    private readonly StrokeRenderer _extensionStrokes = new();
    public ImageSceneRenderer Images { get; }
    private readonly AiTextSceneRenderer _text = new();
    private readonly FormulaSceneRenderer _formulas = new();

    public void RetainNativeElements(IEnumerable<DrawingObject> items)
    {
        _text.Retain(items.Select(item => item.Element).OfType<IBoardTextElement>());
        _formulas.Retain(items.Select(item => item.Element).OfType<IBoardFormulaElement>());
    }

    public void DrawNativeElement(CanvasDrawingSession session, ISelectionElement element, Matrix3x2 view)
    {
        if (element is IBoardTextElement text) _text.Draw(session, text, view);
        else if (element is IBoardFormulaElement formula) _formulas.Draw(session, formula, view);
    }

    public InkSceneRenderer() : this(0) { }
    public InkSceneRenderer(uint maximumImageDimension) => Images = new() { MaximumDecodeDimension = maximumImageDimension };

    public bool ShowSelectionOutline { get; set; } = true;
    public static bool NeedsLayeredLiveInk(InkController controller) => controller.Playback is null
        && (controller.LiveStrokes.Count > 0 || controller.RecognitionPreview.Count > 0
            || controller.ShapePreview.Count > 0 || controller.ExtensionPreview.Count > 0 || controller.DotAnimations.Count > 0)
        && controller.HasInkAboveActiveLayer;

    /// <summary>Everything that only changes when the document, selection or view changes.</summary>
    public void DrawCommitted(CanvasDrawingSession session, InkController controller, Matrix3x2? transformOverride = null)
    {
        if (controller is null) return;
        var view = transformOverride ?? controller.ViewTransform;
        Images.Retain(controller);
        RetainNativeElements(controller.DrawingObjects);
        foreach (var item in controller.DrawingObjects) DrawObject(session, controller, item, view);

    }

    public void DrawObject(CanvasDrawingSession session, InkController controller, DrawingObject item, Matrix3x2 view)
    {
        DrawNativeElement(session, item.Element, view);
        if (item.Element is IImageSelectionElement { ImageData.Length: > 0 } image) Images.Draw(session, image, view);
        if (item.Stroke is { } stroke && controller.Playback?.Includes(stroke) != true && !controller.IsAnimatingDot(stroke)
            && !controller.IsRecognitionPreviewSource(stroke))
            Strokes.Draw(session, stroke, controller.IsPreviewStroke(stroke) ? controller.SelectionPreviewTransform * view : view);
    }

    /// <summary>The in-progress stroke and shape preview: the only part that moves every frame.</summary>
    /// <param name="transformOverride">
    /// Optional world-to-surface transform, used when rendering into a small region target
    /// whose origin is not the screen origin.
    /// </param>
    public void DrawLive(CanvasDrawingSession session, InkController controller, Matrix3x2? transformOverride = null)
    {
        if (controller is null) return;
        var view = transformOverride ?? controller.ViewTransform;
        DrawLiveInk(session, controller, view);
        DrawAdornments(session, controller, view);
    }

    public void DrawLiveInk(CanvasDrawingSession session, InkController controller, Matrix3x2 view)
    {
        if (controller.Playback is { } playback)
        {
            Strokes.RetainLive(playback.VisibleStrokes);
            foreach (var stroke in playback.VisibleStrokes) Strokes.Draw(session, stroke, view);
            return;
        }
        Strokes.RetainLive(controller.LiveStrokes);
        foreach (var stroke in controller.LiveStrokes)
            if (!controller.IsRecognitionPreviewSource(stroke)) Strokes.Draw(session, stroke, view);
        foreach (var stroke in controller.RecognitionPreview) Strokes.Draw(session, stroke, view);
        foreach (var stroke in controller.ShapePreview)
            Strokes.Draw(session, stroke, view);
        _extensionStrokes.Retain(controller.ExtensionPreview);
        foreach (var stroke in controller.ExtensionPreview)
            _extensionStrokes.Draw(session, stroke, view);
        foreach (var animation in controller.DotAnimations)
            Strokes.Draw(session, animation.Preview, view);
    }

    public void DrawAdornments(CanvasDrawingSession session, InkController controller, Matrix3x2 view)
    {
        if (controller.Playback is not null) return;
        if (controller.IsLassoErasing) DrawLasso(session, controller, view);
        if (ShowSelectionOutline && controller.Tool == InkTool.Select) DrawSelection(session, controller, view);
        if (controller.IsEraserPreviewVisible) DrawEraserPreview(session, controller, view);
        if (controller.IsPenCursorVisible)
        {
            if (Matrix3x2.Invert(controller.ViewTransform, out var inverse))
            {
                var cursorView = inverse * view;
                var center = Vector2.Transform(controller.PenCursorCenter, cursorView);
                float radius = controller.PenCursorRadius * MathF.Sqrt(MathF.Abs(cursorView.GetDeterminant()));
                session.DrawCircle(center, radius, Windows.UI.Color.FromArgb(230, 255, 255, 255), 2);
                session.DrawCircle(center, radius, Windows.UI.Color.FromArgb(230, 30, 30, 30), 1);
            }
        }
    }

    /// <summary>Allows page hosts to show selection handles outside the clipped paper extent.</summary>
    public void DrawSelectionOverlay(CanvasDrawingSession session, InkController controller)
    {
        if (controller?.Playback is null && controller?.Tool == InkTool.Select)
            DrawSelection(session, controller, controller.ViewTransform);
    }

    private static void DrawEraserPreview(CanvasDrawingSession session, InkController controller, Matrix3x2 view)
    {
        float scale = MathF.Sqrt(MathF.Abs(view.GetDeterminant()));
        if (scale < .0001f) return;
        var previous = session.Transform;
        try
        {
            session.Transform = view;
            var center = controller.EraserPreviewCenter;
            float radius = controller.EraserPreviewRadius;
            session.FillCircle(center, radius, Windows.UI.Color.FromArgb(24, 255, 255, 255));
            // Two contrasting outlines remain legible over dark ink, whiteboards and slides.
            session.DrawCircle(center, radius, Windows.UI.Color.FromArgb(220, 0, 0, 0), 2.5f / scale);
            session.DrawCircle(center, radius, Windows.UI.Color.FromArgb(240, 255, 255, 255), 1 / scale);
        }
        finally { session.Transform = previous; }
    }

    public void Draw(CanvasDrawingSession session, InkController controller)
    {
        if (controller is not null && NeedsLayeredLiveInk(controller))
        {
            DrawWithLayeredLiveInk(session, controller, controller.ViewTransform);
            return;
        }
        DrawCommitted(session, controller);
        DrawLive(session, controller);
    }

    public void DrawWithLayeredLiveInk(CanvasDrawingSession session, InkController controller, Matrix3x2 view,
        Rect? viewport = null)
    {
        Images.Retain(controller);
        bool drawnLive = false;
        foreach (var item in controller.DrawingObjects)
        {
            if (!drawnLive && item.InkLayerIndex > controller.ActiveInkLayer)
            {
                DrawLiveInk(session, controller, view);
                drawnLive = true;
            }
            if (viewport is { } bounds && item.Stroke is { } stroke)
            {
                var transform = controller.IsPreviewStroke(stroke) ? controller.SelectionPreviewTransform * view : view;
                if (!CommittedInkLayer.TransformBounds(stroke.Bounds, transform).Intersects(bounds)) continue;
            }
            DrawObject(session, controller, item, view);
        }
        if (!drawnLive) DrawLiveInk(session, controller, view);
        DrawAdornments(session, controller, view);
    }

    private static void DrawLasso(CanvasDrawingSession session, InkController controller, Matrix3x2 view)
    {
        var points = controller.LassoPath;
        if (points.Count == 0 || !Matrix3x2.Invert(controller.ViewTransform, out var inverse)) return;
        var transform = inverse * view;
        float scale = MathF.Sqrt(MathF.Abs(transform.GetDeterminant()));
        var color = Windows.UI.Color.FromArgb(255, 0, 120, 212);
        if (points.Count == 1)
        {
            session.DrawCircle(Vector2.Transform(points[0], transform), 3 * scale, color, scale);
            return;
        }
        using var builder = new CanvasPathBuilder(session);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
        builder.BeginFigure(Vector2.Transform(points[0], transform));
        for (int i = 1; i < points.Count; i++) builder.AddLine(Vector2.Transform(points[i], transform));
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var geometry = CanvasGeometry.CreatePath(builder);
        using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        session.FillGeometry(geometry, Windows.UI.Color.FromArgb(35, 0, 120, 212));
        session.DrawGeometry(geometry, color, 1.5f * scale, dashed);
    }

    private void DrawSelection(CanvasDrawingSession session, InkController controller, Matrix3x2 view)
    {
        if (!Matrix3x2.Invert(controller.ViewTransform, out var inverse)) return;
        var screenToSurface = inverse * view;
        float scale = MathF.Sqrt(MathF.Abs(screenToSurface.GetDeterminant()));
        Vector2 Surface(Vector2 point) => Vector2.Transform(point, screenToSurface);
        var accent = Windows.UI.Color.FromArgb(255, 0, 120, 212);
        var fill = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        if (controller.IsLassoSelecting) DrawLasso(session, controller, view);
        if (controller.IsMarqueeSelecting && !controller.MarqueeBounds.IsEmpty)
        {
            var bounds = controller.MarqueeBounds;
            var topLeft = Surface(new Vector2(bounds.Left, bounds.Top));
            var bottomRight = Surface(new Vector2(bounds.Right, bounds.Bottom));
            session.FillRectangle(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y,
                Windows.UI.Color.FromArgb(32, 0, 120, 212));
            using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            session.DrawRectangle(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y, accent, scale, dash);
        }
        if (!controller.HasSelection) return;
        var controls = controller.ShapeControlPoints;
        if (controls.Count > 0)
        {
            if (controller.ShowPolygonAngles) DrawPolygonAngles(session, view, scale, controls, accent, fill);
            foreach (var control in controls)
            {
                var point = Surface(Vector2.Transform(control, controller.ViewTransform));
                session.FillRectangle(point.X - 5 * scale, point.Y - 5 * scale, 10 * scale, 10 * scale, fill);
                session.DrawRectangle(point.X - 5 * scale, point.Y - 5 * scale, 10 * scale, 10 * scale, accent, 1.5f * scale);
            }
            return;
        }
        var frame = controller.SelectionFrame;
        var corners = frame.Corners(controller.ViewTransform).Select(Surface).ToArray();
        for (int i = 0; i < 4; i++)
            session.DrawLine(corners[i], corners[(i + 1) % 4], accent, scale);
        if (controller.AdvancedSelection is { } advanced)
        {
            DrawAdvancedSelection(session, controller, advanced, view, scale, accent, fill);
            return;
        }
        foreach (var handle in frame.VisibleResizeHandles(controller.ViewTransform))
        {
            var point = Surface(frame.HandlePosition(handle, controller.ViewTransform));
            session.FillCircle(point, 3.5f * scale, fill);
            session.DrawCircle(point, 3.5f * scale, accent, scale);
        }
        var topMid = (corners[0] + corners[1]) * 0.5f;
        var rotate = Surface(frame.HandlePosition(SelectionHandle.Rotate, controller.ViewTransform));
        session.DrawLine(topMid, rotate, accent, scale);
        session.FillCircle(rotate, 10 * scale, fill);
        session.DrawCircle(rotate, 10 * scale, accent, scale);
        // Circular arrow makes rotation distinct from the eight sizing handles.
        var previous = rotate + new Vector2(MathF.Cos(-0.4f), MathF.Sin(-0.4f)) * 5.5f * scale;
        for (int i = 1; i <= 14; i++)
        {
            float angle = -0.4f + i * 4.9f / 14;
            var next = rotate + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 5.5f * scale;
            session.DrawLine(previous, next, accent, 1.5f * scale);
            previous = next;
        }
        session.DrawLine(previous, previous + new Vector2(-3, -1) * scale, accent, 1.5f * scale);
        session.DrawLine(previous, previous + new Vector2(-1, 3) * scale, accent, 1.5f * scale);
        if (controller.IsRotatingSelection)
        {
            using var format = new CanvasTextFormat { FontSize = 12 * scale };
            session.FillRoundedRectangle(rotate.X + 15 * scale, rotate.Y - 12 * scale, 55 * scale, 24 * scale,
                4 * scale, 4 * scale, accent);
            session.DrawText($"{controller.SelectionRotationDegrees:0}°", rotate.X + 22 * scale, rotate.Y - 9 * scale, fill, format);
        }
    }

    private void DrawAdvancedSelection(CanvasDrawingSession session, InkController controller, AdvancedSelectionTransform state,
        Matrix3x2 view, float scale, Windows.UI.Color accent, Windows.UI.Color fill)
    {
        using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        if (state.IsReady && (state.Mode != AdvancedSelectionMode.Rotate || MathF.Abs(state.Angle) > .001f))
        {
            var transformed = state.Matrix * view;
            var bounds = controller.TransformAdvancedSourceBounds(transformed);
            using var preview = session.CreateLayer(.58f,
                new Windows.Foundation.Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            foreach (var stroke in controller.Selection) Strokes.Draw(session, stroke, transformed);
            foreach (var element in controller.SelectedElements)
            {
                if (element is IImageSelectionElement { ImageData.Length: > 0 } image) Images.Draw(session, image, transformed);
                else
                {
                    var corners = element.Frame.Corners(transformed);
                    for (int i = 0; i < 4; i++) session.DrawLine(corners[i], corners[(i + 1) % 4], accent, 2 * scale, dash);
                }
            }
        }
        if (state.First is not { } first) return;
        var anchor = Vector2.Transform(first, view);
        var handle = state.Second ?? controller.AdvancedRotationHandle;
        if (handle is { } second)
        {
            var end = Vector2.Transform(second, view);
            session.DrawLine(anchor, end, fill, 3 * scale);
            session.DrawLine(anchor, end, accent, 1.5f * scale, dash);
            DrawHandle(end);
        }
        DrawHandle(anchor);
        if (state.Mode == AdvancedSelectionMode.AxisReflection) return;
        session.DrawLine(anchor - Vector2.UnitX * 3 * scale, anchor + Vector2.UnitX * 3 * scale, accent, scale);
        session.DrawLine(anchor - Vector2.UnitY * 3 * scale, anchor + Vector2.UnitY * 3 * scale, accent, scale);

        void DrawHandle(Vector2 point)
        {
            session.FillCircle(point, 6 * scale, fill);
            session.DrawCircle(point, 6 * scale, accent, 1.5f * scale);
        }
    }

    private static void DrawPolygonAngles(CanvasDrawingSession session, Matrix3x2 view, float scale,
        IReadOnlyList<Vector2> points, Windows.UI.Color accent, Windows.UI.Color fill)
    {
        var angles = PolygonGeometry.Measure(points);
        if (angles.Length == 0) return;
        using var format = new CanvasTextFormat
        {
            FontSize = 11 * scale, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        var labels = new List<Rect>(angles.Length);
        int reflection = Math.Sign(view.GetDeterminant());
        for (int i = 0; i < angles.Length; i++)
        {
            var angle = angles[i];
            var vertex = Vector2.Transform(angle.Vertex, view);
            var edge = Vector2.TransformNormal(angle.NextEdge, view);
            float start = MathF.Atan2(edge.Y, edge.X);
            float sweep = angle.Degrees * MathF.PI / 180 * angle.Winding * reflection;
            float radius = 12 * scale;
            var previous = vertex + Unit(start) * radius;
            int steps = Math.Clamp((int)(angle.Degrees / 12), 4, 24);
            for (int step = 1; step <= steps; step++)
            {
                var next = vertex + Unit(start + sweep * step / steps) * radius;
                session.DrawLine(previous, next, accent, scale);
                previous = next;
            }

            var direction = -Unit(start + sweep / 2);
            Rect badge = Rect.Empty;
            for (int distance = 36; distance <= 96; distance += 20)
            {
                var p = vertex + direction * distance * scale;
                badge = new(p.X - 31 * scale, p.Y - 10 * scale, p.X + 31 * scale, p.Y + 10 * scale);
                if (!labels.Any(placed => placed.Inflate(2 * scale).Intersects(badge))) break;
            }
            labels.Add(badge);
            session.FillRoundedRectangle(badge.Left, badge.Top, badge.Width, badge.Height, 4 * scale, 4 * scale, fill);
            session.DrawRoundedRectangle(badge.Left, badge.Top, badge.Width, badge.Height, 4 * scale, 4 * scale, accent, scale);
            session.DrawText($"{(char)('A' + i)} {angle.Degrees:0.#}°", new Windows.Foundation.Rect(badge.Left, badge.Top, badge.Width, badge.Height),
                Windows.UI.Color.FromArgb(255, 0, 71, 132), format);
        }
        static Vector2 Unit(float theta) => new(MathF.Cos(theta), MathF.Sin(theta));
    }

    /// <summary>Drop cached geometry for strokes that were mutated in place.</summary>
    public void InvalidateGeometry(IEnumerable<InkStroke> strokes)
    {
        foreach (var s in strokes) Strokes.Invalidate(s);
    }

    public void ReleaseResources() { Images.Clear(); _text.Clear(); _formulas.Clear(); Strokes.InvalidateAll(); _extensionStrokes.InvalidateAll(); }
    public void Dispose() { Images.Dispose(); _text.Dispose(); _formulas.Dispose(); Strokes.Dispose(); _extensionStrokes.Dispose(); }
}
