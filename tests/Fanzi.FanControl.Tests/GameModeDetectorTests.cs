using Fanzi.FanControl.AI;

namespace Fanzi.FanControl.Tests;

/// <summary>Dashboard Game Mode tile text: short static title, name + duration on the detail line.</summary>
public sealed class GameModeDetectorTests
{
    [Fact]
    public void DetailMessage_Desktop_IsEmpty()
    {
        var detector = new GameModeDetector(() => (false, ""));
        Assert.False(detector.IsGameModeActive);
        Assert.Equal("", detector.DetailMessage);
        Assert.Equal("Desktop mode", detector.StatusMessage);
    }

    [Fact]
    public void DetailMessage_DetectedGame_CarriesNameAndDuration()
    {
        var detector = new GameModeDetector(() => (true, "Cyberpunk2077"));
        detector.SetDetectedGame("Cyberpunk2077");

        Assert.Equal("Cyberpunk2077 · 0 min", detector.DetailMessage);
        // Full text kept for the tooltip / tray / log.
        Assert.Equal("🎮 Game Mode: Cyberpunk2077 (0m)", detector.StatusMessage);
    }

    [Fact]
    public void DetailMessage_LoadHeuristicWithoutName_DescribesTheTrigger()
    {
        var detector = new GameModeDetector(() => (false, ""));
        for (int i = 0; i < 5; i++)
            detector.Update(gpuLoadPercent: 90, cpuTempC: 80);

        Assert.True(detector.IsGameModeActive);
        Assert.Equal("Sustained GPU load · 0 min", detector.DetailMessage);
    }
}
