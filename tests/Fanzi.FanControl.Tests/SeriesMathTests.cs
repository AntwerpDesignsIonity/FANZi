using Avalonia;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public class SeriesMathTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static HistorySample[] Series(params double[] values)
        => values.Select((v, i) => new HistorySample(T0.AddSeconds(i), v)).ToArray();

    // ── Axis ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(62, 78, 10, 60, 80, 5)]    // typical desktop load: 5 °C grid with headroom
    [InlineData(70, 96, 10, 60, 100, 10)]  // hot climb: wider span → 10 °C grid
    [InlineData(72, 72, 10, 65, 80, 5)]    // flat line still gets the minimum span
    [InlineData(20, 95, 10, 0, 100, 20)]   // very wide span → 20 °C grid
    [InlineData(1, 3, 10, 0, 10, 5)]       // never below zero for non-negative data
    public void ComputeAxis_SnapsOutwardToGridStep(double dataMin, double dataMax, double minSpan,
        double expectedMin, double expectedMax, double expectedStep)
    {
        var axis = SeriesMath.ComputeAxis(dataMin, dataMax, minSpan);
        Assert.Equal(new AxisRange(expectedMin, expectedMax, expectedStep), axis);
        Assert.True(axis.Min < dataMin && axis.Max > dataMax || axis.Min == 0, "data must sit inside the frame");
    }

    [Fact]
    public void ComputeAxis_SwappedOrNonFiniteInput_IsSafe()
    {
        Assert.Equal(SeriesMath.ComputeAxis(62, 78, 10), SeriesMath.ComputeAxis(78, 62, 10));
        var fallback = SeriesMath.ComputeAxis(double.NaN, 70, 10);
        Assert.True(fallback.Max > fallback.Min && fallback.Step > 0);
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeStats_ReturnsMinMaxAndNewest()
    {
        Assert.Equal(new SeriesStats(58, 81, 66), SeriesMath.ComputeStats(Series(70, 58, 81, 66)));
        Assert.Null(SeriesMath.ComputeStats(Array.Empty<HistorySample>()));
    }

    // ── Downsampling ──────────────────────────────────────────────────────────

    [Fact]
    public void Downsample_FewerPointsThanLimit_ReturnsInputUnchanged()
    {
        var samples = Series(1, 2, 3);
        Assert.Same(samples, SeriesMath.Downsample(samples, 10));
    }

    [Fact]
    public void Downsample_KeepsEndpointsExactAndTimeOrdered()
    {
        var samples = Enumerable.Range(0, 900).Select(i => new HistorySample(T0.AddSeconds(i), 60 + Math.Sin(i / 20.0) * 10)).ToArray();

        var reduced = SeriesMath.Downsample(samples, 150);

        Assert.Equal(150, reduced.Count);
        Assert.Equal(samples[0], reduced[0]);
        Assert.Equal(samples[^1], reduced[^1]); // the "now" dot sits on the live value
        Assert.True(reduced.Zip(reduced.Skip(1)).All(p => p.First.TimeUtc < p.Second.TimeUtc));
        Assert.All(reduced, s => Assert.InRange(s.Value, 50, 70));
    }

    [Fact]
    public void Downsample_AveragesEachBucket()
    {
        // 2 + 4 interior samples into 2 interior buckets of 2: (10,20) → 15, (30,40) → 35.
        var reduced = SeriesMath.Downsample(Series(0, 10, 20, 30, 40, 99), 4);
        Assert.Equal(new[] { 0.0, 15, 35, 99 }, reduced.Select(s => s.Value));
        Assert.Equal(T0.AddSeconds(1.5), reduced[1].TimeUtc);
    }

    // ── Projection ────────────────────────────────────────────────────────────

    [Fact]
    public void Project_MapsTimeToXAndValueToInvertedY_Clamped()
    {
        var plot = new Rect(10, 20, 200, 100);
        var axis = new AxisRange(60, 80, 5);
        var samples = new[]
        {
            new HistorySample(T0, 60),                 // window start, axis min → bottom-left
            new HistorySample(T0.AddSeconds(30), 70),  // middle
            new HistorySample(T0.AddSeconds(60), 95),  // window end, above axis → clamped to top
            new HistorySample(T0.AddSeconds(-10), 40), // before the window, below axis → clamped
        };

        var points = SeriesMath.Project(samples, T0, T0.AddSeconds(60), plot, axis);

        Assert.Equal(new Point(10, 120), points[0]);
        Assert.Equal(new Point(110, 70), points[1]);
        Assert.Equal(new Point(210, 20), points[2]);
        Assert.Equal(new Point(10, 120), points[3]);
        Assert.Equal(70, SeriesMath.ValueAtY(70, plot, axis), 9); // inverse mapping
        Assert.Equal(80, SeriesMath.ValueAtY(20, plot, axis), 9);
    }

    // ── Smoothing ─────────────────────────────────────────────────────────────

    [Fact]
    public void MonotoneControlPoints_NeverOvershootTheSamples()
    {
        // Spiky data that makes Catmull-Rom curves bulge past the peaks.
        var points = new[] { 0.0, 80, 10, 90, 85, 85, 20, 21, 100, 0 }
            .Select((y, i) => new Point(i * 12.5, y)).ToArray();

        var controls = SeriesMath.MonotoneControlPoints(points);

        Assert.Equal(points.Length - 1, controls.Length);
        for (int i = 0; i < controls.Length; i++)
        {
            double lo = Math.Min(points[i].Y, points[i + 1].Y), hi = Math.Max(points[i].Y, points[i + 1].Y);
            Assert.InRange(controls[i].C1.Y, lo - 1e-9, hi + 1e-9);
            Assert.InRange(controls[i].C2.Y, lo - 1e-9, hi + 1e-9);
            Assert.InRange(controls[i].C1.X, points[i].X, points[i + 1].X);
            Assert.InRange(controls[i].C2.X, points[i].X, points[i + 1].X);
        }
    }

    [Fact]
    public void MonotoneControlPoints_FlatSegmentsAndExtremaStayFlat()
    {
        var points = new[] { new Point(0, 50), new Point(10, 50), new Point(20, 30), new Point(30, 50) };

        var controls = SeriesMath.MonotoneControlPoints(points);

        // Flat first segment: both handles on the line.
        Assert.Equal(50, controls[0].C1.Y);
        Assert.Equal(50, controls[0].C2.Y);
        // Local minimum at (20,30): horizontal tangent on both sides.
        Assert.Equal(30, controls[1].C2.Y);
        Assert.Equal(30, controls[2].C1.Y);
    }

    [Fact]
    public void MonotoneControlPoints_TwoPoints_IsAStraightLine()
    {
        var controls = SeriesMath.MonotoneControlPoints(new[] { new Point(0, 0), new Point(30, 60) });
        var (c1, c2) = Assert.Single(controls);
        Assert.Equal(new Point(10, 20), c1);
        Assert.Equal(new Point(20, 40), c2);
    }

    [Fact]
    public void MonotoneControlPoints_FewerThanTwoPointsOrDuplicateX_IsSafe()
    {
        Assert.Empty(SeriesMath.MonotoneControlPoints(Array.Empty<Point>()));
        Assert.Empty(SeriesMath.MonotoneControlPoints(new[] { new Point(5, 5) }));

        var controls = SeriesMath.MonotoneControlPoints(new[] { new Point(0, 10), new Point(0, 20), new Point(10, 30) });
        Assert.All(controls, c => Assert.True(double.IsFinite(c.C1.Y) && double.IsFinite(c.C2.Y)));
    }
}
