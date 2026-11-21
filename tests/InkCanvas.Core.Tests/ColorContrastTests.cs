using InkCanvas.Core.Ink;
using InkCanvas.Core.Theming;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ColorContrastTests
{
    [Theory]
    [InlineData(0, 103, 192, false)]
    [InlineData(255, 245, 157, true)]
    [InlineData(76, 194, 255, true)]
    public void AccentTextChoosesTheMoreReadableForeground(byte r, byte g, byte b, bool black)
    {
        var background = InkColor.Opaque(r, g, b);
        Assert.Equal(black ? InkColor.Black : InkColor.White, ColorContrast.Foreground(background));
        Assert.True(ColorContrast.Ratio(background, ColorContrast.Foreground(background)) >= 4.5);
    }

    [Fact]
    public void ForegroundMeetsNormalTextContrastAcrossTheRgbGamut()
    {
        for (int red = 0; red <= 255; red += 15)
        for (int green = 0; green <= 255; green += 15)
        for (int blue = 0; blue <= 255; blue += 15)
        {
            var fill = InkColor.Opaque((byte)red, (byte)green, (byte)blue);
            Assert.True(ColorContrast.Ratio(fill, ColorContrast.Foreground(fill)) >= 4.5);
        }
    }
}
