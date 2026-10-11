using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class SensorMatchingTests
{
    private const string Board = "/lpc/nct6798d/0";
    private const string Gpu = "/gpu-nvidia/0";

    [Theory]
    [InlineData("Fan #1", "1")]
    [InlineData("Fan Control #1", "1")]
    [InlineData("Fan #10", "10")]
    [InlineData("CPU Fan", "cpu")]
    [InlineData("System Fan #2", "system2")]
    [InlineData("Fan", "")]
    [InlineData("Fan Control", "")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void NormalizeSensorName_StripsFanControlHashAndWhitespace(string? name, string expected)
    {
        Assert.Equal(expected, HardwareMonitorService.NormalizeSensorName(name));
    }

    [Fact]
    public void Fan1_PicksControl1_NotControl10()
    {
        var controls = new[]
        {
            new SensorMatchKey(Board, 9, "Fan Control #10"),
            new SensorMatchKey(Board, 0, "Fan Control #1"),
        };

        int match = HardwareMonitorService.FindBestControlIndex(new SensorMatchKey(Board, 0, "Fan #1"), controls);

        Assert.Equal(1, match);
    }

    [Fact]
    public void Fan10_PicksControl10_NotControl1()
    {
        var controls = new[]
        {
            new SensorMatchKey(Board, 0, "Fan Control #1"),
            new SensorMatchKey(Board, 9, "Fan Control #10"),
        };

        int match = HardwareMonitorService.FindBestControlIndex(new SensorMatchKey(Board, 9, "Fan #10"), controls);

        Assert.Equal(1, match);
    }

    [Fact]
    public void Fan1_DoesNotMatchControl10_ByContains()
    {
        // The old bidirectional Contains matched "1" inside "10" and produced duplicate channel IDs.
        var controls = new[] { new SensorMatchKey(Board, 9, "Fan Control #10") };

        int match = HardwareMonitorService.FindBestControlIndex(new SensorMatchKey(Board, 0, "Fan #1"), controls);

        Assert.Equal(-1, match);
    }

    [Fact]
    public void EmptyNormalizedName_NeverMatchesByName()
    {
        // "Fan" normalizes to "" — it used to "contain" in every control name and match everything.
        var controls = new[]
        {
            new SensorMatchKey(Board, 3, "Fan Control #4"),
            new SensorMatchKey(Board, 5, "Control"),
        };

        int match = HardwareMonitorService.FindBestControlIndex(new SensorMatchKey(Board, 0, "Fan"), controls);

        Assert.Equal(-1, match);
    }

    [Fact]
    public void EmptyNormalizedName_StillMatchesBySameIndex()
    {
        var controls = new[] { new SensorMatchKey(Gpu, 0, "GPU Fan") };

        int match = HardwareMonitorService.FindBestControlIndex(new SensorMatchKey(Gpu, 0, "Fan"), controls);

        Assert.Equal(0, match);
    }

    [Fact]
    public void DifferentHardware_NeverMatches()
    {
        var controls = new[] { new SensorMatchKey(Gpu, 0, "Fan Control #1") };

        int match = HardwareMonitorService.FindBestControlIndex(new SensorMatchKey(Board, 0, "Fan #1"), controls);

        Assert.Equal(-1, match);
    }

    [Fact]
    public void ExactNameMatch_WinsOverIndexMatch()
    {
        var controls = new[]
        {
            new SensorMatchKey(Board, 2, "Fan Control #7"), // same index, different name
            new SensorMatchKey(Board, 6, "Fan Control #3"), // exact name
        };

        int match = HardwareMonitorService.FindBestControlIndex(new SensorMatchKey(Board, 2, "Fan #3"), controls);

        Assert.Equal(1, match);
    }

    [Fact]
    public void ClaimedControl_IsSkipped()
    {
        var controls = new[] { new SensorMatchKey(Board, 0, "Fan Control #1") };
        var claimed = new HashSet<int> { 0 };

        int match = HardwareMonitorService.FindBestControlIndex(new SensorMatchKey(Board, 0, "Fan #1"), controls, claimed);

        Assert.Equal(-1, match);
    }

    [Fact]
    public void PickUniqueChannelId_FallsBackToFanIdThenSuffix()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("/ctrl/0", HardwareMonitorService.PickUniqueChannelId("/ctrl/0", "/fan/0", used));
        Assert.Equal("/fan/1", HardwareMonitorService.PickUniqueChannelId("/ctrl/0", "/fan/1", used));
        Assert.Equal("/ctrl/0#2", HardwareMonitorService.PickUniqueChannelId("/ctrl/0", "/fan/1", used));
        Assert.Equal("/fan/2", HardwareMonitorService.PickUniqueChannelId(null, "/fan/2", used));
        Assert.Equal("channel", HardwareMonitorService.PickUniqueChannelId(null, null, used));
        Assert.Equal("channel#2", HardwareMonitorService.PickUniqueChannelId(null, null, used));
    }

    [Fact]
    public void PickUniqueChannelId_IsDeterministicAcrossPolls()
    {
        string[] Poll()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new[]
            {
                HardwareMonitorService.PickUniqueChannelId("/ctrl/0", "/fan/0", used),
                HardwareMonitorService.PickUniqueChannelId("/ctrl/0", null, used),
            };
        }

        Assert.Equal(Poll(), Poll());
    }
}
