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
    bool PawnIoRunning);

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

    private static readonly Regex VidPidRegex = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads the Windows-side inventory (WMI + service query) off the UI thread.</summary>
    [SupportedOSPlatform("windows")]
    public static Task<SystemRgbInventory> ReadSystemAsync(CancellationToken token = default) => Task.Run(() =>
    {
        string board = "Unknown motherboard";
        var dimms = new List<string>();
        var gpus = new List<string>();
        var usb = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

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
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var o in s.Get())
            {
                var name = Convert.ToString(o["Name"], CultureInfo.InvariantCulture) ?? "";
                if (!name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) && name.Length > 0) gpus.Add(name);
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
            using var s = new System.Management.ManagementObjectSearcher("SELECT State FROM Win32_SystemDriver WHERE Name = 'PawnIO'");
            foreach (var o in s.Get())
                pawnIo = string.Equals(Convert.ToString(o["State"], CultureInfo.InvariantCulture), "Running", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { AppLog.Warn("Deep scan: PawnIO query failed", ex); }

        return new SystemRgbInventory(board, dimms, gpus, usb.Values.ToList(), pawnIo);
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
        list.Add(rgbBoard.Count > 0
            ? new("Motherboard", $"{system.Motherboard} — RGB controller found", string.Join(", ", rgbBoard.Select(b => b.DisplayName)), DeepScanStatus.Ok)
            : new("Motherboard", $"{system.Motherboard} — no RGB controller in OpenRGB",
                "This board's lighting controller is not supported by OpenRGB (or needs PawnIO / Administrator). Its headers follow the BIOS lighting setting.",
                DeepScanStatus.Warning));

        // ── GPU ──
        foreach (var gpu in system.Gpus)
        {
            bool found = rgbGpu.Any(g => SharesModel(g.Name, gpu));
            list.Add(found || rgbGpu.Count > 0
                ? new("GPU", gpu, $"RGB via {string.Join(", ", rgbGpu.Select(g => $"{g.DisplayName} · {g.ControlLabel}"))}", DeepScanStatus.Ok)
                : new("GPU", gpu, "No RGB controller found for this card in OpenRGB (card without RGB, or vendor not supported).", DeepScanStatus.Info));
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

        return list;
    }

    private static bool SharesModel(string a, string b)
    {
        var tokens = Regex.Matches(b, @"\d{4}").Select(m => m.Value);
        return tokens.Any(t => a.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
