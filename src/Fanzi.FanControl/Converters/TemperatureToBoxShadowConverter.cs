using Avalonia.Data.Converters;
using Avalonia.Media;
using Fanzi.FanControl.Services;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Fanzi.FanControl.Converters;

/// <summary>
/// Converts a temperature in °C (<c>double?</c>) to <see cref="BoxShadows"/>: a soft outer glow in the
/// scale colour plus the theme's 1px inner top highlight. A missing reading returns the neutral card shadow.
/// As a multi-value converter it takes <c>[temperature, isPointerOver]</c> and returns a stronger,
/// lifted glow while hovered — the temperature colour is kept, so hover never hides the reading.
/// </summary>
public sealed class TemperatureToBoxShadowConverter : IValueConverter, IMultiValueConverter
{
    public static readonly TemperatureToBoxShadowConverter Instance = new();

    private static readonly BoxShadow InnerHighlight = new()
    {
        OffsetX = 0, OffsetY = 1, Blur = 0, Spread = 0,
        Color = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
        IsInset = true,
    };

    private static readonly BoxShadow HoverInnerHighlight = new()
    {
        OffsetX = 0, OffsetY = 1, Blur = 0, Spread = 0,
        Color = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
        IsInset = true,
    };

    internal static readonly BoxShadows Neutral = new(
        new BoxShadow { OffsetY = 4, Blur = 24, Color = Color.FromArgb(0x59, 0x00, 0x00, 0x00) },
        new[] { InnerHighlight });

    /// <summary>Same as the theme's Border.card:pointerover shadow (no reading, hovered).</summary>
    internal static readonly BoxShadows NeutralHover = new(
        new BoxShadow { OffsetY = 6, Blur = 28, Spread = 2, Color = Color.FromArgb(0x33, 0x25, 0x63, 0xEB) },
        new[] { HoverInnerHighlight });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Create(value, hovered: false);

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        object? temperature = values.Count > 0 ? values[0] : null;
        bool hovered = values.Count > 1 && values[1] is true;
        return Create(temperature, hovered);
    }

    internal static BoxShadows Create(object? value, bool hovered)
    {
        if (!TemperatureConverterHelpers.TryGetDouble(value, out double celsius))
            return hovered ? NeutralHover : Neutral;

        var color = TemperatureColorScale.ForTemperature(celsius);
        return hovered
            ? new BoxShadows(
                new BoxShadow { OffsetY = 4, Blur = 30, Spread = 3, Color = TemperatureColorScale.WithAlpha(color, 0.5) },
                new[] { HoverInnerHighlight })
            : new BoxShadows(
                new BoxShadow { Blur = 22, Spread = 1, Color = TemperatureColorScale.WithAlpha(color, 0.3) },
                new[] { InnerHighlight });
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
