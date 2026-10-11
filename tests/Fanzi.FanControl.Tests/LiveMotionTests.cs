using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>
/// v4.0.3 "real readings" motion: numbers, gauges, bars and charts move continuously from one real reading to the
/// next (linear, ~92 % of the measured poll interval) instead of snapping and then sitting still.
/// </summary>
public class LiveCadenceTests
{
    [Fact]
    public void UsesTheDefault_UntilARhythmIsMeasured()
    {
        var cadence = new LiveCadence(TimeSpan.FromMilliseconds(400));
        Assert.Equal(TimeSpan.FromMilliseconds(400), cadence.Next(0));
        Assert.Equal(TimeSpan.FromMilliseconds(400), cadence.Next(20)); // a burst (two setters at once) is not a rhythm
        Assert.Null(cadence.AverageGapMs);
    }

    [Fact]
    public void OneSecondPoll_GlidesForMostOfTheSecond()
    {
        var cadence = new LiveCadence(TimeSpan.FromMilliseconds(400));
        long t = 0;
        cadence.Next(t);
        for (int i = 0; i < 6; i++) cadence.Next(t += 1000);
        Assert.Equal(1000, cadence.AverageGapMs!.Value, 6);
        Assert.Equal(TimeSpan.FromMilliseconds(920), cadence.Glide);
    }

    [Fact]
    public void JitterIsSmoothed_RepeatsAreNotASlowerPoll_AndAPauseRelearns()
    {
        var cadence = new LiveCadence(TimeSpan.FromMilliseconds(400));
        long t = 0;
        cadence.Next(t);
        foreach (var gap in new[] { 1000, 1100, 900, 1050, 950 }) cadence.Next(t += gap);
        Assert.InRange(cadence.AverageGapMs!.Value, 940, 1060);

        cadence.Next(t += 3000); // value unchanged for two polls: still a 1 s rhythm
        Assert.InRange(cadence.AverageGapMs!.Value, 940, 1060);

        cadence.Next(t += 500); // switched to a faster rhythm (0.5 s panel): adopted at once
        Assert.Equal(500, cadence.AverageGapMs!.Value, 6);

        cadence.Next(t += 60_000); // window hidden / sleep: forget and relearn
        Assert.Null(cadence.AverageGapMs);
        Assert.Equal(TimeSpan.FromMilliseconds(400), cadence.Glide);
    }

    [Theory]
    [InlineData(50, 150)]     // never shorter than 150 ms
    [InlineData(500, 460)]    // 0.5 s panels
    [InlineData(1000, 920)]   // 1 s hardware poll
    [InlineData(3000, 1500)]  // slow polls settle within 1.5 s
    public void GlideFor_FillsTheIntervalWithinBounds(double gapMs, double expectedMs)
        => Assert.Equal(expectedMs, LiveCadence.GlideFor(gapMs).TotalMilliseconds, 6);

    [Fact]
    public void GlideAt_IsLinear_Clamped_AndLandsExactly()
    {
        Assert.Equal(72, NumberTween.GlideAt(72, 80, 0));
        Assert.Equal(74, NumberTween.GlideAt(72, 80, 0.25), 9);
        Assert.Equal(76, NumberTween.GlideAt(72, 80, 0.5), 9);
        Assert.Equal(80, NumberTween.GlideAt(72, 80, 1));
        Assert.Equal(80, NumberTween.GlideAt(72, 80, 7));
        Assert.Equal(72, NumberTween.GlideAt(72, 80, -1));
        Assert.Equal(80, NumberTween.GlideAt(72, 80, double.NaN));
        Assert.Equal(90, NumberTween.GlideAt(96, 80, 0.375), 9); // counts down too
    }
}

public class LiveSeriesMathTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static List<HistorySample> Ramp(int seconds) // 50, 51, 52 … one per second
        => Enumerable.Range(0, seconds + 1).Select(i => new HistorySample(T0.AddSeconds(i), 50 + i)).ToList();

    [Fact]
    public void IsLive_OnlyForAFreshFeed()
    {
        var samples = Ramp(10);
        Assert.True(SeriesMath.IsLive(samples, T0.AddSeconds(10.4)));
        Assert.True(SeriesMath.IsLive(samples, T0.AddSeconds(13.4)));
        Assert.False(SeriesMath.IsLive(samples, T0.AddSeconds(20)));  // stale (poll stopped / historic data)
        Assert.False(SeriesMath.IsLive(samples, T0.AddSeconds(5)));   // newest sample far in the future
        Assert.False(SeriesMath.IsLive(samples.Take(1).ToList(), T0)); // one sample is no feed
    }

    [Fact]
    public void DisplayLag_IsOneIntervalPlusSlack()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1210), SeriesMath.DisplayLag(Ramp(5)));
        Assert.Equal(TimeSpan.Zero, SeriesMath.DisplayLag(Ramp(0)));
    }

    [Fact]
    public void Slice_InterpolatesBothEdges()
    {
        var slice = SeriesMath.Slice(Ramp(10), T0.AddSeconds(2.5), T0.AddSeconds(7.25));
        Assert.Equal(T0.AddSeconds(2.5), slice[0].TimeUtc);
        Assert.Equal(52.5, slice[0].Value, 9);
        Assert.Equal(T0.AddSeconds(7.25), slice[^1].TimeUtc);
        Assert.Equal(57.25, slice[^1].Value, 9);
        Assert.Equal(new double[] { 52.5, 53, 54, 55, 56, 57, 57.25 }, slice.Select(s => Math.Round(s.Value, 6)));

        // Clamped to the data; an empty range gives nothing.
        var all = SeriesMath.Slice(Ramp(3), T0.AddSeconds(-10), T0.AddSeconds(99));
        Assert.Equal(4, all.Count);
        Assert.Empty(SeriesMath.Slice(Ramp(3), T0.AddSeconds(2), T0.AddSeconds(1)));
    }

    [Fact]
    public void ValueAtTime_InterpolatesAndClamps()
    {
        var samples = Ramp(4);
        Assert.Equal(50, SeriesMath.ValueAtTime(samples, T0.AddSeconds(-1)));
        Assert.Equal(51.5, SeriesMath.ValueAtTime(samples, T0.AddSeconds(1.5)), 9);
        Assert.Equal(54, SeriesMath.ValueAtTime(samples, T0.AddSeconds(9)));
        Assert.True(double.IsNaN(SeriesMath.ValueAtTime(Array.Empty<HistorySample>(), T0)));
    }

    [Fact]
    public void DownsampleByTime_UsesFixedSlots_SoSlidingTheWindowKeepsInteriorPointsStable()
    {
        var samples = Ramp(300);
        var bucket = TimeSpan.FromSeconds(4);

        var a = SeriesMath.DownsampleByTime(SeriesMath.Slice(samples, T0.AddSeconds(10.2), T0.AddSeconds(250.2)), bucket);
        var b = SeriesMath.DownsampleByTime(SeriesMath.Slice(samples, T0.AddSeconds(10.7), T0.AddSeconds(250.7)), bucket);

        Assert.True(a.Count < 80);
        Assert.Equal(T0.AddSeconds(10.2), a[0].TimeUtc);           // edges kept exactly
        Assert.Equal(T0.AddSeconds(250.2), a[^1].TimeUtc);
        // Interior buckets fully inside both windows are identical (no shimmer while scrolling).
        var interiorA = a.Skip(2).Take(a.Count - 4).ToList();
        var interiorB = b.Skip(2).Take(b.Count - 4).ToList();
        Assert.Equal(interiorA, interiorB.Take(interiorA.Count));
        for (int i = 1; i < a.Count; i++) Assert.True(a[i].TimeUtc >= a[i - 1].TimeUtc);
    }
}

public class LiveMotionControlTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public void LiveGlide_GivesTheBarALinearValueGlide_TunedToItsCadence()
    {
        long now = 0;
        var oldClock = LiveCadence.Clock;
        LiveCadence.Clock = () => now;
        try
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 10 };
            LiveGlide.SetIsEnabled(bar, true);

            var transitions = Assert.IsType<Transitions>(bar.Transitions);
            var glide = Assert.IsType<DoubleTransition>(transitions[0]);
            Assert.Equal(ProgressBar.ValueProperty, glide.Property);
            Assert.IsType<LinearEasing>(glide.Easing);
            Assert.Contains(transitions, t => t is BrushTransition); // plain bars keep the colour fade
            Assert.Equal(LiveGlide.DefaultGlide, LiveGlide.NextGlide(bar));

            for (int i = 1; i <= 5; i++) { now += 1000; bar.Value = 10 + i * 7; }
            Assert.Equal(TimeSpan.FromMilliseconds(920), LiveGlide.NextGlide(bar));
            Assert.Same(transitions, bar.Transitions); // tuned in place, never swapped mid-glide

            MotionPreferences.ReduceMotion = true;
            now += 1000; bar.Value = 80;
            Assert.Null(bar.Transitions);
            MotionPreferences.ReduceMotion = false;
            now += 1000; bar.Value = 75;
            Assert.Same(transitions, bar.Transitions);

            LiveGlide.SetIsEnabled(bar, false);
            Assert.Null(LiveGlide.NextGlide(bar));
            Assert.False(bar.IsSet(ProgressBar.TransitionsProperty));
        }
        finally
        {
            MotionPreferences.ReduceMotion = false;
            LiveCadence.Clock = oldClock;
        }
    }

    [AvaloniaFact]
    public void HeatBars_KeepTheirOwnColours_AndPeaksCountReadingsNotFrames()
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        HeatBar.SetIsEnabled(bar, true);
        LiveGlide.SetIsEnabled(bar, true);
        var transitions = Assert.IsType<Transitions>(bar.Transitions);
        Assert.DoesNotContain(transitions, t => t is BrushTransition); // HeatBar sets the fill per frame itself

        foreach (var v in new[] { 20.0, 25, 22, 30 }) bar.Value = v;
        Assert.Equal(0.30, HeatBar.ReadingFraction(bar), 6);
        Assert.Equal(0.30, HeatBar.PeakFraction(bar)!.Value, 6);
    }

    [AvaloniaFact]
    public void Gauge_SweepIsLinear_AndFollowsTheMeasuredPoll()
    {
        long now = 0;
        var oldClock = LiveCadence.Clock;
        LiveCadence.Clock = () => now;
        try
        {
            var gauge = new ThermalGauge { Value = 40 };
            for (int i = 1; i <= 5; i++) { now += 1000; gauge.Value = 40 + i; }
            Assert.Equal(TimeSpan.FromMilliseconds(920), gauge.SweepDuration);
        }
        finally
        {
            LiveCadence.Clock = oldClock;
        }
    }

    [AvaloniaFact]
    public void AnimatedNumber_CountLengthFollowsTheMeasuredPoll()
    {
        long now = 0;
        var oldClock = LiveCadence.Clock;
        LiveCadence.Clock = () => now;
        try
        {
            var block = new TextBlock();
            for (int i = 0; i <= 5; i++) { AnimatedNumber.SetText(block, $"{60 + i} °C"); now += 500; }
            Assert.Equal(TimeSpan.FromMilliseconds(460), AnimatedNumber.NextGlide(block));
            Assert.Equal("65 °C", block.Text); // detached: set at once
        }
        finally
        {
            LiveCadence.Clock = oldClock;
        }
    }

    [AvaloniaFact]
    public void Chart_ScrollsContinuously_WithTheLiveDotOnAnInterpolatedReading()
    {
        var oldNow = ThermalHistoryChart.UtcNow;
        var now = T0.AddSeconds(60.2);
        ThermalHistoryChart.UtcNow = () => now;
        try
        {
            var history = new MetricHistory();
            for (int i = 0; i <= 60; i++) history.Add(T0.AddSeconds(i), 50 + i % 20); // last = 50, saw-tooth
            var chart = new ThermalHistoryChart { History = history, TimeWindow = TimeSpan.FromMinutes(1), Width = 400, Height = 150 };
            var window = new Window { Content = chart, Width = 500, Height = 300 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(chart.IsLiveScrolling);
            var plot = ThermalHistoryChart.PlotRect(new Avalonia.Size(400, 150), showLabels: true);
            var first = Assert.NotNull(chart.NowPoint);
            Assert.Equal(plot.Right, first.X, 6);                 // the curve still ends on the right edge…
            Assert.Equal(new SeriesStats(50, 69, 50), chart.Stats); // …and the header keeps the real latest reading

            // 0.2 s later the curve has moved a little — not a whole sample step.
            var dotY = new List<double> { first.Y };
            for (int f = 0; f < 4; f++)
            {
                now += TimeSpan.FromMilliseconds(50);
                chart.InvalidateArrange();
                Dispatcher.UIThread.RunJobs();
                dotY.Add(chart.NowPoint!.Value.Y);
            }
            double maxStep = dotY.Zip(dotY.Skip(1), (a, b) => Math.Abs(a - b)).Max();
            double sampleStep = plot.Height / 25; // ~1 °C on this axis; a whole-sample jump moves the dot ~19 °C here
            Assert.True(maxStep < sampleStep * 2, $"dot jumped {maxStep:F2} px in 50 ms");

            // A new reading arrives: the edge does not jump forwards or backwards.
            history.Add(T0.AddSeconds(61), 70);
            chart.Revision++;
            Dispatcher.UIThread.RunJobs();
            Assert.True(chart.IsLiveScrolling);
            Assert.Equal(70, chart.Stats!.Value.Current);

            // Poll stops: after a few intervals the chart falls back to "right edge = newest sample".
            now = T0.AddSeconds(90);
            chart.InvalidateArrange();
            Dispatcher.UIThread.RunJobs();
            Assert.False(chart.IsLiveScrolling);

            window.Close();
        }
        finally
        {
            ThermalHistoryChart.UtcNow = oldNow;
        }
    }

    [Fact]
    public void ScrollFrameRate_MatchesPixelTravel()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(50), ThermalHistoryChart.ScrollFrameInterval(TimeSpan.FromMinutes(1), 400));
        Assert.Equal(TimeSpan.FromMilliseconds(100), ThermalHistoryChart.ScrollFrameInterval(TimeSpan.FromMinutes(5), 100));
        Assert.Equal(TimeSpan.FromMilliseconds(16), ThermalHistoryChart.ScrollFrameInterval(TimeSpan.FromSeconds(10), 2000));
    }
}
