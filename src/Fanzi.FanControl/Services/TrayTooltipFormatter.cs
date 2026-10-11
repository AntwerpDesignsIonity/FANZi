using System;
using System.Globalization;

namespace Fanzi.FanControl.Services;

/// <summary>Builds the tray icon tooltip, e.g. "FANZi — CPU 62°C · GPU 55°C · Health 94".</summary>
internal static class TrayTooltipFormatter
{
    /// <summary>Tooltip before the first hardware sample.</summary>
    internal const string Default = "FANZi — AI Fan Control & RGB";

    /// <summary>Missing or invalid readings show as "--".</summary>
    internal static string Format(double? cpuTempC, double? gpuTempC, double? healthScore)
        => $"FANZi — CPU {Temperature(cpuTempC)} · GPU {Temperature(gpuTempC)} · Health {Number(healthScore)}";

    /// <summary>NOTIFYICONDATA.szTip holds 128 chars including the terminator.</summary>
    internal const int MaxLength = 127;

    /// <summary>
    /// Appends an alert line ("⚠ CPU temperature high: …") under the readings, trimmed with "…" so the
    /// whole tooltip stays within <see cref="MaxLength"/>. No alert → the readings unchanged.
    /// </summary>
    internal static string WithAlert(string tooltip, string? alert)
    {
        if (string.IsNullOrWhiteSpace(alert))
            return tooltip.Length <= MaxLength ? tooltip : tooltip[..(MaxLength - 1)] + "…";

        string line = "⚠ " + alert.Trim().ReplaceLineEndings(" ");
        string combined = tooltip + "\n" + line;
        if (combined.Length <= MaxLength)
            return combined;

        int room = MaxLength - tooltip.Length - 1;
        if (room < 12) // not enough room for a useful alert line: alert alone
            return line.Length <= MaxLength ? line : line[..(MaxLength - 1)] + "…";
        return tooltip + "\n" + line[..(room - 1)] + "…";
    }

    private static string Temperature(double? celsius)
        => IsValid(celsius) ? $"{Number(celsius)}°C" : "--";

    private static string Number(double? value)
        => IsValid(value) ? Math.Round(value!.Value, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) : "--";

    private static bool IsValid(double? value)
        => value is double v && !double.IsNaN(v) && !double.IsInfinity(v);
}
