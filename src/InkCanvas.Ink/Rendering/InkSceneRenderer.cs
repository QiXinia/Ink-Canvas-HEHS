using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
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
    public AiTextSceneRenderer TextObjects { get; } = new();
    public FormulaSceneRenderer Formulas { get; } = new();

    public InkSceneRenderer() : this(0) { }
    public InkSceneRenderer(uint maximumImageDimension) => Images = new() { MaximumDecodeDimension = maximumImageDimension };

    public bool ShowSelectionOutline { get; set; } = true;

    private readonly HashSet<Guid> _loggedElementDraws = new();
    private readonly HashSet<InkController> _loggedPageSummaries = new();

    /// <summary>
    /// Draws one ordered native text/formula run. The tiled <see cref="CommittedInkLayer"/> uses this
    /// instead of its stroke-only path; every element logs once at its first real canvas draw.
    /// </summary>
    public void DrawElement(CanvasDrawingSession session, InkController controller, ISelectionElement element, Matrix3x2 view)
    {
        if (element is null || !element.IsPresent || controller is null) return;
        if (element is IBoardTextElement text)
        {
            LogFirstElementDraw(controller, element);
            TextObjects.Draw(session, text, view);
        }
        else if (element is IBoardFormulaElement formula)
        {
            LogFirstElementDraw(controller, element);
            Formulas.Draw(session, formula, view);
        }
    }

    private void LogFirstElementDraw(InkController controller, ISelectionElement element)
    {
        if (_loggedPageSummaries.Add(controller))
        {
            var objects = controller.DrawingObjects;
            int texts = objects.Count(item => item.Element is IBoardTextElement);
            int formulas = objects.Count(item => item.Element is IBoardFormulaElement);
            int images = objects.Count(item => item.Element is IImageSelectionElement);
            int strokes = objects.Count(item => item.Stroke is not null);
            FileLog.WriteLogToFile(
                $"[AI] 画布绘制页摘要：page={controller.GetHashCode():X8} texts={texts} "
                + $"formulas={formulas} images={images} strokes={strokes}",
                LogType.Trace);
        }
        if (!_loggedElementDraws.Add(element.Id)) return;
        var frame = element.Frame;
        string detail = element switch
        {
            IBoardTextElement text => $"kind=text type={text.Type} chars={text.Content?.Length ?? 0} "
                + $"font={text.FontFamily} fontSize={text.FontSize:0.#} lineHeight={text.LineHeight:0.##} "
                + $"align={text.Align} color={text.Color}",
            IBoardFormulaElement formula => $"kind=formula latex={Shorten(formula.Latex)} "
                + $"font={formula.FontFamily} fontSize={formula.FontSize:0.#} color={formula.Color}",
            _ => "kind=other",
        };
        FileLog.WriteLogToFile(
            $"[AI] 画布元素绘制入口：page={controller.GetHashCode():X8} id={element.Id} "
            + $"left={frame.Bounds.Left:0.##} top={frame.Bounds.Top:0.##} "
            + $"w={frame.Bounds.Width:0.##} h={frame.Bounds.Height:0.##} {detail}",
            LogType.Trace);
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        return text.Length <= 80 ? text : text[..80] + "…";
    }

    /// <summary>Everything that only changes when the document, selection or view changes.</summary>
    public void DrawCommitted(CanvasDrawingSession session, InkController controller, Matrix3x2? transformOverride = null)
    {
        if (controller is null) return;
        var view = transformOverride ?? controller.ViewTransform;
        Images.Retain(controller);
        var elements = controller.DrawingObjects.Select(item => item.Element).ToArray();
        TextObjects.Retain(elements.OfType<IBoardTextElement>());
        Formulas.Retain(elements.OfType<IBoardFormulaElement>());
        foreach (var item in controller.DrawingObjects) DrawObject(session, controller, item, view);

    }

    public void DrawObject(CanvasDrawingSession session, InkController controller, DrawingObject item, Matrix3x2 view)
    {
        if (item.Element is IBoardTextElement text)
            TextObjects.Draw(session, text, view);
        else if (item.Element is IBoardFormulaElement formula)
            Formulas.Draw(session, formula, view);
        else if (item.Element is IImageSelectionElement { ImageData.Length: > 0 } image)
            Images.Draw(session, image, view);
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
        DrawCommitted(session, controller);
        DrawLive(session, controller);
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

    private static void DrawSelection(CanvasDrawingSession session, InkController controller, Matrix3x2 view)
    {
        if (!Matrix3x2.Invert(controller.ViewTransform, out var inverse)) return;
        var screenToSurface = inverse * view;
        float scale = MathF.Sqrt(MathF.Abs(screenToSurface.GetDeterminant()));
        Vector2 Surface(Vector2 point) => Vector2.Transform(point, screenToSurface);
        var accent = Windows.UI.Color.FromArgb(255, 0, 120, 212);
        var fill = Windows.UI.Color.FromArgb(255, 255, 255, 255);
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
        if (controller.ShapeControlPoints.Count > 0)
        {
            foreach (var control in controller.ShapeControlPoints)
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

    /// <summary>Drop cached geometry for strokes that were mutated in place.</summary>
    public void InvalidateGeometry(IEnumerable<InkStroke> strokes)
    {
        foreach (var s in strokes) Strokes.Invalidate(s);
    }

    public void ReleaseResources()
    {
        Images.Clear();
        TextObjects.Clear();
        Formulas.Clear();
        _loggedElementDraws.Clear();
        _loggedPageSummaries.Clear();
        Strokes.InvalidateAll();
        _extensionStrokes.InvalidateAll();
    }

    public void Dispose()
    {
        Images.Dispose();
        TextObjects.Dispose();
        Formulas.Dispose();
        Strokes.Dispose();
        _extensionStrokes.Dispose();
    }
}
