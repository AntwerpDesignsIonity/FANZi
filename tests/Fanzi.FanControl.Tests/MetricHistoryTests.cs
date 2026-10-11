using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public class MetricHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Defaults_Keep900SamplesAndFifteenMinutes()
    {
        var history = new MetricHistory();
        Assert.Equal(900, history.Capacity);
        Assert.Equal(TimeSpan.FromMinutes(15), history.MaxAge);
        Assert.Null(history.Latest);
        Assert.Empty(history.GetWindow(TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Add_MissingOrNonFinite_IsSkipped(double? value)
    {
        var history = new MetricHistory();
        Assert.False(history.Add(T0, value));
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Add_OneSecondPollForTwentyMinutes_CapsAt900AndKeepsNewest()
    {
        var history = new MetricHistory();
        for (int i = 0; i < 1200; i++)
            Assert.True(history.Add(T0.AddSeconds(i), i));

        Assert.Equal(900, history.Count);
        Assert.Equal(new HistorySample(T0.AddSeconds(1199), 1199), history.Latest);
        Assert.Equal(300, history.GetWindow(TimeSpan.FromHours(1))[0].Value); // oldest kept = 1199 - 899
    }

    [Fact]
    public void Add_SlowPoll_DropsSamplesOlderThanMaxAge()
    {
        // 10 s polling (window hidden): 900 slots would hold 2.5 h, but only 15 min are kept.
        var history = new MetricHistory();
        for (int i = 0; i <= 180; i++) // 30 minutes
            history.Add(T0.AddSeconds(i * 10), i);

        var all = history.GetWindow(TimeSpan.FromDays(1));
        Assert.Equal(91, all.Count); // 15 min / 10 s + the sample exactly at the cutoff
        Assert.Equal(T0.AddMinutes(15), all[0].TimeUtc);
        Assert.Equal(T0.AddMinutes(30), all[^1].TimeUtc);
    }

    [Fact]
    public void Add_ClockMovedBack_RestartsHistory()
    {
        var history = new MetricHistory();
        history.Add(T0, 60);
        history.Add(T0.AddSeconds(3), 61);

        history.Add(T0.AddHours(-1), 62);

        Assert.Equal(1, history.Count);
        Assert.Equal(new HistorySample(T0.AddHours(-1), 62), history.Latest);
    }

    [Theory]
    [InlineData(1, 61)]   // 60 s back from the newest sample, both ends inclusive
    [InlineData(5, 301)]
    [InlineData(15, 600)] // only 600 samples exist
    public void GetWindow_ReturnsSamplesWithinWindowOfNewest_OldestFirst(int minutes, int expectedCount)
    {
        var history = new MetricHistory();
        for (int i = 0; i < 600; i++)
            history.Add(T0.AddSeconds(i), 40 + i % 7);

        var window = history.GetWindow(TimeSpan.FromMinutes(minutes));

        Assert.Equal(expectedCount, window.Count);
        Assert.Equal(T0.AddSeconds(599), window[^1].TimeUtc);
        Assert.True(window.Zip(window.Skip(1)).All(p => p.First.TimeUtc < p.Second.TimeUtc), "window must be oldest first");
        Assert.True(window[0].TimeUtc >= T0.AddSeconds(599) - TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public void GetWindow_ZeroOrNegative_ReturnsOnlyNewest()
    {
        var history = new MetricHistory();
        history.Add(T0, 1);
        history.Add(T0.AddSeconds(1), 2);

        Assert.Equal(new[] { 2.0 }, history.GetWindow(TimeSpan.Zero).Select(s => s.Value));
        Assert.Equal(new[] { 2.0 }, history.GetWindow(TimeSpan.FromSeconds(-5)).Select(s => s.Value));
    }

    [Fact]
    public void GetWindow_IsASnapshot_NotALiveView()
    {
        var history = new MetricHistory();
        history.Add(T0, 1);
        var window = history.GetWindow(TimeSpan.FromMinutes(5));

        history.Add(T0.AddSeconds(1), 2);

        Assert.Single(window);
    }

    [Fact]
    public void ThermalHistory_Record_SkipsMissingReadingsPerSeries()
    {
        var history = new ThermalHistory();
        history.Record(T0, cpuTempC: 72, gpuTempC: null, cpuLoadPct: 38, gpuLoadPct: double.NaN, coolerRpm: 1180);
        history.Record(T0.AddSeconds(3), cpuTempC: 73, gpuTempC: 64, cpuLoadPct: 40, gpuLoadPct: 47, coolerRpm: null);

        Assert.Equal(2, history.CpuTemperature.Count);
        Assert.Equal(1, history.GpuTemperature.Count);
        Assert.Equal(2, history.CpuLoad.Count);
        Assert.Equal(1, history.GpuLoad.Count);
        Assert.Equal(1, history.CoolerRpm.Count);
        Assert.Equal(73, history.CpuTemperature.Latest!.Value.Value);
        Assert.Equal(1180, history.CoolerRpm.Latest!.Value.Value);

        history.Clear();
        Assert.Equal(0, history.CpuTemperature.Count + history.GpuTemperature.Count + history.CpuLoad.Count
                         + history.GpuLoad.Count + history.CoolerRpm.Count);
    }
}
