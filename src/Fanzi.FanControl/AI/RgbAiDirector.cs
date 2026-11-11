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
    TimeSpan LocalTime,
    // 4.5: monotonic seconds. When given, scene changes must hold for a dwell time before they show (no flapping).
    double? NowSeconds = null);

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

    // 4.5: hysteresis + dwell so the scene does not flap around a threshold.
    /// <summary>Hot releases this far below its trigger.</summary>
    public const double HotHysteresisC = 4;
    /// <summary>Working releases when load falls below this.</summary>
    public const double BusyReleasePct = 25;
    /// <summary>A new scene must hold this long before it replaces the current one.</summary>
    public const double DwellSeconds = 8;
    /// <summary>Warm-up is shown sooner…</summary>
    public const double HotDwellSeconds = 3;
    /// <summary>…and a game is not dropped on a short alt-tab.</summary>
    public const double LeaveGameDwellSeconds = 20;
    /// <summary>Night dimming fades in / out over this long instead of jumping.</summary>
    public static readonly TimeSpan NightRamp = TimeSpan.FromMinutes(45);

    private bool _guardianLatched;
    private bool _hotLatched;
    private bool _busyLatched;
    private RgbAiMode? _shown;
    private RgbAiMode? _candidate;
    private double _candidateSince;

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
        double cap = NightDimming ? NightCapAt(input.LocalTime) : 1.0;

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
            _hotLatched = _busyLatched = false; _shown = null; _candidate = null;
            return night && NightDimming
                ? new RgbAiDecision(RgbAiMode.Night, RgbEffectType.Pulse, userPrimary, userSecondary, 0.4, cap,
                    "Night dimming: brightness capped at 35 % until 06:30.")
                : null;
        }

        // ── Smart Ambient ──
        // Raw state with hysteresis: a threshold has to be left by a margin before the scene lets go.
        bool hotNow = cpu >= HotCpuC || gpu >= HotGpuC;
        bool hotClear = cpu < HotCpuC - HotHysteresisC && gpu < HotGpuC - HotHysteresisC;
        if (hotNow) _hotLatched = true; else if (hotClear) _hotLatched = false;
        if (load >= BusyLoadPct) _busyLatched = true; else if (load < BusyReleasePct) _busyLatched = false;

        RgbAiMode raw = _hotLatched ? RgbAiMode.Hot
            : input.IsGameMode ? RgbAiMode.Gaming
            : night ? RgbAiMode.Night
            : _busyLatched ? RgbAiMode.Working
            : RgbAiMode.Idle;

        RgbAiMode mode = Settle(raw, input.NowSeconds);

        return mode switch
        {
            RgbAiMode.Hot => new RgbAiDecision(RgbAiMode.Hot, RgbEffectType.TemperatureReactive, RgbColor.IceBlue, RgbColor.Red, 1.0, cap,
                $"Smart Ambient: running warm (CPU {cpu:F0}°C / GPU {gpu:F0}°C) — colour follows temperature."),
            RgbAiMode.Gaming => new RgbAiDecision(RgbAiMode.Gaming, RgbEffectType.Performance, userPrimary, RgbColor.Red, 1.2, cap,
                $"Smart Ambient: game detected{(string.IsNullOrWhiteSpace(input.GameName) ? "" : $" ({input.GameName})")} — performance-reactive lighting."),
            RgbAiMode.Night => new RgbAiDecision(RgbAiMode.Night, RgbEffectType.Pulse, userPrimary, userSecondary, 0.35, Math.Min(cap, 0.5),
                "Smart Ambient: late evening — slow, low breathing."),
            RgbAiMode.Working => new RgbAiDecision(RgbAiMode.Working, RgbEffectType.CpuLoadReactive, userPrimary, userSecondary, 1.0, cap,
                $"Smart Ambient: busy ({load:F0}% CPU) — pace follows load."),
            _ => new RgbAiDecision(RgbAiMode.Idle, RgbEffectType.Pulse, userPrimary, userSecondary, 0.6, cap,
                "Smart Ambient: idle — calm breathing in your colours."),
        };
    }

    /// <summary>
    /// Dwell: with a clock, a new scene replaces the shown one only after it has been wanted continuously for its dwell
    /// time. Without a clock (<paramref name="now"/> null) the raw state shows at once.
    /// </summary>
    private RgbAiMode Settle(RgbAiMode raw, double? now)
    {
        if (now is not double t || _shown is not RgbAiMode shown)
        {
            _shown = raw; _candidate = null;
            return raw;
        }
        if (raw == shown) { _candidate = null; return shown; }
        if (_candidate != raw) { _candidate = raw; _candidateSince = t; }

        double dwell = raw == RgbAiMode.Hot ? HotDwellSeconds
            : shown == RgbAiMode.Gaming ? LeaveGameDwellSeconds
            : DwellSeconds;
        if (t - _candidateSince < dwell) return shown;

        _shown = raw; _candidate = null;
        return raw;
    }

    /// <summary>
    /// Night brightness cap for a time of day: <see cref="NightBrightnessCap"/> during the night, full outside it, and a
    /// linear fade over <see cref="NightRamp"/> before the night starts and after it ends — no visible jump at 22:00.
    /// </summary>
    public static double NightCapAt(TimeSpan local)
    {
        if (IsNight(local)) return NightBrightnessCap;
        double day = TimeSpan.FromDays(1).TotalMinutes, ramp = NightRamp.TotalMinutes;
        double untilStart = ((NightStart - local).TotalMinutes % day + day) % day;
        if (untilStart <= ramp) return NightBrightnessCap + (1.0 - NightBrightnessCap) * (untilStart / ramp);
        double sinceEnd = ((local - NightEnd).TotalMinutes % day + day) % day;
        if (sinceEnd <= ramp) return NightBrightnessCap + (1.0 - NightBrightnessCap) * (sinceEnd / ramp);
        return 1.0;
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
