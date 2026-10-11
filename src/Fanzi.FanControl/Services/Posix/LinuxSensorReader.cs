using Fanzi.FanControl.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Fanzi.FanControl.Services.Posix;

/// <summary>
/// Linux sensors without drivers or root: <c>/sys/class/hwmon</c> (CPU / GPU / board temperatures, fans, PWM, Vcore),
/// <c>/proc/stat</c> (CPU load), <c>/proc/cpuinfo</c> + cpufreq (name, clock), Intel RAPL (package power),
/// amdgpu sysfs (load, clock, power, VRAM) and <c>nvidia-smi</c> (NVIDIA GPUs).
/// The file-system root is injectable so the parser is unit-tested against a fake sysfs tree.
/// </summary>
public sealed class LinuxSensorReader
{
    private readonly string _root;
    private readonly Func<string, string, string?> _runTool;
    private (ulong Idle, ulong Total)? _lastCpu;
    private (ulong EnergyUj, long Ticks)? _lastRapl;
    private DateTime _nvidiaRetryAfter = DateTime.MinValue;
    private bool _nvidiaMissing;
    private readonly Dictionary<string, string> _savedEnable = new(StringComparer.Ordinal);

    /// <param name="root">File-system root ("/" on a real system).</param>
    /// <param name="runTool">Runs a tool (file, arguments) and returns stdout, or null when it is missing / fails.</param>
    public LinuxSensorReader(string root = "/", Func<string, string, string?>? runTool = null)
    {
        _root = root;
        _runTool = runTool ?? RunTool;
    }

    /// <summary>One hwmon chip: driver name and folder.</summary>
    internal sealed record HwmonChip(string Name, string Path);

    /// <summary>GPU summary from amdgpu sysfs or nvidia-smi.</summary>
    internal sealed record GpuInfo(string Name, double? Temp, double? Hotspot, double? Load, double? ClockMhz, double? Watts,
        double? VramUsedMb, double? VramTotalMb);

    internal string P(string relative) => Path.Combine(_root, relative.TrimStart('/'));

    internal IReadOnlyList<HwmonChip> Chips()
    {
        var dir = P("sys/class/hwmon");
        if (!Directory.Exists(dir)) return Array.Empty<HwmonChip>();
        return Directory.GetDirectories(dir)
            .Select(path => new HwmonChip(ReadText(Path.Combine(path, "name"))?.Trim() ?? Path.GetFileName(path), path))
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ToList();
    }

    public HardwareSnapshot Read(DateTimeOffset now)
    {
        var chips = Chips();
        var cpuReadings = new List<CpuReadingSnapshot>();
        var gpuReadings = new List<GpuReadingSnapshot>();

        // ── CPU temperatures ──
        double? package = null;
        var cores = new List<double>();
        foreach (var chip in chips.Where(c => IsCpuChip(c.Name)))
        {
            foreach (var (label, value) in Temperatures(chip))
            {
                cpuReadings.Add(new CpuReadingSnapshot(label, $"{value:F1} C", "Temperature"));
                if (IsPackageLabel(chip.Name, label)) package ??= value;
                else cores.Add(value);
            }
        }
        if (package is null && cores.Count == 0)
        {
            // Thermal zones (laptops, ARM boards, VMs with ACPI zones).
            foreach (var (type, value) in ThermalZones())
            {
                if (type is "x86_pkg_temp" or "cpu-thermal" or "cpu_thermal" or "soc-thermal" or "acpitz")
                {
                    package ??= value;
                    cpuReadings.Add(new CpuReadingSnapshot(type, $"{value:F1} C", "Temperature"));
                }
            }
        }
        var allCpuTemps = cores.Count > 0 ? cores : package is double p ? new List<double> { p } : new List<double>();
        package ??= cores.Count > 0 ? cores.Max() : null;

        // ── CPU load / clock / power / Vcore ──
        double? load = CpuLoad();
        if (load is double l) cpuReadings.Add(new CpuReadingSnapshot("CPU Total", $"{l:F0}%", "Load"));
        double? clock = CpuClockMhz();
        if (clock is double mhz) cpuReadings.Add(new CpuReadingSnapshot("CPU Average Clock", $"{mhz:F0} MHz", "Clock"));
        double? power = RaplPackageWatts();
        if (power is double w) cpuReadings.Add(new CpuReadingSnapshot("CPU Package", $"{w:F1} W", "Power"));
        double? vcore = null;
        foreach (var chip in chips)
        {
            foreach (var (label, volts) in Voltages(chip))
            {
                if (label.Contains("vcore", StringComparison.OrdinalIgnoreCase) || label.Equals("CPU Core", StringComparison.OrdinalIgnoreCase))
                {
                    vcore ??= volts;
                    cpuReadings.Add(new CpuReadingSnapshot(label, $"{volts:F3} V", "Voltage"));
                }
            }
        }

        // ── GPU ──
        var gpu = AmdGpu(chips, gpuReadings) ?? NvidiaGpu(gpuReadings);

        // ── Fans ──
        var fans = Fans(chips);
        var cpuFan = fans.FirstOrDefault(f => f.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase)) ?? fans.FirstOrDefault();
        if (cpuFan is not null)
            fans = fans.OrderByDescending(f => f.Id == cpuFan.Id).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

        string status = fans.Count switch
        {
            0 when package is null => "Linux: no hwmon temperature or fan sensors were exposed. Load the sensor drivers (sudo sensors-detect) and restart FANZi.",
            0 => "Linux: temperatures are live from hwmon; no fan headers were exposed (load your board's Super-IO driver, e.g. nct6775 / it87).",
            _ when fans.All(f => !f.CanControl) => "Linux: fan telemetry is live. Manual control needs write access to the hwmon pwm files (run as root or add a udev rule).",
            _ => "Linux: telemetry is live. Apply sets a manual PWM duty; Auto hands the header back to the chip.",
        };

        return new HardwareSnapshot(
            now,
            package,
            allCpuTemps.Count == 0 ? null : allCpuTemps.Average(),
            allCpuTemps.Count == 0 ? package : Math.Max(allCpuTemps.Max(), package ?? double.MinValue),
            load,
            clock,
            power,
            vcore,
            gpu?.Temp,
            gpu?.Hotspot,
            gpu?.Load,
            gpu?.ClockMhz,
            gpu?.Watts,
            cpuFan,
            cpuReadings,
            gpuReadings,
            fans,
            status,
            CpuName(),
            gpu?.Name,
            gpu?.VramUsedMb,
            gpu?.VramTotalMb);
    }

    internal static bool IsCpuChip(string name) =>
        name is "coretemp" or "k10temp" or "zenpower" or "cpu_thermal" or "soc_thermal" or "cpu-thermal";

    internal static bool IsPackageLabel(string chip, string label) =>
        label.StartsWith("Package", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Tctl", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Tdie", StringComparison.OrdinalIgnoreCase)
        || label.Equals("CPU", StringComparison.OrdinalIgnoreCase)
        || chip is "cpu_thermal" or "soc_thermal" or "cpu-thermal";

    internal IEnumerable<(string Label, double Celsius)> Temperatures(HwmonChip chip)
    {
        foreach (var input in Inputs(chip.Path, "temp"))
        {
            if (ReadLong(input.File) is not long milli) continue;
            double c = milli / 1000.0;
            if (c is < -40 or > 150) continue; // disconnected probes report nonsense
            yield return (ReadText(input.Label)?.Trim() is { Length: > 0 } label ? label : $"temp{input.Index}", c);
        }
    }

    internal IEnumerable<(string Label, double Volts)> Voltages(HwmonChip chip)
    {
        foreach (var input in Inputs(chip.Path, "in"))
        {
            if (ReadText(input.Label)?.Trim() is not { Length: > 0 } label) continue;
            if (ReadLong(input.File) is long mv) yield return (label, mv / 1000.0);
        }
    }

    /// <summary>Numbered <c>{prefix}N_input</c> files of a chip with their <c>_label</c> companions.</summary>
    private static IEnumerable<(int Index, string File, string Label)> Inputs(string chipPath, string prefix)
    {
        if (!Directory.Exists(chipPath)) yield break;
        var found = new List<(int, string, string)>();
        foreach (var file in Directory.GetFiles(chipPath, prefix + "*_input"))
        {
            var name = Path.GetFileName(file);
            var digits = name.Substring(prefix.Length, name.Length - prefix.Length - "_input".Length);
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) continue;
            found.Add((index, file, Path.Combine(chipPath, $"{prefix}{index}_label")));
        }
        foreach (var item in found.OrderBy(f => f.Item1)) yield return item;
    }

    private IEnumerable<(string Type, double Celsius)> ThermalZones()
    {
        var dir = P("sys/class/thermal");
        if (!Directory.Exists(dir)) yield break;
        foreach (var zone in Directory.GetDirectories(dir, "thermal_zone*").OrderBy(z => z, StringComparer.Ordinal))
        {
            if (ReadText(Path.Combine(zone, "type"))?.Trim() is not { } type) continue;
            if (ReadLong(Path.Combine(zone, "temp")) is long milli) yield return (type, milli / 1000.0);
        }
    }

    internal double? CpuLoad()
    {
        var line = ReadText(P("proc/stat"))?.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(s => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0UL).ToArray();
        if (parts.Length < 4) return null;
        ulong idle = parts[3] + (parts.Length > 4 ? parts[4] : 0); // idle + iowait
        ulong total = 0;
        foreach (var v in parts.Take(8)) total += v; // user..steal (guest is already counted in user)
        var last = _lastCpu;
        _lastCpu = (idle, total);
        if (last is not { } prev || total <= prev.Total) return null;
        double busy = 1 - (double)(idle - Math.Min(idle, prev.Idle)) / (total - prev.Total);
        return Math.Clamp(busy * 100, 0, 100);
    }

    internal double? CpuClockMhz()
    {
        var cpuDir = P("sys/devices/system/cpu");
        if (Directory.Exists(cpuDir))
        {
            var khz = Directory.GetDirectories(cpuDir, "cpu*")
                .Select(d => ReadLong(Path.Combine(d, "cpufreq", "scaling_cur_freq")))
                .Where(v => v is > 0).Select(v => v!.Value).ToList();
            if (khz.Count > 0) return khz.Average() / 1000.0;
        }
        var mhz = ReadText(P("proc/cpuinfo"))?.Split('\n')
            .Where(l => l.StartsWith("cpu MHz", StringComparison.Ordinal))
            .Select(l => double.TryParse(l.Split(':').Last().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Where(v => v > 0).ToList();
        return mhz is { Count: > 0 } ? mhz.Average() : null;
    }

    internal string? CpuName()
    {
        var info = ReadText(P("proc/cpuinfo"));
        if (info is null) return null;
        foreach (var key in new[] { "model name", "Model", "Hardware" })
        {
            var line = info.Split('\n').FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal) && l.Contains(':'));
            if (line is not null) return line[(line.IndexOf(':') + 1)..].Trim();
        }
        return null;
    }

    private double? RaplPackageWatts()
    {
        if (ReadLong(P("sys/class/powercap/intel-rapl:0/energy_uj")) is not long uj) return null; // root-only on most kernels
        long ticks = Stopwatch.GetTimestamp();
        var last = _lastRapl;
        _lastRapl = ((ulong)uj, ticks);
        if (last is not { } prev || (ulong)uj < prev.EnergyUj) return null;
        double seconds = (ticks - prev.Ticks) / (double)Stopwatch.Frequency;
        return seconds > 0.05 ? ((ulong)uj - prev.EnergyUj) / 1e6 / seconds : null;
    }

    private GpuInfo? AmdGpu(IReadOnlyList<HwmonChip> chips, List<GpuReadingSnapshot> readings)
    {
        var chip = chips.FirstOrDefault(c => c.Name == "amdgpu");
        if (chip is null) return null;
        double? edge = null, junction = null;
        foreach (var (label, value) in Temperatures(chip))
        {
            readings.Add(new GpuReadingSnapshot($"GPU {label}", $"{value:F1} C", "Temperature"));
            if (label.Equals("edge", StringComparison.OrdinalIgnoreCase)) edge = value;
            else if (label.Equals("junction", StringComparison.OrdinalIgnoreCase)) junction = value;
            else edge ??= value;
        }
        string device = Path.Combine(chip.Path, "device");
        double? load = ReadLong(Path.Combine(device, "gpu_busy_percent"));
        double? clock = ReadLong(Path.Combine(chip.Path, "freq1_input")) is long hz ? hz / 1e6 : null;
        double? watts = (ReadLong(Path.Combine(chip.Path, "power1_average")) ?? ReadLong(Path.Combine(chip.Path, "power1_input"))) is long uw ? uw / 1e6 : null;
        double? used = ReadLong(Path.Combine(device, "mem_info_vram_used")) is long u ? u / 1048576.0 : null;
        double? total = ReadLong(Path.Combine(device, "mem_info_vram_total")) is long t ? t / 1048576.0 : null;
        if (load is double l) readings.Add(new GpuReadingSnapshot("GPU Core", $"{l:F0}%", "Load"));
        if (clock is double c) readings.Add(new GpuReadingSnapshot("GPU Core", $"{c:F0} MHz", "Clock"));
        if (watts is double w) readings.Add(new GpuReadingSnapshot("GPU Package", $"{w:F1} W", "Power"));
        string name = ReadText(Path.Combine(device, "product_name"))?.Trim() is { Length: > 0 } pn ? pn : "AMD Radeon GPU (amdgpu)";
        return new GpuInfo(name, edge, junction, load, clock, watts, used, total);
    }

    private GpuInfo? NvidiaGpu(List<GpuReadingSnapshot> readings)
    {
        if (_nvidiaMissing || DateTime.UtcNow < _nvidiaRetryAfter) return null;
        var output = _runTool("nvidia-smi",
            "--query-gpu=name,temperature.gpu,utilization.gpu,clocks.gr,power.draw,memory.used,memory.total --format=csv,noheader,nounits");
        if (output is null)
        {
            _nvidiaMissing = true; // no driver / tool: never spawn it again this session
            return null;
        }
        var line = output.Split('\n').FirstOrDefault(l => l.Contains(','));
        if (line is null)
        {
            _nvidiaRetryAfter = DateTime.UtcNow.AddSeconds(30);
            return null;
        }
        var f = line.Split(',').Select(s => s.Trim()).ToArray();
        static double? Num(string[] a, int i) => i < a.Length && double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var info = new GpuInfo(f[0], Num(f, 1), null, Num(f, 2), Num(f, 3), Num(f, 4), Num(f, 5), Num(f, 6));
        if (info.Temp is double t) readings.Add(new GpuReadingSnapshot("GPU Core", $"{t:F1} C", "Temperature"));
        if (info.Load is double l) readings.Add(new GpuReadingSnapshot("GPU Core", $"{l:F0}%", "Load"));
        if (info.ClockMhz is double c) readings.Add(new GpuReadingSnapshot("GPU Core", $"{c:F0} MHz", "Clock"));
        if (info.Watts is double w) readings.Add(new GpuReadingSnapshot("GPU Package", $"{w:F1} W", "Power"));
        return info;
    }

    // ── Fans ─────────────────────────────────────────────────────────────

    internal List<FanChannelSnapshot> Fans(IReadOnlyList<HwmonChip> chips)
    {
        var fans = new List<FanChannelSnapshot>();
        foreach (var chip in chips)
        {
            foreach (var input in Inputs(chip.Path, "fan"))
            {
                double? rpm = ReadLong(input.File);
                string label = ReadText(input.Label)?.Trim() is { Length: > 0 } l ? l : $"{chip.Name} fan{input.Index}";
                string pwm = Path.Combine(chip.Path, $"pwm{input.Index}");
                double? duty = ReadLong(pwm) is long raw ? Math.Round(raw / 2.55, 0) : null;
                bool hasPwm = File.Exists(pwm);
                bool canControl = hasPwm && IsWritable(pwm) && IsWritable(pwm + "_enable");
                var kind = label.Contains("pump", StringComparison.OrdinalIgnoreCase) ? FanDeviceKind.Pump : FanDeviceKind.Fan;
                string message = !hasPwm ? "This header reports RPM only (no PWM file)."
                    : canControl ? "Manual PWM duty is available."
                    : "Manual control needs write access to the hwmon pwm files (root or a udev rule).";
                fans.Add(new FanChannelSnapshot(FanId(chip, input.Index), label, rpm, duty, canControl, kind, message,
                    $"hwmon:{chip.Name}", input.Index));
            }
        }
        return fans;
    }

    internal static string FanId(HwmonChip chip, int index) => $"linux/{Path.GetFileName(chip.Path)}/{chip.Name}/fan{index}";

    /// <summary>Resolves a fan channel id back to its pwm file (null when it does not exist).</summary>
    internal string? PwmFor(string channelId)
    {
        var parts = channelId.Split('/');
        if (parts.Length != 4 || parts[0] != "linux" || !parts[3].StartsWith("fan", StringComparison.Ordinal)) return null;
        var chip = Chips().FirstOrDefault(c => Path.GetFileName(c.Path) == parts[1]);
        if (chip is null) return null;
        var pwm = Path.Combine(chip.Path, "pwm" + parts[3][3..]);
        return File.Exists(pwm) ? pwm : null;
    }

    public SetFanControlResult SetDuty(string channelId, double percent)
    {
        if (PwmFor(channelId) is not { } pwm) return new SetFanControlResult(false, "This fan header has no PWM control on Linux.");
        try
        {
            string enable = pwm + "_enable";
            if (!_savedEnable.ContainsKey(pwm) && ReadText(enable)?.Trim() is { Length: > 0 } original)
                _savedEnable[pwm] = original;
            File.WriteAllText(enable, "1");
            int raw = (int)Math.Round(Math.Clamp(percent, 0, 100) * 2.55);
            File.WriteAllText(pwm, raw.ToString(CultureInfo.InvariantCulture));
            return new SetFanControlResult(true, $"Fan set to {percent:F0}% (PWM {raw}).");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new SetFanControlResult(false, "Linux needs write access to the hwmon pwm files: run FANZi as root or add a udev rule.");
        }
    }

    public SetFanControlResult RestoreAutomatic(string channelId)
    {
        if (PwmFor(channelId) is not { } pwm) return new SetFanControlResult(false, "This fan header has no PWM control on Linux.");
        try
        {
            // Back to what the chip had before FANZi touched it (2 = chip automatic on most drivers).
            string value = _savedEnable.TryGetValue(pwm, out var saved) && saved != "1" ? saved : "2";
            File.WriteAllText(pwm + "_enable", value);
            _savedEnable.Remove(pwm);
            return new SetFanControlResult(true, "Fan returned to automatic control.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new SetFanControlResult(false, "Linux needs write access to the hwmon pwm files: run FANZi as root or add a udev rule.");
        }
    }

    // ── IO helpers ─────────────────────────────────────────────────────────

    internal static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static long? ReadLong(string path)
        => ReadText(path)?.Trim() is { } s && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool IsWritable(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); // opened, nothing written
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    internal static string? RunTool(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch { } return null; }
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
