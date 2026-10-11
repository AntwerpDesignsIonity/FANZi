using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public class ThermalHistoryChartTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public void Chart_PlacesNowDotOnTheNewestSample_AndFollowsRevision()
    {
        var history = new MetricHistory();
        var chart = new ThermalHistoryChart { History = history, TimeWindow = TimeSpan.FromMinutes(1), Width = 400, Height = 150 };
        var window = new Window { Content = chart, Width = 500, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Empty: no dot, no stats.
        Assert.Null(chart.NowPoint);
        Assert.Null(chart.Stats);

        for (int i = 0; i <= 60; i++)
            history.Add(T0.AddSeconds(i), 60 + i % 10);
        chart.Revision++;
        Dispatcher.UIThread.RunJobs();

        var plot = ThermalHistoryChart.PlotRect(new Avalonia.Size(400, 150), showLabels: true);
        var now = Assert.NotNull(chart.NowPoint);
        Assert.Equal(plot.Right, now.X, 6);
        Assert.InRange(now.Y, plot.Top, plot.Bottom);
        Assert.Equal(new SeriesStats(60, 69, 60), chart.Stats);

        // Hotter newest sample → the dot moves up (smaller Y) after the next revision.
        history.Add(T0.AddSeconds(61), 69);
        chart.Revision++;
        Dispatcher.UIThread.RunJobs();
        Assert.True(chart.NowPoint!.Value.Y < now.Y);

        window.Close();
    }

    [AvaloniaFact]
    public void Chart_WindowSelectsWhichSamplesCount()
    {
        var history = new MetricHistory();
        for (int i = 0; i < 600; i++) // 10 min: a 95 °C spike 8 min ago, then calm
            history.Add(T0.AddSeconds(i), i == 120 ? 95 : 65);

        var chart = new ThermalHistoryChart { History = history, TimeWindow = TimeSpan.FromMinutes(5), Width = 300, Height = 120 };
        var window = new Window { Content = chart, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(65, chart.Stats!.Value.Max);

        chart.TimeWindow = TimeSpan.FromMinutes(15);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(95, chart.Stats!.Value.Max);

        window.Close();
    }
}
