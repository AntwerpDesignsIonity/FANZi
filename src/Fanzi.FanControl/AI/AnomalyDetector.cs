using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

public sealed class AnomalyDetector
{
    private readonly List<double> _tempSamples = new();
    private readonly List<double> _fanRpmSamples = new();
    private const int WindowSize = 60;
    private const double TempAnomalyThresholdStdDev = 2.5;
    private const double RpmAnomalyThresholdStdDev = 3.0;

    public AnomalyReport? LastReport { get; private set; }

    public void AddSample(double tempC, double? fanRpm)
    {
        _tempSamples.Add(tempC);
        if (_tempSamples.Count > WindowSize)
            _tempSamples.RemoveAt(0);

        if (fanRpm.HasValue)
        {
            _fanRpmSamples.Add(fanRpm.Value);
            if (_fanRpmSamples.Count > WindowSize)
                _fanRpmSamples.RemoveAt(0);
        }

        Evaluate(tempC, fanRpm);
    }

    private void Evaluate(double currentTemp, double? currentRpm)
    {
        var anomalies = new List<string>();
        var severity = AnomalySeverity.Normal;

        if (_tempSamples.Count >= 10)
        {
            double mean = _tempSamples.Average();
            double stdDev = Math.Sqrt(_tempSamples.Sum(x => (x - mean) * (x - mean)) / _tempSamples.Count);

            if (stdDev > 0.1 && Math.Abs(currentTemp - mean) > TempAnomalyThresholdStdDev * stdDev)
            {
                anomalies.Add($"Temperature spike: {currentTemp:F1}C (mean: {mean:F1}C, {TempAnomalyThresholdStdDev}σ threshold)");
                severity = AnomalySeverity.Warning;
            }

            if (currentTemp > 95)
            {
                anomalies.Add($"Critical temperature: {currentTemp:F1}C");
                severity = AnomalySeverity.Critical;
            }
        }

        if (_fanRpmSamples.Count >= 10 && currentRpm.HasValue)
        {
            double mean = _fanRpmSamples.Average();
            double stdDev = Math.Sqrt(_fanRpmSamples.Sum(x => (x - mean) * (x - mean)) / _fanRpmSamples.Count);

            if (currentRpm.Value < 100 && mean > 500)
            {
                anomalies.Add($"Fan stall detected: {currentRpm.Value:F0} RPM (expected ~{mean:F0} RPM)");
                severity = AnomalySeverity.Critical;
            }
            else if (stdDev > 0.1 && Math.Abs(currentRpm.Value - mean) > RpmAnomalyThresholdStdDev * stdDev)
            {
                anomalies.Add($"Unusual fan speed: {currentRpm.Value:F0} RPM (mean: {mean:F0} RPM)");
                if (severity < AnomalySeverity.Warning)
                    severity = AnomalySeverity.Warning;
            }
        }

        LastReport = anomalies.Count > 0
            ? new AnomalyReport(DateTimeOffset.UtcNow, severity, anomalies)
            : null;
    }

    public void Reset()
    {
        _tempSamples.Clear();
        _fanRpmSamples.Clear();
        LastReport = null;
    }
}

public enum AnomalySeverity { Normal, Warning, Critical }

public sealed record AnomalyReport(DateTimeOffset Timestamp, AnomalySeverity Severity, List<string> Anomalies);
