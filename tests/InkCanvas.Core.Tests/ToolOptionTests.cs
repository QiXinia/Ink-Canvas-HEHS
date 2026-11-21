using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using Xunit;

namespace InkCanvas.Core.Tests;

public class ToolOptionTests
{
    [Fact]
    public void SwitchingToolsRestoresTheirOwnColorAndWidth()
    {
        using var controller = new InkController();
        var marker = InkColor.Opaque(250, 204, 21);
        controller.ConfigurePenColors(InkColor.Red, marker);
        controller.ConfigurePenWidths(4, 24);
        controller.Tool = InkTool.Highlighter;
        Assert.Equal(marker, controller.CurrentAttributes.Color);
        Assert.Equal(24, controller.CurrentAttributes.BaseWidth);
        controller.Tool = InkTool.Eraser;
        controller.Tool = InkTool.Pen;
        Assert.Equal(InkColor.Red, controller.CurrentAttributes.Color);
        Assert.Equal(4, controller.CurrentAttributes.BaseWidth);
        controller.Tool = InkTool.Shape;
        Assert.Equal(InkColor.Red, controller.CurrentAttributes.Color);
    }

    [Fact]
    public void ToolChoicesSurviveRestartAndLegacySettingsKeepDefaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "inkcanvas-tool-options-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            var settings = new AppSettings();
            settings.Canvas.PenColor = InkColor.Red.PackedArgb;
            settings.Canvas.HighlighterColor = InkColor.White.PackedArgb;
            settings.Canvas.ShapeKind = ShapeKind.Ellipse;
            settings.Canvas.EraserType = 3;
            store.Save(settings);
            var restored = new SettingsStore(directory).Load().Canvas;
            Assert.Equal(settings.Canvas.PenColor, restored.PenColor);
            Assert.Equal(settings.Canvas.HighlighterColor, restored.HighlighterColor);
            Assert.Equal(ShapeKind.Ellipse, restored.ShapeKind);
            Assert.Equal(3, restored.EraserType);
            File.WriteAllText(store.SettingsPath, "{\"canvas\":{\"inkWidth\":6,\"eraserType\":2}}");
            var legacy = store.Load().Canvas;
            Assert.Equal(6, legacy.InkWidth);
            Assert.Equal(2, legacy.EraserType);
            Assert.Null(legacy.PenColor);
            Assert.Null(legacy.ShapeKind);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
