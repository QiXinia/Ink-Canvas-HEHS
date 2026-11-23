using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ToolbarLayoutTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(-1920, 0, 1920, 1080)]
    [InlineData(2560, -900, 640, 480)]
    public void SelectionMenusStayOnMonitorAndAvoidMainBar(float x, float y, float width, float height)
    {
        var work = new Rect(x, y, x + width, y + height);
        var main = new Rect(x + width / 2 - 220, y + height - 90, x + width / 2 + 220, y + height - 30);
        foreach (var point in new[] { work.Center, new Vector2(work.Left, work.Top), new Vector2(work.Right, work.Bottom), main.Center })
        foreach (var size in new[] { new Vector2(274, 58), new Vector2(54, 58), new Vector2(274, 210) })
        {
            var selection = new Rect(point.X - 35, point.Y - 25, point.X + 35, point.Y + 25);
            var result = FloatingToolbarLayout.Place(work, selection, main, size);
            Assert.True(result.Left >= work.Left && result.Right <= work.Right);
            Assert.True(result.Top >= work.Top && result.Bottom <= work.Bottom);
            Assert.False(result.Intersects(main));
        }
    }

    [Fact]
    public void OversizedMenuIsConstrainedToWorkArea()
    {
        var work = new Rect(-800, -400, -500, -200);
        var result = FloatingToolbarLayout.Place(work, new Rect(-700, -300, -650, -250), Rect.Empty, new Vector2(900, 800));
        Assert.Equal(work, result);
    }

    [Fact]
    public void SelectionToolbarPrefersTheAreaAboveTheSelection()
    {
        var work = new Rect(0, 8, 1200, 792);
        var selection = new Rect(400, 300, 700, 500);
        var result = FloatingToolbarLayout.Place(work, selection, Rect.Empty, new Vector2(274, 58), gap: 8);

        Assert.False(result.Intersects(selection));
        Assert.Equal(selection.Top - 8, result.Bottom, 2);
        Assert.True(result.Top >= work.Top && result.Bottom <= work.Bottom);
    }

    [Fact]
    public void SelectionToolbarFallsBelowWhenAboveWouldOverlapTheTopEdge()
    {
        var work = new Rect(0, 8, 1200, 792);
        var selection = new Rect(400, 12, 700, 150);
        var result = FloatingToolbarLayout.Place(work, selection, Rect.Empty, new Vector2(274, 58), gap: 8);

        Assert.False(result.Intersects(selection));
        Assert.True(result.Top >= selection.Bottom + 8 - .01f);
        Assert.True(result.Top >= work.Top && result.Bottom <= work.Bottom);
    }

    [Fact]
    public void SelectionToolbarAvoidsSelectionAndDockNearTheBottom()
    {
        var work = new Rect(0, 8, 1200, 792);
        var selection = new Rect(400, 620, 700, 760);
        var dock = new Rect(0, 720, 1200, 792);
        var result = FloatingToolbarLayout.Place(work, selection, dock, new Vector2(274, 58), gap: 8);

        Assert.False(result.Intersects(selection));
        Assert.False(result.Intersects(dock));
        Assert.True(result.Left >= work.Left && result.Right <= work.Right);
        Assert.True(result.Top >= work.Top && result.Bottom <= work.Bottom);
    }

    [Fact]
    public void SelectionToolbarIsHorizontallyClampedAtTheWindowEdge()
    {
        var work = new Rect(0, 8, 1200, 792);
        var selection = new Rect(1000, 300, 1150, 450);
        var result = FloatingToolbarLayout.Place(work, selection, Rect.Empty, new Vector2(274, 58), gap: 8);

        Assert.False(result.Intersects(selection));
        Assert.True(result.Left >= work.Left && result.Right <= work.Right);
        Assert.Equal(work.Right, result.Right, 2);
    }

    [Fact]
    public void HighlighterWidthSurvivesToolSwitchAndStrokeSave()
    {
        using var controller = new InkController();
        controller.ConfigurePenWidths(3, 60);
        controller.Tool = InkTool.Highlighter;
        var point = new OverlayPointer(1, 100, 100, .5f, 0, 0, OverlayPointerKind.Mouse, 1);
        controller.OnPointerDown(point);
        controller.OnPointerUp(point with { TimeMs = 15 });
        Assert.Equal(60, controller.Document.Strokes.Single().Attributes.BaseWidth);
        controller.Tool = InkTool.Pen;
        Assert.Equal(3, controller.CurrentAttributes.BaseWidth);
        controller.Tool = InkTool.Highlighter;
        Assert.Equal(60, controller.CurrentAttributes.BaseWidth);
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
        {
            Canvas = new CanvasSettings { InkWidth = 3, HighlighterWidth = 60 },
        }));
        Assert.Equal(60, settings.Canvas.HighlighterWidth);
        Assert.Equal(3, settings.Canvas.InkWidth);
    }

    [Fact]
    public void LegacySettingsKeepTheirHighlighterWidth()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"canvas\":{\"inkWidth\":7}}");
        Assert.Null(settings.Canvas.HighlighterWidth);
        Assert.Equal(7, settings.Canvas.HighlighterWidth ?? settings.Canvas.InkWidth);
    }

    [Fact]
    public void SelectionStyleChangesAreUndoable()
    {
        using var controller = new InkController();
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4 });
        stroke.AddPoint(100, 100); stroke.AddPoint(150, 150); stroke.Complete();
        controller.Document.Add(stroke);
        controller.Tool = InkTool.Select;
        controller.SelectAll();
        var before = stroke.Attributes.Color;
        var red = InkColor.Opaque(220, 38, 38);
        controller.RecolorSelection(red);
        controller.ScaleSelectionWidth(1.25f);
        Assert.Equal(5, stroke.Attributes.BaseWidth);
        controller.Undo();
        Assert.Equal(4, stroke.Attributes.BaseWidth);
        Assert.Equal(red, stroke.Attributes.Color);
        controller.Undo();
        Assert.Equal(before, stroke.Attributes.Color);
        controller.Redo();
        Assert.Equal(red, stroke.Attributes.Color);
    }
}
