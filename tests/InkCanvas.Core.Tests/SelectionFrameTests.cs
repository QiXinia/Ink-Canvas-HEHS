using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class SelectionFrameTests
{
    private static readonly Rect Bounds = new(100, 100, 240, 180);

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(2f)]
    public void RotationHandleUsesViewDistanceAtEveryZoom(float zoom)
    {
        var frame = new SelectionFrame(Bounds, Matrix3x2.CreateRotation(0.6f, Bounds.Center));
        var view = Matrix3x2.CreateScale(zoom);
        var corners = frame.Corners(view);
        var top = (corners[0] + corners[1]) / 2;
        var rotate = frame.HandlePosition(SelectionHandle.Rotate, view);
        Assert.Equal(SelectionGeometry.RotateOffset, Vector2.Distance(top, rotate), 3);
        Assert.Equal(SelectionHandle.Rotate, frame.HitTest(rotate, view));
    }

    [Fact]
    public void ShiftRotationSnapsAbsoluteAngleToFifteenDegrees()
    {
        var frame = new SelectionFrame(Bounds, Matrix3x2.Identity);
        var from = frame.HandlePosition(SelectionHandle.Rotate, Matrix3x2.Identity);
        var to = Vector2.Transform(from, Matrix3x2.CreateRotation(23 * MathF.PI / 180, Bounds.Center));
        var delta = frame.Drag(SelectionHandle.Rotate, from, to, Matrix3x2.Identity, true, out float radians);
        Assert.Equal(30, radians * 180 / MathF.PI, 3);
        Assert.True(Vector2.Distance(Bounds.Center, Vector2.Transform(Bounds.Center, delta)) < 0.001f);
    }

    [Fact]
    public void RotatedCornerScalingKeepsOppositeCornerFixed()
    {
        var frame = new SelectionFrame(Bounds, Matrix3x2.CreateRotation(0.8f, Bounds.Center));
        var corners = frame.Corners(Matrix3x2.Identity);
        var from = corners[2];
        var to = corners[0] + (from - corners[0]) * 1.5f;
        var delta = frame.Drag(SelectionHandle.BottomRight, from, to, Matrix3x2.Identity, false, out _);
        Assert.True(Vector2.Distance(corners[0], Vector2.Transform(corners[0], delta)) < 0.001f);
        Assert.True(Vector2.Distance(to, Vector2.Transform(from, delta)) < 0.001f);
    }

    [Fact]
    public void SideHandleChangesOnlyItsOwnAxis()
    {
        var frame = new SelectionFrame(Bounds, Matrix3x2.Identity);
        var from = frame.HandlePosition(SelectionHandle.Right, Matrix3x2.Identity);
        var delta = frame.Drag(SelectionHandle.Right, from, from + new Vector2(40, 30), Matrix3x2.Identity, false, out _);
        Assert.Equal(1f, delta.M22);
        Assert.Equal(0f, delta.M32);
        Assert.Equal(Bounds.Right + 40, Vector2.Transform(from, delta).X, 3);
    }

    [Fact]
    public void MarqueeFindsStrokeCrossingBoxWithoutAnySamplesInside()
    {
        var document = new InkDocument();
        var crossing = new InkStroke(new StrokeAttributes());
        crossing.AddPoint(0, 150); crossing.AddPoint(400, 150);
        var miss = new InkStroke(new StrokeAttributes());
        miss.AddPoint(0, 200); miss.AddPoint(400, 200);
        document.AddRange(new[] { crossing, miss });
        Assert.Same(crossing, Assert.Single(document.HitTest(new Rect(100, 100, 200, 170))));
    }

    [Fact]
    public void DuplicatingStrokeGetsIndependentIdentityAndPoints()
    {
        var source = new InkStroke(new StrokeAttributes());
        source.AddPoint(100, 100); source.AddPoint(200, 200); source.Complete();
        var duplicate = source.Clone(preserveId: false);
        Assert.NotEqual(source.Id, duplicate.Id);
        duplicate.Transform(Matrix3x2.CreateTranslation(30, 10));
        Assert.Equal(new Vector2(100, 100), source.Points[0].Position);
        Assert.Equal(source.Id, source.Clone().Id); // history snapshots preserve identity
    }

    [Fact]
    public void ObjectOrientationCanBeSavedAndRestoredWithoutChangingInk()
    {
        var stroke = new InkStroke(new StrokeAttributes());
        stroke.AddPoint(100, 100); stroke.AddPoint(240, 180); stroke.Complete();
        stroke.Transform(Matrix3x2.CreateRotation(0.6f, stroke.Bounds.Center));
        var restored = new InkStroke(stroke.Attributes.Clone());
        restored.RestorePoints(stroke.Points);
        restored.ImportObjectFrame(stroke.ExportObjectFrame());
        Assert.Equal(stroke.ObjectFrame, restored.ObjectFrame);
        Assert.Equal(stroke.Points.Select(p => p.Position), restored.Points.Select(p => p.Position));
        Assert.Equal(stroke.ObjectFrame, stroke.Clone(preserveId: false).ObjectFrame);
    }

    [Fact]
    public void MarqueeTestsSegmentsInRotatedViewCoordinates()
    {
        var document = new InkDocument();
        var stroke = new InkStroke(new StrokeAttributes());
        stroke.AddPoint(0, 0); stroke.AddPoint(200, 0); document.Add(stroke);
        var view = Matrix3x2.CreateRotation(MathF.PI / 4) * Matrix3x2.CreateTranslation(200, 100);
        var middle = Vector2.Transform(new Vector2(100, 0), view);
        Assert.Same(stroke, Assert.Single(document.HitTest(new Rect(middle.X - 5, middle.Y - 5, middle.X + 5, middle.Y + 5), view)));
        Assert.Empty(document.HitTest(new Rect(200, 210, 210, 220), view));
    }

    [Fact]
    public void RotatedObjectDoesNotSelectEmptyCornersOfItsBoundingBox()
    {
        var frame = new SelectionFrame(new Rect(100, 100, 200, 200), Matrix3x2.CreateRotation(MathF.PI / 4, new Vector2(150, 150)));
        Assert.False(frame.Intersects(new Rect(80, 80, 95, 95), Matrix3x2.Identity));
        Assert.True(frame.Intersects(new Rect(145, 145, 155, 155), Matrix3x2.Identity));
        Assert.True(frame.Intersects(new Rect(0, 0, 300, 300), Matrix3x2.Identity));
    }

    [Fact]
    public void ThinLineCanBeDraggedByItsCenter()
    {
        var frame = new SelectionFrame(new Rect(100, 100, 300, 104), Matrix3x2.Identity);
        Assert.Equal(SelectionHandle.Move, frame.HitTest(frame.Center(Matrix3x2.Identity), Matrix3x2.Identity));
        Assert.DoesNotContain(SelectionHandle.Top, frame.VisibleResizeHandles(Matrix3x2.Identity));
    }

    [Fact]
    public void ShiftSnapsDocumentAngleWhenCanvasViewIsRotated()
    {
        var frame = new SelectionFrame(Bounds, Matrix3x2.Identity);
        var view = Matrix3x2.CreateRotation(0.7f) * Matrix3x2.CreateScale(2);
        var from = frame.HandlePosition(SelectionHandle.Rotate, view);
        var to = Vector2.Transform(from, Matrix3x2.CreateRotation(23 * MathF.PI / 180, frame.Center(view)));
        var delta = frame.Drag(SelectionHandle.Rotate, from, to, view, true, out _);
        Assert.Equal(30, MathF.Atan2(delta.M12, delta.M11) * 180 / MathF.PI, 3);
    }

    [Theory]
    [InlineData(320, 1, true)]
    [InlineData(800, 0.5, false)]
    [InlineData(1024, 1, true)]
    [InlineData(1280, 1, false)]
    [InlineData(1280, 1.5, true)]
    [InlineData(1920, 1.5, false)]
    [InlineData(1800, 1, false)]
    public void BoardDockFitsSmallAndWideViewports(double width, double scale, bool twoRows)
    {
        var layout = BoardDockLayout.Calculate(width, scale, 250, 700, 250);
        Assert.Equal(twoRows, layout.TwoRows);
        Assert.InRange(layout.Scale, 0.25, 1.5);
        Assert.True(700 * layout.Scale <= width - 12);
        if (layout.TwoRows) Assert.True(500 * layout.Scale <= width - 12);
        Assert.True(layout.Height > 0);
    }
}
