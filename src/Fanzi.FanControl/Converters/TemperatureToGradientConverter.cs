using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Fanzi.FanControl.Services;
using System;
using System.Globalization;

namespace Fanzi.FanControl.Converters;

/// <summary>
/// Converts a temperature in °C (<c>double?</c>) to a diagonal <see cref="LinearGradientBrush"/>
/// running from the scale colour to a lighter tint of it — used for big metric values.
/// A missing reading returns the plain primary text brush.
/// </summary>
public sealed class TemperatureToGradientConverter : IValueConverter
{
    public static readonly TemperatureToGradientConverter Instance = new();

    internal static readonly Color FallbackColor = Color.FromRgb(0xE8, 0xF0, 0xFF);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!TemperatureConverterHelpers.TryGetDouble(value, out double celsius))
            return new SolidColorBrush(FallbackColor);

        return CreateGradient(TemperatureColorScale.ForTemperature(celsius));
    }

    /// <summary>Scale colour → 45 % lighter tint, top-left to bottom-right.</summary>
    internal static LinearGradientBrush CreateGradient(Color color) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(color, 0),
            new GradientStop(TemperatureColorScale.Lighten(color, 0.45), 1),
        },
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
