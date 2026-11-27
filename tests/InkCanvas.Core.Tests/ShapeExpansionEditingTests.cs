using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Modules.ShapeExpansion;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ShapeExpansionEditingTests
{
    [Theory]
    [InlineData(ShapeKind.Sector, 3)]
    [InlineData(ShapeKind.ConeFrustum, 3)]
    [InlineData(ShapeKind.TriangularFrustum, 4)]
    [InlineData(ShapeKind.SquareFrustum, 4)]
    [InlineData(ShapeKind.PentagonalFrustum, 4)]
    [InlineData(ShapeKind.HexagonalFrustum, 4)]
    [InlineData(ShapeKind.RoundedRectangle, 3)]
    [InlineData(ShapeKind.SuperRoundedRectangle, 3)]
    [InlineData(ShapeKind.RoundedRectangleRing, 4)]
    public void DrawingAndDraggingTheLastHandleRegeneratesAnUndoableShape(ShapeKind kind, int controls)
    {
        var module = new ShapeExpansionModule();
        using var controller = new InkController { ViewTransform = Matrix3x2.CreateRotation(.3f) * Matrix3x2.CreateScale(1.25f) };
        if (ShapeGenerator.IsRoundedRectangle(kind))
        {
            controller.SelectShape(kind);
            Assert.Equal(InkTool.Pen, controller.Tool);
            controller.GetPolygonExtension = () => module;
        }
        controller.SelectShape(kind);
        Drag(controller, new(110, 145), new(320, 290));
        if (ShapeGenerator.IsTwoStepSolid(kind))
        {
            Assert.Empty(controller.Document.Strokes);
            Drag(controller, new(280, 240), new(310, 130));
        }
        var source = Assert.Single(controller.Document.Strokes.Select(stroke => stroke.ShapeSource).Distinct());
        Assert.Equal(kind, source.Geometry.Kind);
        Assert.Equal(1, controller.History.UndoCount);
        controller.SelectAll();
        controller.ToggleShapeEditing();
        var before = controller.ShapeControlPoints.ToArray();
        Assert.Equal(controls, before.Length);
        var screen = Vector2.Transform(before[^1], controller.ViewTransform);
        var delta = kind == ShapeKind.RoundedRectangleRing ? new Vector2(0, -20) : new Vector2(17, 0);
        Drag(controller, screen, screen + delta);
        var after = controller.ShapeControlPoints.ToArray();
        Assert.Equal(before.SkipLast(1), after.SkipLast(1));
        Assert.True(Vector2.Distance(before[^1], after[^1]) > 2, $"{kind} handle stayed at {before[^1]}");
        Assert.All(controller.Document.Strokes, stroke => Assert.Equal(source.GroupId, stroke.ShapeSource.GroupId));
        Assert.Equal(2, controller.History.UndoCount);
        controller.Undo();
        Assert.Equal(before, controller.ShapeControlPoints);
        controller.Redo();
        Assert.Equal(after, controller.ShapeControlPoints);

        string path = Path.GetTempFileName();
        try
        {
            InkFileStore.Save(path, controller.Document.Strokes);
            var reopened = InkFileStore.Load(path);
            Assert.All(reopened, stroke => Assert.Equal(after, stroke.ShapeSource.Geometry.ControlPoints()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SectorSupportsAReflexAngleAndTheSecondHandleStaysOnTheSameCircle()
    {
        var center = new Vector2(100, 100);
        var definition = ShapeDefinition.FromBounds(ShapeKind.Sector, center, new(200, 100), []);
        var edited = definition.MoveControlPoint(2, new(100, 50));
        Assert.Equal(3 * MathF.PI / 2, edited.Parameters[4], 5);
        Assert.InRange(Vector2.Distance(edited.ControlPoints()[2], new(100, 0)), 0, .001f);
        var outline = Assert.Single(edited.Generate(new(), Guid.NewGuid()));
        Assert.Equal(center, outline.Points[0].Position);
        Assert.Equal(center, outline.Points[^1].Position);
        Assert.True(outline.Bounds.Top < 1 && outline.Bounds.Left < 1);
    }

    [Fact]
    public void CircularAndSuperellipseCornersHaveDistinctOutlinesAtTheSameRadius()
    {
        var rounded = new ShapeDefinition { Kind = ShapeKind.RoundedRectangle, Parameters = [0, 0, 200, 140, 50] };
        var circular = Assert.Single(rounded.Generate(new(), Guid.NewGuid()));
        var superellipse = Assert.Single((rounded with { Kind = ShapeKind.SuperRoundedRectangle }).Generate(new(), Guid.NewGuid()));
        Assert.Equal(circular.Bounds, superellipse.Bounds);
        Assert.Equal(circular.Points[0], superellipse.Points[0]);
        Assert.True(superellipse.Points[8].X > circular.Points[8].X);
        Assert.True(superellipse.Points[8].Y > circular.Points[8].Y);
    }

    [Fact]
    public void AdvancedRotationKeepsThePivotFixedAndCommitsOneUndoableTransform()
    {
        using var controller = new InkController();
        var stroke = ShapeGenerator.Stroke(new(), new(100, 100), new(150, 100));
        controller.Document.Add(stroke);
        controller.SelectAll();
        Assert.False(controller.BeginAdvancedSelection(AdvancedSelectionMode.Rotate));
        var plugin = new ShapeExpansionModule();
        controller.GetPolygonExtension = () => plugin;
        controller.ViewTransform = Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateRotation(.3f) * Matrix3x2.CreateTranslation(25, 30);
        Assert.True(controller.BeginAdvancedSelection(AdvancedSelectionMode.Rotate));
        var pivot = new Vector2(100, 150);
        var screen = Vector2.Transform(pivot, controller.ViewTransform);
        Drag(controller, screen, screen);
        controller.SetAdvancedRotationAngle(90);
        Assert.InRange(Vector2.Distance(pivot, Vector2.Transform(pivot, controller.AdvancedSelection.Value.Matrix)), 0, .001f);
        Assert.Equal(new Vector2(100, 100), stroke.Points[0].Position);
        Assert.True(controller.CommitAdvancedSelection());
        Assert.InRange(Vector2.Distance(stroke.Points[0].Position, new(150, 150)), 0, .001f);
        Assert.Equal(1, controller.History.UndoCount);
        controller.Undo();
        Assert.Equal(new Vector2(100, 100), stroke.Points[0].Position);
        controller.Redo();
        Assert.InRange(Vector2.Distance(stroke.Points[0].Position, new(150, 150)), 0, .001f);
    }

    [Fact]
    public void AxisCanBePickedMovedAndCanceledBeforeApplyingPointSymmetry()
    {
        var plugin = new ShapeExpansionModule();
        using var controller = new InkController { GetPolygonExtension = () => plugin };
        var stroke = ShapeGenerator.Stroke(new(), new(100, 20), new(120, 20));
        controller.Document.Add(stroke);
        controller.SelectAll();
        Assert.True(controller.BeginAdvancedSelection(AdvancedSelectionMode.AxisReflection));
        Drag(controller, new(30, 40), new(30, 40));
        Assert.False(controller.CanCommitAdvancedSelection);
        Drag(controller, new(150, 40), new(150, 40));
        Assert.True(controller.CanCommitAdvancedSelection);
        Drag(controller, new(80, 40), new(80, 60));
        var axis = controller.AdvancedSelection.Value;
        Assert.Equal(new Vector2(30, 60), axis.First.Value);
        Assert.Equal(new Vector2(150, 60), axis.Second.Value);
        Assert.Equal(new Vector2(100, 100), Vector2.Transform(stroke.Points[0].Position, axis.Matrix));
        controller.OnPointerDown(Pointer(new(150, 60)));
        controller.OnPointerMove(Pointer(new(145, 105)));
        Assert.NotEqual(axis, controller.AdvancedSelection);
        controller.OnPointerCanceled(Pointer(new(145, 105)));
        Assert.Equal(axis, controller.AdvancedSelection);
        Assert.True(controller.CancelAdvancedSelection());
        Assert.True(controller.HasSelection);
        Assert.Equal(0, controller.History.UndoCount);
        Assert.True(controller.BeginAdvancedSelection(AdvancedSelectionMode.PointReflection));
        Drag(controller, new(100, 100), new(100, 100));
        Drag(controller, new(100, 100), new(110, 90));
        Assert.True(controller.CommitAdvancedSelection());
        Assert.Equal(new Vector2(120, 160), stroke.Points[0].Position);
    }

    private static void Drag(InkController controller, Vector2 start, Vector2 end)
    {
        controller.OnPointerDown(Pointer(start));
        controller.OnPointerMove(Pointer(end));
        controller.OnPointerUp(Pointer(end));
    }

    private static OverlayPointer Pointer(Vector2 point) => new(1, point.X, point.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 0);
}
