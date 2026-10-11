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

    /// <summary>One running conflicting product and its processes.</summary>
    public sealed record Conflict(string Product, IReadOnlyList<int> ProcessIds);

    /// <summary>Running conflicting products (grouped), or an empty list.</summary>
    public static IReadOnlyList<Conflict> Find() => Classify(SafeProcessList());

    /// <summary>Groups (name, pid) pairs into conflicts. Pure — unit-tested.</summary>
    internal static IReadOnlyList<Conflict> Classify(IEnumerable<(string Name, int Pid)> processes) =>
        processes
            .Where(p => KnownProcesses.ContainsKey(p.Name))
            .GroupBy(p => KnownProcesses[p.Name])
            .Select(g => new Conflict(g.Key, g.Select(p => p.Pid).Distinct().ToArray()))
            .OrderBy(c => c.Product, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Human summary, e.g. "SignalRGB, Corsair iCUE"; empty when none.</summary>
    public static string Describe(IReadOnlyList<Conflict> conflicts) =>
        string.Join(", ", conflicts.Select(c => c.Product));

    /// <summary>
    /// Closes the given products' processes (FANZi runs elevated). Returns how many processes
    /// stopped. Background services of these products may restart their apps — the RGB tab
    /// re-checks and keeps showing the warning in that case.
    /// </summary>
    public static int Stop(IReadOnlyList<Conflict> conflicts)
    {
        int stopped = 0;
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
