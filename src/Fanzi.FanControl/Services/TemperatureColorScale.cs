using Avalonia.Media;
using System;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Pure temperature → colour scale shared by the dashboard, gauges, overlay and LEDs.
/// Cool blue → cyan → green → amber → orange → red, linearly interpolated per sRGB channel.
/// </summary>
public static class TemperatureColorScale
{
    /// <summary>Lowest temperature stop (°C); anything colder clamps to the first colour.</summary>
    public const double MinStop = 30;

    /// <summary>Highest temperature stop (°C); anything hotter clamps to the last colour.</summary>
    public const double MaxStop = 95;

    private static readonly (double At, Color Color)[] Stops =
    {
        (30, Color.FromRgb(0x38, 0xBD, 0xF8)), // ≤30 °C  cool blue
        (45, Color.FromRgb(0x22, 0xD3, 0xEE)), //  45 °C  cyan
        (60, Color.FromRgb(0x22, 0xC5, 0x5E)), //  60 °C  green
        (75, Color.FromRgb(0xFB, 0xBF, 0x24)), //  75 °C  amber
        (85, Color.FromRgb(0xF9, 0x73, 0x16)), //  85 °C  orange
        (95, Color.FromRgb(0xEF, 0x44, 0x44)), // ≥95 °C  red
    };

    /// <summary>Colour for a temperature in °C (clamped to the 30..95 °C scale; NaN → coolest).</summary>
    public static Color ForTemperature(double celsius)
    {
        if (double.IsNaN(celsius) || celsius <= Stops[0].At) return Stops[0].Color;
        if (celsius >= Stops[^1].At) return Stops[^1].Color;

        for (int i = 1; i < Stops.Length; i++)
        {
            if (celsius <= Stops[i].At)
            {
                var (a, ca) = Stops[i - 1];
                var (b, cb) = Stops[i];
                return Lerp(ca, cb, (celsius - a) / (b - a));
            }
        }

        return Stops[^1].Color;
    }

    /// <summary>
    /// Colour for a 0..100 health score — the inverse of the temperature scale:
    /// 100 → cool blue (healthy), 0 → red (critical).
    /// </summary>
    public static Color ForHealth(double score)
    {
        if (double.IsNaN(score)) score = 0;
        double s = Math.Clamp(score, 0, 100) / 100.0;
        return ForTemperature(MaxStop - s * (MaxStop - MinStop));
    }

    /// <summary>
    /// Heat scale for graphs and load bars: LOW 0 % cool blue → royal blue → violet → crimson → HOT 100 % red.
    /// It deliberately contains no orange, so <see cref="PeakColor"/> always stands out as the peak marker.
    /// </summary>
    private static readonly (double At, Color Color)[] HeatStops =
    {
        (0.00, Color.FromRgb(0x38, 0xBD, 0xF8)), //   0 %  cool blue (LOW)
        (0.30, Color.FromRgb(0x3B, 0x82, 0xF6)), //  30 %  blue
        (0.55, Color.FromRgb(0x8B, 0x5C, 0xF6)), //  55 %  violet
        (0.80, Color.FromRgb(0xDB, 0x27, 0x77)), //  80 %  crimson
        (1.00, Color.FromRgb(0xEF, 0x44, 0x44)), // 100 %  red (HOT)
    };

    /// <summary>Peak / new-peak marker colour (orange) used by charts and the overlay.</summary>
    public static readonly Color PeakColor = Color.FromRgb(0xF9, 0x73, 0x16);

    /// <summary>The heat-scale stops (fraction 0..1, colour) — for building bar / legend gradients.</summary>
    public static ReadOnlySpan<(double At, Color Color)> HeatScaleStops => HeatStops;

    /// <summary>Colour on the heat scale for a 0..1 fraction (clamped; NaN → LOW blue).</summary>
    public static Color ForHeat(double fraction)
    {
        if (double.IsNaN(fraction) || fraction <= HeatStops[0].At) return HeatStops[0].Color;
        if (fraction >= HeatStops[^1].At) return HeatStops[^1].Color;

        for (int i = 1; i < HeatStops.Length; i++)
        {
            if (fraction <= HeatStops[i].At)
            {
                var (a, ca) = HeatStops[i - 1];
                var (b, cb) = HeatStops[i];
                return Lerp(ca, cb, (fraction - a) / (b - a));
            }
        }

        return HeatStops[^1].Color;
    }

    /// <summary>Heat-scale colour for <paramref name="value"/> within [<paramref name="low"/>, <paramref name="high"/>].</summary>
    public static Color ForHeat(double value, double low, double high)
        => high > low ? ForHeat((value - low) / (high - low)) : ForHeat(double.IsNaN(value) ? 0 : 0.5);

    /// <summary>Colour for a 0..100 % load on the heat scale: 0 % → LOW blue, 100 % → HOT red.</summary>
    public static Color ForLoad(double percent)
    {
        if (double.IsNaN(percent)) percent = 0;
        return ForHeat(Math.Clamp(percent, 0, 100) / 100.0);
    }

    /// <summary>Blends <paramref name="color"/> toward white by <paramref name="amount"/> (0..1).</summary>
    public static Color Lighten(Color color, double amount)
        => Lerp(color, Color.FromRgb(0xFF, 0xFF, 0xFF), Math.Clamp(amount, 0, 1));

    /// <summary>Returns <paramref name="color"/> with the given alpha (0..1).</summary>
    public static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255, MidpointRounding.AwayFromZero), color.R, color.G, color.B);

    private static Color Lerp(Color a, Color b, double t)
    {
        static byte Channel(byte x, byte y, double t)
            => (byte)Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero);

        return Color.FromArgb(Channel(a.A, b.A, t), Channel(a.R, b.R, t), Channel(a.G, b.G, t), Channel(a.B, b.B, t));
    }
}
