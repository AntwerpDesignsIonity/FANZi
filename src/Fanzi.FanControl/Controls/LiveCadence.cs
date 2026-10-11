using System;
using System.Diagnostics;

namespace Fanzi.FanControl.Controls;

/// <summary>
/// v4.0.3 "real readings" motion: learns how often a live value actually changes (1 s hardware poll,
/// 0.5 s panels, 3 s on battery, …) and returns how long the glide to the next reading should last.
/// <para>The glide fills ~92 % of the measured interval, so each value moves continuously (linear) from
/// one real reading to the next and lands just before the next one arrives — instead of the old fixed
/// 0.4–0.6 s ease-out that snapped most of the way at once and then sat still for the rest of the second
/// (the "jumping" look). Pure apart from the default millisecond clock; replace <see cref="Clock"/> in tests.</para>
/// </summary>
public sealed class LiveCadence
{
    /// <summary>Shortest glide (very fast sources still animate, but never lag behind).</summary>
    public static readonly TimeSpan MinGlide = TimeSpan.FromMilliseconds(150);

    /// <summary>Longest glide (a slow 3–5 s cadence still settles within 1.5 s).</summary>
    public static readonly TimeSpan MaxGlide = TimeSpan.FromMilliseconds(1500);

    /// <summary>Fraction of the measured interval the glide uses.</summary>
    public const double Fill = 0.92;

    /// <summary>Gaps shorter than this are bursts (initial binding, two setters in one pass) — not a cadence.</summary>
    internal const double MinGapMs = 100;

    /// <summary>Gaps longer than this (window hidden, sleep, paused poll) reset the estimate.</summary>
    internal const double MaxGapMs = 5000;

    /// <summary>Weight of the newest gap in the moving average (smooths poll jitter).</summary>
    private const double Alpha = 0.4;

    /// <summary>Monotonic millisecond clock shared by every cadence (replaceable in tests).</summary>
    internal static Func<long> Clock { get; set; } = () => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    private readonly TimeSpan _default;
    private long? _lastMs;
    private double? _averageGapMs;

    /// <param name="defaultGlide">Glide used until a cadence has been measured.</param>
    public LiveCadence(TimeSpan defaultGlide)
    {
        _default = defaultGlide;
    }

    /// <summary>Measured interval between readings in ms (null until two readings have arrived in a steady rhythm).</summary>
    public double? AverageGapMs => _averageGapMs;

    /// <summary>Records that a new reading arrived now and returns the glide to use for it.</summary>
    public TimeSpan Next() => Next(Clock());

    /// <summary>Records a reading at <paramref name="nowMs"/> and returns the glide to use for it.</summary>
    public TimeSpan Next(long nowMs)
    {
        if (_lastMs is long last)
        {
            double gap = nowMs - last;
            if (gap > MaxGapMs || gap < 0)
                _averageGapMs = null; // paused / clock reset: relearn
            else if (gap >= MinGapMs)
            {
                if (_averageGapMs is double avg)
                {
                    // An unchanged reading raises no event, so a 3 s gap on a 1 s poll is three intervals, not a slower poll.
                    if (gap > avg * 1.6) gap /= Math.Round(gap / avg);
                    // A clearly faster rhythm wins at once (the first gap may have spanned repeated readings).
                    _averageGapMs = gap < avg / 1.6 ? gap : avg + (gap - avg) * Alpha;
                }
                else
                {
                    _averageGapMs = gap;
                }
            }
            // gap < MinGapMs: a burst, keep the current estimate
        }
        _lastMs = nowMs;
        return Glide;
    }

    /// <summary>Glide for the current estimate (the default until a cadence is known).</summary>
    public TimeSpan Glide => _averageGapMs is double avg ? GlideFor(avg) : _default;

    /// <summary>~92 % of <paramref name="gapMs"/>, clamped to <see cref="MinGlide"/>..<see cref="MaxGlide"/>.</summary>
    public static TimeSpan GlideFor(double gapMs)
    {
        if (!double.IsFinite(gapMs) || gapMs <= 0) return MinGlide;
        double ms = Math.Clamp(gapMs * Fill, MinGlide.TotalMilliseconds, MaxGlide.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }
}
