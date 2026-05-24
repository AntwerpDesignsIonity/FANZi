using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

public sealed class ThermalPredictor
{
    private readonly List<ThermalSample> _history = new();
    private const int MaxSamples = 120;
    private const int PredictionWindowSamples = 10;

    public double? PredictedTempIn30s { get; private set; }
    public double? TrendPerSecond { get; private set; }
    public ThermalTrend Trend { get; private set; } = ThermalTrend.Stable;

    public void AddSample(double temperatureC, double cpuLoadPct)
    {
        _history.Add(new ThermalSample(DateTimeOffset.UtcNow, temperatureC, cpuLoadPct));
        if (_history.Count > MaxSamples)
            _history.RemoveAt(0);

        ComputePrediction();
    }

    public double SuggestFanPercent(double currentTemp, double targetTemp = 75)
    {
        double error = currentTemp - targetTemp;
        double trend = TrendPerSecond ?? 0;

        double proportional = error * 2.5;
        double derivative = trend * 15.0;

        double suggested = 40 + proportional + derivative;
        return Math.Clamp(suggested, 25, 100);
    }

    private void ComputePrediction()
    {
        if (_history.Count < PredictionWindowSamples)
        {
            PredictedTempIn30s = null;
            TrendPerSecond = null;
            Trend = ThermalTrend.Stable;
            return;
        }

        var recent = _history.TakeLast(PredictionWindowSamples).ToList();
        double firstTime = recent[0].Timestamp.ToUnixTimeMilliseconds() / 1000.0;
        double lastTime = recent[^1].Timestamp.ToUnixTimeMilliseconds() / 1000.0;
        double timeDelta = lastTime - firstTime;

        if (timeDelta < 1.0)
        {
            Trend = ThermalTrend.Stable;
            return;
        }

        double tempDelta = recent[^1].TemperatureC - recent[0].TemperatureC;
        TrendPerSecond = tempDelta / timeDelta;
        PredictedTempIn30s = recent[^1].TemperatureC + (TrendPerSecond.Value * 30.0);

        Trend = TrendPerSecond.Value switch
        {
            > 0.3 => ThermalTrend.RisingFast,
            > 0.05 => ThermalTrend.Rising,
            < -0.3 => ThermalTrend.CoolingFast,
            < -0.05 => ThermalTrend.Cooling,
            _ => ThermalTrend.Stable
        };
    }

    public void Reset()
    {
        _history.Clear();
        PredictedTempIn30s = null;
        TrendPerSecond = null;
        Trend = ThermalTrend.Stable;
    }
}

public enum ThermalTrend
{
    Stable,
    Rising,
    RisingFast,
    Cooling,
    CoolingFast
}

public readonly record struct ThermalSample(DateTimeOffset Timestamp, double TemperatureC, double CpuLoadPct);
