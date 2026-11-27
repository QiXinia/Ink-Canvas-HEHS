using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Board;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class EditableShapeTests
{
    private static StrokeAttributes Attributes() => new() { BaseWidth = 4, TaperKind = TaperKind.None };
    public static IEnumerable<object[]> Kinds => Enum.GetValues<ShapeKind>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryPresetRetainsItsConstructionInInkAndPdfFiles(ShapeKind kind)
    {
        var strokes = ShapeGenerator.IsTwoStepSolid(kind)
            ? ShapeGenerator.GenerateSolid(kind, new(100, 100), new(260, 230), new(-40, 50), Attributes())
            : ShapeGenerator.Generate(kind, new(100, 100), new(260, 230), Attributes());
        Assert.NotEmpty(strokes);
        var source = Assert.Single(strokes.Select(s => s.ShapeSource).Distinct());
        Assert.Equal(kind, source.Geometry.Kind); Assert.True(source.Geometry.IsValid);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".icink");
        try
        {
            InkFileStore.Save(path, strokes);
            var loaded = InkFileStore.Load(path);
            var data = Assert.Single(loaded.Select(s => s.ShapeSource).Distinct());
            Assert.All(loaded, stroke => Assert.Same(data, stroke.ShapeSource));
            Assert.Equal(source, data);
            var rebuilt = data.Geometry.Generate(Attributes(), data.GroupId);
            Assert.Equal(strokes.Count, rebuilt.Count);
            for (int i = 0; i < strokes.Count; i++) Assert.Equal(strokes[i].Points, rebuilt[i].Points);
            Assert.All(PdfInkCodec.Decode(PdfInkCodec.Encode(strokes)), s => Assert.Equal(source, s.ShapeSource));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ShapePreviewAndCommitUseScreenAxesWhenTheCanvasIsRotated()
    {
        using var controller = new InkController { Tool = InkTool.Shape, ShapeKind = ShapeKind.Rectangle };
        var view = Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateRotation(.63f) * Matrix3x2.CreateTranslation(70, 35);
        controller.ViewTransform = view;
        Vector2 start = new(100, 125), end = new(250, 205);
        var expected = ShapeGenerator.Generate(ShapeKind.Rectangle, start, end, Attributes()).Single();

        controller.OnPointerDown(Pointer(start));
        controller.OnPointerMove(Pointer(end));
        CheckScreenCorners(Assert.Single(controller.ShapePreview));
        controller.OnPointerUp(Pointer(end));

        var stroke = Assert.Single(controller.Document.Strokes);
        CheckScreenCorners(stroke);
        CheckScreenCorners(Assert.Single(stroke.ShapeSource.Geometry.Generate(Attributes(), stroke.ShapeSource.GroupId)));

        void CheckScreenCorners(InkStroke actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int index = 0; index < actual.Count; index++)
                Close(expected.Points[index].Position, Vector2.Transform(actual.Points[index].Position, view));
        }
    }

    [Fact]
    public void ControlDragRespectsRotationZoomAndOneUndoPerGesture()
    {
        using var c = new InkController { Tool = InkTool.Select };
        c.Document.AddRange(ShapeGenerator.Generate(ShapeKind.Triangle, new(100, 100), new(280, 250), Attributes()));
        c.SelectAll(); c.TransformSelectionUndoable(Matrix3x2.CreateRotation(.4f, new(190, 170)), "rotate");
        c.ViewTransform = Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateTranslation(30, 50);
        c.ToggleShapeEditing();
        var original = c.ShapeControlPoints.ToArray();
        var from = Vector2.Transform(original[0], c.ViewTransform); var to = from + new Vector2(35, -25);
        int history = c.History.UndoCount;
        c.OnPointerDown(Pointer(from)); c.OnPointerMove(Pointer(to)); c.OnPointerUp(Pointer(to));
        Assert.Equal(history + 1, c.History.UndoCount);
        Close(to, Vector2.Transform(c.ShapeControlPoints[0], c.ViewTransform)); Close(original[1], c.ShapeControlPoints[1]);
        c.Undo(); Close(original[0], c.ShapeControlPoints[0]);
        c.Redo(); Close(to, Vector2.Transform(c.ShapeControlPoints[0], c.ViewTransform));
    }

    [Fact]
    public void CanceledDragAndClickPreserveTheOriginalShapeAndHistory()
    {
        using var c = new InkController { Tool = InkTool.Select };
        c.Document.AddRange(ShapeGenerator.Generate(ShapeKind.Rectangle, new(100, 100), new(200, 220), Attributes()));
        c.SelectAll(); c.ToggleShapeEditing();
        var before = c.Selection.ToArray(); var point = c.ShapeControlPoints[0];
        c.OnPointerDown(Pointer(point)); c.OnPointerUp(Pointer(point)); Assert.Equal(0, c.History.UndoCount);
        c.OnPointerDown(Pointer(point)); c.OnPointerMove(Pointer(point + new Vector2(40, 20)));
        c.OnPointerCanceled(Pointer(point + new Vector2(40, 20)));
        Assert.Equal(before, c.Document.Strokes); Assert.Equal(0, c.History.UndoCount);
    }

    [Fact]
    public async Task FillAndLineStylePersistInBoardAndUndoWithoutChangingTheOtherColor()
    {
        var board = new BoardService(); var loaded = new BoardService();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".icink");
        try
        {
            var c = board.Current;
            c.Document.AddRange(ShapeGenerator.Generate(ShapeKind.Rectangle, new(100, 100), new(320, 260), Attributes())); c.SelectAll();
            var red = InkColor.Opaque(220, 38, 38);
            c.SetShapeFill(red); c.SetShapeLineStyle(ShapeLineStyle.Dashed); Assert.True(c.Document.Count > 5);
            var fill = Assert.Single(c.Document.Strokes, s => s.ShapeSource.Geometry.IsFill); Assert.Equal(0, fill.DistanceTo(new(200, 180)));
            c.RecolorSelection(InkColor.Opaque(0, 0, 255)); Assert.Equal(red, fill.Attributes.Color);
            await board.SaveBoardAsync(path); await loaded.LoadBoardAsync(path);
            Assert.All(loaded.Current.Document.Strokes, s => Assert.Equal(ShapeLineStyle.Dashed, s.ShapeSource.Geometry.LineStyle));
            loaded.Current.SelectAll(); loaded.Current.SetShapeLineStyle(ShapeLineStyle.Solid); Assert.Equal(2, loaded.Current.Document.Count);
            loaded.Current.Undo(); Assert.True(loaded.Current.Document.Count > 5);
            loaded.Current.Redo(); loaded.Current.SetShapeFill(null); Assert.Single(loaded.Current.Document.Strokes);
        }
        finally { foreach (var page in board.Pages.Concat(loaded.Pages)) page.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void ConnectorFollowsDuringDraggingUndoAndClonesLinkToCopiedNodes()
    {
        using var c = Connected();
        var connector = c.Document.Strokes.Single(s => s.ShapeSource.Geometry.Construction == ShapeConstruction.Connector);
        var source = connector.ShapeSource.Geometry;
        c.ClearSelection(); c.Selection.AddRange(c.Document.Strokes.Where(s => s.ShapeSource.GroupId == source.StartNode));
        var originalEnd = connector.Points[0].Position; var press = c.SelectionBounds.Center;
        c.OnPointerDown(Pointer(press)); c.OnPointerMove(Pointer(press + new Vector2(30, 45)));
        Assert.True(Vector2.Distance(originalEnd, connector.Points[0].Position) > 20);
        c.OnPointerUp(Pointer(press + new Vector2(30, 45)));
        c.Undo(); Close(originalEnd, connector.Points[0].Position); c.Redo();
        var cloned = InkStroke.CloneObjects(c.Document.Strokes.Reverse());
        var copy = cloned.Single(s => s.ShapeSource.Geometry.Construction == ShapeConstruction.Connector).ShapeSource.Geometry;
        Assert.NotEqual(source.StartNode, copy.StartNode);
        Assert.Contains(cloned, s => s.ShapeSource.GroupId == copy.StartNode); Assert.Contains(cloned, s => s.ShapeSource.GroupId == copy.EndNode);
        Assert.Equal(source.StartNode, connector.ShapeSource.Geometry.StartNode);
    }

    [Fact]
    public void DashedConnectorTracksNodesUntilDetached()
    {
        using var c = Connected();
        var connector = c.Document.Strokes.Single(s => s.ShapeSource.Geometry.Construction == ShapeConstruction.Connector);
        c.ClearSelection(); c.Selection.Add(connector); c.SetShapeLineStyle(ShapeLineStyle.Dashed); connector = c.Selection.Single();
        Assert.True(ShapeDefinition.RenderContours(connector).Count > 2);
        var source = connector.ShapeSource.Geometry;
        c.ClearSelection(); c.Selection.AddRange(c.Document.Strokes.Where(s => s.ShapeSource.GroupId == source.EndNode));
        c.TransformSelectionUndoable(Matrix3x2.CreateTranslation(40, -30), "move"); var end = connector.Points[1].Position;
        c.ClearSelection(); c.Selection.Add(connector); c.DetachShapeConnections(); connector = c.Selection.Single();
        c.ClearSelection(); c.Selection.AddRange(c.Document.Strokes.Where(s => s.ShapeSource.GroupId == source.EndNode));
        c.TransformSelectionUndoable(Matrix3x2.CreateTranslation(40, 30), "move"); Close(end, connector.Points[1].Position);
    }

    [Fact]
    public void TwoStepShapesRetainBothConstructionDrags()
    {
        foreach (var kind in new[] { ShapeKind.Cuboid, ShapeKind.SquarePyramid, ShapeKind.HyperbolaWithFociAndAsymptotes })
        {
            using var c = new InkController { Tool = InkTool.Shape, ShapeKind = kind, RecognizeShapes = false };
            c.OnPointerDown(Pointer(new(100, 100))); c.OnPointerUp(Pointer(new(260, 230)));
            c.OnPointerDown(Pointer(new(280, 240))); c.OnPointerUp(Pointer(new(320, 290)));
            Assert.NotEmpty(c.Document.Strokes);
            var source = Assert.Single(c.Document.Strokes.Select(s => s.ShapeSource).Distinct());
            Assert.Equal(kind, source.Geometry.Kind); Assert.Equal(6, source.Geometry.Parameters.Length);
            Assert.Equal(3, source.Geometry.ControlPoints().Length);
            Assert.Equal(c.Document.Count, source.Geometry.Generate(Attributes(), source.GroupId).Count);
        }
    }

    [Fact]
    public void FilledObjectSelectsFromInteriorAndErasesAsOneUndoableObject()
    {
        using var c = new InkController { Tool = InkTool.Select };
        c.Document.AddRange(ShapeGenerator.Generate(ShapeKind.Rectangle, new(100, 100), new(300, 240), Attributes()));
        c.SelectAll(); c.SetShapeFill(InkColor.Opaque(200, 40, 40)); c.ClearSelection();
        c.OnPointerDown(Pointer(new(180, 160))); c.OnPointerUp(Pointer(new(180, 160))); Assert.Equal(2, c.Selection.Count);
        c.Tool = InkTool.Eraser; c.EraserMode = InkEraserMode.WholeStroke;
        c.OnPointerDown(Pointer(new(180, 160))); c.OnPointerUp(Pointer(new(180, 160))); Assert.Empty(c.Document.Strokes);
        c.Undo(); Assert.Equal(2, c.Document.Count); Assert.All(c.Document.Strokes, s => Assert.NotNull(s.ShapeSource.Geometry));
    }

    [Fact]
    public async Task PdfAnnotationRoundTripKeepsFillControlPointsAndConnectorBindings()
    {
        string root = Directory.CreateTempSubdirectory("inkcanvas-native-shape-pdf-").FullName;
        string source = Path.Combine(root, "source.pdf"), saved = Path.Combine(root, "saved.pdf");
        try
        {
            using (var document = new PdfSharp.Pdf.PdfDocument()) { document.AddPage(); document.Save(source); }
            using var c = Connected();
            c.SetShapeFill(InkColor.Opaque(240, 180, 40));
            using (var file = await PdfAnnotationFile.OpenAsync(source))
                await file.SaveAsync(saved, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = c.Document.Strokes });
            using var reopened = await PdfAnnotationFile.OpenAsync(saved);
            var restored = reopened.GetInk(0);
            Assert.Equal(c.Document.Count, restored.Count);
            Assert.Equal(2, restored.Count(s => s.ShapeSource.Geometry.IsFill));
            Assert.All(restored, s => Assert.True(s.ShapeSource.Geometry.IsValid));
            using var loaded = new InkController(); loaded.Document.AddRange(restored);
            Assert.Single(loaded.Document.Strokes, s => s.ShapeSource.Geometry.Construction == ShapeConstruction.Connector);
            foreach (var stroke in c.Document.Strokes)
                Assert.Equal(stroke.ShapeSource, restored.Single(s => s.Id == stroke.Id).ShapeSource);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FilledInteriorMarqueeAndDashedConnectorReplayUseTheRenderedGeometry()
    {
        using var c = Connected();
        c.SetShapeFill(InkColor.Opaque(20, 80, 220));
        Assert.NotEmpty(c.Document.HitTest(new Rect(130, 130, 160, 160)));
        c.ClearSelection();
        var connector = c.Document.Strokes.Single(s => s.ShapeSource.Geometry.Construction == ShapeConstruction.Connector);
        c.Selection.Add(connector); c.SetShapeLineStyle(ShapeLineStyle.Dotted); connector = c.Selection.Single();
        var replay = new InkCanvas.Core.Replay.InkReplaySession(new[] { connector });
        var contours = ShapeDefinition.RenderContours(connector);
        Assert.Equal(contours.Count, replay.StrokeCount);
        var visible = replay.VisibleAt((float)replay.Duration.TotalMilliseconds / 2);
        Assert.InRange(visible.Count, 1, contours.Count - 1);
    }

    private static InkController Connected()
    {
        var c = new InkController { Tool = InkTool.Select, RecognizeShapes = false };
        c.Document.AddRange(ShapeGenerator.Generate(ShapeKind.Rectangle, new(100, 100), new(220, 240), Attributes()));
        c.Document.AddRange(ShapeGenerator.Generate(ShapeKind.Diamond, new(400, 100), new(550, 240), Attributes()));
        c.SelectAll(); Assert.True(c.ConnectSelectedShapes()); return c;
    }
    private static OverlayPointer Pointer(Vector2 point) => new(991, point.X, point.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 0);
    private static void Close(Vector2 expected, Vector2 actual) => Assert.True(Vector2.Distance(expected, actual) < .05f, $"Expected {expected}, got {actual}");
}
