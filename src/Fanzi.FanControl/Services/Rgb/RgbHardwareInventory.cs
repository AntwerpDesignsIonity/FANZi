using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services.Rgb;

public enum DeepScanStatus { Ok, Info, Warning, Missing }

/// <summary>One line of the deep-scan report.</summary>
public sealed record DeepScanFinding(string Category, string Title, string Detail, DeepScanStatus Status)
{
    public string Icon => Category switch
    {
        "RAM" => "▤",
        "Fans" => "✇",
        "GPU" => "▣",
        "Motherboard" => "▦",
        "USB" => "⌁",
        "Monitor" => "▭",
        "Driver" => "⚙",
        "Software" => "⚠",
        "SMBus" => "⇄",
        _ => "●",
    };

    public string StatusColor => Status switch
    {
        DeepScanStatus.Ok => "#22C55E",
        DeepScanStatus.Info => "#38BDF8",
        DeepScanStatus.Warning => "#FBBF24",
        _ => "#EF4444",
    };

    public string StatusLabel => Status switch
    {
        DeepScanStatus.Ok => "FOUND",
        DeepScanStatus.Info => "INFO",
        DeepScanStatus.Warning => "CHECK",
        _ => "MISSING",
    };
}

/// <summary>What Windows reports, independent of OpenRGB.</summary>
public sealed record SystemRgbInventory(
    string Motherboard,
    IReadOnlyList<string> MemoryModules,
    IReadOnlyList<string> Gpus,
    IReadOnlyList<(string VidPid, string Vendor, string Name)> RgbUsbCandidates,
    bool PawnIoRunning)
{
    /// <summary>GPU boards with their add-in-board partner read from the PCI subsystem vendor (4.2).</summary>
    public IReadOnlyList<GpuBoard> GpuBoards { get; init; } = Array.Empty<GpuBoard>();

    /// <summary>Low-level hardware-access kernel drivers present (PawnIO, WinRing0, inpoutx64, …).</summary>
    public IReadOnlyList<KernelDriver> KernelDrivers { get; init; } = Array.Empty<KernelDriver>();

    /// <summary>SMBus host controllers Windows exposes (RAM / motherboard RGB live behind these).</summary>
    public IReadOnlyList<string> SmBusControllers { get; init; } = Array.Empty<string>();

    /// <summary>Installed RGB / vendor lighting software (from the Uninstall registry keys).</summary>
    public IReadOnlyList<string> InstalledRgbSoftware { get; init; } = Array.Empty<string>();

    /// <summary>RGB software running right now (processes + services).</summary>
    public IReadOnlyList<RgbConflictDetector.Conflict> RunningConflicts { get; init; } = Array.Empty<RgbConflictDetector.Conflict>();
}

/// <summary>A graphics card and the board partner that made it (PCI SUBSYS vendor).</summary>
public sealed record GpuBoard(string Name, string Partner, string SubsystemVendorId, string PciDeviceId);

/// <summary>A hardware-access kernel driver and whether it is running.</summary>
public sealed record KernelDriver(string Name, bool Running, string Path);

/// <summary>
/// "Deep scan" for the RGB tab (v4): cross-checks what OpenRGB controls against what Windows sees —
/// installed DIMMs, the motherboard, GPUs, USB devices from known RGB vendors and the PawnIO driver —
/// and explains every gap (e.g. RGB RAM needs PawnIO; fan LEDs on an unsupported board's ARGB headers
/// cannot be reached by any OpenRGB-based app). Read-only: it never writes to hardware.
/// </summary>
public static class RgbHardwareInventory
{
    /// <summary>USB vendor IDs of RGB hardware makers (controllers, hubs, peripherals, AIOs).</summary>
    internal static readonly IReadOnlyDictionary<string, string> RgbVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["1B1C"] = "Corsair", ["0B05"] = "ASUS (Aura)", ["1462"] = "MSI (Mystic Light)", ["048D"] = "ITE (motherboard RGB controller)",
        ["1044"] = "Gigabyte", ["0CF2"] = "Lian Li / ENE", ["1E71"] = "NZXT", ["264A"] = "Thermaltake", ["2516"] = "Cooler Master",
        ["1532"] = "Razer", ["046D"] = "Logitech", ["1038"] = "SteelSeries", ["1A86"] = "WCH (generic ARGB controller)",
        ["0416"] = "Winbond / Nuvoton (ARGB hub / display controller)", ["2433"] = "Asetek (AIO)", ["16D0"] = "MCS (Hydro/DIY ARGB)",
        ["3633"] = "DeepCool", ["34D3"] = "Phanteks?", ["1D50"] = "OpenMoko (DIY ARGB / WLED)", ["2E8A"] = "Raspberry Pi (DIY ARGB)",
        ["303A"] = "Espressif (DIY ARGB / ESP32)", ["0483"] = "STMicro (ARGB hub)", ["320F"] = "Glorious / EVision", ["3299"] = "SteelSeries (Prime)",
        ["0951"] = "HyperX", ["03F0"] = "HP / HyperX", ["1AF3"] = "Kingston", ["093A"] = "Pixart (mouse)", ["258A"] = "SINOWEALTH (keyboard)",
    };

    /// <summary>
    /// PCI subsystem vendor → GPU board partner and whether OpenRGB ships an RGB driver for that partner's
    /// cards (ported from OpenRGB's pci_ids.h / GPU detector list). "partial" = only some models.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Partner, string Support)> GpuPartners =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["1043"] = ("ASUS", "yes"), ["1462"] = ("MSI", "yes"), ["1458"] = ("Gigabyte / AORUS", "yes"),
            ["3842"] = ("EVGA", "yes"), ["19DA"] = ("ZOTAC", "yes"), ["1569"] = ("Palit", "yes"),
            ["10B0"] = ("Gainward", "yes"), ["1B4C"] = ("GALAX / KFA2", "yes"), ["196E"] = ("PNY", "partial"),
            ["1DA2"] = ("Sapphire", "yes"), ["148C"] = ("PowerColor", "partial"), ["1682"] = ("XFX", "no"),
            ["1849"] = ("ASRock", "partial"), ["7377"] = ("Colorful", "partial"), ["10DE"] = ("NVIDIA Founders Edition", "partial"),
            ["1002"] = ("AMD reference", "no"), ["17AA"] = ("Lenovo (OEM)", "no"), ["1028"] = ("Dell (OEM)", "no"),
            ["103C"] = ("HP (OEM)", "no"), ["1565"] = ("Biostar", "no"), ["1ACC"] = ("Point of View", "no"),
            ["16F3"] = ("Jetway", "no"), ["174B"] = ("PC Partner / Sapphire", "partial"), ["1642"] = ("Bitland", "no"),
            ["1EAE"] = ("Inno3D", "partial"),
        };

    /// <summary>Motherboard makers with an OpenRGB lighting driver family (Aura, Mystic Light, RGB Fusion 2, Polychrome …).</summary>
    internal static readonly IReadOnlyDictionary<string, string> BoardRgbFamilies =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ASUS"] = "ASUS Aura (SMBus / USB)", ["ASUSTeK"] = "ASUS Aura (SMBus / USB)", ["Micro-Star"] = "MSI Mystic Light (USB)",
            ["MSI"] = "MSI Mystic Light (USB)", ["Gigabyte"] = "Gigabyte RGB Fusion 2 (SMBus / USB)", ["ASRock"] = "ASRock Polychrome (SMBus / USB)",
            ["NZXT"] = "NZXT RGB (USB)", ["EVGA"] = "EVGA (SMBus)",
        };

    /// <summary>Kernel drivers that give user-mode tools port / SMBus access (what OpenRGB, LibreHardwareMonitor, vendor apps use).</summary>
    internal static readonly (string Prefix, string Description)[] KnownKernelDrivers =
    {
        ("PawnIO", "PawnIO — signed SMBus / port driver used by OpenRGB 1.0 and LibreHardwareMonitor"),
        ("WinRing0", "WinRing0 — legacy port driver (blocked by Microsoft's vulnerable-driver list; OpenRGB 1.0 no longer uses it)"),
        ("inpoutx64", "inpoutx64 — legacy port I/O driver (OpenRGB 0.9 SMBus access)"),
        ("RTCore64", "RTCore64 — MSI Afterburner driver"),
        ("AsIO", "AsIO — ASUS AI Suite / Armoury Crate driver"),
        ("GLCKIO", "GLCKIO — ASRock / Gigabyte vendor driver"),
        ("EneTechIo", "ENE vendor RGB driver"),
        ("NTIOLib", "NTIOLib — MSI vendor driver"),
        ("MsIo", "MsIo — MSI vendor driver"),
        ("HwRwDrv", "HwRwDrv — RW-Everything driver"),
    };

    /// <summary>Installed-software name fragments of RGB / vendor lighting apps.</summary>
    internal static readonly string[] RgbSoftwareKeywords =
    {
        "SignalRGB", "iCUE", "Armoury Crate", "AURA", "MSI Center", "Mystic Light", "RGB Fusion", "Gigabyte Control Center",
        "Polychrome", "NZXT CAM", "L-Connect", "Razer Synapse", "G HUB", "SteelSeries GG", "MAXSUN", "FireStorm", "Spectra",
        "OpenRGB", "TT RGB", "MasterPlus", "Corsair", "Thermaltake", "HyperX NGENUITY", "Lian Li", "Phanteks", "DeepCool",
    };

    private static readonly Regex SubsysRegex = new(@"SUBSYS_[0-9A-F]{4}([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PciDevRegex = new(@"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Board partner for a PCI device id ("PCI\VEN_10DE&amp;DEV_2216&amp;SUBSYS_361219DA…" → ZOTAC). Pure — unit-tested.</summary>
    internal static (string Partner, string Support, string SubVendor) PartnerFromPnpId(string pnpId)
    {
        var m = SubsysRegex.Match(pnpId ?? "");
        if (!m.Success) return ("Unknown", "unknown", "");
        string sv = m.Groups[1].Value.ToUpperInvariant();
        return GpuPartners.TryGetValue(sv, out var p) ? (p.Partner, p.Support, sv) : ($"Vendor {sv}", "unknown", sv);
    }

    /// <summary>OpenRGB driver family for a motherboard maker, or null when OpenRGB has none. Pure — unit-tested.</summary>
    internal static string? BoardFamilyFor(string motherboard)
    {
        foreach (var (maker, family) in BoardRgbFamilies)
            if (motherboard.Contains(maker, StringComparison.OrdinalIgnoreCase)) return family;
        return null;
    }

    private static readonly Regex VidPidRegex = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads the Windows-side inventory (WMI + service query) off the UI thread.</summary>
    [SupportedOSPlatform("windows")]
    public static Task<SystemRgbInventory> ReadSystemAsync(CancellationToken token = default) => Task.Run(() =>
    {
        string board = "Unknown motherboard";
        var dimms = new List<string>();
        var gpus = new List<string>();
        var usb = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        var boards = new List<GpuBoard>();
        var drivers = new List<KernelDriver>();
        var smbus = new List<string>();
        var software = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (var o in s.Get())
                board = $"{o["Manufacturer"]} {o["Product"]}".Trim();
        }
        catch (Exception ex) { AppLog.Warn("Deep scan: motherboard query failed", ex); }

        token.ThrowIfCancellationRequested();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, PartNumber, Capacity, DeviceLocator FROM Win32_PhysicalMemory");
            foreach (var o in s.Get())
            {
                double gb = o["Capacity"] is ulong cap ? cap / 1024d / 1024d / 1024d : 0;
                dimms.Add($"{o["DeviceLocator"]}: {o["Manufacturer"]} {Convert.ToString(o["PartNumber"], CultureInfo.InvariantCulture)?.Trim()} ({gb:F0} GB)".Trim());
            }
        }
        catch (Exception ex) { AppLog.Warn("Deep scan: memory query failed", ex); }

        token.ThrowIfCancellationRequested();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (var o in s.Get())
            {
                var name = Convert.ToString(o["Name"], CultureInfo.InvariantCulture) ?? "";
                var pnp = Convert.ToString(o["PNPDeviceID"], CultureInfo.InvariantCulture) ?? "";
                if (!name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) && name.Length > 0)
                {
                    gpus.Add(name);
                    var (partner, _, sv) = PartnerFromPnpId(pnp);
                    var dev = PciDevRegex.Match(pnp);
                    boards.Add(new GpuBoard(name, partner, sv, dev.Success ? $"{dev.Groups[1].Value}:{dev.Groups[2].Value}".ToUpperInvariant() : ""));
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Deep scan: GPU query failed", ex); }

        token.ThrowIfCancellationRequested();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%' OR DeviceID LIKE 'HID\\\\VID_%'");
            foreach (var o in s.Get())
            {
                var id = Convert.ToString(o["DeviceID"], CultureInfo.InvariantCulture) ?? "";
                var m = VidPidRegex.Match(id);
                if (!m.Success) continue;
                string vid = m.Groups[1].Value.ToUpperInvariant(), pid = m.Groups[2].Value.ToUpperInvariant();
                if (!RgbVendors.TryGetValue(vid, out var vendor)) continue;
                string key = $"{vid}:{pid}";
                var name = Convert.ToString(o["Name"], CultureInfo.InvariantCulture) ?? "USB device";
                // Prefer a descriptive interface name over "USB Composite Device".
                if (!usb.TryGetValue(key, out var existing) || existing.Item3.StartsWith("USB ", StringComparison.OrdinalIgnoreCase) || existing.Item3.StartsWith("HID-", StringComparison.OrdinalIgnoreCase))
                    usb[key] = (key, vendor, name);
            }
        }
        catch (Exception ex) { AppLog.Warn("Deep scan: USB query failed", ex); }

        bool pawnIo = false;
        try
        {
            // Same approach as OpenRGB / LibreHardwareMonitor's driver checks: list kernel drivers by service name.
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name, State, PathName FROM Win32_SystemDriver");
            foreach (var o in s.Get())
            {
                var name = Convert.ToString(o["Name"], CultureInfo.InvariantCulture) ?? "";
                if (!KnownKernelDrivers.Any(k => name.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase))) continue;
                bool running = string.Equals(Convert.ToString(o["State"], CultureInfo.InvariantCulture), "Running", StringComparison.OrdinalIgnoreCase);
                drivers.Add(new KernelDriver(name, running, Convert.ToString(o["PathName"], CultureInfo.InvariantCulture) ?? ""));
                if (name.Equals("PawnIO", StringComparison.OrdinalIgnoreCase)) pawnIo = running;
            }
        }
        catch (Exception ex) { AppLog.Warn("Deep scan: driver query failed", ex); }

        token.ThrowIfCancellationRequested();
        try
        {
            // SMBus host controllers (AMD FCH "PIIX4" 1022:790B, Intel PCH SMBus, Nuvoton …) — the bus RGB RAM sits on.
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%SMBus%' OR PNPDeviceID LIKE 'PCI\\\\VEN_1022&DEV_790B%'");
            foreach (var o in s.Get())
            {
                var name = Convert.ToString(o["Name"], CultureInfo.InvariantCulture) ?? "SMBus controller";
                var id = Convert.ToString(o["PNPDeviceID"], CultureInfo.InvariantCulture) ?? "";
                var dev = PciDevRegex.Match(id);
                smbus.Add(dev.Success ? $"{name} ({dev.Groups[1].Value}:{dev.Groups[2].Value})" : name);
            }
        }
        catch (Exception ex) { AppLog.Warn("Deep scan: SMBus query failed", ex); }

        token.ThrowIfCancellationRequested();
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        foreach (var path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(sub);
                    if (app?.GetValue("DisplayName") is not string dn || dn.Length == 0) continue;
                    if (RgbSoftwareKeywords.Any(k => dn.Contains(k, StringComparison.OrdinalIgnoreCase))
                        && !dn.Contains("FANZi", StringComparison.OrdinalIgnoreCase))
                        software.Add(dn.Trim());
                }
            }
            catch (Exception ex) { AppLog.Warn("Deep scan: installed-software query failed", ex); }
        }

        IReadOnlyList<RgbConflictDetector.Conflict> conflictsNow = Array.Empty<RgbConflictDetector.Conflict>();
        try { conflictsNow = RgbConflictDetector.Find(); }
        catch (Exception ex) { AppLog.Warn("Deep scan: conflict query failed", ex); }

        return new SystemRgbInventory(board, dimms, gpus, usb.Values.ToList(), pawnIo)
        {
            GpuBoards = boards,
            KernelDrivers = drivers,
            SmBusControllers = smbus,
            InstalledRgbSoftware = software.ToList(),
            RunningConflicts = conflictsNow,
        };
    }, token);

    /// <summary>
    /// Builds the report from the OpenRGB device list, the Windows inventory and the number of fan headers
    /// that report RPM (LibreHardwareMonitor). Pure function — unit-tested.
    /// </summary>
    public static IReadOnlyList<DeepScanFinding> Analyse(
        IReadOnlyList<RgbDeviceInfo> openRgbDevices,
        SystemRgbInventory system,
        int fanHeadersWithRpm,
        bool openRgbConnected)
    {
        var list = new List<DeepScanFinding>();
        var rgbRam = openRgbDevices.Where(d => d.Category == RgbDeviceCategory.Ram).ToList();
        var rgbBoard = openRgbDevices.Where(d => d.Category == RgbDeviceCategory.Motherboard).ToList();
        var rgbFans = openRgbDevices.Where(d => d.Category is RgbDeviceCategory.Cooler or RgbDeviceCategory.LedStrip
                                                || d.Zones.Any(z => z.Name.Contains("fan", StringComparison.OrdinalIgnoreCase)
                                                                 || z.Name.Contains("argb", StringComparison.OrdinalIgnoreCase)
                                                                 || z.Name.Contains("header", StringComparison.OrdinalIgnoreCase))).ToList();
        var rgbGpu = openRgbDevices.Where(d => d.Category == RgbDeviceCategory.Gpu).ToList();

        if (!openRgbConnected)
            list.Add(new("Driver", "OpenRGB is not connected",
                "The deep scan can only see what Windows reports. Turn RGB on (or click Retry) so OpenRGB can enumerate devices.", DeepScanStatus.Missing));

        list.Add(system.PawnIoRunning
            ? new("Driver", "PawnIO SMBus driver running", "OpenRGB 1.0 can reach RAM and motherboard controllers on the SMBus.", DeepScanStatus.Ok)
            : new("Driver", "PawnIO driver not running", "RGB RAM and most motherboard RGB are invisible to OpenRGB 1.0 without PawnIO — use the Install PawnIO button, then Restart OpenRGB.", DeepScanStatus.Warning));

        // ── Other RGB software holding the hardware (the #1 reason colours "don't apply") ──
        foreach (var c in system.RunningConflicts)
            list.Add(new("Software", $"{c.Product} is running — it overrides FANZi",
                $"{c.Product} streams its own colours to the same devices, so FANZi's colours are overwritten instantly." +
                (c.ServiceNames.Count > 0 ? $" Its service ({string.Join(", ", c.ServiceNames)}) relaunches it." : "") +
                " Use \"Stop & disable autostart\" at the top of the RGB tab.", DeepScanStatus.Missing));

        // ── Kernel drivers (how OpenRGB / LibreHardwareMonitor reach SMBus and ports) ──
        foreach (var d in system.KernelDrivers.Where(d => !d.Name.Equals("PawnIO", StringComparison.OrdinalIgnoreCase)))
        {
            var desc = KnownKernelDrivers.FirstOrDefault(k => d.Name.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase)).Description ?? d.Name;
            string from = d.Path.Length > 0 ? $" Installed at {d.Path.Replace(@"\??\", "")}." : "";
            list.Add(new("Driver", $"{d.Name} {(d.Running ? "running" : "installed (stopped)")}", desc + "." + from, DeepScanStatus.Info));
        }

        // ── SMBus host controller ──
        if (system.SmBusControllers.Count > 0)
            list.Add(new("SMBus", "SMBus controller found", string.Join(" · ", system.SmBusControllers) +
                (system.PawnIoRunning ? " — reachable through PawnIO." : " — needs PawnIO before OpenRGB can talk to RAM / board lighting."),
                system.PawnIoRunning ? DeepScanStatus.Ok : DeepScanStatus.Warning));
        else if (system.MemoryModules.Count > 0)
            list.Add(new("SMBus", "No SMBus controller visible", "Windows did not list an SMBus host controller (install the chipset driver from AMD / Intel). Without it RGB RAM cannot be detected.", DeepScanStatus.Warning));

        // ── RAM ──
        int dimmCount = system.MemoryModules.Count;
        string dimmText = dimmCount == 0 ? "Windows did not report the memory modules." : string.Join(" · ", system.MemoryModules);
        if (rgbRam.Count > 0)
        {
            list.Add(new("RAM", $"{rgbRam.Count} RGB RAM controller{(rgbRam.Count == 1 ? "" : "s")} under FANZi control",
                $"{string.Join(", ", rgbRam.Select(r => $"{r.DisplayName} ({r.LedCount} LEDs)"))}. Installed: {dimmText}",
                rgbRam.Count >= dimmCount || dimmCount == 0 ? DeepScanStatus.Ok : DeepScanStatus.Warning));
        }
        else
        {
            list.Add(new("RAM", dimmCount > 0 ? $"{dimmCount} memory module{(dimmCount == 1 ? "" : "s")} found, no RGB RAM controller" : "No RGB RAM controller",
                $"{dimmText}. If these sticks have RGB: install PawnIO, run FANZi as Administrator, close iCUE/Aura/SignalRGB and click Restart OpenRGB. Non-RGB sticks never appear.",
                DeepScanStatus.Missing));
        }

        // ── Fans / ARGB headers ──
        if (rgbFans.Count > 0)
        {
            list.Add(new("Fans", $"{rgbFans.Count} fan / ARGB controller{(rgbFans.Count == 1 ? "" : "s")} controllable",
                string.Join(", ", rgbFans.Select(f => $"{f.DisplayName} ({f.Zones.Count} zone{(f.Zones.Count == 1 ? "" : "s")}, {f.LedCount} LEDs)")) +
                ". Zones with 0 LEDs need their size set once in OpenRGB (Resize zone) before colours show.",
                DeepScanStatus.Ok));
        }
        else
        {
            string speed = fanHeadersWithRpm > 0
                ? $"{fanHeadersWithRpm} fan header{(fanHeadersWithRpm == 1 ? "" : "s")} report RPM, so speed control works on the Fans tab."
                : "No fan RPM sensors were read yet.";
            list.Add(new("Fans", "No fan RGB controller exposed",
                $"{speed} Fan LEDs are driven by the motherboard ARGB header or a fan hub; OpenRGB found neither for {system.Motherboard}. " +
                "Boards without an OpenRGB driver keep their LEDs in the BIOS / board-software effect — set them there, or connect the fans to a supported USB ARGB hub.",
                DeepScanStatus.Missing));
        }

        // ── Motherboard ──
        string? family = BoardFamilyFor(system.Motherboard);
        list.Add(rgbBoard.Count > 0
            ? new("Motherboard", $"{system.Motherboard} — RGB controller found", string.Join(", ", rgbBoard.Select(b => b.DisplayName)), DeepScanStatus.Ok)
            : family is not null
                ? new("Motherboard", $"{system.Motherboard} — {family} not detected",
                    $"OpenRGB has a {family} driver for this maker, but it did not answer: install PawnIO, run FANZi as Administrator, close the vendor's own RGB app, then Restart OpenRGB.",
                    DeepScanStatus.Warning)
                : new("Motherboard", $"{system.Motherboard} — onboard RGB not supported",
                    "No open-source tool (OpenRGB, SignalRGB's free tier, LibreHardwareMonitor) has a lighting driver for this board maker. Its onboard / ARGB-header lighting stays " +
                    "on the BIOS or the maker's own app. FANZi still controls RAM, GPU, monitor and USB RGB devices.",
                    DeepScanStatus.Info));

        // ── GPU ──
        foreach (var gpu in system.Gpus)
        {
            bool found = rgbGpu.Any(g => SharesModel(g.Name, gpu));
            var board = system.GpuBoards.FirstOrDefault(b => b.Name == gpu);
            string partner = board is null ? "" : $" ({board.Partner}{(board.SubsystemVendorId.Length > 0 ? $", subsystem {board.SubsystemVendorId}" : "")})";
            string support = board is null ? "unknown" : (GpuPartners.TryGetValue(board.SubsystemVendorId, out var p) ? p.Support : "unknown");
            list.Add(found || rgbGpu.Count > 0
                ? new("GPU", gpu + partner, $"RGB via {string.Join(", ", rgbGpu.Select(g => $"{g.DisplayName} · {g.ControlLabel}"))}", DeepScanStatus.Ok)
                : new("GPU", gpu + partner, support switch
                {
                    "yes" => "OpenRGB supports this board partner's RGB, but the card did not answer — close the partner's app (FireStorm, Afterburner, Armoury Crate, …) and Restart OpenRGB.",
                    "partial" => "OpenRGB supports only some of this partner's cards — this model may not be in its list, or has no RGB.",
                    "no" => "This board partner's RGB is not supported by OpenRGB (or the card has no RGB).",
                    _ => "No RGB controller found for this card in OpenRGB (card without RGB, or vendor not supported).",
                }, support == "yes" ? DeepScanStatus.Warning : DeepScanStatus.Info));
        }

        // ── Other OpenRGB devices (monitors, peripherals) ──
        foreach (var d in openRgbDevices.Where(d => d.Category is not (RgbDeviceCategory.Ram or RgbDeviceCategory.Gpu or RgbDeviceCategory.Motherboard)
                                                    && !rgbFans.Contains(d)))
            list.Add(new(d.Category == RgbDeviceCategory.Monitor ? "Monitor" : "USB", d.DisplayName,
                $"{d.FriendlyType} · {d.LedCount} LEDs · {d.ControlLabel}", DeepScanStatus.Ok));

        // ── USB devices from RGB vendors that OpenRGB did not claim ──
        foreach (var (vidPid, vendor, name) in system.RgbUsbCandidates)
        {
            bool claimed = openRgbDevices.Any(d => d.Location.Contains(vidPid.Replace(":", "&PID_"), StringComparison.OrdinalIgnoreCase)
                                                || d.Location.Contains("VID_" + vidPid[..4], StringComparison.OrdinalIgnoreCase));
            if (claimed) continue;
            list.Add(new("USB", $"{vendor} device {vidPid}", $"{name} — Windows sees it but OpenRGB did not load a controller for it. " +
                "If it is an ARGB hub or fan controller, check OpenRGB's supported-devices list or close the vendor's own RGB software.",
                DeepScanStatus.Info));
        }

        // ── Installed RGB software that is not running (can start again at sign-in) ──
        var runningProducts = system.RunningConflicts.Select(c => c.Product).ToArray();
        foreach (var app in system.InstalledRgbSoftware.Where(a => !a.Contains("OpenRGB", StringComparison.OrdinalIgnoreCase)
                                                                   && !runningProducts.Any(r => a.Contains(r.Split(' ')[0], StringComparison.OrdinalIgnoreCase))))
            list.Add(new("Software", $"{app} installed", "Not running now. If it starts with Windows it will take the lighting back — keep it closed while FANZi drives RGB.", DeepScanStatus.Info));

        return list;
    }

    private static bool SharesModel(string a, string b)
    {
        var tokens = Regex.Matches(b, @"\d{4}").Select(m => m.Value);
        return tokens.Any(t => a.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
