using System;

namespace Fanzi.FanControl.Models;

/// <summary>
/// Represents an RGB colour value as three byte channels.
/// </summary>
public record struct RgbColor(byte R, byte G, byte B)
{
    // ── Standard colour constants ─────────────────────────────────────────────
    public static readonly RgbColor Black   = new(0,   0,   0);
    public static readonly RgbColor White   = new(255, 255, 255);
    public static readonly RgbColor Red     = new(255, 0,   0);
    public static readonly RgbColor Orange  = new(255, 100, 0);
    public static readonly RgbColor Yellow  = new(255, 200, 0);
    public static readonly RgbColor Green   = new(0,   220, 80);
    public static readonly RgbColor Cyan    = new(0,   200, 255);
    public static readonly RgbColor Blue    = new(0,   100, 255);
    public static readonly RgbColor Purple  = new(120, 0,   255);
    public static readonly RgbColor Magenta = new(255, 0,   200);
    public static readonly RgbColor IceBlue = new(100, 180, 255);
    public static readonly RgbColor Teal    = new(0,   180, 180);

    // ── Colour math ──────────────────────────────────────────────────────────

    /// <summary>Linearly interpolates between two colours.</summary>
    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return new RgbColor(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>
    /// Creates an RGB colour from HSV (hue 0-360, saturation 0-1, value 0-1).
    /// </summary>
    public static RgbColor FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        double c = value * saturation;
        double x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        double m = value - c;

        double r, g, b;
        if      (hue < 60)  { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else                { r = c; g = 0; b = x; }

        return new RgbColor(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }

    /// <summary>Maps a temperature to a colour gradient (cool → hot).</summary>
    public static RgbColor FromTemperature(double tempCelsius)
    {
        // ≤ 40 °C  → ice blue
        // 40-60    → blue → cyan
        // 60-75    → cyan → yellow
        // 75-85    → yellow → orange
        // ≥ 85 °C  → orange → red (danger)
        return tempCelsius switch
        {
            <= 40 => IceBlue,
            <= 60 => Lerp(Blue,   Cyan,   (tempCelsius - 40) / 20.0),
            <= 75 => Lerp(Cyan,   Yellow, (tempCelsius - 60) / 15.0),
            <= 85 => Lerp(Yellow, Orange, (tempCelsius - 75) / 10.0),
            _     => Lerp(Orange, Red,    Math.Clamp((tempCelsius - 85) / 10.0, 0, 1)),
        };
    }

    /// <summary>Maps a load percentage (0-100) to a cool→hot gradient.</summary>
    public static RgbColor FromLoad(double loadPercent)
    {
        double t = Math.Clamp(loadPercent / 100.0, 0, 1);
        return t switch
        {
            <= 0.3 => Lerp(Blue,   Cyan,   t / 0.3),
            <= 0.7 => Lerp(Cyan,   Orange, (t - 0.3) / 0.4),
            _      => Lerp(Orange, Red,    (t - 0.7) / 0.3),
        };
    }

    /// <summary>Scales brightness of the colour by a factor 0-1.</summary>
    public RgbColor Scale(double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new RgbColor(
            (byte)(R * factor),
            (byte)(G * factor),
            (byte)(B * factor));
    }

    /// <summary>Hex string representation, e.g. #FF6400.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Parses a hex string like #RRGGBB or RRGGBB.</summary>
    public static bool TryParseHex(string hex, out RgbColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        hex = hex.TrimStart('#').Trim();
        if (hex.Length != 6) return false;

        try
        {
            color = new RgbColor(
                Convert.ToByte(hex[0..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
