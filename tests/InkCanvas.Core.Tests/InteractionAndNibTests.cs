using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Replay;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public class InteractionAndNibTests
{
    private static OverlayPointer Pointer(float x, float y, uint id = 1, OverlayPointerKind kind = OverlayPointerKind.Mouse)
        => new(id, x, y, .5f, 0, 0, kind, 0);
    private static InkStroke Stroke(PenNib nib = PenNib.Round)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 20, TaperKind = TaperKind.None, Nib = nib });
        stroke.AddPoint(100, 100); stroke.AddPoint(200, 150); stroke.Complete();
        return stroke;
    }

    [Fact]
    public void TwoFingerPanUsesScreenCoordinatesAtZoomAndKeepsMoving()
    {
        using var controller = new InkController { RecognizeShapes = false, PalmErases = false, MultiTouchEnabled = false,
            ViewTransform = Matrix3x2.CreateScale(2) * Matrix3x2.CreateTranslation(50, -20) };
        controller.ConfigureGestures(false, true, false);
        controller.OnPointerDown(Pointer(100, 100, 1, OverlayPointerKind.Touch));
        controller.OnPointerDown(Pointer(300, 100, 2, OverlayPointerKind.Touch));
        for (int i = 1; i <= 100; i++)
        {
            controller.OnPointerMove(Pointer(100 + i, 100 + i, 1, OverlayPointerKind.Touch));
            controller.OnPointerMove(Pointer(300 + i, 100 + i, 2, OverlayPointerKind.Touch));
        }
        Assert.Equal(150, controller.ViewTransform.M31, 3);
        Assert.Equal(80, controller.ViewTransform.M32, 3);
        Assert.Equal(2, controller.ViewTransform.M11, 4);
        Assert.Empty(controller.LiveStrokes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionPreviewDefersPointEditsAndCommitsOneUndo(bool clone)
    {
        using var controller = new InkController { RecognizeShapes = false };
        var stroke = Stroke(PenNib.Ellipse45); controller.Document.Add(stroke); controller.SelectAll(); controller.CloneMode = clone;
        var before = stroke.Points.ToArray(); long version = stroke.GeometryVersion;
        controller.OnPointerDown(Pointer(150, 125));
        for (int i = 1; i <= 50; i++) controller.OnPointerMove(Pointer(150 + i, 125 + i));
        Assert.Equal(version, stroke.GeometryVersion);
        Assert.Equal(before, stroke.Points.ToArray());
        Assert.Equal(Matrix3x2.CreateTranslation(50, 50), controller.SelectionPreviewTransform);
        controller.OnPointerUp(Pointer(200, 175));
        var moved = controller.Document.Strokes.Last();
        Assert.Equal(new Vector2(150, 150), moved.Points[0].Position);
        Assert.Equal(PenNib.Ellipse45, moved.Attributes.Nib);
        Assert.Equal(1, controller.History.UndoCount);
        Assert.Null(controller.PreviewStrokes);
        controller.Undo();
        Assert.Single(controller.Document.Strokes);
        Assert.Equal(before, stroke.Points.ToArray());
        controller.Redo();
        Assert.Equal(clone ? 2 : 1, controller.Document.Count);
        Assert.Equal(new Vector2(150, 150), controller.Document.Strokes.Last().Points[0].Position);
    }

    [Fact]
    public void EllipseNibDotHasFixedRightSlantedTwoToOneAxes()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 20, TaperKind = TaperKind.None, Nib = PenNib.Ellipse45 });
        stroke.AddPoint(100, 100);
        var contour = StrokeGeometry.BuildUnionContours(stroke).Single();
        var major = Vector2.Normalize(new Vector2(1, -1));
        var minor = Vector2.Normalize(new Vector2(1, 1));
        Assert.Equal(20, contour.Max(p => Vector2.Dot(p, major)) - contour.Min(p => Vector2.Dot(p, major)), 3);
        Assert.Equal(10, contour.Max(p => Vector2.Dot(p, minor)) - contour.Min(p => Vector2.Dot(p, minor)), 3);
        Assert.All(contour, p => Assert.True(stroke.Bounds.Inflate(.001f).Contains(p)));
        Assert.True(StrokeGeometry.SignedArea(contour) > 0);
    }

    [Fact]
    public void NibSurvivesSaveReplayCloneAndRotation()
    {
        var source = Stroke(PenNib.Ellipse45);
        source.Transform(Matrix3x2.CreateRotation(.7f));
        var clone = source.Clone(false);
        Assert.Equal(source.Attributes.NibTransform, clone.Attributes.NibTransform);
        var replay = new InkReplaySession(new[] { clone }).VisibleAt(1).Single();
        Assert.Equal(source.Attributes.NibTransform, replay.Attributes.NibTransform);
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".icink");
        try
        {
            InkFileStore.Save(file, new[] { source });
            var loaded = InkFileStore.Load(file).Single();
            Assert.Equal(source.Attributes.NibTransform, loaded.Attributes.NibTransform);
            Assert.Equal(StrokeGeometry.BuildOutline(source), StrokeGeometry.BuildOutline(loaded));
            File.WriteAllText(file, "{\"Strokes\":[{\"Width\":3,\"Points\":[[10,20,0.5,3,0]]}]}");
            Assert.Equal(PenNib.Round, InkFileStore.Load(file).Single().Attributes.Nib);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void BoardFileAndNewPagePreserveNib()
    {
        var board = new BoardService();
        var source = Stroke(PenNib.Ellipse45);
        source.Transform(Matrix3x2.CreateRotation(.3f));
        board.Current.Document.Add(source);
        board.Current.CurrentAttributes.Nib = PenNib.Ellipse45;
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".icboard");
        try
        {
            board.SaveBoard(file);
            var loaded = new BoardService(); loaded.LoadBoard(file);
            Assert.Equal(source.Attributes.NibTransform, loaded.Current.Document.Strokes.Single().Attributes.NibTransform);
            board.InsertPageAfterCurrent();
            Assert.Equal(PenNib.Ellipse45, board.Current.CurrentAttributes.Nib);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void SelectionGestureDefersAndCommitsAccumulatedTransform()
    {
        using var controller = new InkController { RecognizeShapes = false };
        var source = Stroke(); controller.Document.Add(source); controller.SelectAll();
        var before = source.Points.ToArray();
        controller.BeginSelectionGesture();
        for (int i = 0; i < 100; i++) controller.ApplySelectionGesture(Matrix3x2.CreateTranslation(1, 2));
        Assert.Equal(before, source.Points.ToArray());
        controller.EndSelectionGesture();
        Assert.Equal(new Vector2(200, 300), source.Points[0].Position);
        Assert.Equal(1, controller.History.UndoCount);
        controller.Undo(); Assert.Equal(before, source.Points.ToArray());
    }

    [Fact]
    public void NibReflectsWithSelectedStroke()
    {
        var source = Stroke(PenNib.Ellipse45);
        source.Transform(Matrix3x2.CreateScale(-1, 1));
        var axis = Vector2.TransformNormal(Vector2.UnitX, source.Attributes.NibTransform);
        Assert.True(axis.X * axis.Y > 0);
    }

    [Theory]
    [InlineData(ShapeKind.Hyperbola, false, false)]
    [InlineData(ShapeKind.HyperbolaWithFoci, true, false)]
    [InlineData(ShapeKind.HyperbolaWithAsymptotes, false, true)]
    [InlineData(ShapeKind.HyperbolaWithFociAndAsymptotes, true, true)]
    public void HyperbolaChoiceCommitsDirectlyWithRequestedParts(ShapeKind kind, bool foci, bool asymptotes)
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.SelectShape(kind);
        controller.OnPointerDown(Pointer(100, 100)); controller.OnPointerUp(Pointer(200, 200));
        controller.OnPointerDown(Pointer(150, 150)); controller.OnPointerUp(Pointer(200, 150));
        int guides = ShapeGenerator.Generate(kind, new Vector2(100, 100), new Vector2(200, 200), new()).Count;
        Assert.Equal(4 + (foci ? 2 : 0) + (asymptotes ? guides : 0), controller.Document.Count);
        Assert.Equal(1, controller.History.UndoCount);
        Assert.Empty(controller.ShapePreview);
        controller.Undo(); Assert.Equal(0, controller.Document.Count);
    }

    [Theory]
    [InlineData(60, 40)]
    [InlineData(-60, 40)]
    [InlineData(60, -40)]
    [InlineData(-60, -40)]
    public void CuboidAcceptsSignedDepthAndRebuildsHiddenFirstFace(float dx, float dy)
    {
        using var controller = new InkController { RecognizeShapes = false };
        controller.SelectShape(ShapeKind.Cuboid);
        controller.OnPointerDown(Pointer(100, 100)); controller.OnPointerUp(Pointer(240, 200));
        Assert.Equal(0, controller.Document.Count);
        controller.OnPointerDown(Pointer(170, 150));
        controller.OnPointerUp(Pointer(170 + dx, 150 + dy));
        var allPoints = controller.Document.Strokes.SelectMany(s => s.Points).ToList();
        Assert.Contains(allPoints, p => Vector2.Distance(p.Position, new Vector2(100 + dx, 100 + dy)) < .01f);
        Assert.Equal(1, controller.History.UndoCount);
        var expected = ShapeGenerator.GenerateCuboid(new(100, 100), new(240, 200), new(dx, dy), new());
        Assert.Equal(expected.Count, controller.Document.Count);
        // Nine visible full edges and three hidden dashed edges.
        Assert.Equal(9, controller.Document.Strokes.Count(s => s.ArcLength() >= 70));
        controller.Undo(); Assert.Equal(0, controller.Document.Count);
    }

    [Theory]
    [InlineData(ShapeKind.Circle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Rectangle)]
    public void TwoPointShapesStayInsideDraggedBounds(ShapeKind kind)
    {
        var stroke = ShapeGenerator.Generate(kind, new(300, 250), new(100, 50), new()).Single();
        Assert.All(stroke.Points, p => { Assert.InRange(p.X, 99.99f, 300.01f); Assert.InRange(p.Y, 49.99f, 250.01f); });
    }
}
