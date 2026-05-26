using Fanzi.FanControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

public sealed class SmartFanCurveEngine
{
    private readonly ThermalPredictor _predictor = new();
    private readonly AnomalyDetector _anomalyDetector = new();
    private readonly AcousticIntelligence _acoustic = new();
    private readonly DeltaTController _deltaT = new();
    private double _lastSuggestedPercent = 50;
    private DateTime _lastTick = DateTime.UtcNow;

    public ThermalPredictor Predictor => _predictor;
    public AnomalyDetector AnomalyDetector => _anomalyDetector;
    public AcousticIntelligence Acoustic => _acoustic;
    public DeltaTController DeltaT => _deltaT;
    public double LastSuggestedPercent => _lastSuggestedPercent;
    public bool IsLearning => _predictor.TrendPerSecond is not null;

    /// <summary>True if Acoustic-First mode dampens revving (recommended for daily use).</summary>
    public bool AcousticFirstEnabled { get; set; } = true;

    /// <summary>True if Delta-T airflow control affects case fan suggestions.</summary>
    public bool DeltaTFusionEnabled { get; set; } = true;

    public double ComputeOptimalFanSpeed(
        double currentTempC,
        double cpuLoadPct,
        double? fanRpm,
        List<FanCurvePoint>? customCurve = null,
        double targetTempC = 75,
        double? gpuTempC = null,
        double? caseTempC = null)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Max(0.1, (now - _lastTick).TotalSeconds);
        _lastTick = now;

        _predictor.AddSample(currentTempC, cpuLoadPct);
        _anomalyDetector.AddSample(currentTempC, fanRpm);

        if (_anomalyDetector.LastReport?.Severity == AnomalySeverity.Critical)
        {
            _lastSuggestedPercent = 100;
            return 100;
        }

        double result;
        if (customCurve is { Count: >= 2 })
        {
            result = InterpolateCurve(customCurve, currentTempC);

            if (_predictor.Trend == ThermalTrend.RisingFast)
                result = Math.Min(100, result + 15);
            else if (_predictor.Trend == ThermalTrend.Rising)
                result = Math.Min(100, result + 8);
        }
        else
        {
            result = _predictor.SuggestFanPercent(currentTempC, targetTempC);
        }

        // Delta-T fusion: if GPU/case sensors are providing significant heat-load,
        // bump the suggestion to ensure case airflow keeps up with GPU heat dump.
        if (DeltaTFusionEnabled && (gpuTempC.HasValue || caseTempC.HasValue))
        {
            var deltaT = _deltaT.Compute(caseTempC, currentTempC, gpuTempC);
            // Take the MAX of the curve-based result and ΔT recommendation
            result = Math.Max(result, deltaT.SuggestedFanPercent);
        }

        if (AcousticFirstEnabled)
        {
            // Acoustic smoothing — absorbs spikes, prevents revving, slew-limits ramps
            _lastSuggestedPercent = _acoustic.Smooth(result, currentTempC, dt);
        }
        else
        {
            double smoothed = _lastSuggestedPercent + (result - _lastSuggestedPercent) * 0.3;
            _lastSuggestedPercent = Math.Clamp(smoothed, 20, 100);
        }

        return _lastSuggestedPercent;
    }

    public static List<FanCurvePoint> GenerateDefaultCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 30, FanPercent = 25 },
            new() { TemperatureC = 45, FanPercent = 35 },
            new() { TemperatureC = 60, FanPercent = 50 },
            new() { TemperatureC = 70, FanPercent = 65 },
            new() { TemperatureC = 80, FanPercent = 80 },
            new() { TemperatureC = 90, FanPercent = 95 },
            new() { TemperatureC = 100, FanPercent = 100 },
        };
    }

    public static List<FanCurvePoint> GenerateSilentCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 30, FanPercent = 20 },
            new() { TemperatureC = 50, FanPercent = 25 },
            new() { TemperatureC = 65, FanPercent = 40 },
            new() { TemperatureC = 75, FanPercent = 55 },
            new() { TemperatureC = 85, FanPercent = 75 },
            new() { TemperatureC = 95, FanPercent = 100 },
        };
    }

    public static List<FanCurvePoint> GeneratePerformanceCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 30, FanPercent = 40 },
            new() { TemperatureC = 45, FanPercent = 55 },
            new() { TemperatureC = 55, FanPercent = 70 },
            new() { TemperatureC = 65, FanPercent = 85 },
            new() { TemperatureC = 75, FanPercent = 95 },
            new() { TemperatureC = 80, FanPercent = 100 },
        };
    }

    /// <summary>Aggressive curve for streamers / OBS encoding — keeps CPU cool under sustained load.</summary>
    public static List<FanCurvePoint> GenerateStreamingCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 30, FanPercent = 35 },
            new() { TemperatureC = 45, FanPercent = 50 },
            new() { TemperatureC = 55, FanPercent = 75 },
            new() { TemperatureC = 65, FanPercent = 90 },
            new() { TemperatureC = 75, FanPercent = 100 },
        };
    }

    /// <summary>Gaming curve — quiet at idle, ramps fast when GPU/CPU spike.</summary>
    public static List<FanCurvePoint> GenerateGamingCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 35, FanPercent = 25 },
            new() { TemperatureC = 50, FanPercent = 45 },
            new() { TemperatureC = 60, FanPercent = 70 },
            new() { TemperatureC = 70, FanPercent = 90 },
            new() { TemperatureC = 78, FanPercent = 100 },
        };
    }

    /// <summary>Workstation / rendering curve — sustained moderate cooling for hour-long jobs.</summary>
    public static List<FanCurvePoint> GenerateWorkstationCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 30, FanPercent = 30 },
            new() { TemperatureC = 50, FanPercent = 60 },
            new() { TemperatureC = 65, FanPercent = 80 },
            new() { TemperatureC = 75, FanPercent = 95 },
            new() { TemperatureC = 82, FanPercent = 100 },
        };
    }

    /// <summary>Overclocker curve — max airflow, no compromises.</summary>
    public static List<FanCurvePoint> GenerateOverclockCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 25, FanPercent = 60 },
            new() { TemperatureC = 40, FanPercent = 75 },
            new() { TemperatureC = 55, FanPercent = 90 },
            new() { TemperatureC = 65, FanPercent = 100 },
        };
    }

    /// <summary>Zero-RPM (passive) curve — fans off until threshold, popular for SFF builds.</summary>
    public static List<FanCurvePoint> GenerateZeroRpmCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 0,  FanPercent = 0 },
            new() { TemperatureC = 55, FanPercent = 0 },
            new() { TemperatureC = 60, FanPercent = 40 },
            new() { TemperatureC = 70, FanPercent = 65 },
            new() { TemperatureC = 80, FanPercent = 90 },
            new() { TemperatureC = 85, FanPercent = 100 },
        };
    }

    /// <summary>Linear curve — proportional from 30°C/30% to 80°C/100%.</summary>
    public static List<FanCurvePoint> GenerateLinearCurve()
    {
        return new List<FanCurvePoint>
        {
            new() { TemperatureC = 30, FanPercent = 30 },
            new() { TemperatureC = 80, FanPercent = 100 },
        };
    }

    /// <summary>All built-in presets paired with display name + description.</summary>
    public static IReadOnlyList<(string Name, string Description, List<FanCurvePoint> Curve)> AllPresets => new[]
    {
        ("Silent",        "Whisper-quiet, fans low until 65°C",          GenerateSilentCurve()),
        ("Balanced",      "Default — good cooling, low noise",           GenerateDefaultCurve()),
        ("Performance",   "Aggressive ramp from 45°C — keeps temps low", GeneratePerformanceCurve()),
        ("Gaming",        "Quiet idle, fast ramp on GPU spikes",          GenerateGamingCurve()),
        ("Streaming",     "Sustained encode load — anti-throttle",        GenerateStreamingCurve()),
        ("Workstation",   "Renders/compilers — long sustained loads",    GenerateWorkstationCurve()),
        ("Overclock",     "Max airflow, no compromise",                   GenerateOverclockCurve()),
        ("Zero-RPM",      "Fans off until 55°C (SFF / passive builds)",   GenerateZeroRpmCurve()),
        ("Linear",        "Simple proportional 30→100%",                  GenerateLinearCurve()),
    };

    private static double InterpolateCurve(List<FanCurvePoint> curve, double tempC)
    {
        var sorted = curve.OrderBy(p => p.TemperatureC).ToList();

        if (tempC <= sorted[0].TemperatureC)
            return sorted[0].FanPercent;
        if (tempC >= sorted[^1].TemperatureC)
            return sorted[^1].FanPercent;

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            if (tempC >= sorted[i].TemperatureC && tempC <= sorted[i + 1].TemperatureC)
            {
                double range = sorted[i + 1].TemperatureC - sorted[i].TemperatureC;
                double t = (tempC - sorted[i].TemperatureC) / range;
                return sorted[i].FanPercent + t * (sorted[i + 1].FanPercent - sorted[i].FanPercent);
            }
        }

        return 50;
    }

    public void Reset()
    {
        _predictor.Reset();
        _anomalyDetector.Reset();
        _lastSuggestedPercent = 50;
    }
}
