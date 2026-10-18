using System;
using System.Text.Json.Nodes;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.Tests;

/// <summary>v3 RGB coverage: control planning, OpenRGB config compatibility, conflicts, PawnIO.</summary>
public class RgbHardwareCoverageTests
{
    private static RgbModeDescriptor M(int i, string name, RgbModeColorMode cm, uint min = 0, uint max = 0) =>
        new(i, name, cm, min, max);

    // Modes exactly as OpenRGB reports a ZOTAC GAMING GeForce RTX 3080 AMP Holo LHR (probed live).
    private static readonly RgbModeDescriptor[] ZotacRtx3080 =
    {
        M(0, "Static", RgbModeColorMode.ModeSpecific, 1, 1),
        M(1, "Breath", RgbModeColorMode.ModeSpecific, 1, 1),
        M(2, "Fade", RgbModeColorMode.None),
        M(3, "Wink", RgbModeColorMode.ModeSpecific, 1, 1),
        M(4, "Flash", RgbModeColorMode.None),
        M(5, "Random", RgbModeColorMode.ModeSpecific, 1, 1),
        M(6, "Slide", RgbModeColorMode.ModeSpecific, 1, 1),
        M(7, "Rainbow", RgbModeColorMode.None),
        M(13, "Duet", RgbModeColorMode.ModeSpecific, 2, 2),
        M(14, "Duet (circuit)", RgbModeColorMode.ModeSpecific, 2, 2),
        M(15, "Path", RgbModeColorMode.None),
    };

    // ViewSonic XG270QG monitor (probed live).
    private static readonly RgbModeDescriptor[] ViewSonicXg270 =
    {
        M(0, "Off", RgbModeColorMode.None),
        M(1, "Custom", RgbModeColorMode.PerLed, 0, 0),
        M(2, "Rainbow Wave", RgbModeColorMode.None),
        M(3, "Breathing", RgbModeColorMode.PerLed, 0, 0),
        M(4, "Waterfall", RgbModeColorMode.None),
        M(5, "Elite", RgbModeColorMode.PerLed, 0, 0),
    };

    [Fact]
    public void Zotac_gpu_is_driven_through_its_static_mode_colour()
    {
        var plan = RgbControlPlanner.Plan(ZotacRtx3080);

        Assert.Equal(RgbControlKind.ModeColor, plan.Kind);
        Assert.Equal(0, plan.ModeIndex);
        Assert.Equal("Static", plan.ModeName);
        Assert.Equal(1, plan.ModeColorCount);
        Assert.Equal("Single colour · Static", plan.Label);
    }

    [Fact]
    public void Monitor_uses_its_custom_per_led_mode()
    {
        var plan = RgbControlPlanner.Plan(ViewSonicXg270);

        Assert.Equal(RgbControlKind.PerLed, plan.Kind);
        Assert.Equal(1, plan.ModeIndex);
        Assert.Equal("Per-LED · Custom", plan.Label);
    }

    // ENE DRAM (Patriot DDR5 sticks on this PC) as OpenRGB reports them with PawnIO + Administrator.
    private static readonly RgbModeDescriptor[] EneDram =
    {
        M(0, "Direct", RgbModeColorMode.PerLed),
        M(1, "Off", RgbModeColorMode.None),
        M(2, "Static", RgbModeColorMode.PerLed),
        M(3, "Breathing", RgbModeColorMode.PerLed),
        M(5, "Spectrum Cycle", RgbModeColorMode.None),
    };

    [Fact]
    public void Ram_stick_uses_direct()
    {
        var plan = RgbControlPlanner.Plan(EneDram, ledCount: 8);
        Assert.Equal(RgbControlKind.PerLed, plan.Kind);
        Assert.Equal("Per-LED · Direct", plan.Label);
    }

    [Fact]
    public void Device_reporting_zero_leds_is_listed_but_never_written()
    {
        // Seen live: the second ENE DRAM stick reported 0 LEDs / 0 zones while SignalRGB held SMBus.
        // Writing an empty UpdateLeds threw and dropped the whole OpenRGB connection in a loop.
        var plan = RgbControlPlanner.Plan(EneDram, ledCount: 0);

        Assert.Equal(RgbControlKind.Unsupported, plan.Kind);
        Assert.Contains("No LEDs reported", plan.Label);
    }

    [Fact]
    public void Direct_beats_custom_and_static_for_per_led_devices()
    {
        var plan = RgbControlPlanner.Plan(new[]
        {
            M(0, "Static", RgbModeColorMode.PerLed),
            M(1, "Custom", RgbModeColorMode.PerLed),
            M(2, "Direct", RgbModeColorMode.PerLed),
        });

        Assert.Equal(RgbControlKind.PerLed, plan.Kind);
        Assert.Equal(2, plan.ModeIndex);
    }

    [Fact]
    public void Static_with_mode_specific_colours_is_not_treated_as_per_led()
    {
        // The pre-v3 bug: any mode *named* Static counted as direct, so GPUs never changed.
        var plan = RgbControlPlanner.Plan(new[]
        {
            M(0, "Rainbow", RgbModeColorMode.None),
            M(1, "Static", RgbModeColorMode.ModeSpecific, 1, 1),
        });

        Assert.Equal(RgbControlKind.ModeColor, plan.Kind);
        Assert.Equal(1, plan.ModeIndex);
    }

    [Fact]
    public void Without_static_the_mode_needing_fewest_colours_is_used()
    {
        var plan = RgbControlPlanner.Plan(new[]
        {
            M(0, "Duet", RgbModeColorMode.ModeSpecific, 2, 2),
            M(1, "Breath", RgbModeColorMode.ModeSpecific, 1, 4),
        });

        Assert.Equal(1, plan.ModeIndex);
        Assert.Equal(1, plan.ModeColorCount);
    }

    [Fact]
    public void Effects_only_and_empty_devices_are_unsupported()
    {
        Assert.Equal(RgbControlKind.Unsupported, RgbControlPlanner.Plan(new[] { M(0, "Rainbow", RgbModeColorMode.None) }).Kind);
        Assert.Equal(RgbControlKind.Unsupported, RgbControlPlanner.Plan(Array.Empty<RgbModeDescriptor>()).Kind);
    }

    [Fact]
    public void Average_rounds_per_channel()
    {
        var avg = RgbControlPlanner.Average(new (byte, byte, byte)[] { (255, 0, 10), (0, 255, 11) });
        Assert.Equal((128, 128, 11), (avg.R, avg.G, avg.B));
        Assert.Equal((0, 0, 0), ((int)RgbControlPlanner.Average(Array.Empty<(byte, byte, byte)>()).R, 0, 0));
    }

    // ── OpenRGB config ───────────────────────────────────────────────────────

    [Fact]
    public void ApplyServerSettings_enables_legacy_workaround_and_preserves_other_settings()
    {
        const string existing = """
        {
          "Detectors": { "detectors": { "ZOTAC 30 Series GPU": true } },
          "Server": { "all_controllers": false, "legacy_workaround": false, "default_port": 6742 }
        }
        """;

        var root = JsonNode.Parse(OpenRgbConfigWriter.ApplyServerSettings(existing, 6742))!;

        Assert.True(root["Server"]!["legacy_workaround"]!.GetValue<bool>());
        Assert.Equal("127.0.0.1", root["Server"]!["default_host"]!.GetValue<string>());
        Assert.False(root["Server"]!["all_controllers"]!.GetValue<bool>());
        Assert.True(root["Detectors"]!["detectors"]!["ZOTAC 30 Series GPU"]!.GetValue<bool>());
    }

    [Fact]
    public void ApplyServerSettings_creates_a_config_from_nothing()
    {
        var root = JsonNode.Parse(OpenRgbConfigWriter.ApplyServerSettings(null, 6800))!;
        Assert.True(root["Server"]!["legacy_workaround"]!.GetValue<bool>());
        Assert.Equal(6800, root["Server"]!["default_port"]!.GetValue<int>());
    }

    [Fact]
    public void Duplicate_detector_keys_still_produce_a_valid_compatible_config()
    {
        // Real OpenRGB configs contain duplicate detector names (seen on this PC). Whether the
        // parser keeps one copy or the text-patch fallback runs, the flag must end up on and the
        // detector setting must survive.
        const string existing = """
        { "Detectors": { "detectors": { "Gigabyte RTX 5080": true, "Gigabyte RTX 5080": true } },
          "Server": { "legacy_workaround": false } }
        """;

        string result = OpenRgbConfigWriter.ApplyServerSettings(existing, 6742);

        Assert.Matches("\"legacy_workaround\":\\s*true", result);
        Assert.DoesNotContain("\"legacy_workaround\": false", result);
        Assert.Contains("\"Gigabyte RTX 5080\"", result);
    }

    [Fact]
    public void PatchLegacyFlagText_reports_whether_anything_changed()
    {
        OpenRgbConfigWriter.PatchLegacyFlagText("{\"legacy_workaround\" :false}", out bool changed);
        Assert.True(changed);

        string same = OpenRgbConfigWriter.PatchLegacyFlagText("{\"legacy_workaround\": true}", out bool unchanged);
        Assert.False(unchanged);
        Assert.Equal("{\"legacy_workaround\": true}", same);
    }

    [Fact]
    public void Server_arguments_quote_the_private_config_folder()
    {
        Assert.Equal("--server --server-port 6742",
            OpenRgbServerManager.BuildServerArguments(6742, null));
        Assert.Equal("--server --server-port 6742 --config \"C:\\Users\\Johan PC\\AppData\\Local\\FANZI\\OpenRGB\\config\"",
            OpenRgbServerManager.BuildServerArguments(6742, @"C:\Users\Johan PC\AppData\Local\FANZI\OpenRGB\config\"));
    }

    // ── Conflicting software ─────────────────────────────────────────────────

    [Fact]
    public void Conflicts_are_grouped_per_product()
    {
        var conflicts = RgbConflictDetector.Classify(new[]
        {
            ("SignalRgb", 19596), ("SignalRgbLauncher", 20936), ("explorer", 100), ("iCUE", 7),
        });

        Assert.Equal(2, conflicts.Count);
        Assert.Equal("Corsair iCUE", conflicts[0].Product);
        Assert.Equal("SignalRGB", conflicts[1].Product);
        Assert.Equal(new[] { 19596, 20936 }, conflicts[1].ProcessIds);
        Assert.Equal("Corsair iCUE, SignalRGB", RgbConflictDetector.Describe(conflicts));
    }

    [Fact]
    public void No_known_processes_means_no_conflicts()
    {
        Assert.Empty(RgbConflictDetector.Classify(new[] { ("explorer", 1), ("Fanzi.FanControl", 2) }));
    }

    // ── 4.2: services, native detection ─────────────────────────────────────

    [Fact]
    public void Conflict_services_are_grouped_with_their_product()
    {
        var conflicts = RgbConflictDetector.Classify(new[] { ("SignalRgbService", 4900) }, new[] { "SignalRgb.Service", "Spooler" });
        var c = Assert.Single(conflicts);
        Assert.Equal("SignalRGB", c.Product);
        Assert.Equal(new[] { 4900 }, c.ProcessIds);
        Assert.Equal(new[] { "SignalRgb.Service" }, c.ServiceNames);
    }

    [Fact]
    public void A_running_service_alone_is_a_conflict()
    {
        var c = Assert.Single(RgbConflictDetector.Classify(Array.Empty<(string, int)>(), new[] { "LightingService" }));
        Assert.Equal("ASUS Aura (LightingService)", c.Product);
        Assert.Empty(c.ProcessIds);
    }

    [Fact]
    public void Conflict_message_names_the_relaunching_service()
    {
        var msg = Fanzi.FanControl.ViewModels.RgbControlViewModel.BuildConflictMessage(new[]
        {
            new RgbConflictDetector.Conflict("SignalRGB", new[] { 1 }) { ServiceNames = new[] { "SignalRgb.Service" } },
        });
        Assert.Contains("SignalRgb.Service", msg);
        Assert.Contains("overwritten", msg);
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2216&SUBSYS_361219DA&REV_A1\4&4188C91&0&0009", "ZOTAC", "yes")]
    [InlineData(@"PCI\VEN_10DE&DEV_2684&SUBSYS_88E21043&REV_A1", "ASUS", "yes")]
    [InlineData(@"PCI\VEN_1002&DEV_13C0&SUBSYS_07606688&REV_E6", "Vendor 6688", "unknown")]
    [InlineData("garbage", "Unknown", "unknown")]
    public void Gpu_board_partner_comes_from_the_pci_subsystem_vendor(string pnp, string partner, string support)
    {
        var (p, s, _) = RgbHardwareInventory.PartnerFromPnpId(pnp);
        Assert.Equal(partner, p);
        Assert.Equal(support, s);
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC. ROG STRIX X670E-F GAMING WIFI", true)]
    [InlineData("Micro-Star International Co., Ltd. MAG B650", true)]
    [InlineData("MAXSUN MS-iCraft X870M", false)]
    public void Board_rgb_family_is_known_only_for_supported_makers(string board, bool supported)
    {
        Assert.Equal(supported, RgbHardwareInventory.BoardFamilyFor(board) is not null);
    }

    [Fact]
    public void Deep_scan_reports_running_rgb_software_and_unsupported_board()
    {
        var system = new SystemRgbInventory("MAXSUN MS-iCraft X870M", new[] { "DIMM 1: Patriot (16 GB)" }, new[] { "NVIDIA GeForce RTX 3080" },
            Array.Empty<(string, string, string)>(), true)
        {
            RunningConflicts = new[] { new RgbConflictDetector.Conflict("SignalRGB", new[] { 1 }) { ServiceNames = new[] { "SignalRgb.Service" } } },
            KernelDrivers = new[] { new KernelDriver("WinRing0_1_2_0", false, @"\??\C:\Program Files (x86)\MAXSUN_View\WinRing0x64.sys") },
            SmBusControllers = new[] { "AMD SMBus (1022:790B)" },
            GpuBoards = new[] { new GpuBoard("NVIDIA GeForce RTX 3080", "ZOTAC", "19DA", "10DE:2216") },
        };
        var findings = RgbHardwareInventory.Analyse(Array.Empty<Fanzi.FanControl.Models.RgbDeviceInfo>(), system, 2, openRgbConnected: true);

        Assert.Contains(findings, f => f.Category == "Software" && f.Title.Contains("SignalRGB") && f.Status == DeepScanStatus.Missing);
        Assert.Contains(findings, f => f.Category == "Driver" && f.Title.StartsWith("WinRing0"));
        Assert.Contains(findings, f => f.Category == "SMBus" && f.Status == DeepScanStatus.Ok);
        Assert.Contains(findings, f => f.Category == "Motherboard" && f.Title.Contains("not supported"));
        Assert.Contains(findings, f => f.Category == "GPU" && f.Title.Contains("ZOTAC") && f.Status == DeepScanStatus.Warning);
    }

    // ── PawnIO ───────────────────────────────────────────────────────────────

    [Fact]
    public void PawnIo_setup_asset_is_taken_only_from_the_official_repo()
    {
        const string release = """
        { "tag_name": "2.2.0", "assets": [
          { "name": "PawnIO_setup.exe", "browser_download_url": "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe" } ] }
        """;
        Assert.Equal("https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe",
            PawnIoInstaller.SelectSetupAsset(release));

        const string foreign = """
        { "assets": [ { "name": "PawnIO_setup.exe", "browser_download_url": "https://evil.example/PawnIO_setup.exe" } ] }
        """;
        Assert.Null(PawnIoInstaller.SelectSetupAsset(foreign));
        Assert.Null(PawnIoInstaller.SelectSetupAsset("{}"));
    }

    [Fact]
    public void Unsigned_file_is_not_a_trusted_pawnio_installer()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fanzi-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0, 0 });
        try
        {
            Assert.False(PawnIoInstaller.IsTrustedSigner(path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
