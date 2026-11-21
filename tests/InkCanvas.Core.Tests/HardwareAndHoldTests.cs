using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Recognition;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using InkCanvas.Ink.Manipulation;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class HardwareAndHoldTests
{
    private static OverlayPointer Pointer(float x, float y, uint id = 1, OverlayPointerKind kind = OverlayPointerKind.Pen)
        => new(id, x, y, .5f, kind == OverlayPointerKind.Touch ? 4 : 0, 4, kind, 100);

    [Fact]
    public void ScreenSpacePanZoomKeepsBothContactPositionsAttached()
    {
        var manipulator = new TwoFingerManipulator();
        manipulator.Begin(new(0, 0), new(100, 0));
        var transform = Matrix3x2.Identity;
        manipulator.Update(new(10, 20), new(210, 20), ref transform, new(110, 20), screenSpace: true);
        Assert.Equal(new Vector2(10, 20), Vector2.Transform(Vector2.Zero, transform));
        Assert.Equal(new Vector2(210, 20), Vector2.Transform(new(100, 0), transform));
    }

    [Theory]
    [InlineData(TaperKind.None)]
    [InlineData(TaperKind.Tail)]
    [InlineData(TaperKind.SoftPen)]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.Calligraphy)]
    [InlineData(TaperKind.InkWash)]
    public void RealPressureAffectsWidthAndCompletionPreservesGeometry(TaperKind style)
    {
        var light = new InkStroke(new() { BaseWidth = 8, TaperKind = style });
        var heavy = new InkStroke(new() { BaseWidth = 8, TaperKind = style });
        for (int i = 0; i < 60; i++)
        {
            light.AddPoint(i * 4, 100, .1f, i * 8, true, 45, 20);
            heavy.AddPoint(i * 4, 100, .9f, i * 8, true, 45, 20);
        }
        Assert.True(heavy.Points[30].Width > light.Points[30].Width * 2);
        var outline = StrokeGeometry.BuildOutline(heavy);
        heavy.Complete();
        Assert.Equal(outline, StrokeGeometry.BuildOutline(heavy));
    }

    [Fact]
    public void HighlighterFollowsHardwarePressureAndKeepsItsConfiguredWidthWithoutPressure()
    {
        var stroke = new InkStroke(new() { BaseWidth = 30, IsHighlighter = true });
        stroke.AddPoint(0, 0, .1f, 0, true); stroke.AddPoint(30, 0, .9f, 8, true);
        Assert.True(stroke.Points[0].Width < 30);
        Assert.True(stroke.Points[1].Width > stroke.Points[0].Width);
        var touch = new InkStroke(new() { BaseWidth = 30, IsHighlighter = true });
        touch.AddPoint(0, 0, .1f); touch.AddPoint(30, 0, .9f);
        Assert.All(touch.Points, p => Assert.Equal(30, p.Width));
    }

    [Fact]
    public void TiltSurvivesPdfCodecAndTransformsWithTheObject()
    {
        var stroke = new InkStroke(new() { BaseWidth = 12, TaperKind = TaperKind.InkWash });
        stroke.AddPoint(20, 20, .7f, 0, true, 40, 0); stroke.AddPoint(200, 60, .8f, 16, true, 40, 0);
        stroke.Complete();
        var restored = Assert.Single(PdfInkCodec.Decode(PdfInkCodec.Encode(new[] { stroke })));
        Assert.Equal(StrokeGeometry.BuildOutline(stroke), StrokeGeometry.BuildOutline(restored));
        restored.Transform(Matrix3x2.CreateRotation(MathF.PI / 2));
        Assert.InRange(restored.Points[0].TiltY, 39.99f, 40.01f);
        Assert.True(restored.Points[0].HasPressure);
    }

    [Fact]
    public void NewSettingsPersistAndOldSettingsReceiveDefaults()
    {
        var settings = new AppSettings();
        settings.InkToShape.HoldMilliseconds = 800; settings.InkToShape.AlignmentAngle = 8;
        settings.InkToShape.RecognizeAxes = false; settings.Canvas.UsePenTilt = false;
        settings.Gesture.PenHasPriority = false;
        var restored = settings.Clone();
        Assert.Equal(800, restored.InkToShape.HoldMilliseconds);
        Assert.Equal(8, restored.InkToShape.AlignmentAngle);
        Assert.False(restored.InkToShape.RecognizeAxes);
        Assert.False(restored.Canvas.UsePenTilt);
        Assert.False(restored.Gesture.PenHasPriority);
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}").Gesture.PenHasPriority);
    }

    [Fact]
    public void PalmCannotStartPinchAndLatePalmCancelsItsAccidentalInk()
    {
        using var ink = new InkController { MultiTouchEnabled = false, RecognizeShapes = false };
        var first = Pointer(100, 100, 1, OverlayPointerKind.Touch);
        ink.OnPointerDown(first);
        var palm = Pointer(200, 200, 2, OverlayPointerKind.Touch) with { ContactWidth = 70, ContactHeight = 50 };
        ink.OnPointerDown(palm);
        ink.OnPointerMove(palm with { X = 250 });
        Assert.Equal(Matrix3x2.Identity, ink.ViewTransform);
        Assert.Single(ink.LiveStrokes);
        ink.OnPointerUp(palm);
        ink.OnPointerMove(first with { ContactWidth = 60, ContactHeight = 60 });
        Assert.Empty(ink.LiveStrokes);
        ink.OnPointerUp(first);
        Assert.Empty(ink.Document.Strokes);
    }

    [Fact]
    public void PenSuppressesSameScreenPalmUntilLiftAndPreservesOtherScreenWriting()
    {
        using var ink = new InkController { RecognizeShapes = false };
        var touch = Pointer(20, 20, 1, OverlayPointerKind.Touch) with { InputSurface = 1 };
        var other = Pointer(200, 20, 2, OverlayPointerKind.Touch) with { InputSurface = 2 };
        var pen = Pointer(100, 100, 3) with { InputSurface = 1 };
        ink.OnPointerDown(touch); ink.OnPointerDown(other); ink.OnPointerDown(pen);
        Assert.Equal(2, ink.LiveStrokes.Count());
        ink.OnPointerMove(touch with { X = 60, ContactWidth = 80 });
        ink.OnPointerUp(pen); ink.OnPointerMove(touch with { X = 70 }); ink.OnPointerUp(touch);
        ink.OnPointerUp(other);
        Assert.Equal(2, ink.Document.Count);
    }

    [Theory]
    [InlineData(RecognizedShape.Triangle)]
    [InlineData(RecognizedShape.Rectangle)]
    [InlineData(RecognizedShape.Parallelogram)]
    public void ClosedPolygonsFitAtAnySeamAndIgnoreStationaryJitter(RecognizedShape expected)
    {
        Vector2[] corners = expected switch
        {
            RecognizedShape.Triangle => new[] { new Vector2(100, 100), new Vector2(300, 100), new Vector2(200, 280) },
            RecognizedShape.Parallelogram => new[] { new Vector2(100, 100), new Vector2(300, 100), new Vector2(360, 260), new Vector2(160, 260) },
            _ => new[] { new Vector2(100, 100), new Vector2(300, 100), new Vector2(300, 260), new Vector2(100, 260) },
        };
        var points = new List<Vector2>();
        for (int i = 0; i < corners.Length; i++)
        for (int j = 0; j < 30; j++) points.Add(Vector2.Lerp(corners[i], corners[(i + 1) % corners.Length], j / 30f));
        points = points.Skip(15).Concat(points.Take(15)).ToList(); points.Add(points[0]);
        var stroke = new InkStroke(new() { TaperKind = TaperKind.None });
        foreach (var point in points) stroke.AddPoint(point.X, point.Y);
        for (int i = 0; i < 120; i++) stroke.AddPoint(points[0].X + i % 2 * .3f, points[0].Y);
        Assert.Equal(expected, ShapeRecognizer.RecognizeHeld(new[] { stroke }).Shape);
    }

    [Fact]
    public void HeldLinePreviewsWhileDownRefinesAndUndoRestoresTheOriginal()
    {
        using var ink = new InkController();
        ink.OnPointerDown(Pointer(100, 100));
        for (uint i = 1; i <= 30; i++) ink.OnPointerMove(Pointer(100 + i * 5, 100 + i * .1f) with { TimeMs = 100 + i * 8 });
        var original = Assert.Single(ink.LiveStrokes);
        ink.AdvanceShapeHold(449); Assert.Empty(ink.RecognitionPreview);
        ink.AdvanceShapeHold(1); Assert.Single(ink.RecognitionPreview);
        ink.OnPointerMove(Pointer(310, 102) with { TimeMs = 900 });
        ink.OnPointerUp(Pointer(310, 102) with { TimeMs = 901 });
        var shape = Assert.Single(ink.Document.Strokes);
        Assert.True(shape.IsRecognizedShape); Assert.InRange(shape.Bounds.Right, 309, 314);
        Assert.Empty(ink.RecognitionPreview);
        ink.Undo(); Assert.Same(original, Assert.Single(ink.Document.Strokes));
    }

    [Fact]
    public void CubeRecognitionRequiresAllTwelveEdges()
    {
        var vertices = new Vector2[8];
        for (int i = 0; i < 8; i++) vertices[i] = new Vector2(100, 160)
            + ((i & 1) != 0 ? new Vector2(150, 0) : Vector2.Zero)
            + ((i & 2) != 0 ? new Vector2(0, 150) : Vector2.Zero)
            + ((i & 4) != 0 ? new Vector2(70, -60) : Vector2.Zero);
        var strokes = new List<InkStroke>();
        for (int mask = 0; mask < 8; mask++)
        for (int axis = 0; axis < 3; axis++)
            if ((mask & (1 << axis)) == 0)
                strokes.Add(ShapeGenerator.Stroke(new() { TaperKind = TaperKind.None }, vertices[mask], vertices[mask | (1 << axis)]));
        Assert.Equal(RecognizedShape.Cube, ShapeRecognizer.RecognizeHeld(strokes).Shape);
        strokes.RemoveAt(0);
        Assert.NotEqual(RecognizedShape.Cube, ShapeRecognizer.RecognizeHeld(strokes).Shape);
    }

    [Fact]
    public void AxisHoldReplacesBothStrokesAsOneSelectableObjectAndUndoRestoresThem()
    {
        using var ink = new InkController();
        ink.OnPointerDown(Pointer(100, 200));
        for (uint i = 1; i <= 40; i++) ink.OnPointerMove(Pointer(100 + i * 5, 200) with { TimeMs = 100 + i * 8 });
        ink.OnPointerUp(Pointer(300, 200) with { TimeMs = 421 });
        var original = Assert.Single(ink.Document.Strokes);
        ink.OnPointerDown(Pointer(200, 100, 2));
        for (uint i = 1; i <= 40; i++) ink.OnPointerMove(Pointer(200, 100 + i * 5, 2) with { TimeMs = 500 + i * 8 });
        ink.AdvanceShapeHold(450);
        Assert.True(ink.RecognitionPreview.Count > 1);
        var observed = new List<InkStroke[]>();
        ink.Document.Changed += () => observed.Add(ink.Document.Strokes.ToArray());
        ink.OnPointerUp(Pointer(200, 300, 2) with { TimeMs = 1300 });
        Assert.All(ink.Document.Strokes, stroke => Assert.Equal("CoordinateAxes", stroke.ShapeSource.Definition));
        Assert.Single(ink.Document.Strokes.Select(stroke => stroke.ShapeSource.GroupId).Distinct());
        Assert.Single(observed);
        Assert.All(observed[0], stroke => Assert.True(stroke.IsRecognizedShape));
        ink.Undo(); Assert.Equal(2, ink.Document.Count); Assert.Contains(original, ink.Document.Strokes);
    }

    [Fact]
    public void RecognitionSettingsControlPreviewAndAlignment()
    {
        using var ink = new InkController();
        ink.RecognitionOptions.HoldMilliseconds = 800;
        ink.RecognitionOptions.AutoAlign = false;
        ink.OnPointerDown(Pointer(100, 100));
        for (uint i = 1; i <= 40; i++) ink.OnPointerMove(Pointer(100 + i * 5, 100 + i * .25f) with { TimeMs = 100 + i * 8 });
        ink.AdvanceShapeHold(799); Assert.Empty(ink.RecognitionPreview);
        ink.AdvanceShapeHold(1);
        var shape = Assert.Single(ink.RecognitionPreview);
        Assert.True(MathF.Abs(shape.Points[^1].Y - shape.Points[0].Y) > 5);
        ink.OnPointerCanceled(Pointer(300, 110)); Assert.Empty(ink.RecognitionPreview);
        Assert.False(Assert.Single(ink.Document.Strokes).IsRecognizedShape);
    }
}
