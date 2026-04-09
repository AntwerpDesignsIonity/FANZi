using System.Collections.Generic;

namespace Fanzi.FanControl.Models;

/// <summary>
/// A named preset that bundles a lighting effect with colour and speed choices.
/// </summary>
public sealed record RgbThemePreset(
    string Name,
    string Emoji,
    string Description,
    RgbEffectType Effect,
    RgbColor PrimaryColor,
    RgbColor SecondaryColor,
    double SpeedMultiplier,
    double Brightness)
{
    // ── Built-in theme catalogue ──────────────────────────────────────────────

    public static readonly RgbThemePreset Ocean = new(
        "Ocean",       "🌊", "Deep blue breathing pulse",
        RgbEffectType.Pulse,    RgbColor.Blue,   RgbColor.Cyan,   1.0, 1.0);

    public static readonly RgbThemePreset Inferno = new(
        "Inferno",     "🔥", "Scorching rainbow wave",
        RgbEffectType.ColorWave, RgbColor.Red,   RgbColor.Orange, 1.3, 1.0);

    public static readonly RgbThemePreset Glacier = new(
        "Glacier",     "❄",  "Ice-cold static glow",
        RgbEffectType.Static,   RgbColor.IceBlue, RgbColor.White, 1.0, 0.85);

    public static readonly RgbThemePreset Neon = new(
        "Neon",        "⚡", "Cyan/magenta dual flash",
        RgbEffectType.DualColorFlash, RgbColor.Cyan, RgbColor.Magenta, 1.6, 1.0);

    public static readonly RgbThemePreset Nature = new(
        "Nature",      "🌿", "Calm green breathe",
        RgbEffectType.Pulse,    RgbColor.Green,  RgbColor.Teal,   0.8, 0.9);

    public static readonly RgbThemePreset Sunset = new(
        "Sunset",      "🌅", "Orange to purple palette",
        RgbEffectType.ColorWave, RgbColor.Orange, RgbColor.Purple, 0.9, 1.0);

    public static readonly RgbThemePreset Spectrum = new(
        "Spectrum",    "🌈", "Full HSV rainbow cycle",
        RgbEffectType.Rainbow,  RgbColor.Red,   RgbColor.Blue,   1.0, 1.0);

    public static readonly RgbThemePreset BloodMoon = new(
        "Blood Moon",  "🩸", "Deep crimson pulse",
        RgbEffectType.Pulse,    new RgbColor(180, 0, 20), RgbColor.Red, 0.7, 0.95);

    public static readonly RgbThemePreset Arctic = new(
        "Arctic",      "🐧", "Pure static ice white",
        RgbEffectType.Static,   RgbColor.White,  RgbColor.IceBlue, 1.0, 0.7);

    public static readonly RgbThemePreset TempReactive = new(
        "Temp Reactive","🌡", "Colour follows CPU/GPU temperature",
        RgbEffectType.TemperatureReactive, RgbColor.IceBlue, RgbColor.Red, 1.0, 1.0);

    public static readonly RgbThemePreset Performance = new(
        "Performance", "🚀", "Speed + colour follow real load & temps",
        RgbEffectType.Performance, RgbColor.Blue, RgbColor.Red,   1.0, 1.0);

    public static readonly RgbThemePreset Strobe = new(
        "Strobe",      "💡", "Rapid strobe flash",
        RgbEffectType.Strobe,   RgbColor.White,  RgbColor.Black,  2.0, 1.0);

    // ── Catalogue ─────────────────────────────────────────────────────────────

    public static readonly IReadOnlyList<RgbThemePreset> All =
    [
        Ocean, Inferno, Glacier, Neon, Nature, Sunset,
        Spectrum, BloodMoon, Arctic, TempReactive, Performance, Strobe,
    ];
}
