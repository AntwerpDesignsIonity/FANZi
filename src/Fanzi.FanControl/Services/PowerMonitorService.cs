using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Per-component power consumption monitor. Sources data from LibreHardwareMonitor
/// (CPU package power, GPU power, RAM power if exposed). Adds drive list with
/// Device Manager quick-link.
/// </summary>
public sealed class PowerMonitorService
{
    private readonly Computer? _computer;

    public PowerMonitorService()
    {
        if (!OperatingSystem.IsWindows()) return;

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = false,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
            IsNetworkEnabled = false,
            IsPowerMonitorEnabled = true,
        };
        _computer.Open();
    }

    public Task<PowerSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var components = new List<ComponentPower>();
            double total = 0;

            if (_computer is not null)
            {
                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware) sub.Update();

                    double? watts = null;
                    foreach (var sensor in hw.Sensors.Where(s => s.SensorType == SensorType.Power))
                    {
                        if (sensor.Value.HasValue)
                        {
                            // Pick the most-aggregate sensor (Package / Total)
                            if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("GPU Power", StringComparison.OrdinalIgnoreCase))
                            {
                                watts = sensor.Value;
                                break;
                            }
                            watts ??= sensor.Value;
                        }
                    }

                    if (watts.HasValue)
                    {
                        components.Add(new ComponentPower(
                            Category: HwTypeToCategory(hw.HardwareType),
                            Name: hw.Name,
                            Watts: watts.Value));
                        total += watts.Value;
                    }
                }
            }

            // Drives — enumerate physical drives, link to Device Manager
            var drives = new List<DriveInfoStats>();
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
                {
                    drives.Add(new DriveInfoStats(
                        Letter: d.Name,
                        Label: string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveFormat : d.VolumeLabel,
                        TotalGb: d.TotalSize / 1024.0 / 1024.0 / 1024.0,
                        FreeGb: d.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0,
                        Format: d.DriveFormat,
                        Type: d.DriveType.ToString()));
                }
            }
            catch { }

            double estimatedSystemWatts = total > 0 ? total + 25 /* fans, board, accessories */ : 0;

            return new PowerSnapshot(
                DateTime.UtcNow,
                components,
                drives,
                total,
                estimatedSystemWatts);
        }, ct);
    }

    public static void OpenDeviceManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch { }
    }

    public static void OpenDiskManagement()
    {
        try { Process.Start(new ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true }); }
        catch { }
    }

    public static void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
        catch { }
    }

    public static void OpenResourceMonitor()
    {
        try { Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true }); }
        catch { }
    }

    public static void OpenNetworkAdapters()
    {
        try { Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true }); }
        catch { }
    }

    public static void OpenPowerOptions()
    {
        try { Process.Start(new ProcessStartInfo("powercfg.cpl") { UseShellExecute = true }); }
        catch { }
    }

    private static string HwTypeToCategory(HardwareType t) => t switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "Memory",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Motherboard",
        HardwareType.Storage => "Storage",
        HardwareType.Battery => "Battery",
        HardwareType.Psu => "PSU",
        _ => t.ToString(),
    };

    public void Dispose() => _computer?.Close();
}

public sealed record ComponentPower(string Category, string Name, double Watts);

public sealed record DriveInfoStats(string Letter, string Label, double TotalGb, double FreeGb, string Format, string Type);

public sealed record PowerSnapshot(
    DateTime Timestamp,
    IReadOnlyList<ComponentPower> Components,
    IReadOnlyList<DriveInfoStats> Drives,
    double TotalComponentWatts,
    double EstimatedSystemWatts);
