using System.Text.Json;
using InkCanvas.Core.Settings;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class CustomColorSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"canvas\":{\"customColors\":null}}")]
    public void OlderSettingsEnableCustomPalettesAndStartWithAnEmptyIndependentList(string json)
    {
        var first = JsonSerializer.Deserialize<AppSettings>(json);
        var second = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.True(first.Canvas.EnableCustomColorPalette);
        Assert.Null(first.Canvas.ColorPalette);
        Assert.Empty(first.Canvas.CustomColors);
        first.Canvas.CustomColors.Add(0xff2468ac);
        Assert.Empty(second.Canvas.CustomColors);
    }

    [Fact]
    public void EditingPreferencesRetainsColoursAcrossSaveCloneAndDisabling()
    {
        var settings = new AppSettings();
        settings.Canvas.ColorPalette = ColorPaletteKind.Custom;
        settings.Canvas.CustomColors.AddRange(new uint[] { 0xff2468ac, 0xffffeeaa });
        settings.Canvas.EnableCustomColorPalette = false;
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
        Assert.False(restored.Canvas.EnableCustomColorPalette);
        Assert.Equal(ColorPaletteKind.Custom, restored.Canvas.ColorPalette);
        Assert.Equal(settings.Canvas.CustomColors, restored.Canvas.CustomColors);
        var clone = restored.Clone();
        clone.Canvas.CustomColors[0] = 0xff000000;
        Assert.Equal(0xff2468acu, restored.Canvas.CustomColors[0]);
    }
}
