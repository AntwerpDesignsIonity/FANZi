namespace Fanzi.FanControl.Services;

/// <summary>
/// Picks the CPU temperature to show when only one number fits (hero gauge, overlay gauge):
/// package → hotspot → average core. Missing or NaN readings are skipped; null when none exist,
/// which the gauges render as "--".
/// </summary>
public static class CpuTemperatureFallback
{
    public static double? Select(double? package, double? hotspot, double? average)
    {
        if (IsReading(package)) return package;
        if (IsReading(hotspot)) return hotspot;
        if (IsReading(average)) return average;
        return null;
    }

    private static bool IsReading(double? value) => value is double v && double.IsFinite(v);
}
