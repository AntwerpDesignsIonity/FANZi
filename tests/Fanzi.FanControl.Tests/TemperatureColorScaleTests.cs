using Avalonia.Media;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public class TemperatureColorScaleTests
{
    private static Color Hex(string hex) => Color.Parse(hex);

    [Theory]
    [InlineData(30, "#38BDF8")]
    [InlineData(45, "#22D3EE")]
    [InlineData(60, "#22C55E")]
    [InlineData(75, "#FBBF24")]
    [InlineData(85, "#F97316")]
    [InlineData(95, "#EF4444")]
    public void ForTemperature_ExactStops(double celsius, string expected)
        => Assert.Equal(Hex(expected), TemperatureColorScale.ForTemperature(celsius));

    [Theory]
    [InlineData(37.5, "#2DC8F3")] // 38BDF8 ↔ 22D3EE
    [InlineData(52.5, "#22CCA6")] // 22D3EE ↔ 22C55E
    [InlineData(67.5, "#8FC241")] // 22C55E ↔ FBBF24 (142.5 rounds away from zero)
    [InlineData(80, "#FA991D")]   // FBBF24 ↔ F97316
    [InlineData(90, "#F45C2D")]   // F97316 ↔ EF4444 (91.5 rounds away from zero)
    public void ForTemperature_Midpoints(double celsius, string expected)
        => Assert.Equal(Hex(expected), TemperatureColorScale.ForTemperature(celsius));

    [Theory]
    [InlineData(-40)]
    [InlineData(0)]
    [InlineData(29.9)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void ForTemperature_ClampsBelowToCoolBlue(double celsius)
        => Assert.Equal(Hex("#38BDF8"), TemperatureColorScale.ForTemperature(celsius));

    [Theory]
    [InlineData(95.1)]
    [InlineData(120)]
    [InlineData(double.PositiveInfinity)]
    public void ForTemperature_ClampsAboveToRed(double celsius)
        => Assert.Equal(Hex("#EF4444"), TemperatureColorScale.ForTemperature(celsius));

    [Fact]
    public void ForTemperature_IsOpaque()
    {
        for (double t = 0; t <= 110; t += 0.5)
            Assert.Equal(0xFF, TemperatureColorScale.ForTemperature(t).A);
    }

    [Theory]
    [InlineData(100, "#38BDF8")]
    [InlineData(150, "#38BDF8")]
    [InlineData(0, "#EF4444")]
    [InlineData(-10, "#EF4444")]
    [InlineData(double.NaN, "#EF4444")]
    public void ForHealth_IsInverseAndClamped(double score, string expected)
        => Assert.Equal(Hex(expected), TemperatureColorScale.ForHealth(score));

    [Fact]
    public void ForHealth_MidpointMapsToMiddleOfScale()
        => Assert.Equal(TemperatureColorScale.ForTemperature(62.5), TemperatureColorScale.ForHealth(50));

    [Theory]
    [InlineData(0, "#38BDF8")]
    [InlineData(-5, "#38BDF8")]
    [InlineData(100, "#EF4444")]
    [InlineData(250, "#EF4444")]
    public void ForLoad_EndsAndClamping(double percent, string expected)
        => Assert.Equal(Hex(expected), TemperatureColorScale.ForLoad(percent));

    [Fact]
    public void ForLoad_MidpointMapsToMiddleOfScale()
        => Assert.Equal(TemperatureColorScale.ForTemperature(62.5), TemperatureColorScale.ForLoad(50));

    [Fact]
    public void WithAlpha_And_Lighten()
    {
        var red = Hex("#EF4444");
        Assert.Equal(Color.FromArgb(0x66, 0xEF, 0x44, 0x44), TemperatureColorScale.WithAlpha(red, 0.4));
        Assert.Equal(red, TemperatureColorScale.Lighten(red, 0));
        Assert.Equal(Colors.White, TemperatureColorScale.Lighten(red, 1));
    }
}
