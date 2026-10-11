using Avalonia.Data.Converters;
using Avalonia.Media;
using Fanzi.FanControl.Controls;
using System;
using System.Globalization;

namespace Fanzi.FanControl.Converters;

/// <summary>
/// Temperature (°C, <c>double?</c>) → brush on the thermal frame scale (blue cold, green stable, red hot), for the
/// title-bar chip that names the frame's state. ConverterParameter <c>soft</c> returns the colour at 16 % alpha
/// (chip background), <c>edge</c> at 55 % (chip border). No reading → the neutral navy.
/// </summary>
public sealed class ThermalFrameColorConverter : IValueConverter
{
    public static readonly ThermalFrameColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double? celsius = TemperatureConverterHelpers.TryGetDouble(value, out double c) ? c : null;
        var color = ThermalFrameMath.ColorFor(celsius);
        double alpha = (parameter as string) switch
        {
            "soft" => 0.16,
            "edge" => 0.55,
            _ => 1.0,
        };
        return new SolidColorBrush(color, alpha);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
