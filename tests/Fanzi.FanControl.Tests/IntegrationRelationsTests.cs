using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

/// <summary>Task 9 "relations": shared colour scale on LEDs, GPU-aware AI, RPM-less coaching, RGB chip dot.</summary>
public sealed class IntegrationRelationsTests
{
    [Theory]
    [InlineData(20, 0x38, 0xBD, 0xF8)]   // clamps below the first stop
    [InlineData(30, 0x38, 0xBD, 0xF8)]
    [InlineData(45, 0x22, 0xD3, 0xEE)]
    [InlineData(60, 0x22, 0xC5, 0x5E)]
    [InlineData(75, 0xFB, 0xBF, 0x24)]
    [InlineData(85, 0xF9, 0x73, 0x16)]
    [InlineData(95, 0xEF, 0x44, 0x44)]
    [InlineData(120, 0xEF, 0x44, 0x44)]  // clamps above the last stop
    public void RgbTemperatureColour_MatchesUiScale(double celsius, int r, int g, int b)
    {
        Assert.Equal(new RgbColor((byte)r, (byte)g, (byte)b), RgbColor.FromTemperature(celsius));
    }

    [Theory]
    [InlineData(37.5)]
    [InlineData(52.5)]
    [InlineData(67.5)]
    [InlineData(90)]
    public void RgbTemperatureColour_MidpointsMatchUiScale(double celsius)
    {
        var ui = TemperatureColorScale.ForTemperature(celsius);

        Assert.Equal(new RgbColor(ui.R, ui.G, ui.B), RgbColor.FromTemperature(celsius));
    }

    [Fact]
    public void TemperatureReactiveEffect_UsesSharedScale()
    {
        var color = RgbEffectsEngine.Tick(0, RgbEffectType.TemperatureReactive, RgbColor.Blue, RgbColor.Cyan,
            1.0, 1.0, cpuTempC: 60, gpuTempC: 45, cpuLoadPct: 10);

        Assert.Equal(new RgbColor(0x22, 0xC5, 0x5E), color);
    }

    [Fact]
    public void FanEngine_GpuTemperatureFeedsDeltaT()
    {
        var curve = SmartFanCurveEngine.GenerateDefaultCurve();
        var cpuOnly = new SmartFanCurveEngine { AcousticFirstEnabled = false };
        var withGpu = new SmartFanCurveEngine { AcousticFirstEnabled = false };

        double a = cpuOnly.ComputeOptimalFanSpeed(40, 10, 900, curve);
        double b = withGpu.ComputeOptimalFanSpeed(40, 10, 900, curve, gpuTempC: 80);

        Assert.True(b > a, $"GPU heat should raise the suggestion (cpu-only {a:F1}%, with GPU {b:F1}%)");
    }

    [Fact]
    public void FanEngine_LightLoadWithGpuDoesNotSaturate()
    {
        var curve = SmartFanCurveEngine.GenerateSilentCurve();
        var engine = new SmartFanCurveEngine();
        var cpuOnly = new SmartFanCurveEngine();

        double withGpu = 0, without = 0;
        for (int i = 0; i < 30; i++)
        {
            withGpu = engine.ComputeOptimalFanSpeed(55, 10, 900, curve, gpuTempC: 40);
            without = cpuOnly.ComputeOptimalFanSpeed(55, 10, 900, curve);
        }

        Assert.True(withGpu < 60, $"CPU hotspot 55 °C + GPU 40 °C on a quiet curve should stay quiet, got {withGpu:F1}%");
        Assert.Equal(without, withGpu, 3); // an idle GPU changes nothing
    }

    [Fact]
    public void FanEngine_HotGpuRaisesButDoesNotPinCpuFan()
    {
        var curve = SmartFanCurveEngine.GenerateSilentCurve();
        var engine = new SmartFanCurveEngine { AcousticFirstEnabled = false };
        var cpuOnly = new SmartFanCurveEngine { AcousticFirstEnabled = false };

        double withGpu = engine.ComputeOptimalFanSpeed(50, 20, 900, curve, gpuTempC: 75);
        double without = cpuOnly.ComputeOptimalFanSpeed(50, 20, 900, curve);

        Assert.True(withGpu > without, $"Hot GPU should raise the suggestion (cpu-only {without:F1}%, with GPU {withGpu:F1}%)");
        Assert.True(withGpu < 90, $"A gaming-load GPU alone must not pin the CPU fan, got {withGpu:F1}%");
    }

    [Fact]
    public void ThermalCoach_RunsWithoutFanRpm()
    {
        var coach = new ThermalCoach();
        for (int i = 0; i < 12; i++)
            coach.Record(45, 10, fanRpm: null);

        var recs = coach.GetRecommendations();

        Assert.Contains(recs, r => r.Title.StartsWith("Cool & quiet", StringComparison.Ordinal));
    }

    [Fact]
    public void ThermalCoach_SkipsRpmRulesWithoutRpm()
    {
        var coach = new ThermalCoach();
        for (int i = 0; i < 12; i++)
            coach.Record(78, 50, fanRpm: null);

        var recs = coach.GetRecommendations();

        Assert.DoesNotContain(recs, r => r.Title.Contains("underperforming", StringComparison.Ordinal));
        Assert.DoesNotContain(recs, r => r.Title.Contains("near-max", StringComparison.Ordinal));
    }

    [Fact]
    public void ThermalCoach_UsesPumpRpmWhenGiven()
    {
        var coach = new ThermalCoach();
        for (int i = 0; i < 12; i++)
            coach.Record(78, 50, fanRpm: 600);

        Assert.Contains(coach.GetRecommendations(), r => r.Title.Contains("underperforming", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false, "#22C55E")]
    [InlineData(true, true, "#22C55E")]
    [InlineData(false, true, "#FBBF24")]
    [InlineData(false, false, "#EF4444")]
    public void RgbChipDot_FollowsConnectionState(bool connected, bool connecting, string expected)
    {
        Assert.Equal(expected, RgbControlViewModel.DotColorFor(connected, connecting));
    }

    [Theory]
    [InlineData(true, false, 3, "Connected · 3 devices")]
    [InlineData(true, false, 1, "Connected · 1 device")]
    [InlineData(true, false, 0, "Connected · no devices")]
    [InlineData(false, true, 0, "Connecting…")]
    [InlineData(false, false, 4, "Offline")]
    public void RgbChipText_IsShort(bool connected, bool connecting, int devices, string expected)
    {
        Assert.Equal(expected, RgbControlViewModel.ChipTextFor(connected, connecting, devices));
        Assert.True(expected.Length <= 24, "the dashboard chip must not need trimming");
    }

    // RgbTabIndex is checked against the real MainWindow tab order in XamlLoadTests.
}
