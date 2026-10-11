using Avalonia;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.Controls;

/// <summary>Y axis of a history chart: [Min, Max] with grid lines every <see cref="Step"/>.</summary>
internal readonly record struct AxisRange(double Min, double Max, double Step);

/// <summary>Lowest, highest and newest value of a series window.</summary>
internal readonly record struct SeriesStats(double Min, double Max, double Current);

/// <summary>Highest sample of a window; <see cref="IsNew"/> when the newest sample just set it.</summary>
internal readonly record struct SeriesPeak(DateTime TimeUtc, double Value, bool IsNew);

/// <summary>Pure geometry for <see cref="ThermalHistoryChart"/>: axis range, downsampling, projection, smoothing.</summary>
internal static class SeriesMath
{
    /// <summary>
    /// Axis covering [<paramref name="dataMin"/>, <paramref name="dataMax"/>] with at least
    /// <paramref name="minimumSpan"/>, snapped outward to the grid step (5 / 10 / 20) with a
    /// little headroom so the line never sits on the frame. Never goes below 0 for non-negative data.
    /// </summary>
    public static AxisRange ComputeAxis(double dataMin, double dataMax, double minimumSpan)
    {
        if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax))
            return new AxisRange(0, Math.Max(10, minimumSpan), 5);
        if (dataMax < dataMin) (dataMin, dataMax) = (dataMax, dataMin);

        double span = Math.Max(dataMax - dataMin, Math.Max(minimumSpan, 1e-6));
        double centre = (dataMin + dataMax) / 2;
        double lo = centre - span / 2, hi = centre + span / 2;
        double step = span <= 20 ? 5 : span <= 50 ? 10 : 20;

        double min = Math.Floor((lo - 1) / step) * step;
        double max = Math.Ceiling((hi + 1) / step) * step;
        if (dataMin >= 0) min = Math.Max(0, min);
        return new AxisRange(min, max, step);
    }

    /// <summary>Min / max / newest of <paramref name="samples"/>; null when empty.</summary>
    public static SeriesStats? ComputeStats(IReadOnlyList<HistorySample> samples)
    {
        if (samples.Count == 0) return null;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var s in samples)
        {
            if (s.Value < min) min = s.Value;
            if (s.Value > max) max = s.Value;
        }
        return new SeriesStats(min, max, samples[^1].Value);
    }

    /// <summary>
    /// The highest sample of <paramref name="samples"/> (latest one wins ties) and whether the newest sample
    /// is a NEW peak: it strictly exceeds every earlier sample in the window by at least
    /// <paramref name="epsilon"/>. Needs two samples and a non-flat series; null otherwise.
    /// </summary>
    public static SeriesPeak? ComputePeak(IReadOnlyList<HistorySample> samples, double epsilon = 0.05)
    {
        if (samples.Count < 2) return null;
        int peak = 0;
        double min = samples[0].Value, priorMax = double.NegativeInfinity;
        for (int i = 0; i < samples.Count; i++)
        {
            double v = samples[i].Value;
            if (v >= samples[peak].Value) peak = i;
            if (v < min) min = v;
            if (i < samples.Count - 1 && v > priorMax) priorMax = v;
        }
        if (samples[peak].Value - min < epsilon) return null; // flat line: no meaningful peak
        bool isNew = samples[^1].Value >= priorMax + epsilon;
        return new SeriesPeak(samples[peak].TimeUtc, samples[peak].Value, isNew);
    }

    /// <summary>
    /// Reduces <paramref name="samples"/> to at most <paramref name="maxPoints"/> by averaging
    /// equal-count buckets. The first and last samples are kept exactly so the line still starts
    /// at the window edge and ends on the live value.
    /// </summary>
    public static IReadOnlyList<HistorySample> Downsample(IReadOnlyList<HistorySample> samples, int maxPoints)
    {
        maxPoints = Math.Max(2, maxPoints);
        int n = samples.Count;
        if (n <= maxPoints) return samples;

        var result = new HistorySample[maxPoints];
        result[0] = samples[0];
        result[^1] = samples[n - 1];

        // Interior samples 1..n-2 are split into maxPoints-2 buckets.
        int interior = n - 2, buckets = maxPoints - 2;
        for (int b = 0; b < buckets; b++)
        {
            int from = 1 + (int)((long)b * interior / buckets);
            int to = 1 + (int)((long)(b + 1) * interior / buckets); // exclusive
            // Average time as integer offsets from the bucket's first sample (exact, no double rounding).
            long origin = samples[from].TimeUtc.Ticks, offsets = 0;
            double sum = 0;
            for (int i = from; i < to; i++)
            {
                offsets += samples[i].TimeUtc.Ticks - origin;
                sum += samples[i].Value;
            }
            int count = to - from;
            result[b + 1] = new HistorySample(new DateTime(origin + offsets / count, DateTimeKind.Utc), sum / count);
        }
        return result;
    }

    /// <summary>
    /// Maps samples into <paramref name="plot"/>: time [<paramref name="start"/>, <paramref name="end"/>]
    /// → left..right, value [axis.Min, axis.Max] → bottom..top (clamped to the plot).
    /// </summary>
    public static Point[] Project(IReadOnlyList<HistorySample> samples, DateTime start, DateTime end, Rect plot, AxisRange axis)
    {
        double duration = Math.Max(1, (end - start).Ticks);
        double range = axis.Max - axis.Min;
        var points = new Point[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            double tx = Math.Clamp((samples[i].TimeUtc - start).Ticks / duration, 0, 1);
            double ty = range > 0 ? Math.Clamp((samples[i].Value - axis.Min) / range, 0, 1) : 0.5;
            points[i] = new Point(plot.Left + tx * plot.Width, plot.Bottom - ty * plot.Height);
        }
        return points;
    }

    // ── v4.0.3 live scrolling ───────────────────────────────────────────────

    /// <summary>
    /// How far behind the newest sample a live chart draws its right edge: one measured sample interval
    /// (gap between the two newest samples, clamped 0.2–5 s) plus 15 % and 60 ms of slack for poll jitter.
    /// The newest reading then slides in from the right instead of the whole curve stepping left once per poll.
    /// </summary>
    public static TimeSpan DisplayLag(IReadOnlyList<HistorySample> samples)
    {
        if (samples.Count < 2) return TimeSpan.Zero;
        double gapMs = (samples[^1].TimeUtc - samples[^2].TimeUtc).TotalMilliseconds;
        gapMs = Math.Clamp(double.IsFinite(gapMs) ? gapMs : 1000, 200, 5000);
        return TimeSpan.FromMilliseconds(gapMs * 1.15 + 60);
    }

    /// <summary>
    /// True when <paramref name="samples"/> are a live feed at <paramref name="nowUtc"/>: the newest sample is no
    /// older than three sample intervals (+0.5 s) and not in the future. Historic / test data keeps the classic
    /// "right edge = newest sample" layout.
    /// </summary>
    public static bool IsLive(IReadOnlyList<HistorySample> samples, DateTime nowUtc)
    {
        if (samples.Count < 2) return false;
        var age = nowUtc - samples[^1].TimeUtc;
        var gap = samples[^1].TimeUtc - samples[^2].TimeUtc;
        if (gap <= TimeSpan.Zero) return false;
        return age >= TimeSpan.FromSeconds(-1) && age <= gap * 3 + TimeSpan.FromMilliseconds(500);
    }

    /// <summary>Value of the series at <paramref name="time"/> by linear interpolation (clamped to the ends).</summary>
    public static double ValueAtTime(IReadOnlyList<HistorySample> samples, DateTime time)
    {
        if (samples.Count == 0) return double.NaN;
        if (time <= samples[0].TimeUtc) return samples[0].Value;
        if (time >= samples[^1].TimeUtc) return samples[^1].Value;
        int hi = 1;
        while (hi < samples.Count - 1 && samples[hi].TimeUtc < time) hi++;
        var a = samples[hi - 1];
        var b = samples[hi];
        double span = (b.TimeUtc - a.TimeUtc).Ticks;
        if (span <= 0) return b.Value;
        return a.Value + (b.Value - a.Value) * ((time - a.TimeUtc).Ticks / span);
    }

    /// <summary>
    /// The part of <paramref name="samples"/> inside [<paramref name="start"/>, <paramref name="end"/>], with the
    /// edges interpolated exactly at <paramref name="start"/> and <paramref name="end"/>, so a chart that scrolls
    /// continuously shows a curve that enters on the left and grows on the right without stepping.
    /// </summary>
    public static IReadOnlyList<HistorySample> Slice(IReadOnlyList<HistorySample> samples, DateTime start, DateTime end)
    {
        if (samples.Count == 0 || end < start) return Array.Empty<HistorySample>();
        if (end > samples[^1].TimeUtc) end = samples[^1].TimeUtc;
        if (start < samples[0].TimeUtc) start = samples[0].TimeUtc;
        if (end <= start) return Array.Empty<HistorySample>();

        var result = new List<HistorySample>(samples.Count + 2) { new(start, ValueAtTime(samples, start)) };
        foreach (var s in samples)
            if (s.TimeUtc > start && s.TimeUtc < end) result.Add(s);
        result.Add(new HistorySample(end, ValueAtTime(samples, end)));
        return result;
    }

    /// <summary>
    /// Downsampling for a scrolling chart: samples are averaged per fixed slot of absolute time
    /// (<paramref name="bucket"/>), so the buckets don't shift as the window slides and the curve doesn't shimmer.
    /// The first and last samples (the interpolated edges) are kept exactly.
    /// </summary>
    public static IReadOnlyList<HistorySample> DownsampleByTime(IReadOnlyList<HistorySample> samples, TimeSpan bucket)
    {
        int n = samples.Count;
        if (n <= 2 || bucket <= TimeSpan.Zero) return samples;

        var result = new List<HistorySample>(n) { samples[0] };
        long size = bucket.Ticks;
        long currentSlot = long.MinValue, origin = 0, offsets = 0;
        double sum = 0;
        int count = 0;
        for (int i = 1; i < n - 1; i++)
        {
            long ticks = samples[i].TimeUtc.Ticks;
            long slot = ticks / size;
            if (slot != currentSlot && count > 0)
            {
                result.Add(new HistorySample(new DateTime(origin + offsets / count, DateTimeKind.Utc), sum / count));
                count = 0; sum = 0; offsets = 0;
            }
            if (count == 0) { currentSlot = slot; origin = ticks; }
            offsets += ticks - origin;
            sum += samples[i].Value;
            count++;
        }
        if (count > 0)
            result.Add(new HistorySample(new DateTime(origin + offsets / count, DateTimeKind.Utc), sum / count));
        result.Add(samples[n - 1]);
        return result;
    }

    /// <summary>Inverse of the Y mapping in <see cref="Project"/>: the value drawn at pixel row <paramref name="y"/>.</summary>
    public static double ValueAtY(double y, Rect plot, AxisRange axis)
        => plot.Height > 0 ? axis.Min + (plot.Bottom - y) / plot.Height * (axis.Max - axis.Min) : axis.Min;

    /// <summary>
    /// Cubic Bézier control points (one pair per segment) for a smooth curve through
    /// <paramref name="points"/> (x non-decreasing). Uses monotone cubic Hermite tangents
    /// (harmonic mean of neighbouring slopes, zero at local extrema), so the curve never
    /// overshoots the samples: every control point stays within its segment's Y range.
    /// </summary>
    public static (Point C1, Point C2)[] MonotoneControlPoints(IReadOnlyList<Point> points)
    {
        int n = points.Count;
        if (n < 2) return Array.Empty<(Point, Point)>();

        var slopes = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = points[i + 1].X - points[i].X;
            slopes[i] = dx > 0 ? (points[i + 1].Y - points[i].Y) / dx : 0;
        }

        var tangents = new double[n];
        tangents[0] = slopes[0];
        tangents[n - 1] = slopes[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            double a = slopes[i - 1], b = slopes[i];
            tangents[i] = a * b <= 0 ? 0 : 2 / (1 / a + 1 / b);
        }

        var controls = new (Point, Point)[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            var p0 = points[i];
            var p1 = points[i + 1];
            double third = (p1.X - p0.X) / 3;
            controls[i] = (new Point(p0.X + third, p0.Y + tangents[i] * third),
                           new Point(p1.X - third, p1.Y - tangents[i + 1] * third));
        }
        return controls;
    }
}
