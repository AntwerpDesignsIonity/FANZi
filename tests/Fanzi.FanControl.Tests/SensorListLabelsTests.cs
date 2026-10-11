using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class SensorListLabelsTests
{
    [Fact]
    public void RepeatedNames_GetTheirKindAppended()
    {
        var labels = SensorListLabels.Build(new[]
        {
            ("GPU Core", "Clock"), ("GPU Core", "Load"), ("GPU Package", "Power"), ("GPU Core", "Temperature"),
        });

        Assert.Equal(new[] { "GPU Core · Clock", "GPU Core · Load", "GPU Package", "GPU Core · Temp" }, labels);
    }

    [Fact]
    public void UniqueNames_StayAsTheyAre_CaseInsensitiveMatching()
    {
        var labels = SensorListLabels.Build(new[]
        {
            ("CPU Package", "Temperature"), ("CPU Total", "Load"), ("cpu core #1", "Load"), ("CPU Core #1", "Temperature"),
        });

        Assert.Equal(new[] { "CPU Package", "CPU Total", "cpu core #1 · Load", "CPU Core #1 · Temp" }, labels);
    }

    [Fact]
    public void EmptyList_GivesNoLabels() => Assert.Empty(SensorListLabels.Build(Array.Empty<(string, string)>()));
}
