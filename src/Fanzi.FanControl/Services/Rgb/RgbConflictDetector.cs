using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>
/// Finds other RGB software that grabs the same hardware as OpenRGB (SMBus, GPU I2C, USB HID).
/// While one of these runs, colours fight or are overwritten, or devices are not detected at all.
/// </summary>
public static class RgbConflictDetector
{
    /// <summary>Process name (no .exe) → product name.</summary>
    internal static readonly IReadOnlyDictionary<string, string> KnownProcesses =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SignalRgb"]            = "SignalRGB",
            ["SignalRgbLauncher"]    = "SignalRGB",
            ["SignalRgbService"]     = "SignalRGB",
            ["iCUEDevicePluginHost"] = "Corsair iCUE",
            ["Corsair.Service"]      = "Corsair iCUE",
            ["AuraService"]          = "ASUS Aura",
            ["AacAmbientLighting"]   = "ASUS Aura",
            ["MSI.Mystic.Light"]     = "MSI Mystic Light",
            ["OpenRGB.UI"]           = "another OpenRGB instance",
            ["MAXSUN_View"]          = "MAXSUN View",
            ["MaxsunView"]           = "MAXSUN View",
            ["CAM"]                  = "NZXT CAM",
            ["HyperXNgenuity"]       = "HyperX NGENUITY",
            ["SteelSeriesGG"]        = "SteelSeries GG",
            ["Gigabyte.ControlCenter"] = "Gigabyte Control Center",
            ["iCUE"]                 = "Corsair iCUE",
            ["LightingService"]      = "ASUS Aura (LightingService)",
            ["ArmouryCrate"]         = "ASUS Armoury Crate",
            ["ArmouryCrate.UserSessionHelper"] = "ASUS Armoury Crate",
            ["MSI Center"]           = "MSI Center",
            ["MSI.CentralServer"]    = "MSI Center",
            ["MysticLight"]          = "MSI Mystic Light",
            ["RGBFusion"]            = "Gigabyte RGB Fusion",
            ["GCC"]                  = "Gigabyte Control Center",
            ["RazerCentralService"]  = "Razer Synapse",
            ["Razer Synapse 3"]      = "Razer Synapse",
            ["NZXT CAM"]             = "NZXT CAM",
            ["lghub"]                = "Logitech G HUB",
            ["L-Connect 3"]          = "Lian Li L-Connect",
            ["FireStorm"]            = "ZOTAC FireStorm",
            ["Spectra"]              = "ZOTAC Spectra",
            ["PolychromeRGB"]        = "ASRock Polychrome",
        };

    /// <summary>
    /// Windows services that relaunch those apps or drive the lighting themselves. Killing only the app
    /// is not enough: e.g. SignalRgb.Service restarts SignalRGB within seconds and keeps streaming frames
    /// to RAM / GPU / monitor, so FANZi's colours are overwritten instantly ("RGB doesn't work at all").
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> KnownServices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SignalRgb.Service"]        = "SignalRGB",
            ["LightingService"]          = "ASUS Aura (LightingService)",
            ["ArmouryCrateService"]      = "ASUS Armoury Crate",
            ["AsusAppService"]           = "ASUS Armoury Crate",
            ["CorsairService"]           = "Corsair iCUE",
            ["iCUEDevicePluginHost"]     = "Corsair iCUE",
            ["CorsairLLAService"]        = "Corsair iCUE",
            ["MSI_Center_Service"]       = "MSI Center",
            ["Mystic_Light_Service"]     = "MSI Mystic Light",
            ["MSI_Central_Service"]      = "MSI Center",
            ["Razer Synapse Service"]    = "Razer Synapse",
            ["RzActionSvc"]              = "Razer Synapse",
            ["NZXT CAM Service"]         = "NZXT CAM",
            ["LConnectService"]          = "Lian Li L-Connect",
            ["GigabyteUpdateService"]    = "Gigabyte Control Center",
            ["Polychrome RGB Service"]   = "ASRock Polychrome",
        };

    /// <summary>HKCU\…\Run value names that auto-start these apps at sign-in.</summary>
    internal static readonly IReadOnlyDictionary<string, string> KnownRunEntries =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SignalRgb"] = "SignalRGB",
            ["iCUE"] = "Corsair iCUE",
            ["Corsair iCUE5 Software"] = "Corsair iCUE",
            ["MSI Center"] = "MSI Center",
            ["NZXT.CAM"] = "NZXT CAM",
            ["Razer Synapse"] = "Razer Synapse",
            ["L-Connect 3"] = "Lian Li L-Connect",
        };

    /// <summary>One running conflicting product, its processes and its running Windows services.</summary>
    public sealed record Conflict(string Product, IReadOnlyList<int> ProcessIds)
    {
        /// <summary>Running service names of this product (stopping them stops the relaunch).</summary>
        public IReadOnlyList<string> ServiceNames { get; init; } = Array.Empty<string>();
    }

    /// <summary>Running conflicting products (grouped), or an empty list.</summary>
    public static IReadOnlyList<Conflict> Find() => Classify(SafeProcessList(), RunningServices());

    /// <summary>Groups (name, pid) pairs into conflicts. Pure — unit-tested.</summary>
    internal static IReadOnlyList<Conflict> Classify(IEnumerable<(string Name, int Pid)> processes) =>
        Classify(processes, Array.Empty<string>());

    /// <summary>Groups processes and running service names into conflicts per product. Pure — unit-tested.</summary>
    internal static IReadOnlyList<Conflict> Classify(IEnumerable<(string Name, int Pid)> processes, IEnumerable<string> runningServices)
    {
        var byProduct = new Dictionary<string, (List<int> Pids, List<string> Services)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, pid) in processes)
        {
            if (!KnownProcesses.TryGetValue(name, out var product)) continue;
            if (!byProduct.TryGetValue(product, out var e)) byProduct[product] = e = (new List<int>(), new List<string>());
            if (!e.Pids.Contains(pid)) e.Pids.Add(pid);
        }
        foreach (var svc in runningServices)
        {
            if (!KnownServices.TryGetValue(svc, out var product)) continue;
            if (!byProduct.TryGetValue(product, out var e)) byProduct[product] = e = (new List<int>(), new List<string>());
            if (!e.Services.Contains(svc, StringComparer.OrdinalIgnoreCase)) e.Services.Add(svc);
        }
        return byProduct
            .Select(kv => new Conflict(kv.Key, kv.Value.Pids.ToArray()) { ServiceNames = kv.Value.Services.ToArray() })
            .OrderBy(c => c.Product, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Human summary, e.g. "SignalRGB, Corsair iCUE"; empty when none.</summary>
    public static string Describe(IReadOnlyList<Conflict> conflicts) =>
        string.Join(", ", conflicts.Select(c => c.Product));

    /// <summary>
    /// Closes the given products' processes (FANZi runs elevated). Returns how many processes
    /// stopped. Background services of these products may restart their apps — the RGB tab
    /// re-checks and keeps showing the warning in that case.
    /// </summary>
    public static int Stop(IReadOnlyList<Conflict> conflicts) => Stop(conflicts, disableAutostart: false);

    /// <summary>
    /// Stops the products' services first (so nothing relaunches the apps), then their processes. With
    /// <paramref name="disableAutostart"/> the services are set to Manual start and the HKCU Run entries
    /// are removed so they do not take the lighting back at the next sign-in (undo in services.msc /
    /// the product's own settings). Needs FANZi elevated for the service part.
    /// </summary>
    public static int Stop(IReadOnlyList<Conflict> conflicts, bool disableAutostart)
    {
        int stopped = 0;
        foreach (var svc in conflicts.SelectMany(c => c.ServiceNames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (disableAutostart) RunSc($"config \"{svc}\" start= demand");
            if (RunSc($"stop \"{svc}\"")) stopped++;
        }
        if (disableAutostart && OperatingSystem.IsWindows())
            RemoveRunEntries(conflicts.Select(c => c.Product).ToHashSet(StringComparer.OrdinalIgnoreCase));
        // Give the service a moment to take its child apps down before killing the rest.
        if (conflicts.Any(c => c.ServiceNames.Count > 0)) System.Threading.Thread.Sleep(800);

        foreach (int pid in conflicts.SelectMany(c => c.ProcessIds).Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                stopped++;
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
            catch (Exception ex)
            {
                AppLog.Warn($"RGB: could not close conflicting process {pid}", ex);
            }
        }
        AppLog.Info($"RGB: closed {stopped} conflicting RGB process(es): {Describe(conflicts)}");
        return stopped;
    }

    /// <summary>Running Windows services (names); empty off Windows or when WMI fails.</summary>
    private static IReadOnlyList<string> RunningServices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var list = new List<string>();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE State = 'Running'");
            foreach (var o in s.Get())
                if (o["Name"] is string n && KnownServices.ContainsKey(n)) list.Add(n);
        }
        catch (Exception ex) { AppLog.Warn("RGB: service query failed", ex); }
        return list;
    }

    private static bool RunSc(string args)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(8000);
            bool ok = p.ExitCode == 0;
            if (!ok) AppLog.Warn($"RGB: sc {args} exited {p.ExitCode} (FANZi needs to run as Administrator to stop services)");
            return ok;
        }
        catch (Exception ex) { AppLog.Warn($"RGB: sc {args} failed", ex); return false; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveRunEntries(ISet<string> products)
    {
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run is null) return;
            foreach (var name in run.GetValueNames())
            {
                if (KnownRunEntries.TryGetValue(name, out var product) && products.Contains(product))
                {
                    run.DeleteValue(name, throwOnMissingValue: false);
                    AppLog.Info($"RGB: removed sign-in autostart '{name}' ({product})");
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("RGB: could not edit the Run key", ex); }
    }

    private static IEnumerable<(string, int)> SafeProcessList()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { yield break; }

        foreach (var p in all)
        {
            string name;
            int pid;
            try { name = p.ProcessName; pid = p.Id; }
            catch { p.Dispose(); continue; }
            p.Dispose();
            yield return (name, pid);
        }
    }
}
