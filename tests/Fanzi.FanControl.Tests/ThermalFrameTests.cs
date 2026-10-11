using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Fanzi.FanControl.Controls;
using Fanzi.FanControl.Converters;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Tests;

/// <summary>
/// FANZi 4.1 thermal frame: the running temperature line around the main window
/// (blue = cold, green = stable, red = hot) and its maths.
/// </summary>
public class ThermalFrameMathTests
{
    private static int Dominant(Color c) => c.R >= c.G && c.R >= c.B ? 0 : c.G >= c.B ? 1 : 2;
    private const int Red = 0, Green = 1, Blue = 2;

    [Theory]
    [InlineData(20, Blue)]
    [InlineData(35, Blue)]
    [InlineData(55, Green)]
    [InlineData(64, Green)]
    [InlineData(72, Green)]
    [InlineData(92, Red)]
    [InlineData(110, Red)]
    public void Scale_IsBlueCold_GreenStable_RedHot(double celsius, int dominant)
        => Assert.Equal(dominant, Dominant(ThermalFrameMath.ColorFor(celsius)));

    [Fact]
    public void Scale_HasAGreenPlateau_AndClampsAtTheEnds()
    {
        Assert.Equal(ThermalFrameMath.ColorFor(54), ThermalFrameMath.ColorFor(72));
        Assert.Equal(ThermalFrameMath.ColorFor(ThermalFrameMath.ScaleMin), ThermalFrameMath.ColorFor(-40));
        Assert.Equal(ThermalFrameMath.ColorFor(ThermalFrameMath.ScaleMax), ThermalFrameMath.ColorFor(150));
        Assert.Equal(ThermalFrameMath.NoSensorColor, ThermalFrameMath.ColorFor(null));
        Assert.Equal(ThermalFrameMath.NoSensorColor, ThermalFrameMath.ColorFor(double.NaN));
    }

    [Theory]
    [InlineData(null, ThermalState.Unknown)]
    [InlineData(30.0, ThermalState.Cold)]
    [InlineData(48.0, ThermalState.Cool)]
    [InlineData(64.0, ThermalState.Stable)]
    [InlineData(74.9, ThermalState.Stable)]
    [InlineData(80.0, ThermalState.Warm)]
    [InlineData(87.0, ThermalState.Hot)]
    [InlineData(96.0, ThermalState.Critical)]
    public void States_FollowTheTemperature(double? celsius, ThermalState expected)
        => Assert.Equal(expected, ThermalFrameMath.StateFor(celsius));

    [Fact]
    public void Labels_AreShortUpperCaseWords()
    {
        Assert.Equal("STABLE", ThermalFrameMath.StateLabel(ThermalState.Stable));
        Assert.Equal("HOT", ThermalFrameMath.StateLabel(ThermalState.Hot));
        Assert.Equal("NO SENSOR", ThermalFrameMath.StateLabel(ThermalState.Unknown));
    }

    [Fact]
    public void Line_RunsFasterAsItGetsHotter()
    {
        double cold = ThermalFrameMath.LapsPerSecond(30), stable = ThermalFrameMath.LapsPerSecond(64),
            hot = ThermalFrameMath.LapsPerSecond(92);
        Assert.True(cold < stable && stable < hot, $"{cold} < {stable} < {hot}");
        Assert.InRange(1 / cold, 15, 25);   // ~20 s a lap when cold
        Assert.InRange(1 / hot, 3, 5);      // ~4 s a lap when hot
        Assert.True(ThermalFrameMath.LapsPerSecond(null) > 0, "the no-sensor line still drifts");
    }

    [Fact]
    public void Comets_AreBrightestAtTheHead_AndFadeAlongTheTail()
    {
        const double phase = 0.30;
        Assert.Equal(1, ThermalFrameMath.Highlight(0.30, phase), 6);
        Assert.Equal(1, ThermalFrameMath.Highlight(0.80, phase), 6); // second comet half a lap away
        double mid = ThermalFrameMath.Highlight(0.30 - ThermalFrameMath.TailLength / 2, phase);
        Assert.InRange(mid, 0.2, 0.3);                                // (1 - 0.5)² = 0.25
        Assert.Equal(0, ThermalFrameMath.Highlight(0.31, phase));     // nothing ahead of the head
        Assert.Equal(0, ThermalFrameMath.Highlight(0.30 - ThermalFrameMath.TailLength - 0.01, phase));
        Assert.Equal(1, ThermalFrameMath.Highlight(0.999, 0.999 + 3), 6); // wraps around the perimeter
    }

    [Fact]
    public void Sample_IsTheTemperatureColour_LitAndOpaqueInsideAComet()
    {
        // Away from a comet: a two-thirds opaque line in the stable green family.
        var plain = ThermalFrameMath.Sample(0.55, 64, phase: 0.30, time: 0);
        Assert.Equal(Green, Dominant(plain));
        Assert.InRange(plain.A, 160, 176);

        // On a comet head: fully opaque and lighter.
        var head = ThermalFrameMath.Sample(0.30, 64, phase: 0.30, time: 0);
        Assert.Equal(255, head.A);
        Assert.True(head.R + head.G + head.B > plain.R + plain.G + plain.B);

        // Hot / cold keep their family everywhere on the frame, bands included.
        for (double u = 0; u < 1; u += 0.05)
        {
            Assert.Equal(Red, Dominant(ThermalFrameMath.Sample(u, 98, 0.1, 0)));
            Assert.Equal(Blue, Dominant(ThermalFrameMath.Sample(u, 25, 0.1, 0)));
        }
    }

    [Fact]
    public void Bands_FlowAroundTheFrame()
    {
        // Near a band boundary (78 °C = amber/green blend) the colour changes along the frame and over time.
        var a = ThermalFrameMath.Sample(0.05, 78, phase: 0.6, time: 0);
        var b = ThermalFrameMath.Sample(0.20, 78, phase: 0.6, time: 0);
        var later = ThermalFrameMath.Sample(0.05, 78, phase: 0.9, time: 0);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, later);
    }

    [Fact]
    public void CriticalState_Pulses()
    {
        var x = ThermalFrameMath.Sample(0.55, 96, 0.3, time: 0.0);
        var y = ThermalFrameMath.Sample(0.55, 96, 0.3, time: 0.18);
        Assert.NotEqual(x.A, y.A);
        var calmX = ThermalFrameMath.Sample(0.55, 64, 0.3, time: 0.0);
        var calmY = ThermalFrameMath.Sample(0.55, 64, 0.3, time: 0.18);
        Assert.Equal(calmX.A, calmY.A);
    }

    [Fact]
    public void Ease_SnapsTheFirstReading_ThenGlides()
    {
        Assert.Equal(70, ThermalFrameMath.Ease(double.NaN, 70, 0.02));
        double next = ThermalFrameMath.Ease(60, 70, 0.8);
        Assert.InRange(next, 66, 66.5); // 1 - e^-1 ≈ 63 % of the way
        Assert.Equal(60, ThermalFrameMath.Ease(60, 70, 0));
    }

    [AvaloniaFact] // brushes are created on the UI thread
    public void Converter_GivesTheScaleColour_WithSoftAndEdgeAlpha()
    {
        var conv = ThermalFrameColorConverter.Instance;
        var solid = Assert.IsType<SolidColorBrush>(conv.Convert(64.0, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ThermalFrameMath.ColorFor(64), solid.Color);
        Assert.Equal(1, solid.Opacity);
        var soft = Assert.IsType<SolidColorBrush>(conv.Convert(64.0, typeof(IBrush), "soft", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.16, soft.Opacity, 6);
        var edge = Assert.IsType<SolidColorBrush>(conv.Convert(64.0, typeof(IBrush), "edge", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.55, edge.Opacity, 6);
        var none = Assert.IsType<SolidColorBrush>(conv.Convert(null, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ThermalFrameMath.NoSensorColor, none.Color);
    }

    [Fact]
    public void Setting_DefaultsOn_AndOldFilesWithoutTheFieldStayOn()
    {
        Assert.True(new AppSettings().ThermalFrameEnabled);
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"SettingsVersion\":6,\"ReduceMotion\":true}")!;
        Assert.True(old.ThermalFrameEnabled);
        var off = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(new AppSettings { ThermalFrameEnabled = false }))!;
        Assert.False(off.ThermalFrameEnabled);
    }
}

/// <summary>The <see cref="ThermalFrame"/> control in a headless window.</summary>
public class ThermalFrameControlTests
{
    private static (Window Window, ThermalFrame Frame) Show(double? temperature, bool animated = true)
    {
        var frame = new ThermalFrame { Temperature = temperature, IsAnimated = animated };
        var window = new Window { Width = 800, Height = 600, Content = new Panel { Children = { frame } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, frame);
    }

    [AvaloniaFact]
    public void FourThinStrips_HugTheWindowEdges()
    {
        var (window, frame) = Show(64);
        try
        {
            Assert.False(frame.IsHitTestVisible);
            var s = frame.Edges.Select(e => e.Bounds).ToArray();
            Assert.Equal(new Rect(0, 0, 800, s[0].Height), s[0]);
            Assert.Equal(800, s[1].Right, 6);
            Assert.Equal(600, s[2].Bottom, 6);
            Assert.Equal(0, s[3].Left);
            Assert.All(s, r => Assert.InRange(Math.Min(r.Width, r.Height), 1, 12));
            // Together the strips cover the whole border ring (top + bottom full width, sides in between).
            Assert.Equal(s[0].Bottom, s[1].Top, 6);
            Assert.Equal(s[1].Bottom, s[2].Top, 6);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FirstReadingSnaps_LaterReadingsEase_AndTheLineRuns()
    {
        var (window, frame) = Show(60);
        try
        {
            Assert.Equal(60, frame.ShownTemperature);
            Assert.Equal(ThermalState.Stable, frame.State);

            frame.Temperature = 95;
            double phase = frame.Phase;
            frame.Advance(0.4);
            Assert.InRange(frame.ShownTemperature, 60.5, 94.5);  // easing toward the new reading
            Assert.True(frame.Phase > phase, "the comets advance");
            for (int i = 0; i < 200; i++) frame.Advance(0.05);
            Assert.Equal(95, frame.ShownTemperature, 1);
            Assert.Equal(ThermalState.Critical, frame.State);
            Assert.Equal(ThermalFrameMath.ColorFor(frame.ShownTemperature), frame.CurrentColor);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NotAnimated_IsStatic_AndFollowsReadingsImmediately()
    {
        var (window, frame) = Show(40, animated: false);
        try
        {
            Assert.False(frame.IsRunning);
            frame.Temperature = 85;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(85, frame.ShownTemperature);
            Assert.False(frame.IsRunning);

            frame.IsAnimated = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(frame.IsRunning, "turning motion back on starts the loop");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReduceMotion_StopsTheLoop()
    {
        var (window, frame) = Show(64);
        try
        {
            Assert.True(frame.IsRunning);
            MotionPreferences.ReduceMotion = true;
            frame.IsAnimated = false; // the window binds IsAnimated to !ReduceMotion
            Dispatcher.UIThread.RunJobs();
            Assert.False(frame.IsRunning);
        }
        finally
        {
            MotionPreferences.ReduceMotion = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NoSensor_DrawsTheNeutralLine()
    {
        var (window, frame) = Show(null);
        try
        {
            Assert.True(double.IsNaN(frame.ShownTemperature));
            Assert.Equal(ThermalState.Unknown, frame.State);
            Assert.Equal(ThermalFrameMath.NoSensorColor, frame.CurrentColor);
            frame.Advance(0.1); // must not throw without a reading
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SquareCorners_WhenTheRadiusIsZero()
    {
        var (window, frame) = Show(64);
        try
        {
            frame.FrameRadius = 0;
            Dispatcher.UIThread.RunJobs();
            frame.Advance(0.05); // full-length edges, no corner arcs — renders without error
            Assert.Equal(4, frame.Edges.Count);
        }
        finally { window.Close(); }
    }
}
