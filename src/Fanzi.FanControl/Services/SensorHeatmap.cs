using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fanzi.FanControl.Services;

/// <summary>Which colour scale a heatmap tile uses: °C (<see cref="TemperatureColorScale.ForTemperature"/>) or % (<see cref="TemperatureColorScale.ForLoad"/>).</summary>
public enum HeatmapScale { Temperature, Load }

/// <summary>One heatmap tile: a core or sensor with its primary value and an optional secondary line.</summary>
/// <param name="Key">Stable identity across polls (lets tiles update in place so their brushes animate).</param>
/// <param name="Detail">Secondary line, e.g. "45% load"; empty when there is none.</param>
public sealed record HeatmapTile(string Key, string Label, double Value, HeatmapScale Scale, string ValueText, string Detail)
{
    /// <summary>Fill of the tile's bar, 0..1: 20..100 °C for temperatures, 0..100 % for loads.</summary>
    public double Fill => Scale == HeatmapScale.Temperature
        ? Math.Clamp((Value - SensorHeatmap.FillMinCelsius) / (SensorHeatmap.FillMaxCelsius - SensorHeatmap.FillMinCelsius), 0, 1)
        : Math.Clamp(Value / 100.0, 0, 1);
}

/// <summary>
/// Pure builder for the Sensors-tab heatmap. Turns the formatted sensor readings
/// (name, "72.4 C" / "45%", kind) into per-core CPU tiles, CPU summary tiles and GPU tiles.
/// </summary>
public static class SensorHeatmap
{
    internal const double FillMinCelsius = 20;
    internal const double FillMaxCelsius = 100;
    internal const int MaxSummaryTiles = 6;
    internal const int MaxGpuTiles = 8;

    private const string TemperatureKind = "Temperature";
    private const string LoadKind = "Load";

    // LibreHardwareMonitor core names: "CPU Core #3", "Core #3", "P-Core #2", "E-Core #1",
    // loads per thread: "CPU Core #3 Thread #2". Anything else ("Core Max", "… Distance to TjMax") is not a core.
    private static readonly Regex CoreName = new(
        @"^(?:CPU\s+)?(?:(?<type>[PE])-)?Core\s*#(?<n>\d+)(?:\s+Thread\s*#\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LeadingNumber = new(@"-?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Reads the number from a formatted value ("72.4 C", "72,4 °C", "45%"). Both decimal separators are
    /// accepted because the hardware service formats with the user's culture.
    /// </summary>
    public static bool TryParseValue(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = LeadingNumber.Match(text);
        if (!match.Success) return false;
        if (!double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed))
            return false;
        value = parsed;
        return true;
    }

    /// <summary>
    /// One tile per CPU core. Coloured by the core temperature when the CPU exposes one (Intel), else by
    /// load (AMD Zen has no per-core temperature sensors). Thread loads are averaged per core.
    /// P-cores come first, then E-cores, then plain cores, each by core number.
    /// </summary>
    public static IReadOnlyList<HeatmapTile> BuildCpuCores(IEnumerable<(string Name, string Value, string Kind)> readings)
    {
        var cores = new Dictionary<string, (string? Type, int Number, double? Temp, List<double> Loads)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, text, kind) in readings)
        {
            bool isTemp = IsKind(kind, TemperatureKind), isLoad = IsKind(kind, LoadKind);
            if (!(isTemp || isLoad) || !TryParseValue(text, out double value)) continue;
            var match = CoreName.Match(name?.Trim() ?? string.Empty);
            if (!match.Success || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                continue;

            string? type = match.Groups["type"].Success ? match.Groups["type"].Value.ToUpperInvariant() : null;
            string key = $"{type}{number}";
            if (!cores.TryGetValue(key, out var core))
                core = (type, number, null, new List<double>());

            if (isTemp)
                core.Temp = core.Temp is double t ? Math.Max(t, value) : value;
            else
                core.Loads.Add(Math.Clamp(value, 0, 100));
            cores[key] = core;
        }

        return cores
            .OrderBy(c => c.Value.Type switch { "P" => 0, "E" => 1, _ => 2 })
            .ThenBy(c => c.Value.Number)
            .Select(c =>
            {
                var (type, number, temp, loads) = c.Value;
                string label = type is null ? $"Core {number}" : $"{type}-Core {number}";
                double? load = loads.Count > 0 ? loads.Average() : null;
                return temp is double celsius
                    ? new HeatmapTile("core:" + c.Key, label, celsius, HeatmapScale.Temperature, FormatTemperature(celsius),
                        load is double l ? $"{FormatPercent(l)} load" : string.Empty)
                    : new HeatmapTile("core:" + c.Key, label, load!.Value, HeatmapScale.Load, FormatPercent(load.Value), "load");
            })
            .ToList();
    }

    /// <summary>
    /// CPU-wide sensors that are not a single core: package / CCD / Tctl / max / average temperatures and the
    /// total load. "Distance to TjMax" readings are skipped (they are headroom, not a temperature).
    /// </summary>
    public static IReadOnlyList<HeatmapTile> BuildCpuSummary(IEnumerable<(string Name, string Value, string Kind)> readings)
        => BuildGroup(readings, MaxSummaryTiles, skipCores: true);

    /// <summary>GPU temperature sensors (core, hot spot, memory junction) and GPU loads.</summary>
    public static IReadOnlyList<HeatmapTile> BuildGpu(IEnumerable<(string Name, string Value, string Kind)> readings)
        => BuildGroup(readings, MaxGpuTiles, skipCores: false);

    private static IReadOnlyList<HeatmapTile> BuildGroup(IEnumerable<(string Name, string Value, string Kind)> readings, int max, bool skipCores)
    {
        var temps = new List<HeatmapTile>();
        var loads = new List<HeatmapTile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawName, text, kind) in readings)
        {
            bool isTemp = IsKind(kind, TemperatureKind), isLoad = IsKind(kind, LoadKind);
            string name = rawName?.Trim() ?? string.Empty;
            if (!(isTemp || isLoad) || name.Length == 0 || !TryParseValue(text, out double value)) continue;
            if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) continue;
            if (skipCores && CoreName.IsMatch(name)) continue;

            string key = (isTemp ? "temp:" : "load:") + name;
            if (!seen.Add(key)) continue;

            string label = ShortLabel(name);
            if (isTemp)
                temps.Add(new HeatmapTile(key, label, value, HeatmapScale.Temperature, FormatTemperature(value), "temperature"));
            else
            {
                double load = Math.Clamp(value, 0, 100);
                // Label by sensor type so "Core" (temperature) and "Core" (load) never read as one sensor.
                loads.Add(new HeatmapTile(key, label + " load", load, HeatmapScale.Load, FormatPercent(load), "utilization"));
            }
        }

        // A temperature sharing its name with a load becomes "Core temp"; unique ones ("Hot Spot") stay as they are.
        var loadNames = new HashSet<string>(loads.Select(l => l.Label[..^" load".Length]), StringComparer.OrdinalIgnoreCase);
        temps = temps.Select(t => loadNames.Contains(t.Label) ? t with { Label = t.Label + " temp" } : t).ToList();

        // Temperatures first (they are what the heatmap is about), then loads, capped.
        return temps.Concat(loads).Take(max).ToList();
    }

    /// <summary>"CPU Package" → "Package", "GPU Hot Spot" → "Hot Spot"; the group header already names the device.</summary>
    internal static string ShortLabel(string name)
    {
        foreach (var prefix in new[] { "CPU ", "GPU " })
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..];
        }
        return name;
    }

    private static bool IsKind(string? kind, string expected) => string.Equals(kind, expected, StringComparison.OrdinalIgnoreCase);

    internal static string FormatTemperature(double celsius)
        => Math.Round(celsius, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) + "°C";

    internal static string FormatPercent(double percent)
        => Math.Round(percent, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) + "%";
}
