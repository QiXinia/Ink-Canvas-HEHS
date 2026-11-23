using System.Text.Json;
using InkCanvas.Core.Settings;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class CustomColorSettingsTests
{
    [Fact]
    public void EveryWholeOpacityPercentageSurvivesByteStorage()
    {
        for (int percent = 0; percent <= 100; percent++)
            Assert.Equal(percent, OriginalMenuCatalog.ToHighlighterOpacityPercent(OriginalMenuCatalog.ToStoredHighlighterAlpha(percent)));
        Assert.Equal(0, OriginalMenuCatalog.ToStoredHighlighterAlpha(0));
        Assert.Equal(128, OriginalMenuCatalog.ToStoredHighlighterAlpha(50));
        Assert.Equal(255, OriginalMenuCatalog.ToStoredHighlighterAlpha(100));
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(140, 55)]
    [InlineData(255, 100)]
    [InlineData(300, 100)]
    public void OpacityReadoutClampsWithoutChangingStoredAlpha(int alpha, int percent)
    {
        var settings = new AppSettings();
        settings.Canvas.InkAlpha = alpha;
        Assert.Equal(percent, OriginalMenuCatalog.ToHighlighterOpacityPercent(settings.Canvas.InkAlpha));
        Assert.Equal(alpha, settings.Clone().Canvas.InkAlpha);
    }

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
