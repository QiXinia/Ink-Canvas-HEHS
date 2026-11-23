using System.Text.Json;
using InkCanvas.Core.Settings;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class EraserSizeSettingsTests
{
    [Theory]
    [InlineData(0, 45)]
    [InlineData(1, 72)]
    [InlineData(2, 90)]
    [InlineData(3, 112.5)]
    [InlineData(4, 162)]
    public void LegacyPresetsRetainTheirActualDiameter(int size, double diameter)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"canvas\":{\"eraserSize\":" + size + "}}");
        Assert.Null(settings.Canvas.EraserDiameter);
        Assert.Equal(diameter, OriginalMenuCatalog.GetEraserDiameter(settings.Canvas), 4);
        Assert.Equal(diameter / 2, OriginalMenuCatalog.ToEraserRadius(settings.Canvas), 4);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(73)]
    [InlineData(112.5)]
    [InlineData(200)]
    public void CustomDiameterSurvivesSaveAndOverridesLegacyPreset(double diameter)
    {
        var settings = new AppSettings();
        settings.Canvas.EraserDiameter = diameter;
        var loaded = settings.Clone();
        Assert.Equal(diameter, loaded.Canvas.EraserDiameter);
        Assert.Equal(diameter / 2, OriginalMenuCatalog.ToEraserRadius(loaded.Canvas), 4);
    }

    [Theory]
    [InlineData(-20, 10)]
    [InlineData(500, 200)]
    [InlineData(double.NaN, 90)]
    [InlineData(double.PositiveInfinity, 90)]
    public void InvalidDiameterIsBoundedOrFallsBackToLegacy(double diameter, double expected)
    {
        var settings = new CanvasSettings { EraserDiameter = diameter };
        Assert.Equal(expected, OriginalMenuCatalog.GetEraserDiameter(settings));
    }

    [Fact]
    public void ResetClearsCustomDiameterAndRestoresRecommendedSize()
    {
        var settings = new AppSettings();
        settings.Canvas.EraserDiameter = 175;
        settings.ResetToRecommendation();
        Assert.Null(settings.Canvas.EraserDiameter);
        Assert.Equal(72, OriginalMenuCatalog.GetEraserDiameter(settings.Canvas));
    }
}
