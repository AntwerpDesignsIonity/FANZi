using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Fanzi.FanControl.Converters;
using System.Globalization;

namespace Fanzi.FanControl.Tests;

/// <summary>Converters create Avalonia brushes, so they run on the headless UI thread.</summary>
public class TemperatureConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData("72.5")]
    [InlineData(true)]
    public void Brush_MissingReading_ReturnsNeutralBrush(object? value)
    {
        var brush = Assert.IsType<SolidColorBrush>(
            TemperatureToBrushConverter.Instance.Convert(value, typeof(IBrush), null, Culture));
        Assert.Equal(Color.Parse("#1A2845"), brush.Color);
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData("72.5")]
    [InlineData(true)]
    public void Brush_MissingReading_Glow_ReturnsTransparent(object? value)
    {
        var brush = Assert.IsType<SolidColorBrush>(
            TemperatureToBrushConverter.Instance.Convert(value, typeof(IBrush), "glow", Culture));
        Assert.Equal(0, brush.Color.A);
    }

    [AvaloniaFact]
    public void Brush_UsesScaleColour_AndGlowAlpha()
    {
        var solid = Assert.IsType<SolidColorBrush>(
            TemperatureToBrushConverter.Instance.Convert(60.0, typeof(IBrush), null, Culture));
        Assert.Equal(Color.Parse("#22C55E"), solid.Color);

        var glow = Assert.IsType<SolidColorBrush>(
            TemperatureToBrushConverter.Instance.Convert((double?)95.0, typeof(IBrush), "glow", Culture));
        Assert.Equal(Color.FromArgb(0x66, 0xEF, 0x44, 0x44), glow.Color);

        // Integer readings are accepted too.
        var fromInt = Assert.IsType<SolidColorBrush>(
            TemperatureToBrushConverter.Instance.Convert(30, typeof(IBrush), null, Culture));
        Assert.Equal(Color.Parse("#38BDF8"), fromInt.Color);
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData("72.5")]
    [InlineData(true)]
    public void Gradient_MissingReading_ReturnsPrimaryTextBrush(object? value)
    {
        var brush = Assert.IsType<SolidColorBrush>(
            TemperatureToGradientConverter.Instance.Convert(value, typeof(IBrush), null, Culture));
        Assert.Equal(Color.Parse("#E8F0FF"), brush.Color);
    }

    [AvaloniaFact]
    public void Gradient_RunsFromScaleColourToLighterTint()
    {
        var brush = Assert.IsType<LinearGradientBrush>(
            TemperatureToGradientConverter.Instance.Convert(95.0, typeof(IBrush), null, Culture));
        Assert.Equal(2, brush.GradientStops.Count);
        Assert.Equal(Color.Parse("#EF4444"), brush.GradientStops[0].Color);

        var tint = brush.GradientStops[1].Color;
        Assert.True(tint.R >= 0xEF && tint.G > 0x44 && tint.B > 0x44, $"tint {tint} should be lighter");
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData("72.5")]
    [InlineData(true)]
    public void BoxShadow_MissingReading_ReturnsNeutralShadow(object? value)
    {
        var shadows = Assert.IsType<BoxShadows>(
            TemperatureToBoxShadowConverter.Instance.Convert(value, typeof(BoxShadows), null, Culture));
        Assert.Equal(TemperatureToBoxShadowConverter.Neutral, shadows);
    }

    [AvaloniaFact]
    public void BoxShadow_GlowIsTintedByTemperature()
    {
        var shadows = Assert.IsType<BoxShadows>(
            TemperatureToBoxShadowConverter.Instance.Convert(95.0, typeof(BoxShadows), null, Culture));
        Assert.Equal(2, shadows.Count);
        var glow = shadows[0];
        Assert.False(glow.IsInset);
        Assert.Equal((byte)0xEF, glow.Color.R);
        Assert.True(shadows[1].IsInset);
    }

    [AvaloniaFact]
    public void BoxShadow_Hovered_KeepsTemperatureColourButGlowsStronger()
    {
        var resting = Assert.IsType<BoxShadows>(TemperatureToBoxShadowConverter.Instance.Convert(
            new object?[] { 95.0, false }, typeof(BoxShadows), null, Culture));
        var hovered = Assert.IsType<BoxShadows>(TemperatureToBoxShadowConverter.Instance.Convert(
            new object?[] { 95.0, true }, typeof(BoxShadows), null, Culture));

        Assert.Equal(TemperatureToBoxShadowConverter.Instance.Convert(95.0, typeof(BoxShadows), null, Culture), resting);
        Assert.Equal((byte)0xEF, hovered[0].Color.R); // still the hot red
        Assert.True(hovered[0].Color.A > resting[0].Color.A);
        Assert.True(hovered[0].Blur > resting[0].Blur);
        Assert.True(hovered[0].Spread > resting[0].Spread);
    }

    [AvaloniaFact]
    public void BoxShadow_HoveredWithoutReading_UsesThemeHoverShadow()
    {
        var shadows = Assert.IsType<BoxShadows>(TemperatureToBoxShadowConverter.Instance.Convert(
            new object?[] { null, true }, typeof(BoxShadows), null, Culture));
        Assert.Equal(TemperatureToBoxShadowConverter.NeutralHover, shadows);
    }

    [AvaloniaFact]
    public void ConvertBack_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => TemperatureToBrushConverter.Instance.ConvertBack(null, typeof(double), null, Culture));
        Assert.Throws<NotSupportedException>(() => TemperatureToGradientConverter.Instance.ConvertBack(null, typeof(double), null, Culture));
        Assert.Throws<NotSupportedException>(() => TemperatureToBoxShadowConverter.Instance.ConvertBack(null, typeof(double), null, Culture));
    }
}
