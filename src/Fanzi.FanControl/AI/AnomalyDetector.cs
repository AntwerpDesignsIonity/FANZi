using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

/// <summary>
/// Watches CPU temperature and cooler speed for things that are actually wrong.
///
/// 4.6: rewritten after field logs showed ~350 "anomaly" alerts a day on a healthy PC. The old rule compared the
/// newest sample with 2.5σ of the last minute, so on a steady system (σ ≈ 0.2 °C) every 1 °C step counted as a
/// "temperature spike", and every BIOS fan-curve step counted as "unusual fan speed". The rules now need a
/// deviation that matters in absolute terms, at a temperature that matters, for several polls in a row:
///   • Temperature spike — ≥ 12 °C (and ≥ 2.5σ) above the recent baseline while at / above 70 °C, 3 polls running.
///   • Critical temperature — above 95 °C (immediate, as before: the engine answers with 100 % duty).
///   • Fan stall — under 100 RPM after running above 500 RPM, 3 polls running, and not while the fan is
///     commanded to stop (Zero-RPM curve / 0 % duty — that used to trip the stall alarm and pin the fan at 100 %).
///   • Fan slowed while hot — speed fell to less than half of its baseline while at / above 70 °C, 5 polls running.
/// A fan changing speed with load is its job and is no longer reported.
/// </summary>
public sealed class AnomalyDetector
{
    private readonly List<double> _tempSamples = new();
    private readonly List<double> _fanRpmSamples = new();
    private const int WindowSize = 60;
    private const int MinBaselineSamples = 10;

    internal const double TempAnomalyThresholdStdDev = 2.5;
    /// <summary>A "spike" must be at least this far above the baseline mean (°C).</summary>
    internal const double MinSpikeDeltaC = 12.0;
    /// <summary>Below this temperature a jump is ordinary load, not an anomaly (°C).</summary>
    internal const double SpikeFloorC = 70.0;
    internal const double CriticalTempC = 95.0;
    internal const int SpikePersistSamples = 3;
    internal const int StallPersistSamples = 3;
    internal const int SlowFanPersistSamples = 5;
    /// <summary>At or below this commanded duty a stopped fan is intended (Zero-RPM).</summary>
    internal const double IntentionalStopDutyPercent = 5.0;

    private int _spikeRun;
    private int _stallRun;
    private int _slowRun;

    public AnomalyReport? LastReport { get; private set; }

    /// <param name="tempC">Hottest CPU temperature of this poll.</param>
    /// <param name="fanRpm">CPU cooler speed, when the board reports one.</param>
    /// <param name="commandedDutyPercent">Duty the fan is currently told to run at (null = unknown / BIOS).</param>
    public void AddSample(double tempC, double? fanRpm, double? commandedDutyPercent = null)
    {
        if (!double.IsFinite(tempC)) return;
        if (fanRpm is double r && !double.IsFinite(r)) fanRpm = null;

        // Evaluate against the baseline *before* this sample joins it, so a real jump is not diluted by itself.
        Evaluate(tempC, fanRpm, commandedDutyPercent);

        _tempSamples.Add(tempC);
        if (_tempSamples.Count > WindowSize)
            _tempSamples.RemoveAt(0);

        if (fanRpm.HasValue)
        {
            _fanRpmSamples.Add(fanRpm.Value);
            if (_fanRpmSamples.Count > WindowSize)
                _fanRpmSamples.RemoveAt(0);
        }
    }

    private void Evaluate(double currentTemp, double? currentRpm, double? commandedDutyPercent)
    {
        var anomalies = new List<string>();
        var severity = AnomalySeverity.Normal;

        // ── Temperature ──────────────────────────────────────────────────────
        bool spikeNow = false;
        double tempMean = 0;
        if (_tempSamples.Count >= MinBaselineSamples)
        {
            tempMean = _tempSamples.Average();
            double mean = tempMean;
            double stdDev = Math.Sqrt(_tempSamples.Sum(x => (x - mean) * (x - mean)) / _tempSamples.Count);
            double delta = currentTemp - mean;
            spikeNow = currentTemp >= SpikeFloorC
                       && delta >= MinSpikeDeltaC
                       && delta >= TempAnomalyThresholdStdDev * stdDev;
        }
        _spikeRun = spikeNow ? _spikeRun + 1 : 0;
        if (_spikeRun >= SpikePersistSamples)
        {
            anomalies.Add($"Temperature spike: {currentTemp:F0}C, {currentTemp - tempMean:F0}C above the last minute's {tempMean:F0}C");
            severity = AnomalySeverity.Warning;
        }

        if (currentTemp > CriticalTempC)
        {
            anomalies.Add($"Critical temperature: {currentTemp:F1}C");
            severity = AnomalySeverity.Critical;
        }

        // ── Cooler speed ─────────────────────────────────────────────────────
        bool stallNow = false, slowNow = false;
        double rpmMean = 0;
        if (_fanRpmSamples.Count >= MinBaselineSamples && currentRpm.HasValue)
        {
            rpmMean = _fanRpmSamples.Average();
            bool intendedStop = commandedDutyPercent is double duty && duty <= IntentionalStopDutyPercent;
            stallNow = !intendedStop && currentRpm.Value < 100 && rpmMean > 500;
            slowNow = !stallNow && !intendedStop && currentTemp >= SpikeFloorC
                      && rpmMean > 500 && currentRpm.Value < rpmMean * 0.5;
        }
        _stallRun = stallNow ? _stallRun + 1 : 0;
        _slowRun = slowNow ? _slowRun + 1 : 0;

        if (_stallRun >= StallPersistSamples)
        {
            anomalies.Add($"Fan stall detected: {currentRpm!.Value:F0} RPM (expected ~{rpmMean:F0} RPM)");
            severity = AnomalySeverity.Critical;
        }
        else if (_slowRun >= SlowFanPersistSamples)
        {
            anomalies.Add($"Fan slowed while hot: {currentRpm!.Value:F0} RPM at {currentTemp:F0}C (was ~{rpmMean:F0} RPM)");
            if (severity < AnomalySeverity.Warning)
                severity = AnomalySeverity.Warning;
        }

        LastReport = anomalies.Count > 0
            ? new AnomalyReport(DateTimeOffset.UtcNow, severity, anomalies)
            : null;
    }

    public void Reset()
    {
        _tempSamples.Clear();
        _fanRpmSamples.Clear();
        _spikeRun = _stallRun = _slowRun = 0;
        LastReport = null;
    }
}

public enum AnomalySeverity { Normal, Warning, Critical }

public sealed record AnomalyReport(DateTimeOffset Timestamp, AnomalySeverity Severity, List<string> Anomalies);
