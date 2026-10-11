using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Fanzi.FanControl.Controls;

namespace Fanzi.FanControl.Tests;

public sealed class NumberTweenTests
{
    [Theory]
    [InlineData("72.0 °C", "", 72.0, 1, '.', " °C")]
    [InlineData("72,4°C", "", 72.4, 1, ',', "°C")]
    [InlineData("38 %", "", 38, 0, '.', " %")]
    [InlineData("4725 MHz", "", 4725, 0, '.', " MHz")]
    [InlineData("1.284 V", "", 1.284, 3, '.', " V")]
    [InlineData("-3.5 C", "", -3.5, 1, '.', " C")]
    [InlineData("88", "", 88, 0, '.', "")]
    [InlineData("~ 50 W", "~ ", 50, 0, '.', " W")]
    public void TryParse_SplitsPrefixValueAndSuffix(string text, string prefix, double value, int decimals, char separator, string suffix)
    {
        Assert.True(NumberTween.TryParse(text, out var number));
        Assert.Equal(prefix, number.Prefix);
        Assert.Equal(value, number.Value, 9);
        Assert.Equal(decimals, number.Decimals);
        Assert.Equal(separator, number.Separator);
        Assert.Equal(suffix, number.Suffix);
        Assert.Equal(text, number.Format(value)); // round-trips in the same format
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--")]
    [InlineData("Not detected")]
    [InlineData("2 of 5 fans")]
    [InlineData("Learning...")]
    public void TryParse_RejectsPlaceholdersAndMultiNumberText(string? text)
        => Assert.False(NumberTween.TryParse(text, out _));

    [Fact]
    public void Format_UsesTheTargetsDecimalsAndSeparator_AndNeverPrintsMinusZero()
    {
        Assert.True(NumberTween.TryParse("66,4°C", out var target));
        Assert.Equal("70,3°C", target.Format(70.25));
        Assert.Equal("0,0°C", target.Format(-0.01));
    }

    [Fact]
    public void Compatibility_RequiresTheSameUnitAndPrefix()
    {
        NumberTween.TryParse("72 °C", out var celsius);
        NumberTween.TryParse("80 °C", out var celsius2);
        NumberTween.TryParse("1180 RPM", out var rpm);

        Assert.True(celsius.IsCompatibleWith(celsius2));
        Assert.False(celsius.IsCompatibleWith(rpm));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.875)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void Ease_IsCubicEaseOutClamped(double progress, double expected)
        => Assert.Equal(expected, NumberTween.Ease(progress), 9);

    [Fact]
    public void ValueAt_StartsAtFromAndLandsOnTo_Monotonic()
    {
        double previous = double.MinValue;
        for (int i = 0; i <= 20; i++)
        {
            double v = NumberTween.ValueAt(72, 80, i / 20.0);
            Assert.InRange(v, 72, 80);
            Assert.True(v >= previous);
            previous = v;
        }
        Assert.Equal(72, NumberTween.ValueAt(72, 80, 0));
        Assert.Equal(80, NumberTween.ValueAt(72, 80, 1));
        Assert.Equal(80, NumberTween.ValueAt(96, 80, 1)); // counts down too
    }
}

public sealed class AnimatedNumberTests
{
    private static (Window Window, TextBlock Block) Show()
    {
        var block = new TextBlock();
        // A gauge sweeping next to the number, as on the dashboard. With headless drawing a lone text change
        // produces no compositor work, so the headless render timer stops pulsing the animation clock after
        // the first frame; the sweep keeps it ticking (a real window's render timer always does).
        var gauge = new ThermalGauge { Value = 10 };
        var window = new Window { Content = new StackPanel { Children = { block, gauge } }, Width = 240, Height = 320 };
        window.Opened += (_, _) => gauge.Value = 100;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, block);
    }

    /// <summary>Drives frames until the count finishes (or 3 s), returning every text seen on the way.</summary>
    private static List<string> PumpUntilDone(TextBlock block, out TimeSpan elapsed)
    {
        var seen = new List<string>();
        var clock = Stopwatch.StartNew();
        while (AnimatedNumber.IsAnimating(block) && clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            seen.Add(block.Text ?? "");
            Thread.Sleep(10);
        }
        elapsed = clock.Elapsed;
        return seen;
    }

    [AvaloniaFact]
    public void NewReading_CountsUpToTheTarget_InTheTargetFormat()
    {
        var (window, block) = Show();
        try
        {
            AnimatedNumber.SetText(block, "72,0°C");
            Assert.Equal("72,0°C", block.Text); // the first value has nothing to count from

            AnimatedNumber.SetText(block, "80,0°C");
            Assert.True(AnimatedNumber.IsAnimating(block));
            Assert.Equal("72,0°C", block.Text); // the count starts at the old value

            var seen = PumpUntilDone(block, out var elapsed);

            Assert.False(AnimatedNumber.IsAnimating(block), $"still counting after {elapsed.TotalMilliseconds:F0} ms: {seen.Count} frames, last '{block.Text}', distinct {string.Join("|", seen.Distinct().Take(8))}");
            Assert.Equal("80,0°C", block.Text);
            Assert.True(elapsed >= AnimatedNumber.Duration * 0.75, $"finished after {elapsed.TotalMilliseconds:F0} ms");
            var intermediate = seen.Where(s => s != "72,0°C" && s != "80,0°C").ToList();
            Assert.NotEmpty(intermediate);
            double previous = 72;
            foreach (var text in seen)
            {
                Assert.Matches(@"^\d+,\d°C$", text); // same decimals, separator and unit every frame
                Assert.True(NumberTween.TryParse(text, out var n));
                Assert.InRange(n.Value, 72, 80);
                Assert.True(n.Value >= previous, $"count went backwards: {text}");
                previous = n.Value;
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ReduceMotion_SetsTheTextAtOnce()
    {
        var (window, block) = Show();
        try
        {
            AnimatedNumber.SetText(block, "38 %");
            MotionPreferences.ReduceMotion = true;
            AnimatedNumber.SetText(block, "97 %");

            Assert.False(AnimatedNumber.IsAnimating(block));
            Assert.Equal("97 %", block.Text);
        }
        finally
        {
            MotionPreferences.ReduceMotion = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PlaceholdersUnitChangesAndDetachedBlocks_SetTheTextAtOnce()
    {
        var (window, block) = Show();
        try
        {
            AnimatedNumber.SetText(block, "--");
            AnimatedNumber.SetText(block, "1180 RPM");
            Assert.Equal("1180 RPM", block.Text);
            AnimatedNumber.SetText(block, "Not detected");
            Assert.Equal("Not detected", block.Text);
            Assert.False(AnimatedNumber.IsAnimating(block));
        }
        finally
        {
            window.Close();
        }

        var detached = new TextBlock();
        AnimatedNumber.SetText(detached, "64 °C");
        AnimatedNumber.SetText(detached, "88 °C");
        Assert.False(AnimatedNumber.IsAnimating(detached));
        Assert.Equal("88 °C", detached.Text);
    }

    [AvaloniaFact]
    public void Interrupted_CountRetargetsFromTheShownValue_AndClosingLandsOnTheTarget()
    {
        var (window, block) = Show();
        AnimatedNumber.SetText(block, "40 %");
        AnimatedNumber.SetText(block, "90 %");
        for (int i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        AnimatedNumber.SetText(block, "60 %"); // new reading mid-count
        Assert.True(AnimatedNumber.IsAnimating(block));

        window.Close(); // leaving the visual tree finishes the count
        Dispatcher.UIThread.RunJobs();
        Assert.False(AnimatedNumber.IsAnimating(block));
        Assert.Equal("60 %", block.Text);
    }
}
