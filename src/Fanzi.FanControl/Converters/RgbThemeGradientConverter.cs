using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Converters;

/// <summary>
/// Paints a theme pill in its own colours (v4 RGB studio): primary → secondary diagonal gradient,
/// the full spectrum for rainbow themes. Dark colours (e.g. Strobe's black) are lifted so the pill
/// stays visible on the dark UI.
/// </summary>
public sealed class RgbThemeGradientConverter : IValueConverter
{
    public static readonly RgbThemeGradientConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RgbThemePreset preset) return null;

        if (preset.Effect == RgbEffectType.Rainbow)
            return ViewModels.RgbControlViewModel.Rainbow();

        Color a = Lift(preset.PrimaryColor), b = Lift(preset.SecondaryColor);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        }.ToImmutable();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static Color Lift(RgbColor c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max >= 70) return Color.FromRgb(c.R, c.G, c.B);
        return Color.FromRgb(70, 80, 100); // near-black → slate
    }
}
