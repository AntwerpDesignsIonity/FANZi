using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.AI;

/// <summary>
/// AI Thermal Coach — analyzes hardware patterns and returns plain-English
/// recommendations: which preset to use, when to clean dust, throttle warnings,
/// suggested overclock headroom, and pump health (for AIO/pump systems).
/// </summary>
public sealed class ThermalCoach
{
    private readonly Queue<double> _tempHistory = new();
    private readonly Queue<double> _loadHistory = new();
    private readonly Queue<double> _rpmHistory = new();
    private const int HistorySize = 600; // ~30 min at 3s polling

    private DateTime _lastDustWarning = DateTime.MinValue;
    private DateTime _lastThrottleWarning = DateTime.MinValue;

    public void Record(double cpuTempC, double cpuLoadPct, double fanRpm)
    {
        Enqueue(_tempHistory, cpuTempC);
        Enqueue(_loadHistory, cpuLoadPct);
        Enqueue(_rpmHistory, fanRpm);
    }

    private static void Enqueue(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistorySize) q.Dequeue();
    }

    /// <summary>Returns a prioritized list of coaching recommendations (max 5).</summary>
    public IReadOnlyList<CoachRecommendation> GetRecommendations()
    {
        var recs = new List<CoachRecommendation>();
        if (_tempHistory.Count < 10) return recs;

        double avgTemp = _tempHistory.Average();
        double maxTemp = _tempHistory.Max();
        double avgLoad = _loadHistory.Count > 0 ? _loadHistory.Average() : 0;
        double avgRpm = _rpmHistory.Count > 0 ? _rpmHistory.Average() : 0;
        double maxRpm = _rpmHistory.Count > 0 ? _rpmHistory.Max() : 0;

        // 1. Thermal throttling warning (>90°C sustained)
        if (maxTemp >= 90 && (DateTime.Now - _lastThrottleWarning).TotalMinutes > 5)
        {
            _lastThrottleWarning = DateTime.Now;
            recs.Add(new CoachRecommendation(
                Severity: CoachSeverity.Critical,
                Title: "Thermal throttling detected",
                Message: $"CPU hit {maxTemp:F0}°C — performance is being throttled. Switch to 'Performance' or 'Overclock' preset, or check for dust buildup.",
                ActionLabel: "Apply Performance preset"));
        }

        // 2. Dust / airflow degradation (high temp at low load)
        if (avgLoad < 20 && avgTemp > 55 && (DateTime.Now - _lastDustWarning).TotalMinutes > 30)
        {
            _lastDustWarning = DateTime.Now;
            recs.Add(new CoachRecommendation(
                Severity: CoachSeverity.Warning,
                Title: "High idle temps — possible dust buildup",
                Message: $"Average temp {avgTemp:F0}°C at {avgLoad:F0}% load suggests reduced airflow. Consider cleaning fan filters and heatsinks.",
                ActionLabel: null));
        }

        // 3. Pump/fan health: RPM too low when temps are high
        if (avgTemp > 70 && avgRpm < 800 && avgRpm > 0)
        {
            recs.Add(new CoachRecommendation(
                Severity: CoachSeverity.Warning,
                Title: "Fan/pump may be underperforming",
                Message: $"Pump RPM ({avgRpm:F0}) is low while temps run hot ({avgTemp:F0}°C). Check pump connections or coolant level.",
                ActionLabel: null));
        }

        // 4. Quiet headroom — temps are very cool, suggest silent preset
        if (maxTemp < 55 && avgLoad < 30)
        {
            recs.Add(new CoachRecommendation(
                Severity: CoachSeverity.Info,
                Title: "Cool & quiet headroom available",
                Message: $"Temps stayed under {maxTemp:F0}°C. Try the 'Silent' preset for whisper-quiet operation.",
                ActionLabel: "Apply Silent preset"));
        }

        // 5. Overclock headroom
        if (maxTemp < 65 && avgLoad > 70)
        {
            recs.Add(new CoachRecommendation(
                Severity: CoachSeverity.Info,
                Title: "Overclock headroom detected",
                Message: $"Sustained {avgLoad:F0}% load with peak {maxTemp:F0}°C — your cooling has thermal headroom for overclocking.",
                ActionLabel: null));
        }

        // 6. Fan curve mismatch (fans always at max)
        if (maxRpm > 0 && _rpmHistory.Where(r => r >= maxRpm * 0.95).Count() > _rpmHistory.Count * 0.8)
        {
            recs.Add(new CoachRecommendation(
                Severity: CoachSeverity.Info,
                Title: "Fans running at near-max constantly",
                Message: "Your fan curve is too aggressive. Try 'Balanced' or 'Silent' to reduce noise.",
                ActionLabel: "Apply Balanced preset"));
        }

        return recs.Take(5).ToList();
    }
}

public enum CoachSeverity { Info, Warning, Critical }

public sealed record CoachRecommendation(
    CoachSeverity Severity,
    string Title,
    string Message,
    string? ActionLabel);
