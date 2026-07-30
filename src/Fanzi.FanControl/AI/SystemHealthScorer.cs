using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

/// <summary>
/// AI System Health Scorer — computes a real-time 0-100 health score based on
/// thermal, acoustic, load, and stability metrics. Provides actionable insights.
/// </summary>
public sealed class SystemHealthScorer
{
    private readonly Queue<HealthSample> _history = new();
    private const int MaxHistory = 300;

    public double CurrentScore { get; private set; } = 100;
    public string Grade => CurrentScore switch
    {
        >= 90 => "A+",
        >= 80 => "A",
        >= 70 => "B",
        >= 60 => "C",
        >= 50 => "D",
        >= 30 => "F",
        _ => "Critical"
    };

    public string GradeEmoji => CurrentScore switch
    {
        >= 90 => "🟢",
        >= 70 => "🟡",
        >= 50 => "🟠",
        >= 30 => "🔴",
        _ => "⚠️"
    };

    public IReadOnlyList<string> Insights { get; private set; } = Array.Empty<string>();

    public void Update(double cpuTempC, double gpuTempC, double cpuLoadPct, double gpuLoadPct,
                       double? fanRpm, double? fanPercent, double ambientEstimateC = 25)
    {
        var sample = new HealthSample(DateTime.UtcNow, cpuTempC, gpuTempC, cpuLoadPct, gpuLoadPct, fanRpm, fanPercent);
        _history.Enqueue(sample);
        while (_history.Count > MaxHistory) _history.Dequeue();

        ComputeScore(cpuTempC, gpuTempC, cpuLoadPct, gpuLoadPct, fanRpm, fanPercent, ambientEstimateC);
    }

    private void ComputeScore(double cpuTemp, double gpuTemp, double cpuLoad, double gpuLoad,
                              double? fanRpm, double? fanPct, double ambient)
    {
        double score = 100;
        var insights = new List<string>();

        // Thermal penalty (0-40 points)
        double cpuDelta = cpuTemp - ambient;
        if (cpuDelta > 70) { score -= 40; insights.Add($"CPU Δ{cpuDelta:F0}°C above ambient — critical thermal load"); }
        else if (cpuDelta > 55) { score -= 25; insights.Add($"CPU running hot (Δ{cpuDelta:F0}°C) — consider better airflow"); }
        else if (cpuDelta > 40) { score -= 10; }
        else if (cpuDelta > 30) { score -= 5; }

        double gpuDelta = gpuTemp - ambient;
        if (gpuDelta > 75) { score -= 30; insights.Add("GPU thermal limit approaching — throttling likely"); }
        else if (gpuDelta > 60) { score -= 15; insights.Add("GPU temperatures elevated under load"); }
        else if (gpuDelta > 45) { score -= 5; }

        // Efficiency penalty (0-20 points) — high temp at low load = poor airflow/dust
        if (cpuLoad < 20 && cpuTemp > 60)
        {
            score -= 15;
            insights.Add("High idle temps suggest dust buildup or poor case airflow");
        }

        // Acoustic penalty (0-15 points)
        if (fanPct.HasValue && fanPct.Value > 90)
        {
            score -= 15;
            insights.Add("Fans at maximum — acoustic comfort compromised");
        }
        else if (fanPct.HasValue && fanPct.Value > 75)
        {
            score -= 5;
        }

        // Fan health (0-10 points)
        if (fanRpm.HasValue && fanRpm.Value < 200 && fanPct.HasValue && fanPct.Value > 30)
        {
            score -= 10;
            insights.Add("Fan RPM abnormally low for duty cycle — possible obstruction or bearing wear");
        }

        // Stability bonus/penalty (0-10 points)
        if (_history.Count >= 30)
        {
            double tempVariance = _history.Select(h => h.CpuTemp).Variance();
            if (tempVariance > 200) // high variance = unstable thermals
            {
                score -= 10;
                insights.Add("Thermal instability detected — erratic temperature swings");
            }
            else if (tempVariance < 20)
            {
                score += 5;
                insights.Add("Excellent thermal stability");
            }
        }

        CurrentScore = Math.Clamp(score, 0, 100);
        Insights = insights.Take(5).ToList();
    }

    public string Summary => $"{GradeEmoji} System Health: {CurrentScore:F0}/100 ({Grade})";
}

public static class EnumerableExtensions
{
    public static double Variance(this IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0;
        double mean = list.Average();
        return list.Sum(v => (v - mean) * (v - mean)) / list.Count;
    }
}

public readonly record struct HealthSample(
    DateTime Timestamp, double CpuTemp, double GpuTemp,
    double CpuLoad, double GpuLoad, double? FanRpm, double? FanPercent);
