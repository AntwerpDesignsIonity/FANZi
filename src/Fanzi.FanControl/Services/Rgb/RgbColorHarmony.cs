using System;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>Colour-theory helpers for the RGB studio (v4): HSV conversion and harmony partners.</summary>
public static class RgbColorHarmony
{
    public enum Rule { Complementary, Analogous, Triadic, SplitComplementary, Monochrome }

    /// <summary>Hue 0–360, saturation 0–1, value 0–1.</summary>
    public static (double H, double S, double V) ToHsv(RgbColor c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;

        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        double s = max <= 0 ? 0 : d / max;
        return (h, s, max);
    }

    /// <summary>The partner colour for <paramref name="primary"/> under <paramref name="rule"/>.</summary>
    public static RgbColor Partner(RgbColor primary, Rule rule)
    {
        var (h, s, v) = ToHsv(primary);
        // Greys have no hue: give them a saturated partner so the result is visible on LEDs.
        if (s < 0.08) { s = 0.9; v = Math.Max(v, 0.8); }
        v = Math.Max(v, 0.55);

        return rule switch
        {
            Rule.Complementary => RgbColor.FromHsv(h + 180, s, v),
            Rule.Analogous => RgbColor.FromHsv(h + 30, s, v),
            Rule.Triadic => RgbColor.FromHsv(h + 120, s, v),
            Rule.SplitComplementary => RgbColor.FromHsv(h + 150, s, v),
            Rule.Monochrome => RgbColor.FromHsv(h, Math.Clamp(s * 0.55, 0, 1), Math.Clamp(v * 0.6 + 0.1, 0, 1)),
            _ => primary,
        };
    }

    /// <summary>Human label for the UI.</summary>
    public static string Label(Rule rule) => rule switch
    {
        Rule.SplitComplementary => "Split",
        _ => rule.ToString(),
    };
}
