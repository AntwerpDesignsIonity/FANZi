using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>v4.0.2: LOW-blue → HOT-red heat gradients on graphs and load bars, orange peaks / new peaks.</summary>
public class HeatPeakTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static List<HistorySample> Series(params double[] values)
        => values.Select((v, i) => new HistorySample(T0.AddSeconds(i), v)).ToList();

    [Fact]
    public void ComputePeak_FindsHighestSample_AndNewPeakOnlyWhenLiveValueBeatsHistory()
    {
        var older = SeriesMath.ComputePeak(Series(50, 70, 60, 55))!.Value;
        Assert.Equal(70, older.Value);
        Assert.Equal(T0.AddSeconds(1), older.TimeUtc);
        Assert.False(older.IsNew);

        var fresh = SeriesMath.ComputePeak(Series(50, 70, 60, 72))!.Value;
        Assert.Equal(72, fresh.Value);
        Assert.True(fresh.IsNew);

        // Equal to the old max is not a NEW peak; a flat line has no peak at all.
        Assert.False(SeriesMath.ComputePeak(Series(50, 70, 60, 70))!.Value.IsNew);
        Assert.Null(SeriesMath.ComputePeak(Series(60, 60, 60)));
        Assert.Null(SeriesMath.ComputePeak(Series(60)));
    }

    [AvaloniaFact]
    public void Chart_MarksPeakOrange_AndNowDotTurnsOrangeOnNewPeak()
    {
        var history = new MetricHistory();
        for (int i = 0; i <= 30; i++)
            history.Add(T0.AddSeconds(i), i == 10 ? 80 : 50);

        var chart = new ThermalHistoryChart { History = history, TimeWindow = TimeSpan.FromMinutes(1), Width = 400, Height = 150 };
        var window = new Window { Content = chart, Width = 500, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var peak = Assert.NotNull(chart.Peak);
        Assert.Equal(80, peak.Value);
        Assert.False(peak.IsNew);
        var peakAt = Assert.NotNull(chart.PeakPoint);
        Assert.True(peakAt.Y < chart.NowPoint!.Value.Y, "peak marker sits above the live value");
        Assert.Equal(chart.HeatColor(50), chart.NowDotColor);

        history.Add(T0.AddSeconds(31), 88); // new peak
        chart.Revision++;
        Dispatcher.UIThread.RunJobs();
        Assert.True(chart.Peak!.Value.IsNew);
        Assert.Equal(TemperatureColorScale.PeakColor, chart.NowDotColor);

        history.Add(T0.AddSeconds(32), 60); // falls back: peak stays marked, dot back to heat colour
        chart.Revision++;
        Dispatcher.UIThread.RunJobs();
        Assert.False(chart.Peak!.Value.IsNew);
        Assert.Equal(88, chart.Peak!.Value.Value);
        Assert.Equal(chart.HeatColor(60), chart.NowDotColor);

        window.Close();
    }

    [AvaloniaFact]
    public void Chart_HeatRange_IsLowBlueToHotRed()
    {
        var chart = new ThermalHistoryChart();
        Assert.Equal(Color.Parse("#38BDF8"), chart.HeatColor(30));
        Assert.Equal(Color.Parse("#EF4444"), chart.HeatColor(95));

        chart.HeatLow = 0;
        chart.HeatHigh = 100;
        Assert.Equal(TemperatureColorScale.ForLoad(50), chart.HeatColor(50));
    }

    [Fact]
    public void HeatBarFill_EndsOnTheValuesHeatColour()
    {
        var full = Assert.IsType<ImmutableLinearGradientBrush>(HeatBar.Fill(100));
        Assert.Equal(Color.Parse("#38BDF8"), full.GradientStops[0].Color);
        Assert.Equal(Color.Parse("#EF4444"), full.GradientStops[^1].Color);

        var half = Assert.IsType<ImmutableLinearGradientBrush>(HeatBar.Fill(50));
        Assert.Equal(TemperatureColorScale.ForLoad(50), half.GradientStops[^1].Color);
        Assert.All(half.GradientStops, s => Assert.InRange(s.Offset, 0, 1));

        var peak = Assert.IsType<ImmutableLinearGradientBrush>(HeatBar.NewPeakFill(60));
        Assert.Equal(TemperatureColorScale.PeakColor, peak.GradientStops[^1].Color);

        Assert.Same(HeatBar.Fill(42), HeatBar.Fill(42)); // cached, no per-tick allocation
    }

    [AvaloniaFact]
    public void HeatBar_TracksRollingPeak_AndFlagsNewPeak()
    {
        long now = 0;
        var oldClock = HeatBar.Clock;
        HeatBar.Clock = () => now;
        try
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
            HeatBar.SetIsEnabled(bar, true);
            now += 500; bar.Value = 20; // first real value after start-up is not a "new peak"
            Assert.False(HeatBar.IsNewPeak(bar));
            Assert.Same(HeatBar.Fill(20), bar.Foreground);
            now += 500; bar.Value = 25;
            now += 500; bar.Value = 22;

            now += 500; bar.Value = 70;
            Assert.True(HeatBar.IsNewPeak(bar));
            Assert.Same(HeatBar.NewPeakFill(70), bar.Foreground);
            Assert.Equal(0.70, HeatBar.PeakFraction(bar)!.Value, 6);

            now += 1000; bar.Value = 40;
            Assert.False(HeatBar.IsNewPeak(bar));
            Assert.Same(HeatBar.Fill(40), bar.Foreground);
            Assert.Same(HeatBar.Track(70), bar.Background); // orange tick stays at the 70 % peak

            now += (long)HeatBar.PeakWindow.TotalMilliseconds + 1000; bar.Value = 30; // old peak decays
            Assert.Equal(0.30, HeatBar.PeakFraction(bar)!.Value, 6);

            HeatBar.SetIsEnabled(bar, false); // turning it off restores the styled brushes
            Assert.False(bar.IsSet(ProgressBar.ForegroundProperty));
        }
        finally
        {
            HeatBar.Clock = oldClock;
        }
    }
}
