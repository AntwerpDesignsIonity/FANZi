using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.AI;

/// <summary>
/// Acoustic-first fan controller. Reduces the audible "revving" effect of
/// reactive fan curves by:
///   1. Smoothing setpoints using an exponential moving average (slew limiter).
///   2. Suppressing short temperature spikes that a heatsink/AIO can absorb thermally.
///   3. Asymmetric ramp rates: ramp UP slowly (quiet), ramp DOWN slowly (no audible step).
///   4. Hysteresis band around the current setpoint — small temperature wobbles do nothing.
///
/// Conceptually: humans perceive fan-noise *change* far more than steady-state noise.
/// Holding 50% RPM steady is far less annoying than oscillating between 40% and 65%.
/// </summary>
public sealed class AcousticIntelligence
{
    private double _lastTargetPct;
    private double _emaTempC;
    private bool _initialized;

    // Tuning constants
    private const double EmaAlpha = 0.20;        // 5-sample effective window for temp smoothing
    private const double MaxRampUpPerSec = 4.0;  // max +4% / sec (quiet ramp up)
    private const double MaxRampDownPerSec = 2.0;// max -2% / sec (slower, avoids audible step down)
    private const double HysteresisPct = 3.0;    // ignore deltas smaller than ±3%
    private const double SpikeIgnoreSec = 4.0;   // ignore temp spikes shorter than this
    private DateTime _lastSpikeStart = DateTime.MinValue;
    private bool _inSpike;

    /// <summary>
    /// Given a raw requested fan % (from your curve) and the latest CPU temp,
    /// returns an acoustically-smoothed target % that prevents revving.
    /// </summary>
    /// <param name="rawCurvePercent">What the temperature curve would naively request.</param>
    /// <param name="currentTempC">Latest temperature reading.</param>
    /// <param name="deltaTimeSec">Seconds since last call (used to clamp ramp rate).</param>
    public double Smooth(double rawCurvePercent, double currentTempC, double deltaTimeSec)
    {
        // 1. Smooth the temperature itself (EMA)
        if (!_initialized)
        {
            _emaTempC = currentTempC;
            _lastTargetPct = rawCurvePercent;
            _initialized = true;
            return _lastTargetPct;
        }
        _emaTempC = (EmaAlpha * currentTempC) + ((1 - EmaAlpha) * _emaTempC);

        // 2. Spike absorption — if raw curve jumped >15% but actual smoothed temp is still low,
        //    treat as a transient and don't react for SpikeIgnoreSec.
        double diff = rawCurvePercent - _lastTargetPct;
        if (diff > 15 && _emaTempC < currentTempC - 5)
        {
            if (!_inSpike)
            {
                _inSpike = true;
                _lastSpikeStart = DateTime.Now;
            }
            if ((DateTime.Now - _lastSpikeStart).TotalSeconds < SpikeIgnoreSec)
            {
                // Hold current speed — let the AIO absorb the spike
                return _lastTargetPct;
            }
        }
        else
        {
            _inSpike = false;
        }

        // 3. Hysteresis — ignore tiny changes
        if (Math.Abs(diff) < HysteresisPct)
        {
            return _lastTargetPct;
        }

        // 4. Slew limit — ramp up/down at most N% per second
        double maxStep = diff > 0
            ? MaxRampUpPerSec * deltaTimeSec
            : MaxRampDownPerSec * deltaTimeSec;

        double step = Math.Clamp(diff, -maxStep, maxStep);
        _lastTargetPct = Math.Clamp(_lastTargetPct + step, 0, 100);

        return _lastTargetPct;
    }

    public double SmoothedTempC => _emaTempC;
    public bool InSpikeMode => _inSpike;
    public string Status => _inSpike
        ? $"Acoustic: absorbing spike (held at {_lastTargetPct:F0}%)"
        : $"Acoustic: stable at {_lastTargetPct:F0}%";
}
