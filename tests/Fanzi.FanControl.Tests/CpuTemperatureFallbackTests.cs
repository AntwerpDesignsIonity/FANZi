using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>Hero/overlay CPU gauge: package → hotspot → average core, null ("--") when none.</summary>
public sealed class CpuTemperatureFallbackTests
{
    [Theory]
    [InlineData(70.0, 80.0, 60.0, 70.0)]          // package wins
    [InlineData(null, 80.0, 60.0, 80.0)]          // no package → hotspot
    [InlineData(null, null, 60.0, 60.0)]          // only average core
    [InlineData(double.NaN, 80.0, 60.0, 80.0)]    // NaN package is skipped
    [InlineData(null, double.NaN, 61.5, 61.5)]
    [InlineData(double.PositiveInfinity, null, 55.0, 55.0)]
    public void Select_FollowsFallbackChain(double? package, double? hotspot, double? average, double expected)
    {
        Assert.Equal(expected, CpuTemperatureFallback.Select(package, hotspot, average));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(double.NaN, double.NaN, double.NaN)]
    public void Select_NoReading_ReturnsNull(double? package, double? hotspot, double? average)
    {
        Assert.Null(CpuTemperatureFallback.Select(package, hotspot, average));
    }
}
