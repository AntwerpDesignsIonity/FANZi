using Avalonia.Data.Converters;
using Avalonia.Media;
using Fanzi.FanControl.Services;
using System;
using System.Globalization;

namespace Fanzi.FanControl.Converters;

/// <summary>
/// Converts a temperature in °C (<c>double?</c>) to a <see cref="SolidColorBrush"/> from
/// <see cref="TemperatureColorScale"/>. ConverterParameter <c>glow</c> returns the colour at 40 % alpha.
/// A missing reading (null / NaN / non-numeric) returns the neutral border colour (transparent for glow).
/// </summary>
public sealed class TemperatureToBrushConverter : IValueConverter
{
    public static readonly TemperatureToBrushConverter Instance = new();

    internal static readonly Color NeutralColor = Color.FromRgb(0x1A, 0x28, 0x45);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool glow = TemperatureConverterHelpers.IsGlow(parameter);

        if (!TemperatureConverterHelpers.TryGetDouble(value, out double celsius))
            return new SolidColorBrush(glow ? Colors.Transparent : NeutralColor);

        var color = TemperatureColorScale.ForTemperature(celsius);
        return new SolidColorBrush(glow ? TemperatureColorScale.WithAlpha(color, 0.4) : color);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal static class TemperatureConverterHelpers
{
    /// <summary>Extracts a finite number from a boxed numeric value; false for null/NaN/other types.</summary>
    public static bool TryGetDouble(object? value, out double result)
    {
        result = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => double.NaN,
        };
        return !double.IsNaN(result) && !double.IsInfinity(result);
    }

    public static bool IsGlow(object? parameter)
        => parameter is string s && s.Equals("glow", StringComparison.OrdinalIgnoreCase);
}
