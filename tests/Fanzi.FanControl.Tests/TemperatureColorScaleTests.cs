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
    public void ForLoad_MidpointMapsToMiddleOfHeatScale()
        => Assert.Equal(TemperatureColorScale.ForHeat(0.5), TemperatureColorScale.ForLoad(50));

    [Theory]
    [InlineData(0.00, "#38BDF8")]
    [InlineData(0.30, "#3B82F6")]
    [InlineData(0.55, "#8B5CF6")]
    [InlineData(0.80, "#DB2777")]
    [InlineData(1.00, "#EF4444")]
    [InlineData(-1, "#38BDF8")]
    [InlineData(2, "#EF4444")]
    [InlineData(double.NaN, "#38BDF8")]
    public void ForHeat_StopsLowBlueToHotRed(double fraction, string expected)
        => Assert.Equal(Hex(expected), TemperatureColorScale.ForHeat(fraction));

    [Fact]
    public void ForHeat_RangeOverload_MapsLowAndHigh()
    {
        Assert.Equal(Hex("#38BDF8"), TemperatureColorScale.ForHeat(30, 30, 95));
        Assert.Equal(Hex("#EF4444"), TemperatureColorScale.ForHeat(95, 30, 95));
        Assert.Equal(TemperatureColorScale.ForHeat(0.5), TemperatureColorScale.ForHeat(62.5, 30, 95));
    }

    [Fact]
    public void HeatScale_NeverHitsPeakOrange()
    {
        // The peak marker must stay distinguishable from every heat colour.
        var peak = TemperatureColorScale.PeakColor;
        for (double f = 0; f <= 1.0001; f += 0.01)
        {
            var c = TemperatureColorScale.ForHeat(f);
            int distance = Math.Abs(c.R - peak.R) + Math.Abs(c.G - peak.G) + Math.Abs(c.B - peak.B);
            Assert.True(distance > 90, $"heat colour at {f:P0} ({c}) is too close to the peak orange");
        }
    }

    [Fact]
    public void WithAlpha_And_Lighten()
    {
        var red = Hex("#EF4444");
        Assert.Equal(Color.FromArgb(0x66, 0xEF, 0x44, 0x44), TemperatureColorScale.WithAlpha(red, 0.4));
        Assert.Equal(red, TemperatureColorScale.Lighten(red, 0));
        Assert.Equal(Colors.White, TemperatureColorScale.Lighten(red, 1));
    }
}
