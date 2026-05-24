using Fanzi.FanControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

public sealed class SmartFanCurveEngine
{
    private readonly ThermalPredictor _predictor = new();
    private readonly AnomalyDetector _anomalyDetector = new();
    private double _lastSuggestedPercent = 50;

    public ThermalPredictor Predictor => _predictor;
    public AnomalyDetector AnomalyDetector => _anomalyDetector;
    public double LastSuggestedPercent => _lastSuggestedPercent;
    public bool IsLearning => _predictor.TrendPerSecond is not null;

    public double ComputeOptimalFanSpeed(
        double currentTempC,
        double cpuLoadPct,
        double? fanRpm,
        List<FanCurvePoint>? customCurve = null,
        double targetTempC = 75)
    {
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

        double smoothed = _lastSuggestedPercent + (result - _lastSuggestedPercent) * 0.3;
        _lastSuggestedPercent = Math.Clamp(smoothed, 20, 100);
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
