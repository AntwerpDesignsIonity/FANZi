using Fanzi.FanControl.Models;
using System;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Pure, stateless colour computation engine for all RGB lighting effects.
/// Call <see cref="Tick"/> at your desired frame-rate, passing current hardware
/// readings and user settings.  It returns the colour every LED should display.
/// </summary>
public static class RgbEffectsEngine
{
    // Seconds one full rainbow or wave cycle takes at speedMultiplier = 1.0.
    private const double RainbowPeriodSec   = 5.0;
    private const double WavePeriodSec      = 4.0;
    private const double PulsePeriodSec     = 2.5;
    private const double StrobePeriodSec    = 0.18;
    private const double DualFlashPeriodSec = 0.4;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the single "master" colour at the given elapsed time.
    /// For wave effects, pass the device index to get a phase-shifted colour.
    /// </summary>
    /// <param name="elapsedSeconds">Monotonic seconds since engine start.</param>
    /// <param name="effect">Active effect type.</param>
    /// <param name="primary">User primary colour.</param>
    /// <param name="secondary">User secondary colour.</param>
    /// <param name="speedMultiplier">User speed setting (0.2 – 3.0).</param>
    /// <param name="brightness">User brightness setting (0.0 – 1.0).</param>
    /// <param name="cpuTempC">Latest CPU temperature, or null if unavailable.</param>
    /// <param name="gpuTempC">Latest GPU temperature, or null if unavailable.</param>
    /// <param name="cpuLoadPct">CPU total load 0-100, or null if unavailable.</param>
    /// <param name="deviceIndex">Device index for wave phase offset (default 0).</param>
    /// <param name="deviceCount">Total device count for wave spacing (default 1).</param>
    public static RgbColor Tick(
        double        elapsedSeconds,
        RgbEffectType effect,
        RgbColor      primary,
        RgbColor      secondary,
        double        speedMultiplier,
        double        brightness,
        double?       cpuTempC,
        double?       gpuTempC,
        double?       cpuLoadPct,
        int           deviceIndex = 0,
        int           deviceCount = 1)
    {
        double speed = Math.Max(0.01, speedMultiplier);
        double t     = elapsedSeconds * speed;
        brightness   = Math.Clamp(brightness, 0, 1);

        RgbColor color = effect switch
        {
            RgbEffectType.Static          => primary,
            RgbEffectType.Pulse           => ComputePulse(t, primary, secondary),
            RgbEffectType.Rainbow         => ComputeRainbow(t, brightness),
            RgbEffectType.ColorWave       => ComputeWave(t, primary, secondary, deviceIndex, deviceCount),
            RgbEffectType.TemperatureReactive => ComputeTempReactive(cpuTempC, gpuTempC, t),
            RgbEffectType.CpuLoadReactive => ComputeLoadReactive(t, cpuLoadPct),
            RgbEffectType.Performance     => ComputePerformance(t, cpuTempC, gpuTempC, cpuLoadPct),
            RgbEffectType.Strobe          => ComputeStrobe(t, primary),
            RgbEffectType.DualColorFlash  => ComputeDualFlash(t, primary, secondary),
            _                             => primary,
        };

        return color.Scale(brightness);
    }

    // ── Effect implementations ────────────────────────────────────────────────

    private static RgbColor ComputePulse(double t, RgbColor primary, RgbColor secondary)
    {
        // Sine wave: 0 → 1 → 0 over one period
        double phase = (Math.Sin(t * Math.Tau / PulsePeriodSec) + 1.0) / 2.0;
        return RgbColor.Lerp(secondary.Scale(0.05), primary, phase);
    }

    private static RgbColor ComputeRainbow(double t, double brightness)
    {
        double hue = (t / RainbowPeriodSec * 360.0) % 360.0;
        return RgbColor.FromHsv(hue, 1.0, brightness);
    }

    private static RgbColor ComputeWave(
        double   t,
        RgbColor primary,
        RgbColor secondary,
        int      deviceIndex,
        int      deviceCount)
    {
        // Each device is offset through the wave by a fraction of the period.
        double phaseOffset = deviceCount > 1
            ? (double)deviceIndex / deviceCount * Math.Tau
            : 0.0;
        double phase = (Math.Sin(t * Math.Tau / WavePeriodSec + phaseOffset) + 1.0) / 2.0;
        return RgbColor.Lerp(secondary, primary, phase);
    }

    private static RgbColor ComputeTempReactive(double? cpuTempC, double? gpuTempC, double t)
    {
        double? maxTemp = cpuTempC.HasValue && gpuTempC.HasValue
            ? Math.Max(cpuTempC.Value, gpuTempC.Value)
            : cpuTempC ?? gpuTempC;

        if (maxTemp is null)
        {
            // No data yet — gentle blue pulse as fallback.
            double phase = (Math.Sin(t * Math.Tau / PulsePeriodSec) + 1.0) / 2.0 * 0.6 + 0.4;
            return RgbColor.IceBlue.Scale(phase);
        }

        return RgbColor.FromTemperature(maxTemp.Value);
    }

    private static RgbColor ComputeLoadReactive(double t, double? cpuLoadPct)
    {
        double load = cpuLoadPct ?? 0;

        // Speed of pulse scales with load (low load → slow, high load → fast).
        double speed   = 0.5 + load / 100.0 * 2.5;
        double phase   = (Math.Sin(t * Math.Tau * speed / PulsePeriodSec) + 1.0) / 2.0;
        RgbColor color = RgbColor.FromLoad(load);
        return RgbColor.Lerp(color.Scale(0.1), color, phase);
    }

    private static RgbColor ComputePerformance(
        double  t,
        double? cpuTempC,
        double? gpuTempC,
        double? cpuLoadPct)
    {
        double load = cpuLoadPct ?? 0;

        // Colour follows the hottest sensor.
        double? maxTemp = cpuTempC.HasValue && gpuTempC.HasValue
            ? Math.Max(cpuTempC.Value, gpuTempC.Value)
            : cpuTempC ?? gpuTempC;

        RgbColor targetColor = maxTemp is not null
            ? RgbColor.FromTemperature(maxTemp.Value)
            : RgbColor.FromLoad(load);

        // Pulse speed and depth scale with load.
        double speed  = 0.4 + load / 100.0 * 2.0;
        double minBri = 0.2 + load / 100.0 * 0.4;
        double phase  = (Math.Sin(t * Math.Tau * speed / PulsePeriodSec) + 1.0) / 2.0;
        double bri    = minBri + phase * (1.0 - minBri);

        return targetColor.Scale(bri);
    }

    private static RgbColor ComputeStrobe(double t, RgbColor primary)
    {
        // On for 20 % of the period, off for 80 %.
        double cycle = (t % StrobePeriodSec) / StrobePeriodSec;
        return cycle < 0.2 ? primary : RgbColor.Black;
    }

    private static RgbColor ComputeDualFlash(double t, RgbColor primary, RgbColor secondary)
    {
        double cycle = (t % DualFlashPeriodSec) / DualFlashPeriodSec;
        return cycle < 0.5 ? primary : secondary;
    }
}
