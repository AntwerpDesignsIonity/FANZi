using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class UpgradeHandoverTests
{
    [Theory]
    [InlineData("3.0.0", "4.0.0", true)]
    [InlineData("3.0.0+5a8b166", "4.0.0", true)]
    [InlineData("v2.2.0", "4.0.0", true)]
    [InlineData("4.0.0", "4.0.0", false)]
    [InlineData("4.1.0", "4.0.0", false)]
    [InlineData("garbage", "4.0.0", false)]
    [InlineData(null, "4.0.0", false)]
    public void IsOlder_OnlyReplacesStrictlyOlderBuilds(string? candidate, string current, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, UpgradeHandover.IsOlder(candidate, current));
    }
}
