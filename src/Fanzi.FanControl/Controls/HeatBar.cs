using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// Attached behaviour that turns a <see cref="ProgressBar"/> into a heat bar:
/// <list type="bullet">
/// <item>the fill is a LOW-blue → HOT-red gradient that ends on the heat colour of the current value
/// (0 % blue, 100 % red), so the bar's tip colour always tells the load;</item>
/// <item>an orange tick on the track marks the rolling peak of the last <see cref="PeakWindow"/>;</item>
/// <item>when the value sets a NEW peak, the tip of the fill glows orange.</item>
/// </list>
/// Enabled from the theme for <c>ProgressBar.thin</c>: <c>&lt;Setter Property="controls:HeatBar.IsEnabled" Value="True"/&gt;</c>.
/// Brushes are immutable and cached per whole percent, so updating a bar allocates nothing.
/// </summary>
public static class HeatBar
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ProgressBar, bool>("IsEnabled", typeof(HeatBar));

    /// <summary>Show the orange rolling-peak tick on the track (default true).</summary>
    public static readonly AttachedProperty<bool> ShowPeakProperty =
        AvaloniaProperty.RegisterAttached<ProgressBar, bool>("ShowPeak", typeof(HeatBar), true);

    /// <summary>How long a peak stays marked before it decays to the next-highest recent value.</summary>
    public static readonly TimeSpan PeakWindow = TimeSpan.FromSeconds(60);

    /// <summary>Unfilled track colour (matches the theme's <c>ProgressBar.thin</c> background).</summary>
    public static readonly Color TrackColor = Color.Parse("#142238");

    /// <summary>A new peak must beat the previous one by this fraction of the range (1 %).</summary>
    private const double NewPeakEpsilon = 0.01;

    /// <summary>Earlier samples required before a value can count as a NEW peak.</summary>
    private const int MinSamplesForNewPeak = 3;

    private static readonly IBrush?[] FillCache = new IBrush?[101];
    private static readonly IBrush?[] NewPeakFillCache = new IBrush?[101];
    private static readonly IBrush?[] TrackCache = new IBrush?[101];
    private static readonly IBrush PlainTrack = new ImmutableSolidColorBrush(TrackColor);
    private static readonly ConditionalWeakTable<ProgressBar, PeakState> States = new();

    static HeatBar()
    {
        IsEnabledProperty.Changed.AddClassHandler<ProgressBar>((bar, _) => Update(bar));
        ShowPeakProperty.Changed.AddClassHandler<ProgressBar>((bar, _) => Update(bar));
        RangeBase.ValueProperty.Changed.AddClassHandler<ProgressBar>((bar, _) => Update(bar));
        RangeBase.MaximumProperty.Changed.AddClassHandler<ProgressBar>((bar, _) => Update(bar));
        RangeBase.MinimumProperty.Changed.AddClassHandler<ProgressBar>((bar, _) => Update(bar));
    }

    public static bool GetIsEnabled(ProgressBar bar) => bar.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ProgressBar bar, bool value) => bar.SetValue(IsEnabledProperty, value);
    public static bool GetShowPeak(ProgressBar bar) => bar.GetValue(ShowPeakProperty);
    public static void SetShowPeak(ProgressBar bar, bool value) => bar.SetValue(ShowPeakProperty, value);

    /// <summary>Fraction 0..1 of <paramref name="bar"/>'s value within its range (0 when the range is empty).</summary>
    public static double Fraction(ProgressBar bar)
    {
        double range = bar.Maximum - bar.Minimum;
        if (range <= 0 || double.IsNaN(bar.Value)) return 0;
        return Math.Clamp((bar.Value - bar.Minimum) / range, 0, 1);
    }

    /// <summary>Fraction 0..1 of the bar's reading (base value — the target of a running glide).</summary>
    internal static double ReadingFraction(ProgressBar bar)
    {
        double range = bar.Maximum - bar.Minimum;
        double value = LiveGlide.ReadingOf(bar);
        if (range <= 0 || double.IsNaN(value)) return 0;
        return Math.Clamp((value - bar.Minimum) / range, 0, 1);
    }

    /// <summary>Millisecond clock for the rolling peak (replaceable in tests).</summary>
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Rolling peak fraction currently marked on <paramref name="bar"/> (null when none). Exposed for tests.</summary>
    internal static double? PeakFraction(ProgressBar bar)
        => States.TryGetValue(bar, out var s) && s.Samples.Count > 0 ? s.Peak : null;

    /// <summary>True when the bar's latest value set a new rolling peak. Exposed for tests.</summary>
    internal static bool IsNewPeak(ProgressBar bar) => States.TryGetValue(bar, out var s) && s.IsNewPeak;

    private static void Update(ProgressBar bar)
    {
        if (!GetIsEnabled(bar))
        {
            if (States.TryGetValue(bar, out _))
            {
                States.Remove(bar);
                bar.ClearValue(TemplatedControl.ForegroundProperty);
                bar.ClearValue(TemplatedControl.BackgroundProperty);
            }
            return;
        }

        // The fill follows the shown (possibly mid-glide, see LiveGlide) value every frame; the rolling peak only
        // records real readings (base value), so a glide never adds dozens of in-between "samples" or fakes a peak.
        double fraction = Fraction(bar);
        int percent = (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);

        var state = States.GetOrCreateValue(bar);
        double readingFraction = ReadingFraction(bar);
        bool newReading = !readingFraction.Equals(state.LastReading)
                          || bar.Minimum != state.LastMinimum || bar.Maximum != state.LastMaximum;
        if (newReading)
        {
            state.LastReading = readingFraction;
            state.LastMinimum = bar.Minimum;
            state.LastMaximum = bar.Maximum;
            state.Add(Clock(), readingFraction);
        }

        bar.Foreground = state.IsNewPeak ? NewPeakFill(percent) : Fill(percent);

        if (GetShowPeak(bar) && state.Samples.Count > 1 && state.Peak > 0.005)
            bar.Background = Track((int)Math.Round(state.Peak * 100, MidpointRounding.AwayFromZero));
        else
            bar.Background = PlainTrack;
    }

    /// <summary>
    /// Indicator brush for <paramref name="percent"/>: the heat scale from 0 up to the value, stretched over the
    /// filled part — it looks exactly like revealing a full-width blue → red gradient.
    /// </summary>
    internal static IBrush Fill(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return FillCache[percent] ??= new ImmutableLinearGradientBrush(HeatStopsUpTo(percent / 100.0, tip: null),
            startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
            endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));
    }

    /// <summary>Same as <see cref="Fill"/> but the last ~18 % of the fill flares into the orange peak colour.</summary>
    internal static IBrush NewPeakFill(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return NewPeakFillCache[percent] ??= new ImmutableLinearGradientBrush(
            HeatStopsUpTo(percent / 100.0, tip: TemperatureColorScale.PeakColor),
            startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
            endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));
    }

    /// <summary>Track brush with a 2 %-wide orange tick centred on <paramref name="peakPercent"/>.</summary>
    internal static IBrush Track(int peakPercent)
    {
        peakPercent = Math.Clamp(peakPercent, 1, 100);
        return TrackCache[peakPercent] ??= BuildTrack(peakPercent / 100.0);
    }

    /// <summary>
    /// Gradient stops covering heat fractions 0..<paramref name="end"/>, re-mapped onto 0..1 of the indicator.
    /// With a <paramref name="tip"/> colour the final 18 % of the fill blends into it.
    /// </summary>
    internal static ImmutableGradientStop[] HeatStopsUpTo(double end, Color? tip)
    {
        var stops = new List<ImmutableGradientStop>();
        if (end <= 0.001)
        {
            var c = TemperatureColorScale.ForHeat(0);
            stops.Add(new ImmutableGradientStop(0, c));
            stops.Add(new ImmutableGradientStop(1, c));
            return stops.ToArray();
        }

        stops.Add(new ImmutableGradientStop(0, TemperatureColorScale.ForHeat(0)));
        foreach (var (at, color) in TemperatureColorScale.HeatScaleStops)
        {
            if (at <= 0 || at >= end) continue;
            stops.Add(new ImmutableGradientStop(at / end, color));
        }

        if (tip is { } t)
        {
            const double flare = 0.82;
            // Heat colour where the flare starts, then into orange at the very end.
            if (stops[^1].Offset < flare)
                stops.Add(new ImmutableGradientStop(flare, TemperatureColorScale.ForHeat(end * flare)));
            stops.Add(new ImmutableGradientStop(1, t));
        }
        else
        {
            stops.Add(new ImmutableGradientStop(1, TemperatureColorScale.ForHeat(end)));
        }
        return stops.ToArray();
    }

    private static IBrush BuildTrack(double peak)
    {
        var orange = TemperatureColorScale.PeakColor;
        double half = 0.01;
        double a = Math.Max(0, peak - half), b = Math.Min(1, peak);
        if (b - a < half) a = Math.Max(0, b - half);
        var stops = new List<ImmutableGradientStop>
        {
            new(0, TrackColor),
            new(a, TrackColor),
            new(a, orange),
            new(b, orange),
        };
        if (b < 1)
        {
            stops.Add(new ImmutableGradientStop(b, TrackColor));
            stops.Add(new ImmutableGradientStop(1, TrackColor));
        }
        return new ImmutableLinearGradientBrush(stops.ToArray(),
            startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
            endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));
    }

    /// <summary>Rolling window of (time, fraction) samples for one bar.</summary>
    private sealed class PeakState
    {
        public readonly Queue<(long At, double Fraction)> Samples = new();
        public double LastReading = double.NaN;
        public double LastMinimum = double.NaN;
        public double LastMaximum = double.NaN;
        public double Peak { get; private set; }
        public bool IsNewPeak { get; private set; }

        public void Add(long nowMs, double fraction)
        {
            long cutoff = nowMs - (long)PeakWindow.TotalMilliseconds;
            while (Samples.Count > 0 && Samples.Peek().At < cutoff) Samples.Dequeue();

            double prior = double.NegativeInfinity;
            foreach (var s in Samples) if (s.Fraction > prior) prior = s.Fraction;
            int priorCount = Samples.Count;

            Samples.Enqueue((nowMs, fraction));
            // A NEW peak needs a real history to beat: a few earlier samples, not all zero (the first value
            // arriving after start-up / a binding is not a "peak").
            IsNewPeak = priorCount >= MinSamplesForNewPeak && prior > 0.005 && fraction >= prior + NewPeakEpsilon;
            Peak = Math.Max(prior, fraction);
        }
    }
}
