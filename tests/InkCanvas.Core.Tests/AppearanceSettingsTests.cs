using System.Text.Json;
using InkCanvas.Core.Settings;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class AppearanceSettingsTests
{
    [Fact]
    public void OlderClassroomSettingsLoadWithoutRevivingRetiredControls()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"canvas":{"inkWidth":7},"classroom":{"countdownMinutes":5,"presetId":"practice","toolkitCollapsed":true,"toolkitX":120,"groupSplitValue":4},"features":{"enableResourceBundle":true}}
            """);
        Assert.Equal(7, settings.Canvas.InkWidth);
        Assert.Equal(5, settings.Classroom.CountdownMinutes);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        Assert.False(saved.RootElement.TryGetProperty("features", out _));
        var classroom = saved.RootElement.GetProperty("classroom");
        foreach (string name in new[] { "presetId", "toolkitCollapsed", "toolkitX", "groupSplitValue" })
            Assert.False(classroom.TryGetProperty(name, out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"appearance\":{}}")]
    public void NewAndOlderSettingsDefaultToRegularIcons(string json)
        => Assert.True(JsonSerializer.Deserialize<AppSettings>(json).Appearance.IsIconRegularStyle);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitIconStyleSurvivesSettingsRoundtrip(bool regular)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"appearance\":{\"isIconRegularStyle\":" + regular.ToString().ToLowerInvariant() + "}}");
        Assert.Equal(regular, settings.Appearance.IsIconRegularStyle);
        Assert.Equal(regular, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)).Appearance.IsIconRegularStyle);
    }
}
