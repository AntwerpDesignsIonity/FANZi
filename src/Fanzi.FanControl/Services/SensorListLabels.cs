using System;
using System.Collections.Generic;
using System.Linq;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Row labels for the Sensors tab's plain CPU / GPU lists. LibreHardwareMonitor reuses one name for
/// several sensor kinds ("GPU Core" is a temperature, a load and a clock), so a name that appears more
/// than once gets its kind appended: "GPU Core · Temp", "GPU Core · Load", "GPU Core · Clock".
/// </summary>
public static class SensorListLabels
{
    public const string Separator = " · ";

    /// <summary>One label per reading, in order.</summary>
    public static IReadOnlyList<string> Build(IReadOnlyList<(string Name, string Kind)> readings)
    {
        var counts = readings
            .GroupBy(r => r.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return readings
            .Select(r =>
            {
                string name = r.Name?.Trim() ?? string.Empty;
                return counts[name] > 1 && !string.IsNullOrWhiteSpace(r.Kind) ? name + Separator + ShortKind(r.Kind) : name;
            })
            .ToList();
    }

    /// <summary>"Temperature" → "Temp"; other kinds (Load, Clock, Power, Voltage) are already short.</summary>
    internal static string ShortKind(string kind)
        => string.Equals(kind, "Temperature", StringComparison.OrdinalIgnoreCase) ? "Temp" : kind.Trim();
}
