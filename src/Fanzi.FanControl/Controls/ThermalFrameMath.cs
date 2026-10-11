using Avalonia.Media;
using System;

namespace Fanzi.FanControl.Controls;

/// <summary>Thermal state shown by the window's running frame line and its title-bar chip.</summary>
public enum ThermalState
{
    Unknown,
    Cold,
    Cool,
    Stable,
    Warm,
    Hot,
    Critical,
}

/// <summary>
/// Pure maths behind <see cref="ThermalFrame"/>: the frame's temperature scale (blue = cold, green = stable,
/// red = hot), the running-line colour at any point of the perimeter, and the lap speed.
/// No Avalonia visuals here, so every rule is unit-tested.
/// </summary>
public static class ThermalFrameMath
{
    /// <summary>Colour when no CPU / GPU temperature sensor is available.</summary>
    public static readonly Color NoSensorColor = Color.FromRgb(0x3B, 0x5B, 0x8A);

    /// <summary>
    /// Frame scale: a wide green "stable" plateau (54–72 °C) so a normally loaded system reads green,
    /// blue below it (cold), amber → orange → red above it (hot).
    /// </summary>
    private static readonly (double At, Color Color)[] Stops =
    {
        (32, Color.FromRgb(0x3B, 0x82, 0xF6)), // ≤32 °C  cold blue
        (44, Color.FromRgb(0x38, 0xBD, 0xF8)), //  44 °C  cool sky
        (54, Color.FromRgb(0x22, 0xC5, 0x5E)), //  54 °C  stable green
        (72, Color.FromRgb(0x22, 0xC5, 0x5E)), //  72 °C  stable green (plateau end)
        (80, Color.FromRgb(0xFB, 0xBF, 0x24)), //  80 °C  warm amber
        (86, Color.FromRgb(0xF9, 0x73, 0x16)), //  86 °C  hot orange
        (92, Color.FromRgb(0xEF, 0x44, 0x44)), // ≥92 °C  critical red
    };

    /// <summary>Coldest / hottest stop of the frame scale (°C).</summary>
    public const double ScaleMin = 32, ScaleMax = 92;

    /// <summary>Number of colour bands that flow around the frame.</summary>
    public const int BandCount = 3;

    /// <summary>Half-width (°C) of the flowing bands: the line shows the scale from T − spread to T + spread.</summary>
    public const double BandSpread = 6;

    /// <summary>Band speed relative to the running comets (same direction, slower).</summary>
    public const double BandSpeed = 0.5;

    /// <summary>Length of each comet tail as a fraction of the perimeter.</summary>
    public const double TailLength = 0.14;

    /// <summary>Running comets travelling around the frame (evenly spaced).</summary>
    public const int CometCount = 2;

    /// <summary>Colour on the frame scale (clamped; null / NaN → <see cref="NoSensorColor"/>).</summary>
    public static Color ColorFor(double? celsius)
    {
        if (celsius is not double c || double.IsNaN(c) || double.IsInfinity(c)) return NoSensorColor;
        if (c <= Stops[0].At) return Stops[0].Color;
        if (c >= Stops[^1].At) return Stops[^1].Color;
        for (int i = 1; i < Stops.Length; i++)
        {
            if (c <= Stops[i].At)
            {
                var (a, ca) = Stops[i - 1];
                var (b, cb) = Stops[i];
                return Lerp(ca, cb, (c - a) / (b - a));
            }
        }
        return Stops[^1].Color;
    }

    /// <summary>Thermal state for a temperature (null / NaN → <see cref="ThermalState.Unknown"/>).</summary>
    public static ThermalState StateFor(double? celsius)
    {
        if (celsius is not double c || double.IsNaN(c) || double.IsInfinity(c)) return ThermalState.Unknown;
        return c switch
        {
            < 42 => ThermalState.Cold,
            < 52 => ThermalState.Cool,
            < 75 => ThermalState.Stable,
            < 84 => ThermalState.Warm,
            < 90 => ThermalState.Hot,
            _ => ThermalState.Critical,
        };
    }

    /// <summary>Short upper-case label for a state ("STABLE", "HOT" …).</summary>
    public static string StateLabel(ThermalState state) => state switch
    {
        ThermalState.Cold => "COLD",
        ThermalState.Cool => "COOL",
        ThermalState.Stable => "STABLE",
        ThermalState.Warm => "WARM",
        ThermalState.Hot => "HOT",
        ThermalState.Critical => "CRITICAL",
        _ => "NO SENSOR",
    };

    /// <summary>Laps per second of the running line: calm when cold (~20 s a lap), urgent when hot (~4 s).</summary>
    public static double LapsPerSecond(double? celsius)
    {
        if (celsius is not double c || double.IsNaN(c)) return 0.04;
        double t = Math.Clamp((c - 30) / (ScaleMax - 30), 0, 1);
        return 0.05 + 0.20 * t * t;
    }

    /// <summary>Comet brightness 0..1 at perimeter position <paramref name="u"/> (0..1, clockwise) for lap phase <paramref name="phase"/>.</summary>
    public static double Highlight(double u, double phase)
    {
        double best = 0;
        for (int k = 0; k < CometCount; k++)
        {
            double head = Frac(phase + (double)k / CometCount);
            double behind = Frac(head - u);
            if (behind < TailLength)
            {
                double b = 1 - behind / TailLength;
                best = Math.Max(best, b * b);
            }
        }
        return best;
    }

    /// <summary>Positions (0..1) of the comet heads for lap phase <paramref name="phase"/>.</summary>
    public static double HeadPosition(double phase, int comet) => Frac(phase + (double)comet / CometCount);

    /// <summary>
    /// Running-line colour at perimeter position <paramref name="u"/>: the frame-scale colour of the temperature
    /// (with flowing ±<see cref="BandSpread"/> °C bands), lightened and more opaque inside a comet tail.
    /// <paramref name="time"/> (s) drives the critical-state pulse.
    /// </summary>
    public static Color Sample(double u, double? celsius, double phase, double time)
    {
        double band = Math.Sin(2 * Math.PI * (BandCount * u - phase * BandSpeed));
        Color baseColor;
        if (celsius is double c && !double.IsNaN(c))
            baseColor = ColorFor(c + BandSpread * band);
        else
            baseColor = Lighten(NoSensorColor, 0.12 * (band + 1));

        double h = Highlight(u, phase);
        double alpha = 0.66 + 0.34 * h;
        if (StateFor(celsius) == ThermalState.Critical)
            alpha *= 0.75 + 0.25 * Math.Sin(2 * Math.PI * 1.4 * time); // urgent pulse
        var lit = Lighten(baseColor, 0.55 * h);
        return Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), lit.R, lit.G, lit.B);
    }

    /// <summary>Exponential ease of the shown temperature toward the reading (time constant ~0.8 s).</summary>
    public static double Ease(double shown, double target, double dtSeconds)
    {
        if (double.IsNaN(shown)) return target;
        double k = 1 - Math.Exp(-Math.Max(0, dtSeconds) / 0.8);
        return shown + (target - shown) * k;
    }

    internal static double Frac(double x) => x - Math.Floor(x);

    internal static Color Lighten(Color c, double amount)
        => Lerp(c, Color.FromRgb(0xFF, 0xFF, 0xFF), Math.Clamp(amount, 0, 1));

    private static Color Lerp(Color a, Color b, double t)
    {
        static byte Ch(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero);
        return Color.FromArgb(Ch(a.A, b.A, t), Ch(a.R, b.R, t), Ch(a.G, b.G, t), Ch(a.B, b.B, t));
    }
}
