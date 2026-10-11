using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>Living gradient aura behind every ThermalGauge dial.</summary>
public class GaugeAuraTests
{
    [AvaloniaFact] // brushes are AvaloniaObjects: create them on the UI thread
    public void AuraBrush_IsSeamlessAndCarriesTheReadingColour()
    {
        var hot = TemperatureColorScale.ForTemperature(90);
        var brush = ThermalGauge.BuildAuraBrush(hot);
        Assert.Equal(brush.GradientStops[0].Color, brush.GradientStops[^1].Color); // no seam while it turns
        Assert.Equal(hot.R, brush.GradientStops[0].Color.R);
        Assert.Equal(1, brush.GradientStops[^1].Offset);
    }

    [AvaloniaFact]
    public void Aura_LivesUnlessReduceMotion()
    {
        bool old = MotionPreferences.ReduceMotion;
        try
        {
            MotionPreferences.ReduceMotion = false;
            var gauge = new ThermalGauge { Value = 50 };
            Assert.True(gauge.IsAuraLiving);

            MotionPreferences.ReduceMotion = true;
            gauge.Value = 60;
            Assert.False(gauge.IsAuraLiving);
        }
        finally { MotionPreferences.ReduceMotion = old; }
    }
}
