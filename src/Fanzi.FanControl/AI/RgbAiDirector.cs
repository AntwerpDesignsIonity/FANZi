using System;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.AI;

/// <summary>What the AI lighting director wants right now.</summary>
public enum RgbAiMode { Off, Idle, Working, Gaming, Hot, Critical, Night }

/// <summary>One decision: the scene to show and why (shown in the RGB tab).</summary>
public sealed record RgbAiDecision(
    RgbAiMode Mode,
    RgbEffectType Effect,
    RgbColor Primary,
    RgbColor Secondary,
    double Speed,
    double BrightnessCap,
    string Reason);

/// <summary>Inputs the director looks at (all optional — missing sensors are simply ignored).</summary>
public readonly record struct RgbAiInputs(
    double? CpuTempC,
    double? GpuTempC,
    double? CpuLoadPct,
    bool IsGameMode,
    string? GameName,
    TimeSpan LocalTime);

/// <summary>
/// AEDi lighting director (v4) — a small, deterministic rules engine that turns live system state into
/// lighting. Three independent features:
/// <list type="bullet">
/// <item><b>Smart Ambient</b>: idle → calm breathing in the user's colours, working → steady, gaming →
/// performance-reactive, hot → temperature-reactive.</item>
/// <item><b>Thermal Guardian</b>: CPU ≥ <see cref="CriticalCpuC"/> or GPU ≥ <see cref="CriticalGpuC"/> → red/amber
/// alert flash; it releases only after cooling <see cref="HysteresisC"/> below the trigger (no flicker).</item>
/// <item><b>Night dimming</b>: between <see cref="NightStart"/> and <see cref="NightEnd"/> brightness is capped.</item>
/// </list>
/// Pure logic (no I/O), so it is unit-tested; the view model applies the decisions.
/// </summary>
public sealed class RgbAiDirector
{
    public const double HotCpuC = 80, HotGpuC = 76;
    public const double CriticalCpuC = 92, CriticalGpuC = 86;
    public const double HysteresisC = 6;
    public const double BusyLoadPct = 35;
    public const double NightBrightnessCap = 0.35;
    public static readonly TimeSpan NightStart = TimeSpan.FromHours(22);
    public static readonly TimeSpan NightEnd = TimeSpan.FromHours(6.5);

    private bool _guardianLatched;

    public bool SmartAmbient { get; set; }
    public bool ThermalGuardian { get; set; } = true;
    public bool NightDimming { get; set; }

    /// <summary>True while the guardian alert is active (until the system has cooled).</summary>
    public bool GuardianActive => _guardianLatched;

    /// <summary>
    /// Returns the scene to apply, or null when the director has nothing to override (all features off,
    /// or only night dimming outside night hours) — then the user's own effect keeps running.
    /// </summary>
    public RgbAiDecision? Decide(RgbAiInputs input, RgbColor userPrimary, RgbColor userSecondary)
    {
        double cpu = input.CpuTempC ?? 0, gpu = input.GpuTempC ?? 0, load = input.CpuLoadPct ?? 0;
        bool night = IsNight(input.LocalTime);
        double cap = NightDimming && night ? NightBrightnessCap : 1.0;

        // ── Thermal Guardian (highest priority, latched with hysteresis) ──
        if (ThermalGuardian)
        {
            bool over = cpu >= CriticalCpuC || gpu >= CriticalGpuC;
            bool cooled = cpu < CriticalCpuC - HysteresisC && gpu < CriticalGpuC - HysteresisC;
            if (over) _guardianLatched = true;
            else if (_guardianLatched && cooled) _guardianLatched = false;

            if (_guardianLatched)
            {
                string which = cpu >= gpu - (CriticalCpuC - CriticalGpuC) ? $"CPU {cpu:F0}°C" : $"GPU {gpu:F0}°C";
                // Safety outranks night dimming — an alert must be visible.
                return new RgbAiDecision(RgbAiMode.Critical, RgbEffectType.DualColorFlash, RgbColor.Red, RgbColor.Orange,
                    2.2, 1.0, $"Thermal Guardian: {which} is critical — alert lighting until it cools by {HysteresisC:F0}°C.");
            }
        }
        else
        {
            _guardianLatched = false;
        }

        if (!SmartAmbient)
        {
            return night && NightDimming
                ? new RgbAiDecision(RgbAiMode.Night, RgbEffectType.Pulse, userPrimary, userSecondary, 0.4, cap,
                    "Night dimming: brightness capped at 35 % until 06:30.")
                : null;
        }

        // ── Smart Ambient ──
        if (cpu >= HotCpuC || gpu >= HotGpuC)
            return new RgbAiDecision(RgbAiMode.Hot, RgbEffectType.TemperatureReactive, RgbColor.IceBlue, RgbColor.Red, 1.0, cap,
                $"Smart Ambient: running warm (CPU {cpu:F0}°C / GPU {gpu:F0}°C) — colour follows temperature.");

        if (input.IsGameMode)
            return new RgbAiDecision(RgbAiMode.Gaming, RgbEffectType.Performance, userPrimary, RgbColor.Red, 1.2, cap,
                $"Smart Ambient: game detected{(string.IsNullOrWhiteSpace(input.GameName) ? "" : $" ({input.GameName})")} — performance-reactive lighting.");

        if (night)
            return new RgbAiDecision(RgbAiMode.Night, RgbEffectType.Pulse, userPrimary, userSecondary, 0.35, Math.Min(cap, 0.5),
                "Smart Ambient: late evening — slow, low breathing.");

        if (load >= BusyLoadPct)
            return new RgbAiDecision(RgbAiMode.Working, RgbEffectType.CpuLoadReactive, userPrimary, userSecondary, 1.0, cap,
                $"Smart Ambient: busy ({load:F0}% CPU) — pace follows load.");

        return new RgbAiDecision(RgbAiMode.Idle, RgbEffectType.Pulse, userPrimary, userSecondary, 0.6, cap,
            "Smart Ambient: idle — calm breathing in your colours.");
    }

    public static bool IsNight(TimeSpan local)
        => NightStart > NightEnd ? local >= NightStart || local < NightEnd : local >= NightStart && local < NightEnd;

    /// <summary>One-shot suggestion for the "AEDi suggest" button (time of day + current state).</summary>
    public static (RgbThemePreset Preset, string Reason) Suggest(RgbAiInputs input)
    {
        double cpu = input.CpuTempC ?? 0, gpu = input.GpuTempC ?? 0, load = input.CpuLoadPct ?? 0;
        if (cpu >= HotCpuC || gpu >= HotGpuC)
            return (RgbThemePreset.TempReactive, $"Your system is warm (CPU {cpu:F0}°C, GPU {gpu:F0}°C): Temp Reactive shows heat at a glance.");
        if (input.IsGameMode)
            return (RgbThemePreset.Performance, "A game is running: Performance lighting reacts to load and temperature.");
        if (IsNight(input.LocalTime))
            return (RgbThemePreset.Ocean with { Brightness = 0.45, SpeedMultiplier = 0.5 }, "It's late: a dim, slow Ocean breathe is easy on the eyes.");
        if (load >= BusyLoadPct)
            return (RgbThemePreset.Glacier, $"You're working ({load:F0}% CPU): a calm static Glacier glow avoids distraction.");
        var hour = input.LocalTime.TotalHours;
        return hour < 12
            ? (RgbThemePreset.Nature, "Good morning: a fresh green Nature breathe.")
            : hour < 18
                ? (RgbThemePreset.Spectrum, "Afternoon and idle: show off the full Spectrum.")
                : (RgbThemePreset.Sunset, "Evening: warm Sunset colours.");
    }
}
