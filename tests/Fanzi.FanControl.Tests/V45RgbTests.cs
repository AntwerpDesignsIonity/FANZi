using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

/// <summary>4.5: RGB coexistence with vendor apps, zone segments, AEDi director hysteresis.</summary>
public class V45RgbTests
{
    // ── Coexistence ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ASUS Aura (LightingService)")]
    [InlineData("ASUS Aura")]
    [InlineData("MSI Mystic Light")]
    [InlineData("Gigabyte RGB Fusion")]
    [InlineData("ASRock Polychrome")]
    [InlineData("Corsair iCUE")]
    [InlineData("SignalRGB")]
    [InlineData("G.SKILL Trident Z Lighting")]
    public void Automatic_leaves_smbus_to_a_running_vendor_app(string product)
    {
        var d = RgbCoexistence.Decide(SmbusPolicy.Auto, new[] { product });
        Assert.True(d.HandsOff);
        Assert.False(d.Risky);
        Assert.Contains(product, d.Title);
        Assert.Contains(product, d.Owners);
    }

    [Theory]
    [InlineData("SteelSeries GG")]
    [InlineData("Razer Synapse")]
    [InlineData("Logitech G HUB")]
    [InlineData("NZXT CAM")]
    // Vendor shells light nothing until their lighting part runs — e.g. Gigabyte Control Center installed for a GPU on a MAXSUN board.
    [InlineData("Gigabyte Control Center")]
    [InlineData("ASUS Armoury Crate")]
    [InlineData("MSI Center")]
    [InlineData("MAXSUN View")]
    public void Usb_only_apps_and_vendor_shells_do_not_take_the_ram_away(string product)
    {
        var d = RgbCoexistence.Decide(SmbusPolicy.Auto, new[] { product });
        Assert.False(d.HandsOff);
        Assert.Empty(d.Owners);
    }

    [Fact]
    public void Policies()
    {
        Assert.False(RgbCoexistence.Decide(SmbusPolicy.Auto, Array.Empty<string>()).HandsOff);
        Assert.True(RgbCoexistence.Decide(SmbusPolicy.Vendor, Array.Empty<string>()).HandsOff);

        var forced = RgbCoexistence.Decide(SmbusPolicy.Fanzi, new[] { "MSI Mystic Light" });
        Assert.False(forced.HandsOff);
        Assert.True(forced.Risky);
        Assert.False(RgbCoexistence.Decide(SmbusPolicy.Fanzi, Array.Empty<string>()).Risky);

        // MAXSUN Sync already is the single writer for the RAM.
        Assert.False(RgbCoexistence.Decide(SmbusPolicy.Auto, new[] { "Corsair iCUE" }, maxsunOwnsRam: true).HandsOff);

        Assert.Equal(SmbusPolicy.Vendor, RgbCoexistence.ParsePolicy("Vendor"));
        Assert.Equal(SmbusPolicy.Fanzi, RgbCoexistence.ParsePolicy("Fanzi"));
        Assert.Equal(SmbusPolicy.Auto, RgbCoexistence.ParsePolicy("whatever"));
    }

    [Fact]
    public void Every_detector_product_that_owns_smbus_is_a_known_conflict_name()
    {
        // A typo in the owner list would silently disable the protection for that vendor.
        var known = RgbConflictDetector.KnownProcesses.Values.Concat(RgbConflictDetector.KnownServices.Values)
            .Append("another OpenRGB instance").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string owner in RgbCoexistence.SmbusOwners)
            Assert.Contains(owner, known);
    }

    [Fact]
    public void Shared_ene_hal_processes_are_not_mistaken_for_aura()
    {
        // MAXSUN Sync runs the same ENE HAL executables Aura does.
        Assert.Empty(RgbConflictDetector.Classify(new[] { ("Aac3572DramHal_x86", 1), ("Aac3572MbHal_x86", 2), ("MaxsunSyncService", 3) }));
        Assert.Equal("MSI Mystic Light", Assert.Single(RgbConflictDetector.Classify(new[] { ("LEDKeeper2", 1) })).Product);
        Assert.Equal("Gigabyte RGB Fusion", Assert.Single(RgbConflictDetector.Classify(new[] { ("SelLedV2", 1) })).Product);
    }

    [Fact]
    public void Banner_is_calm_when_sharing_and_red_when_blocked()
    {
        var crate = new RgbConflictDetector.Conflict("ASUS Aura", new[] { 1 });
        var razer = new RgbConflictDetector.Conflict("Razer Synapse", new[] { 2 });

        var sharing = RgbControlViewModel.BuildCoexistenceBanner(new[] { crate }, RgbCoexistence.Decide(SmbusPolicy.Auto, new[] { crate.Product }));
        Assert.True(sharing.Sharing);
        Assert.Contains("keeps your RAM", sharing.Title);

        var mixed = RgbControlViewModel.BuildCoexistenceBanner(new[] { crate, razer }, RgbCoexistence.Decide(SmbusPolicy.Auto, new[] { crate.Product, razer.Product }));
        Assert.True(mixed.Sharing);
        Assert.Contains("Razer Synapse", mixed.Message);

        var blocked = RgbControlViewModel.BuildCoexistenceBanner(new[] { razer }, RgbCoexistence.Decide(SmbusPolicy.Auto, new[] { razer.Product }));
        Assert.False(blocked.Sharing);
        Assert.Contains("controlling your lights", blocked.Title);

        var risky = RgbControlViewModel.BuildCoexistenceBanner(new[] { crate }, RgbCoexistence.Decide(SmbusPolicy.Fanzi, new[] { crate.Product }));
        Assert.False(risky.Sharing);
        Assert.Contains("also driving", risky.Title);

        Assert.Equal(string.Empty, RgbControlViewModel.BuildCoexistenceBanner(Array.Empty<RgbConflictDetector.Conflict>(), RgbCoexistence.Decide(SmbusPolicy.Auto, Array.Empty<string>())).Title);
    }

    [Theory]
    [InlineData("I2C: PawnIO SMBus PIIX4 1, address 0x73", false, true)]
    [InlineData("I2C: Nvidia NvAPI I2C on GPU 0, address 0x49", false, false)]
    [InlineData("I2C: AMD ADL, address 0x29", false, false)]
    [InlineData(@"HID: \\?\HID#VID_1038&PID_1618", false, false)]
    [InlineData(@"HID: \\?\HID#VID_1B1C", true, true)] // DRAM type always counts
    [InlineData(null, false, false)]
    public void Only_the_board_smbus_is_left_alone(string? location, bool isDram, bool expected)
        => Assert.Equal(expected, OpenRgbService.IsBoardSmbusLocation(location, isDram));

    // ── OpenRGB detector gating ──────────────────────────────────────────────

    [Fact]
    public void Detectors_are_switched_off_in_place_and_back_on()
    {
        const string json = """
        {
          "Detectors": {
            "detectors": {
              "ENE SMBus DRAM": true,
              "SteelSeries Apex 7 TKL": true,
              "Corsair DRAM": true
            }
          },
          "Server": { "port": 6742 }
        }
        """;
        string off = OpenRgbConfigWriter.ApplySmbusDetectors(json, RgbCoexistence.SmbusDetectors, enabled: false);
        Assert.Contains("\"ENE SMBus DRAM\": false", off);
        Assert.Contains("\"Corsair DRAM\": false", off);
        Assert.Contains("\"SteelSeries Apex 7 TKL\": true", off);
        Assert.Contains("\"Kingston Fury DDR5 DRAM\": false", off); // missing entries are added
        Assert.Contains("\"port\": 6742", off);
        using (var doc = System.Text.Json.JsonDocument.Parse(off)) // still valid JSON
            Assert.False(doc.RootElement.GetProperty("Detectors").GetProperty("detectors").GetProperty("ASUS Aura SMBus Motherboard").GetBoolean());

        string on = OpenRgbConfigWriter.ApplySmbusDetectors(off, RgbCoexistence.SmbusDetectors, enabled: true);
        Assert.Contains("\"ENE SMBus DRAM\": true", on);
        Assert.DoesNotContain(": false", on);
        Assert.Contains("\"SteelSeries Apex 7 TKL\": true", on);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\n  \"Server\": { \"port\": 6742 }\n}")]
    [InlineData("{ \"Detectors\": { \"detectors\": {} } }")]
    [InlineData("")]
    public void Detector_section_is_created_when_missing(string json)
    {
        string off = OpenRgbConfigWriter.ApplySmbusDetectors(json, RgbCoexistence.SmbusDetectors, enabled: false);
        using var doc = System.Text.Json.JsonDocument.Parse(off);
        var detectors = doc.RootElement.GetProperty("Detectors").GetProperty("detectors");
        foreach (string name in RgbCoexistence.SmbusDetectors)
            Assert.False(detectors.GetProperty(name).GetBoolean());
        // Turning detectors on never adds entries (absent = OpenRGB's default).
        Assert.Equal(json, OpenRgbConfigWriter.ApplySmbusDetectors(json, RgbCoexistence.SmbusDetectors, enabled: true));
    }

    // ── Zone segments ────────────────────────────────────────────────────────

    private static readonly RgbColor A = new(200, 0, 0), B = new(0, 0, 200);

    [Fact]
    public void Solid_and_disabled()
    {
        Assert.All(ZoneSegmentRenderer.Render(8, A, B, ZoneRender.Default, 0), c => Assert.Equal(A, c));
        Assert.All(ZoneSegmentRenderer.Render(8, A, B, ZoneRender.Default with { Enabled = false }, 0), c => Assert.Equal(RgbColor.Black, c));
        Assert.Empty(ZoneSegmentRenderer.Render(0, A, B, ZoneRender.Default, 0));
        Assert.True(ZoneRender.Default.IsPlain);
        Assert.False((ZoneRender.Default with { Pattern = ZonePattern.Chase }).IsPlain);
        Assert.False((ZoneRender.Default with { Enabled = false }).IsPlain);
    }

    [Fact]
    public void Alternate_splits_into_equal_blocks()
    {
        var leds = ZoneSegmentRenderer.Render(8, A, B, ZoneRender.Default with { Segments = 4, Pattern = ZonePattern.Alternate }, 0);
        Assert.Equal(new[] { A, A, B, B, A, A, B, B }, leds);
        var reversed = ZoneSegmentRenderer.Render(8, A, B, ZoneRender.Default with { Segments = 4, Pattern = ZonePattern.Alternate, Reverse = true }, 0);
        Assert.Equal(new[] { B, B, A, A, B, B, A, A }, reversed);
    }

    [Fact]
    public void Gradient_runs_A_to_B_and_mirror_returns_to_A()
    {
        var g = ZoneSegmentRenderer.Render(5, A, B, ZoneRender.Default with { Pattern = ZonePattern.Gradient }, 0);
        Assert.Equal(A, g[0]);
        Assert.Equal(B, g[^1]);
        Assert.True(g[2].R < A.R && g[2].B > A.B);

        var stepped = ZoneSegmentRenderer.Render(8, A, B, ZoneRender.Default with { Pattern = ZonePattern.Gradient, Segments = 2 }, 0);
        Assert.Equal(new[] { A, A, A, A, B, B, B, B }, stepped);

        var m = ZoneSegmentRenderer.Render(5, A, B, ZoneRender.Default with { Pattern = ZonePattern.Mirror }, 0);
        Assert.Equal(A, m[0]); Assert.Equal(B, m[2]); Assert.Equal(A, m[4]);
    }

    [Fact]
    public void Chase_moves_one_lit_block()
    {
        var s = ZoneRender.Default with { Segments = 4, Pattern = ZonePattern.Chase };
        var t0 = ZoneSegmentRenderer.Render(8, A, B, s, 0);
        var t1 = ZoneSegmentRenderer.Render(8, A, B, s, 0.36);
        Assert.Equal(2, t0.Count(c => c == A));
        Assert.Equal(A, t0[0]); Assert.Equal(A, t0[1]);
        Assert.Equal(A, t1[2]); Assert.Equal(A, t1[3]);
        Assert.NotEqual(A, t1[0]);
        // Wraps around.
        Assert.Equal(t0, ZoneSegmentRenderer.Render(8, A, B, s, 0.35 * 4 + 0.01));
    }

    [Fact]
    public void Segments_never_exceed_leds_or_eight()
    {
        Assert.Equal(1, ZoneSegmentRenderer.ClampSegments(0, 8));
        Assert.Equal(8, ZoneSegmentRenderer.ClampSegments(20, 60));
        Assert.Equal(3, ZoneSegmentRenderer.ClampSegments(8, 3));
        Assert.Equal(1, ZoneSegmentRenderer.ClampSegments(4, 0));
        // 3 LEDs, alternate: no out-of-range, every LED coloured.
        var leds = ZoneSegmentRenderer.Render(3, A, B, ZoneRender.Default with { Segments = 8, Pattern = ZonePattern.Alternate }, 0);
        Assert.Equal(new[] { A, B, A }, leds);
    }

    [Fact]
    public void Device_frame_concatenates_zones_and_stays_null_when_plain()
    {
        var plain = new[] { (4, A, B, ZoneRender.Default), (4, B, A, ZoneRender.Default) };
        Assert.Null(RgbControlViewModel.BuildDeviceLeds(plain, 0));

        var mixed = new[]
        {
            (2, A, B, ZoneRender.Default),
            (0, A, B, ZoneRender.Default),
            (4, A, B, ZoneRender.Default with { Segments = 2, Pattern = ZonePattern.Alternate }),
            (2, B, A, ZoneRender.Default with { Enabled = false }),
        };
        var leds = RgbControlViewModel.BuildDeviceLeds(mixed, 0)!;
        Assert.Equal(new[] { A, A, A, A, B, B, RgbColor.Black, RgbColor.Black }, leds);
    }

    [Fact]
    public void Zone_row_apply_mode_holds_edits_until_commit_and_reverts()
    {
        var zone = new ZoneEffectViewModel(new RgbZoneInfo(0, 0, "DIMM", 8), "ENE DRAM");
        bool live = false;
        int edits = 0;
        zone.IsLive = () => live;
        zone.Edited = _ => edits++;

        zone.Segments = 4;
        zone.PatternIndex = (int)ZonePattern.Alternate;
        Assert.True(zone.HasPending);
        Assert.Equal(1, zone.AppliedRender.Segments);          // lights unchanged
        Assert.Equal(2, edits);

        zone.Revert();
        Assert.False(zone.HasPending);
        Assert.Equal(1, zone.Segments);

        zone.Segments = 4;
        zone.Commit();
        Assert.Equal(4, zone.AppliedRender.Segments);
        Assert.False(zone.HasPending);

        live = true;
        zone.ZoneBrightness = 40;
        Assert.Equal(0.4, zone.AppliedRender.Brightness, 3);   // instant
        Assert.False(zone.HasPending);
    }

    [Fact]
    public void Zone_settings_round_trip_and_clamp()
    {
        var zone = new ZoneEffectViewModel(new RgbZoneInfo(1, 2, "Ring", 3), "Hub");
        Assert.True(zone.IsDefault);
        zone.Load(new RgbZoneSettings { Segments = 8, Pattern = "Chase", Direction = "Reverse", Brightness = 55, Speed = 9, PrimaryHex = "#FF0000", SecondaryHex = "#00FF00", Effect = "Pulse", Enabled = false });
        Assert.Equal(3, zone.Segments);                         // 3 LEDs
        Assert.Equal(3.0, zone.ZoneSpeed);
        Assert.Equal(ZonePattern.Chase, zone.AppliedRender.Pattern);
        Assert.True(zone.AppliedRender.Reverse);
        Assert.False(zone.AppliedRender.Enabled);
        Assert.False(zone.IsDefault);

        var saved = zone.ToSettings();
        Assert.Equal("Chase", saved.Pattern);
        Assert.Equal("Reverse", saved.Direction);
        Assert.Equal("#FF0000", saved.PrimaryHex);
        Assert.Equal("#00FF00", saved.SecondaryHex);
        Assert.Equal("Pulse", saved.Effect);
        Assert.Equal("Hub|1|Ring", zone.Key);
        Assert.Equal(RgbColor.Black, zone.Compute(0, 1, 1, null, null, null, null, 1)); // switched off
    }

    // ── AEDi director ────────────────────────────────────────────────────────

    private static RgbAiInputs In(double cpu, double load = 10, bool game = false, double hour = 14, double? now = null)
        => new(cpu, 50, load, game, game ? "Game.exe" : null, TimeSpan.FromHours(hour), now);

    [Fact]
    public void Hot_scene_releases_only_below_the_hysteresis()
    {
        var d = new RgbAiDirector { SmartAmbient = true, ThermalGuardian = false };
        Assert.Equal(RgbAiMode.Hot, d.Decide(In(81), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Hot, d.Decide(In(78), RgbColor.Blue, RgbColor.Cyan)!.Mode);   // 80 − 4 not reached
        Assert.Equal(RgbAiMode.Idle, d.Decide(In(75), RgbColor.Blue, RgbColor.Cyan)!.Mode);
    }

    [Fact]
    public void Working_scene_does_not_flap_around_the_load_threshold()
    {
        var d = new RgbAiDirector { SmartAmbient = true, ThermalGuardian = false };
        Assert.Equal(RgbAiMode.Working, d.Decide(In(50, load: 40), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Working, d.Decide(In(50, load: 30), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Idle, d.Decide(In(50, load: 20), RgbColor.Blue, RgbColor.Cyan)!.Mode);
    }

    [Fact]
    public void Scene_changes_wait_for_the_dwell_time()
    {
        var d = new RgbAiDirector { SmartAmbient = true, ThermalGuardian = false };
        Assert.Equal(RgbAiMode.Idle, d.Decide(In(50, now: 0), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Idle, d.Decide(In(50, load: 60, now: 1), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Idle, d.Decide(In(50, load: 60, now: 1 + RgbAiDirector.DwellSeconds - 0.5), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Working, d.Decide(In(50, load: 60, now: 1 + RgbAiDirector.DwellSeconds), RgbColor.Blue, RgbColor.Cyan)!.Mode);

        // A blip back to idle that does not last is ignored.
        Assert.Equal(RgbAiMode.Working, d.Decide(In(50, load: 5, now: 20), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Working, d.Decide(In(50, load: 60, now: 22), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Working, d.Decide(In(50, load: 5, now: 23), RgbColor.Blue, RgbColor.Cyan)!.Mode);

        // Heat shows sooner than other changes.
        Assert.Equal(RgbAiMode.Working, d.Decide(In(85, load: 60, now: 30), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Hot, d.Decide(In(85, load: 60, now: 30 + RgbAiDirector.HotDwellSeconds), RgbColor.Blue, RgbColor.Cyan)!.Mode);
    }

    [Fact]
    public void A_short_alt_tab_does_not_drop_the_game_scene()
    {
        var d = new RgbAiDirector { SmartAmbient = true, ThermalGuardian = false };
        d.Decide(In(60, game: true, now: 0), RgbColor.Blue, RgbColor.Cyan);
        Assert.Equal(RgbAiMode.Gaming, d.Decide(In(60, game: true, now: 1), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Gaming, d.Decide(In(60, now: 2), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Gaming, d.Decide(In(60, now: 2 + RgbAiDirector.LeaveGameDwellSeconds - 1), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Idle, d.Decide(In(60, now: 2 + RgbAiDirector.LeaveGameDwellSeconds), RgbColor.Blue, RgbColor.Cyan)!.Mode);
    }

    [Fact]
    public void Thermal_alert_ignores_the_dwell()
    {
        var d = new RgbAiDirector { SmartAmbient = true, ThermalGuardian = true };
        d.Decide(In(50, now: 0), RgbColor.Blue, RgbColor.Cyan);
        Assert.Equal(RgbAiMode.Critical, d.Decide(In(95, now: 0.1), RgbColor.Blue, RgbColor.Cyan)!.Mode);
    }

    [Fact]
    public void Night_cap_fades_in_and_out()
    {
        Assert.Equal(1.0, RgbAiDirector.NightCapAt(TimeSpan.FromHours(14)));
        Assert.Equal(RgbAiDirector.NightBrightnessCap, RgbAiDirector.NightCapAt(TimeSpan.FromHours(23)));
        Assert.Equal(RgbAiDirector.NightBrightnessCap, RgbAiDirector.NightCapAt(TimeSpan.FromHours(3)));
        Assert.Equal(1.0, RgbAiDirector.NightCapAt(TimeSpan.FromHours(21)), 3);              // 60 min before: not yet
        double dusk = RgbAiDirector.NightCapAt(TimeSpan.FromHours(21.75));                    // 15 min before
        Assert.InRange(dusk, RgbAiDirector.NightBrightnessCap + 0.01, 0.99);
        double dawn = RgbAiDirector.NightCapAt(TimeSpan.FromHours(6.75));                     // 15 min after
        Assert.InRange(dawn, RgbAiDirector.NightBrightnessCap + 0.01, 0.99);
        Assert.True(RgbAiDirector.NightCapAt(TimeSpan.FromHours(21.9)) < dusk);               // monotonic towards night
        Assert.Equal(1.0, RgbAiDirector.NightCapAt(TimeSpan.FromHours(7.5)), 3);
    }

    [Fact]
    public void SmoothStep_is_clamped_and_monotonic()
    {
        Assert.Equal(0, RgbControlViewModel.SmoothStep(-1));
        Assert.Equal(1, RgbControlViewModel.SmoothStep(2));
        Assert.Equal(0.5, RgbControlViewModel.SmoothStep(0.5), 6);
        Assert.True(RgbControlViewModel.SmoothStep(0.25) < RgbControlViewModel.SmoothStep(0.75));
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    [Fact]
    public void Upgrade_keeps_the_advanced_rgb_view_and_new_installs_start_simple()
    {
        var upgraded = new AppSettings { SettingsVersion = 6 };
        upgraded.Migrate();
        Assert.False(upgraded.RgbSimpleMode);
        Assert.Equal(AppSettings.CurrentVersion, upgraded.SettingsVersion);

        var fresh = new AppSettings();
        Assert.True(fresh.RgbSimpleMode);
        Assert.True(fresh.RgbLiveApply);
        Assert.True(fresh.PumpProtectionEnabled);
        Assert.Equal("Auto", fresh.RgbSmbusPolicy);

        var odd = new AppSettings { RgbSmbusPolicy = "x", PumpMaxPercent = 10, SettingsVersion = AppSettings.CurrentVersion, RgbSimpleMode = true };
        odd.Normalize();
        Assert.Equal("Auto", odd.RgbSmbusPolicy);
        Assert.Equal(60, odd.PumpMaxPercent);
        Assert.True(odd.RgbSimpleMode);
    }
}
