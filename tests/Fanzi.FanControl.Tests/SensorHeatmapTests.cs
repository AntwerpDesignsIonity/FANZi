using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public sealed class SensorHeatmapTests
{
    private static (string Name, string Value, string Kind) T(string name, string value) => (name, value, "Temperature");
    private static (string Name, string Value, string Kind) L(string name, string value) => (name, value, "Load");

    [Theory]
    [InlineData("72.4 C", 72.4)]
    [InlineData("72,4 °C", 72.4)]   // the hardware service formats with the user's culture
    [InlineData("45%", 45)]
    [InlineData("41 %", 41)]
    [InlineData("-3.5 C", -3.5)]
    [InlineData("2610 MHz", 2610)]
    public void TryParseValue_ReadsTheLeadingNumber(string text, double expected)
    {
        Assert.True(SensorHeatmap.TryParseValue(text, out double value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("n/a")]
    public void TryParseValue_RejectsTextWithoutANumber(string? text)
        => Assert.False(SensorHeatmap.TryParseValue(text, out _));

    [Fact]
    public void BuildCpuCores_IntelNames_OneTilePerCore_TemperatureWithAveragedThreadLoad()
    {
        var tiles = SensorHeatmap.BuildCpuCores(new[]
        {
            T("CPU Core #2", "70.0 C"), T("CPU Core #1", "64.6 C"), T("CPU Core #10", "80 C"),
            T("CPU Core #1 Distance to TjMax", "35 C"), T("Core Max", "80 C"), T("CPU Package", "72 C"),
            L("CPU Core #1 Thread #1", "40%"), L("CPU Core #1 Thread #2", "60%"), L("CPU Core #2", "10%"),
            L("CPU Total", "33%"), ("CPU Core #1", "4700 MHz", "Clock"),
        });

        Assert.Equal(new[] { "Core 1", "Core 2", "Core 10" }, tiles.Select(t => t.Label)); // numeric, not alphabetical
        Assert.All(tiles, t => Assert.Equal(HeatmapScale.Temperature, t.Scale));
        Assert.Equal(64.6, tiles[0].Value, 6);
        Assert.Equal("65°C", tiles[0].ValueText);
        Assert.Equal("50% load", tiles[0].Detail);      // thread loads averaged
        Assert.Equal("10% load", tiles[1].Detail);
        Assert.Equal(string.Empty, tiles[2].Detail);    // no load sensor for core 10
    }

    [Fact]
    public void BuildCpuCores_WithoutCoreTemperatures_ColoursByLoad()
    {
        // AMD Zen: CCD/Tctl temperatures only, per-core loads.
        var tiles = SensorHeatmap.BuildCpuCores(new[]
        {
            T("Core (Tctl/Tdie)", "68 C"), T("CCD1 (Tdie)", "61 C"),
            L("CPU Core #1", "12%"), L("CPU Core #2", "87.6%"),
        });

        Assert.Equal(2, tiles.Count);
        Assert.All(tiles, t => Assert.Equal(HeatmapScale.Load, t.Scale));
        Assert.Equal("88%", tiles[1].ValueText);
        Assert.Equal(0.876, tiles[1].Fill, 6);
    }

    [Fact]
    public void BuildCpuCores_HybridCpu_PCoresBeforeECores()
    {
        var tiles = SensorHeatmap.BuildCpuCores(new[]
        {
            T("E-Core #1", "55 C"), T("P-Core #2", "71 C"), T("P-Core #1", "74 C"), T("E-Core #2", "54 C"),
        });

        Assert.Equal(new[] { "P-Core 1", "P-Core 2", "E-Core 1", "E-Core 2" }, tiles.Select(t => t.Label));
        Assert.Equal(tiles.Count, tiles.Select(t => t.Key).Distinct().Count());
    }

    [Fact]
    public void BuildCpuCores_DuplicateCoreTemperature_KeepsTheHottest()
    {
        var tiles = SensorHeatmap.BuildCpuCores(new[] { T("CPU Core #1", "60 C"), T("Core #1", "66 C") });

        Assert.Equal(66, Assert.Single(tiles).Value);
    }

    [Fact]
    public void BuildCpuSummary_SkipsCoresAndHeadroom_TemperaturesBeforeLoads()
    {
        var tiles = SensorHeatmap.BuildCpuSummary(new[]
        {
            L("CPU Total", "38%"), T("CPU Package", "72.0 C"), T("CPU Core #1", "66 C"),
            T("CPU Core #1 Distance to TjMax", "34 C"), T("Core Max", "78.5 C"), L("CPU Core #1", "50%"),
            ("Package", "99.2 W", "Power"),
        });

        Assert.Equal(new[] { "Package", "Core Max", "Total load" }, tiles.Select(t => t.Label));
        Assert.Equal(new[] { HeatmapScale.Temperature, HeatmapScale.Temperature, HeatmapScale.Load }, tiles.Select(t => t.Scale));
        Assert.Equal("79°C", tiles[1].ValueText);
    }

    [Fact]
    public void BuildGpu_KeysByKindSoSameNamedSensorsBothShow_AndIsCapped()
    {
        var readings = new List<(string, string, string)>
        {
            T("GPU Core", "64 C"), L("GPU Core", "47%"), T("GPU Hot Spot", "71.2 C"), T("GPU Core", "64 C"),
        };
        for (int i = 0; i < 20; i++) readings.Add(L($"GPU Engine {i}", "5%"));

        var tiles = SensorHeatmap.BuildGpu(readings);

        Assert.Equal(SensorHeatmap.MaxGpuTiles, tiles.Count);
        Assert.Equal("Core temp", tiles[0].Label);
        Assert.Equal(HeatmapScale.Temperature, tiles[0].Scale);
        Assert.Equal("Hot Spot", tiles[1].Label);
        Assert.Equal(HeatmapScale.Load, tiles[2].Scale);   // the load "GPU Core" is its own tile
        Assert.Equal("Core load", tiles[2].Label);
        Assert.Equal(tiles.Count, tiles.Select(t => t.Key).Distinct().Count());
    }

    [Fact]
    public void BuildGpu_LabelsTilesBySensorType()
    {
        var tiles = SensorHeatmap.BuildGpu(new[]
        {
            T("GPU Core", "64 C"), T("GPU Hot Spot", "71 C"), T("GPU Memory Junction", "74 C"),
            L("GPU Core", "47%"), L("GPU Memory", "41%"),
        });

        Assert.Equal(new[] { "Core temp", "Hot Spot", "Memory Junction", "Core load", "Memory load" }, tiles.Select(t => t.Label));
        Assert.Equal(tiles.Count, tiles.Select(t => t.Label).Distinct().Count());
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(60, 0.5)]
    [InlineData(100, 1)]
    [InlineData(130, 1)]
    [InlineData(-10, 0)]
    public void Fill_MapsTemperaturesOnto20To100(double celsius, double expected)
    {
        var tile = new HeatmapTile("k", "Core 1", celsius, HeatmapScale.Temperature, "", "");
        Assert.Equal(expected, tile.Fill, 6);
    }
}
