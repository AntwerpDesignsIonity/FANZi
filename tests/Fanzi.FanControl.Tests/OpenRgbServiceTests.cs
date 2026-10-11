using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public class OpenRgbServiceTests
{
    private static readonly RgbColor Red = new(255, 0, 0);
    private static readonly RgbColor Blue = new(0, 0, 255);

    [Fact]
    public void FitToLedCount_trims_extra_colours()
    {
        var fitted = OpenRgbService.FitToLedCount(new[] { Red, Blue, Red }, 2);

        Assert.Equal(2, fitted.Length);
        Assert.Equal(255, fitted[0].R);
        Assert.Equal(255, fitted[1].B);
    }

    [Fact]
    public void FitToLedCount_pads_with_last_colour()
    {
        var fitted = OpenRgbService.FitToLedCount(new[] { Red, Blue }, 4);

        Assert.Equal(4, fitted.Length);
        Assert.Equal(255, fitted[0].R);
        Assert.All(fitted.Skip(1), c => Assert.Equal(255, c.B));
    }

    [Fact]
    public void FitToLedCount_empty_input_gives_black_and_zero_leds_gives_empty()
    {
        var black = OpenRgbService.FitToLedCount(Array.Empty<RgbColor>(), 3);
        Assert.Equal(3, black.Length);
        Assert.All(black, c => Assert.True(c.R == 0 && c.G == 0 && c.B == 0));

        Assert.Empty(OpenRgbService.FitToLedCount(new[] { Red }, 0));
    }

    [Fact]
    public async Task Connect_after_dispose_returns_false_without_creating_a_client()
    {
        var svc = new OpenRgbService();
        svc.Dispose();

        bool ok = await svc.TryConnectAsync(port: 1);

        Assert.False(ok);
        Assert.False(svc.IsConnected);
    }

    [Fact]
    public async Task Writes_while_offline_are_noops()
    {
        using var svc = new OpenRgbService();

        await svc.SetDeviceColorsAsync(0, new[] { Red });
        await svc.SetAllDevicesColorAsync(Blue);

        Assert.False(svc.IsConnected);
        Assert.Equal("Not connected", svc.ServerVersion);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var svc = new OpenRgbService();
        svc.Dispose();
        svc.Dispose();
        Assert.False(svc.IsConnected);
    }
}
