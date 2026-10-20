using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.Tests;

/// <summary>4.3 Typing FX: keystroke → light, pure and deterministic.</summary>
public class TypingFxTests
{
    private static readonly RgbColor Base = new(10, 20, 30);
    private static readonly RgbColor Primary = new(0, 100, 255);
    private static readonly RgbColor Secondary = new(255, 0, 200);

    private static int Energy(RgbColor c) => c.R + c.G + c.B;

    [Fact]
    public void Idle_engine_shows_the_dimmed_base_colour_on_every_led()
    {
        var fx = new TypingFxEngine { BackgroundLevel = 0.5 };
        var frame = fx.Render(10, LedLayout.Linear(8), Base, Primary, Secondary, isKeyboard: false);
        Assert.Equal(8, frame.Length);
        Assert.All(frame, c => Assert.Equal(Base.Scale(0.5), c));
    }

    [Theory]
    [InlineData(TypingFxStyle.Ripple)]
    [InlineData(TypingFxStyle.Pulse)]
    [InlineData(TypingFxStyle.Line)]
    [InlineData(TypingFxStyle.Area)]
    [InlineData(TypingFxStyle.Fireworks)]
    [InlineData(TypingFxStyle.Typewriter)]
    [InlineData(TypingFxStyle.Matrix)]
    [InlineData(TypingFxStyle.Heat)]
    public void Every_style_lights_something_right_after_a_key_and_fades_out(TypingFxStyle style)
    {
        var fx = new TypingFxEngine { Style = style, BackgroundLevel = 0 };
        var layout = LedLayout.FromMatrix(112, 18, 6, (r, c) => r * 18 + c < 112 ? r * 18 + c : -1);
        fx.Press(1.0, new LedPoint(0.5, 0.5), seed: 3);

        var lit = fx.Render(1.05, layout, Base, Primary, Secondary, isKeyboard: true);
        Assert.True(lit.Sum(Energy) > 0, $"{style} produced no light");

        fx.Tick(30);
        var later = fx.Render(30, layout, Base, Primary, Secondary, isKeyboard: true);
        // With background 0 and no live hits, the only residue allowed is the Typewriter cursor.
        int residue = later.Sum(Energy);
        if (style == TypingFxStyle.Typewriter) Assert.True(residue > 0); else Assert.Equal(0, residue);
    }

    [Fact]
    public void Ripple_is_brightest_at_the_key_first_then_moves_outward()
    {
        var fx = new TypingFxEngine { Style = TypingFxStyle.Ripple, BackgroundLevel = 0 };
        var layout = LedLayout.Linear(21); // x = 0, 0.05, … 1
        fx.Press(0, new LedPoint(0.5, 0.5));

        var early = fx.Render(0.02, layout, Base, Primary, Secondary, false);
        Assert.True(Energy(early[10]) > Energy(early[0]));

        var later = fx.Render(0.4, layout, Base, Primary, Secondary, false);
        // ring radius ≈ 0.44 → the ends are now brighter than the centre
        Assert.True(Energy(later[1]) > Energy(later[10]) && Energy(later[19]) > Energy(later[10]));
    }

    [Fact]
    public void Typing_rate_is_smoothed_and_decays()
    {
        var fx = new TypingFxEngine();
        for (int i = 0; i < 10; i++) fx.Press(i * 0.1, new LedPoint(0.5, 0.5)); // 10 keys/s
        Assert.InRange(fx.KeysPerSecond, 5, 15);
        Assert.InRange(fx.WordsPerMinute, 60, 180);
        fx.Tick(60);
        Assert.True(fx.KeysPerSecond < 5);
        Assert.Equal(0, fx.ActiveHits);
    }

    [Fact]
    public void Hit_list_is_bounded()
    {
        var fx = new TypingFxEngine();
        for (int i = 0; i < 500; i++) fx.Press(i * 0.01, new LedPoint(0.1, 0.1));
        Assert.Equal(TypingFxEngine.MaxHits, fx.ActiveHits);
    }

    [Fact]
    public void Unknown_key_origin_is_still_deterministic()
    {
        var a = new TypingFxEngine(); a.Press(0, null, seed: 42);
        var b = new TypingFxEngine(); b.Press(0, null, seed: 42);
        var la = LedLayout.Linear(10);
        Assert.Equal(a.Render(0.1, la, Base, Primary, Secondary, false), b.Render(0.1, la, Base, Primary, Secondary, false));
    }

    [Fact]
    public void Matrix_layout_places_leds_and_fills_gaps()
    {
        // 3×2 grid, LED 5 missing from the map
        var layout = LedLayout.FromMatrix(6, 3, 2, (r, c) => r == 1 && c == 2 ? -1 : r * 3 + c);
        Assert.True(layout.IsMatrix);
        Assert.Equal(new LedPoint(0, 0), layout.Points[0]);
        Assert.Equal(new LedPoint(1, 0), layout.Points[2]);
        Assert.Equal(new LedPoint(0.5, 1), layout.Points[4]);
        Assert.Equal(1.0, layout.Points[5].Y); // unplaced → bottom edge
        Assert.False(LedLayout.Linear(4).IsMatrix);
        Assert.Equal(new LedPoint(0.5, 0.5), LedLayout.Linear(1).Points[0]);
    }

    [Fact]
    public void Keyboard_geometry_knows_the_main_keys()
    {
        var a = KeyboardGeometry.Position('A');
        var l = KeyboardGeometry.Position('L');
        var space = KeyboardGeometry.Position(0x20);
        var f1 = KeyboardGeometry.Position(0x70);
        Assert.NotNull(a); Assert.NotNull(l); Assert.NotNull(space); Assert.NotNull(f1);
        Assert.True(a!.Value.X < l!.Value.X);           // A is left of L
        Assert.Equal(a.Value.Y, l.Value.Y);            // same row
        Assert.True(space!.Value.Y > a.Value.Y);        // space below home row
        Assert.Equal(0, f1!.Value.Y);                   // function row at the top
        Assert.True(KeyboardGeometry.IsBreakKey(0x0D));
        Assert.False(KeyboardGeometry.IsBreakKey('A'));
        Assert.Equal("Key: A", KeyboardGeometry.OpenRgbLedName('A'));
        Assert.Equal("Key: Space", KeyboardGeometry.OpenRgbLedName(0x20));
        Assert.Null(KeyboardGeometry.Position(0xFE));
    }

    [Fact]
    public void Style_names_round_trip()
    {
        foreach (TypingFxStyle s in Enum.GetValues<TypingFxStyle>())
        {
            Assert.Equal(s, TypingFxEngine.ParseStyle(s.ToString()));
            Assert.Equal(s, TypingFxEngine.ParseStyle(TypingFxEngine.Label(s)));
            Assert.False(string.IsNullOrWhiteSpace(TypingFxEngine.Hint(s)));
        }
        Assert.Equal(TypingFxStyle.Ripple, TypingFxEngine.ParseStyle("nonsense"));
        Assert.Equal(TypingFxStyle.Ripple, TypingFxEngine.ParseStyle(null));
    }

    [Fact]
    public void Heat_colour_runs_blue_to_white()
    {
        var cold = TypingFxEngine.HeatColor(0);
        var hot = TypingFxEngine.HeatColor(1);
        Assert.True(cold.B > cold.R);
        Assert.Equal(RgbColor.White, hot);
    }
}
