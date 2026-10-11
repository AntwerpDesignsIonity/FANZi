using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.Tests;

/// <summary>v4 RGB studio logic: mood engine, colour harmony, AI director, device categories, deep scan.</summary>
public sealed class RgbStudioTests
{
    // ── Mood engine ──────────────────────────────────────────────────────────

    [Fact]
    public void Mood_KnownScene_PicksItsEffectAndColours()
    {
        var r = RgbMoodEngine.Generate("calm ocean");
        Assert.Contains("ocean", r.Explanation);
        Assert.True(r.Primary.B > r.Primary.R, "ocean is blue");
    }

    [Fact]
    public void Mood_Modifiers_ChangeSpeedAndBrightness()
    {
        var normal = RgbMoodEngine.Generate("cyberpunk");
        var fast = RgbMoodEngine.Generate("fast cyberpunk");
        var dim = RgbMoodEngine.Generate("dim cyberpunk");
        Assert.True(fast.Speed > normal.Speed);
        Assert.True(dim.Brightness <= 0.4);
    }

    [Fact]
    public void Mood_ColourWords_RecolourTheScene()
    {
        var r = RgbMoodEngine.Generate("red galaxy");
        Assert.Equal(RgbColor.Red, r.Primary);
        Assert.Contains("galaxy", r.Explanation);
    }

    [Fact]
    public void Mood_MultiWordScene_SouthAfrica()
    {
        var r = RgbMoodEngine.Generate("South Africa");
        Assert.Contains("South Africa", r.Explanation);
        Assert.Equal(new RgbColor(0, 122, 77), r.Primary);
    }

    [Fact]
    public void Mood_UnknownWords_AreStableAndUnique()
    {
        var a1 = RgbMoodEngine.Generate("zorblax quintessence");
        var a2 = RgbMoodEngine.Generate("zorblax quintessence");
        var b = RgbMoodEngine.Generate("flurbo widget");
        Assert.Equal(a1.Primary, a2.Primary);
        Assert.NotEqual(a1.Primary, b.Primary);
    }

    [Fact]
    public void Mood_Static_WordHoldsStatic()
        => Assert.Equal(RgbEffectType.Static, RgbMoodEngine.Generate("static purple").Effect);

    [Fact]
    public void Mood_Empty_GivesHelp()
        => Assert.Contains("Type a mood", RgbMoodEngine.Generate("  ").Explanation);

    // ── Harmony ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RgbColorHarmony.Rule.Complementary, 180)]
    [InlineData(RgbColorHarmony.Rule.Triadic, 120)]
    [InlineData(RgbColorHarmony.Rule.Analogous, 30)]
    public void Harmony_RotatesHue(RgbColorHarmony.Rule rule, double degrees)
    {
        var red = new RgbColor(255, 0, 0);
        var (h, _, _) = RgbColorHarmony.ToHsv(RgbColorHarmony.Partner(red, rule));
        Assert.InRange(h, degrees - 3, degrees + 3);
    }

    [Fact]
    public void Harmony_GreyGetsASaturatedPartner()
    {
        var (_, s, _) = RgbColorHarmony.ToHsv(RgbColorHarmony.Partner(new RgbColor(128, 128, 128), RgbColorHarmony.Rule.Complementary));
        Assert.True(s > 0.5);
    }

    // ── AI director ──────────────────────────────────────────────────────────

    private static RgbAiInputs In(double cpu, double gpu = 50, double load = 10, bool game = false, double hour = 14)
        => new(cpu, gpu, load, game, game ? "Game.exe" : null, TimeSpan.FromHours(hour));

    [Fact]
    public void Director_AllOff_ReturnsNull()
    {
        var d = new RgbAiDirector { ThermalGuardian = false };
        Assert.Null(d.Decide(In(60), RgbColor.Blue, RgbColor.Cyan));
    }

    [Fact]
    public void Director_Guardian_LatchesWithHysteresis()
    {
        var d = new RgbAiDirector { ThermalGuardian = true };
        Assert.Equal(RgbAiMode.Critical, d.Decide(In(95), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        // Just under the trigger: still latched (no flicker).
        Assert.Equal(RgbAiMode.Critical, d.Decide(In(90), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        // Cooled by more than the hysteresis: released.
        Assert.Null(d.Decide(In(80), RgbColor.Blue, RgbColor.Cyan));
        Assert.False(d.GuardianActive);
    }

    [Fact]
    public void Director_SmartAmbient_FollowsState()
    {
        var d = new RgbAiDirector { SmartAmbient = true, ThermalGuardian = false };
        Assert.Equal(RgbAiMode.Idle, d.Decide(In(50, load: 5), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Working, d.Decide(In(60, load: 70), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Gaming, d.Decide(In(70, game: true), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Hot, d.Decide(In(84), RgbColor.Blue, RgbColor.Cyan)!.Mode);
        Assert.Equal(RgbAiMode.Night, d.Decide(In(50, hour: 23), RgbColor.Blue, RgbColor.Cyan)!.Mode);
    }

    [Fact]
    public void Director_NightDimming_CapsBrightness_ButNotAlerts()
    {
        var d = new RgbAiDirector { NightDimming = true, ThermalGuardian = true };
        var night = d.Decide(In(50, hour: 1), RgbColor.Blue, RgbColor.Cyan)!;
        Assert.Equal(RgbAiDirector.NightBrightnessCap, night.BrightnessCap);
        Assert.Null(d.Decide(In(50, hour: 12), RgbColor.Blue, RgbColor.Cyan));
        var alert = d.Decide(In(99, hour: 1), RgbColor.Blue, RgbColor.Cyan)!;
        Assert.Equal(1.0, alert.BrightnessCap);
    }

    [Fact]
    public void Director_Suggest_UsesState()
    {
        Assert.Equal(RgbThemePreset.TempReactive.Name, RgbAiDirector.Suggest(In(88)).Preset.Name);
        Assert.Equal(RgbThemePreset.Performance.Name, RgbAiDirector.Suggest(In(60, game: true)).Preset.Name);
        Assert.Equal(RgbThemePreset.Ocean.Name, RgbAiDirector.Suggest(In(50, hour: 23.5)).Preset.Name);
    }

    // ── Device categories ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Dram", RgbDeviceCategory.Ram)]
    [InlineData("Gpu", RgbDeviceCategory.Gpu)]
    [InlineData("20", RgbDeviceCategory.Monitor)]
    [InlineData("0", RgbDeviceCategory.Motherboard)]
    [InlineData("LedStrip", RgbDeviceCategory.LedStrip)]
    [InlineData("", RgbDeviceCategory.Other)]
    public void Device_CategoryFromOpenRgbType(string type, RgbDeviceCategory expected)
        => Assert.Equal(expected, RgbDeviceInfo.CategoryFor(type));

    [Fact]
    public void Device_SlotNumbersDuplicateNames()
    {
        var d = new RgbDeviceInfo(0, "ENE DRAM", "Dram", 8, true) { Slot = 2 };
        Assert.Equal("ENE DRAM · stick 2", d.DisplayName);
        Assert.Equal("RAM", d.FriendlyType);
    }

    // ── Deep scan analysis ───────────────────────────────────────────────────

    private static SystemRgbInventory System(int dimms, bool pawnIo = true, params (string, string, string)[] usb)
        => new("MAXSUN MS-iCraft X870M",
            Enumerable.Range(1, dimms).Select(i => $"DIMM {i}: Patriot 6000 Series (16 GB)").ToList(),
            new[] { "NVIDIA GeForce RTX 3080" }, usb, pawnIo);

    private static readonly RgbDeviceInfo[] UserRig =
    [
        new(0, "ENE DRAM", "Dram", 8, true) { Location = "I2C: PawnIO SMBus PIIX4 0, address 0x71" },
        new(1, "ENE DRAM", "Dram", 8, true) { Location = "I2C: PawnIO SMBus PIIX4 0, address 0x73" },
        new(2, "ZOTAC GAMING GeForce RTX 3080 AMP Holo LHR", "Gpu", 1, true) { ControlLabel = "Single colour · Static" },
        new(3, "ViewSonic Monitor XG270QG", "20", 2, true) { Location = @"\\?\HID#VID_0416&PID_5020&MI_00#a" },
    ];

    [Fact]
    public void DeepScan_RamFound_FansExplained()
    {
        var findings = RgbHardwareInventory.Analyse(UserRig, System(2), fanHeadersWithRpm: 3, openRgbConnected: true);

        var ram = Assert.Single(findings, f => f.Category == "RAM");
        Assert.Equal(DeepScanStatus.Ok, ram.Status);
        Assert.Contains("2 RGB RAM controllers", ram.Title);

        var fans = Assert.Single(findings, f => f.Category == "Fans");
        Assert.Equal(DeepScanStatus.Missing, fans.Status);
        Assert.Contains("3 fan headers report RPM", fans.Detail);
        Assert.Contains("MAXSUN", fans.Detail);

        Assert.Contains(findings, f => f.Category == "GPU" && f.Status == DeepScanStatus.Ok);
        Assert.Contains(findings, f => f.Category == "Monitor");
    }

    [Fact]
    public void DeepScan_NoRgbRam_PointsAtPawnIo()
    {
        var findings = RgbHardwareInventory.Analyse(UserRig.Skip(2).ToArray(), System(2, pawnIo: false), 0, true);
        Assert.Contains(findings, f => f.Category == "Driver" && f.Status == DeepScanStatus.Warning);
        var ram = Assert.Single(findings, f => f.Category == "RAM");
        Assert.Equal(DeepScanStatus.Missing, ram.Status);
        Assert.Contains("PawnIO", ram.Detail);
    }

    [Fact]
    public void DeepScan_ListsUnclaimedRgbUsbDevices_Only()
    {
        var findings = RgbHardwareInventory.Analyse(UserRig, System(2, true,
            ("0416:5020", "Winbond / Nuvoton", "ViewSonic"),
            ("1A86:E2E3", "WCH (generic ARGB controller)", "USB Input Device")), 3, true);

        Assert.DoesNotContain(findings, f => f.Title.Contains("0416:5020"));
        Assert.Contains(findings, f => f.Title.Contains("1A86:E2E3"));
    }

    [Fact]
    public void DeepScan_Offline_SaysSo()
        => Assert.Contains(RgbHardwareInventory.Analyse(Array.Empty<RgbDeviceInfo>(), System(1), 0, false),
            f => f.Status == DeepScanStatus.Missing && f.Title.Contains("not connected"));

    // ── Overlay accents ──────────────────────────────────────────────────────

    [Fact]
    public void OverlayAccent_ByName_FallsBackToDefault()
    {
        Assert.Equal("Ember", OverlayAccent.ByName("ember").Name);
        Assert.Equal("Ionity Blue", OverlayAccent.ByName("nope").Name);
        Assert.True(OverlayAccent.ByName(OverlayAccent.RgbSyncName).IsRgbSync);
    }
}
