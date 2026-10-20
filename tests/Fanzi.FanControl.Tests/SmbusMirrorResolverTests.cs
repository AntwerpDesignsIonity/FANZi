using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services.Rgb;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

/// <summary>4.3 RAM fix: the same ENE DRAM controller listed on two SMBus host ports must be driven exactly once.</summary>
public class SmbusMirrorResolverTests
{
    private static SmbusDeviceSketch Ram(int index, int port, int addr, int leds) =>
        new(index, "ENE DRAM", "Dram", $"I2C: PawnIO SMBus PIIX4 {port}, address 0x{addr:X2}", leds);

    [Fact]
    public void Parses_openrgb_i2c_location()
    {
        var a = SmbusMirrorResolver.Parse("I2C: PawnIO SMBus PIIX4 1, address 0x73");
        Assert.NotNull(a);
        Assert.Equal("PawnIO SMBus PIIX4", a!.Value.Adapter);
        Assert.Equal(1, a.Value.Port);
        Assert.Equal(0x73, a.Value.Address);

        Assert.Null(SmbusMirrorResolver.Parse(@"HID: \\?\HID#VID_1038&PID_1618"));
        Assert.Null(SmbusMirrorResolver.Parse(null));
    }

    [Fact]
    public void Johans_pc_two_sticks_on_two_ports_drives_one_entry_per_stick()
    {
        // Exactly what OpenRGB reported on 2026-10-20: 4 ENE DRAM entries, 2 physical sticks (A2 / B2 = ENE 0x71 / 0x73).
        var devices = new[]
        {
            Ram(0, 0, 0x71, 8), Ram(1, 0, 0x73, 8),
            Ram(2, 1, 0x71, 0), Ram(3, 1, 0x73, 8),
            new SmbusDeviceSketch(4, "ZOTAC RTX 3080", "Gpu", "I2C: Nvidia NvAPI I2C on GPU 0, address 0x49", 1),
            new SmbusDeviceSketch(5, "Apex 7 TKL", "Keyboard", @"HID: \\?\HID#VID_1038", 112),
        };

        var d = SmbusMirrorResolver.Resolve(devices);

        // Port 0 reads both sticks sanely (8 + 8 LEDs), port 1 reads one as 0 LEDs → the whole adapter is driven via port 0.
        // Never one stick per port: the two PIIX4 ports share the bus wires and OpenRGB only serialises per port.
        Assert.True(d[0].Drive); Assert.False(d[2].Drive); Assert.Equal(0, d[2].MirrorOf);
        Assert.True(d[1].Drive); Assert.False(d[3].Drive); Assert.Equal(1, d[3].MirrorOf);
        // Non-mirrored devices untouched.
        Assert.True(d[4].Drive); Assert.True(d[5].Drive);
        Assert.Equal(2, d.Count(x => !x.Drive));
        Assert.Contains("Mirror", d[3].Note);
        Assert.Contains("port 0", d[0].Note);
    }

    [Fact]
    public void Forced_port_overrides_the_automatic_pick()
    {
        var devices = new[] { Ram(0, 0, 0x71, 8), Ram(1, 1, 0x71, 8) };
        var d = SmbusMirrorResolver.Resolve(devices, forcedPort: 0);
        Assert.True(d[0].Drive);
        Assert.False(d[1].Drive);
    }

    [Fact]
    public void Garbage_led_counts_lose_to_plausible_ones()
    {
        // 2026-10-16 diagnostics: port 0 read 249 / 257 LEDs, port 1 read 8.
        var devices = new[] { Ram(0, 0, 0x71, 249), Ram(1, 0, 0x73, 257), Ram(2, 1, 0x71, 8), Ram(3, 1, 0x73, 8) };
        var d = SmbusMirrorResolver.Resolve(devices);
        Assert.False(d[0].Drive); Assert.False(d[1].Drive);
        Assert.True(d[2].Drive); Assert.True(d[3].Drive);
    }

    [Fact]
    public void Both_ports_plausible_picks_port_0_for_every_stick()
    {
        // 4.3.3: Patriot DDR5 in A2 / B2 (ENE 0x71 / 0x73) on the MAXSUN X870M. A clean OpenRGB detection lists the sticks on
        // PIIX4 port 0 only; 4.3.2 drove the port 1 copies and the sticks never changed colour.
        var devices = new[] { Ram(0, 0, 0x71, 8), Ram(1, 0, 0x73, 8), Ram(2, 1, 0x71, 8), Ram(3, 1, 0x73, 8) };
        var d = SmbusMirrorResolver.Resolve(devices);
        Assert.True(d[0].Drive); Assert.True(d[1].Drive);
        Assert.False(d[2].Drive); Assert.False(d[3].Drive);
        Assert.Equal(0, d[2].MirrorOf); Assert.Equal(1, d[3].MirrorOf);
        Assert.Contains("port 0", d[0].Note);
    }

    [Fact]
    public void One_port_per_adapter_even_when_a_stick_reads_garbage_on_the_winning_port()
    {
        // Port 1 wins 2:1 on plausibility; stick 0x75 reads 0 LEDs there but is still driven via port 1 (layout re-read later),
        // never via port 0 — that would mix host ports on one bus.
        var devices = new[] { Ram(0, 0, 0x71, 300), Ram(1, 0, 0x73, 257), Ram(2, 0, 0x75, 8), Ram(3, 1, 0x71, 8), Ram(4, 1, 0x73, 8), Ram(5, 1, 0x75, 0) };
        var d = SmbusMirrorResolver.Resolve(devices);
        Assert.All(new[] { 0, 1, 2 }, i => Assert.False(d[i].Drive));
        Assert.All(new[] { 3, 4, 5 }, i => Assert.True(d[i].Drive));
    }

    [Fact]
    public void Same_port_twice_is_two_real_devices()
    {
        var devices = new[] { Ram(0, 1, 0x71, 8), Ram(1, 1, 0x73, 8) };
        var d = SmbusMirrorResolver.Resolve(devices);
        Assert.All(d, x => Assert.True(x.Drive));
        Assert.Empty(SmbusMirrorResolver.Ports(Array.Empty<SmbusDeviceSketch>()));
        Assert.Equal(new[] { 1 }, SmbusMirrorResolver.Ports(devices));
    }

    [Fact]
    public void Control_report_explains_mirrors_and_empty_ram()
    {
        var devices = new List<RgbDeviceInfo>
        {
            new(0, "ENE DRAM", "Dram", 8, true) { IsControllable = true, ControlLabel = "Per-LED · Direct" },
            new(1, "ENE DRAM", "Dram", 8, true) { IsControllable = false, IsMirror = true, MirrorOf = 0 },
            new(2, "ENE DRAM", "Dram", 0, true) { IsControllable = false },
        };
        string report = RgbControlViewModel.BuildControlReport(devices, "1 per-LED, 1 SMBus mirror(s) skipped", false);
        Assert.StartsWith("1 of 3 device(s) under FANZi control", report);
        Assert.Contains("another SMBus port", report);
        Assert.Contains("0 LEDs", report);
        Assert.DoesNotContain("effects-only in OpenRGB", report.Split('\n')[1]);
    }

    [Fact]
    public void Mirror_control_kind_is_not_controllable_and_has_a_label()
    {
        var c = RgbDeviceControl.MirrorOf(3, "Mirror of the same stick");
        Assert.Equal(RgbControlKind.Mirror, c.Kind);
        Assert.False(c.IsControllable);
        Assert.Equal(3, c.MirrorIndex);
        Assert.Equal("Mirror of the same stick", c.Label);
        Assert.True(new RgbDeviceControl(RgbControlKind.PerLed, 0, "Direct", 0).IsControllable);
        Assert.Equal("Mirror — not driven", new RgbDeviceControl(RgbControlKind.Mirror, -1, "", 0).Label);
    }
}
