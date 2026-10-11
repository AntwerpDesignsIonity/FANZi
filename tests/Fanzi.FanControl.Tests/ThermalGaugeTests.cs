using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public class ThermalGaugeTests
{
    [Theory]
    [InlineData(null, 0, 110, 0)]
    [InlineData(double.NaN, 0, 110, 0)]
    [InlineData(0.0, 0, 110, 0)]
    [InlineData(55.0, 0, 110, 135)]
    [InlineData(110.0, 0, 110, 270)]
    [InlineData(150.0, 0, 110, 270)]
    [InlineData(-20.0, 0, 110, 0)]
    [InlineData(75.0, 0, 100, 202.5)]
    [InlineData(50.0, 100, 100, 0)] // degenerate range
    public void ComputeSweep_NormalizesAndClamps(double? value, double min, double max, double expected)
        => Assert.Equal(expected, ThermalGauge.ComputeSweep(value, min, max), 6);

    [Fact]
    public void ColorAt_Inverted_UsesHealthScale()
    {
        Assert.Equal(TemperatureColorScale.ForHealth(100), ThermalGauge.ColorAt(100, 0, 100, inverted: true));
        Assert.Equal(TemperatureColorScale.ForHealth(0), ThermalGauge.ColorAt(0, 0, 100, inverted: true));
        Assert.Equal(TemperatureColorScale.ForTemperature(72), ThermalGauge.ColorAt(72, 0, 110, inverted: false));
    }

    [AvaloniaFact]
    public void Gauge_LoadsAndReflectsValue()
    {
        var gauge = new ThermalGauge { Size = 120, Thickness = 10, Label = "CPU", Unit = "°C" };
        var window = new Window { Content = gauge, Width = 300, Height = 300 };
        window.Show();

        // No reading yet: value arc hidden, placeholder text.
        Assert.False(gauge.PART_Value.IsVisible);
        Assert.Equal("--", gauge.PART_ValueText.Text);

        gauge.Value = 55;
        Assert.True(gauge.PART_Value.IsVisible);
        Assert.Equal("55", gauge.PART_ValueText.Text);
        Assert.Equal(120, gauge.PART_Root.Width);
        Assert.Equal(10, gauge.PART_Track.StrokeThickness);
        Assert.Equal("CPU", gauge.PART_LabelText.Text);
        Assert.Equal("°C", gauge.PART_UnitText.Text);

        var conic = Assert.IsType<ConicGradientBrush>(gauge.PART_Value.Stroke);
        Assert.Equal(ThermalGauge.ArcStartAngle + 90, conic.Angle);
        Assert.Equal(TemperatureColorScale.ForTemperature(0), conic.GradientStops[0].Color);

        Assert.Equal("CPU 55 °C", AutomationProperties.GetName(gauge));

        // ValueText overrides the formatted value; the readout counts from 55 to it.
        gauge.ValueText = "94";
        Assert.Equal("94", AnimatedNumber.GetText(gauge.PART_ValueText));
        Assert.True(AnimatedNumber.IsAnimating(gauge.PART_ValueText));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (AnimatedNumber.IsAnimating(gauge.PART_ValueText) && clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal("94", gauge.PART_ValueText.Text);
        Assert.Equal("CPU 94 °C", AutomationProperties.GetName(gauge));

        window.Close();
    }

    [AvaloniaFact]
    public void ReduceMotion_GaugeJumpsWithoutSweepOrCount()
    {
        var gauge = new ThermalGauge { Label = "GPU", Unit = "°C" };
        var window = new Window { Content = gauge, Width = 300, Height = 300 };
        window.Show();
        try
        {
            gauge.Value = 40;
            Assert.NotNull(gauge.PART_Value.Transitions); // normally the arc sweeps

            MotionPreferences.ReduceMotion = true;
            gauge.Value = 90;

            Assert.Null(gauge.PART_Value.Transitions);
            Assert.Null(gauge.PART_Glow.Transitions);
            Assert.Equal(ThermalGauge.ComputeSweep(90, 0, 110), gauge.PART_Value.SweepAngle, 6);
            Assert.Equal("90", gauge.PART_ValueText.Text);

            MotionPreferences.ReduceMotion = false;
            gauge.Value = 50;
            Assert.NotNull(gauge.PART_Value.Transitions); // switched back on
        }
        finally
        {
            MotionPreferences.ReduceMotion = false;
            window.Close();
        }
    }

    [Theory]
    [InlineData("CPU", "72", "°C", "CPU 72 °C")]
    [InlineData("IO-NITY HEALTH", "88", "/100", "IO-NITY HEALTH 88/100")]
    [InlineData("GPU", "--", "°C", "GPU: no reading")]
    [InlineData(null, "50", null, "Gauge 50")]
    public void AutomationName_ReadsLabelValueAndUnit(string? label, string value, string? unit, string expected)
        => Assert.Equal(expected, ThermalGauge.AutomationNameFor(label, value, unit));
}
