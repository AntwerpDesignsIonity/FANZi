using Avalonia.Data.Converters;
using Avalonia.Media;
using Fanzi.FanControl.Models;
using System;
using System.Globalization;

namespace Fanzi.FanControl.Converters;

/// <summary>
/// Converts an <see cref="RgbColor"/> to an Avalonia <see cref="IBrush"/>
/// so it can be used directly in AXAML bindings.
/// </summary>
public sealed class RgbColorToBrushConverter : IValueConverter
{
    public static readonly RgbColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RgbColor c)
            return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));

        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
