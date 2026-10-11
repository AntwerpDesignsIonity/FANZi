using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Posix;

namespace Fanzi.FanControl.Tests;

/// <summary>
/// 4.1 Linux / macOS sensor back-ends: the hwmon / procfs parser against a fake sysfs tree, the SMC value decoder,
/// and the factory that keeps LibreHardwareMonitor off non-Windows systems.
/// </summary>
public sealed class LinuxSensorReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fanzi-sysfs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A Ryzen board: k10temp (Tctl + 2 CCDs), nct6798 (Vcore, CPU fan with PWM, chassis fan RPM-only), amdgpu.</summary>
    private void SeedRyzenBoard()
    {
        Write("sys/class/hwmon/hwmon0/name", "k10temp\n");
        Write("sys/class/hwmon/hwmon0/temp1_input", "72125\n");
        Write("sys/class/hwmon/hwmon0/temp1_label", "Tctl\n");
        Write("sys/class/hwmon/hwmon0/temp3_input", "64000\n");
        Write("sys/class/hwmon/hwmon0/temp3_label", "Tccd1\n");
        Write("sys/class/hwmon/hwmon0/temp4_input", "68500\n");
        Write("sys/class/hwmon/hwmon0/temp4_label", "Tccd2\n");

        Write("sys/class/hwmon/hwmon1/name", "nct6798\n");
        Write("sys/class/hwmon/hwmon1/in0_input", "1284\n");
        Write("sys/class/hwmon/hwmon1/in0_label", "Vcore\n");
        Write("sys/class/hwmon/hwmon1/fan1_input", "820\n");
        Write("sys/class/hwmon/hwmon1/fan1_label", "SYSFAN1\n");
        Write("sys/class/hwmon/hwmon1/fan2_input", "1180\n");
        Write("sys/class/hwmon/hwmon1/fan2_label", "CPUFAN\n");
        Write("sys/class/hwmon/hwmon1/pwm2", "117\n");
        Write("sys/class/hwmon/hwmon1/pwm2_enable", "5\n");
        Write("sys/class/hwmon/hwmon1/temp9_input", "-128000\n"); // disconnected probe

        Write("sys/class/hwmon/hwmon2/name", "amdgpu\n");
        Write("sys/class/hwmon/hwmon2/temp1_input", "61000\n");
        Write("sys/class/hwmon/hwmon2/temp1_label", "edge\n");
        Write("sys/class/hwmon/hwmon2/temp2_input", "70000\n");
        Write("sys/class/hwmon/hwmon2/temp2_label", "junction\n");
        Write("sys/class/hwmon/hwmon2/freq1_input", "2400000000\n");
        Write("sys/class/hwmon/hwmon2/power1_average", "188700000\n");
        Write("sys/class/hwmon/hwmon2/device/gpu_busy_percent", "47\n");
        Write("sys/class/hwmon/hwmon2/device/mem_info_vram_used", (6860L * 1048576).ToString());
        Write("sys/class/hwmon/hwmon2/device/mem_info_vram_total", (16384L * 1048576).ToString());

        Write("proc/cpuinfo", "processor\t: 0\nmodel name\t: AMD Ryzen 7 7800X3D 8-Core Processor\ncpu MHz\t\t: 4725.000\n");
        Write("sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "4700000\n");
        Write("sys/devices/system/cpu/cpu1/cpufreq/scaling_cur_freq", "4750000\n");
        Write("proc/stat", "cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 1 2 3 4\n");
    }

    [Fact]
    public void RyzenBoard_TemperaturesClocksGpuAndVcore()
    {
        SeedRyzenBoard();
        var reader = new LinuxSensorReader(_root, (_, _) => null);
        var s = reader.Read(DateTimeOffset.UtcNow);

        Assert.Equal(72.125, s.CpuPackageTemperature);              // Tctl
        Assert.Equal((64 + 68.5) / 2, s.CpuAverageTemperature!.Value, 6); // CCDs
        Assert.Equal(72.125, s.CpuHotspotTemperature);
        Assert.Equal("AMD Ryzen 7 7800X3D 8-Core Processor", s.CpuName);
        Assert.Equal(4725, s.CpuAverageClockMhz!.Value, 6);
        Assert.Equal(1.284, s.CpuCoreVoltage!.Value, 6);

        Assert.Equal(61, s.GpuCoreTemperature);
        Assert.Equal(70, s.GpuHotspotTemperature);
        Assert.Equal(47, s.GpuLoadPercent);
        Assert.Equal(2400, s.GpuCoreClockMhz!.Value, 6);
        Assert.Equal(188.7, s.GpuPowerWatts!.Value, 6);
        Assert.Equal(6860, s.GpuMemoryUsedMb!.Value, 6);
        Assert.Equal(16384, s.GpuMemoryTotalMb!.Value, 6);
        Assert.Contains(s.CpuReadings, r => r.Name == "Tctl" && r.Kind == "Temperature" && r.Value == $"{72.1:F1} C");
        Assert.DoesNotContain(s.CpuReadings, r => r.Value.StartsWith("-128", StringComparison.Ordinal));
    }

    [Fact]
    public void Fans_CpuFanFirst_PwmDuty_AndRpmOnlyHeaders()
    {
        SeedRyzenBoard();
        var s = new LinuxSensorReader(_root, (_, _) => null).Read(DateTimeOffset.UtcNow);

        Assert.Equal(2, s.Fans.Count);
        var cpu = s.Fans[0];
        Assert.Equal("CPUFAN", cpu.Name);
        Assert.Same(cpu, s.CpuFan);
        Assert.Equal(1180, cpu.SpeedRpm);
        Assert.Equal(46, cpu.CurrentControlPercent); // 117 / 2.55
        var sys = s.Fans[1];
        Assert.False(sys.CanControl);
        Assert.Contains("RPM only", sys.CapabilityMessage);
    }

    [Fact]
    public void ManualDuty_WritesPwm_AndAutoRestoresTheChipMode()
    {
        SeedRyzenBoard();
        var reader = new LinuxSensorReader(_root, (_, _) => null);
        var cpu = reader.Read(DateTimeOffset.UtcNow).Fans[0];
        Assert.True(cpu.CanControl, "test files are writable");

        var set = reader.SetDuty(cpu.Id, 60);
        Assert.True(set.Success, set.Message);
        Assert.Equal("1", File.ReadAllText(Path.Combine(_root, "sys/class/hwmon/hwmon1/pwm2_enable")));
        Assert.Equal("153", File.ReadAllText(Path.Combine(_root, "sys/class/hwmon/hwmon1/pwm2")));

        var auto = reader.RestoreAutomatic(cpu.Id);
        Assert.True(auto.Success, auto.Message);
        Assert.Equal("5", File.ReadAllText(Path.Combine(_root, "sys/class/hwmon/hwmon1/pwm2_enable"))); // the chip's own mode

        Assert.False(reader.SetDuty("linux/hwmon1/nct6798/fan1", 50).Success); // RPM-only header
        Assert.False(reader.SetDuty("bogus", 50).Success);
    }

    [Fact]
    public void CpuLoad_ComesFromProcStatDeltas()
    {
        SeedRyzenBoard();
        var reader = new LinuxSensorReader(_root, (_, _) => null);
        Assert.Null(reader.Read(DateTimeOffset.UtcNow).CpuTotalLoadPercent); // first sample has no delta
        Write("proc/stat", "cpu  150 0 150 900 0 0 0 0 0 0\n");              // +100 busy, +100 idle
        Assert.Equal(50, reader.Read(DateTimeOffset.UtcNow).CpuTotalLoadPercent!.Value, 6);
    }

    [Fact]
    public void IntelLaptop_CoretempPackage_AndNvidiaSmi()
    {
        Write("sys/class/hwmon/hwmon3/name", "coretemp\n");
        Write("sys/class/hwmon/hwmon3/temp1_input", "58000\n");
        Write("sys/class/hwmon/hwmon3/temp1_label", "Package id 0\n");
        Write("sys/class/hwmon/hwmon3/temp2_input", "55000\n");
        Write("sys/class/hwmon/hwmon3/temp2_label", "Core 0\n");
        Write("sys/class/hwmon/hwmon3/temp3_input", "61000\n");
        Write("sys/class/hwmon/hwmon3/temp3_label", "Core 1\n");
        int calls = 0;
        var reader = new LinuxSensorReader(_root, (file, _) =>
        {
            calls++;
            return file == "nvidia-smi" ? "NVIDIA GeForce RTX 3080, 64, 47, 1905, 188.70, 6860, 10240\n" : null;
        });
        var s = reader.Read(DateTimeOffset.UtcNow);

        Assert.Equal(58, s.CpuPackageTemperature);
        Assert.Equal(58, s.CpuAverageTemperature);
        Assert.Equal(61, s.CpuHotspotTemperature);
        Assert.Equal("NVIDIA GeForce RTX 3080", s.GpuName);
        Assert.Equal(64, s.GpuCoreTemperature);
        Assert.Equal(1905, s.GpuCoreClockMhz);
        Assert.Equal(10240, s.GpuMemoryTotalMb);
        Assert.Contains("no fan headers", s.StatusMessage);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NoSensors_NoNvidia_ReportsWhy_AndStopsSpawningTheTool()
    {
        int calls = 0;
        var reader = new LinuxSensorReader(_root, (_, _) => { calls++; return null; });
        var s = reader.Read(DateTimeOffset.UtcNow);
        reader.Read(DateTimeOffset.UtcNow);
        Assert.Null(s.CpuPackageTemperature);
        Assert.Null(s.GpuName);
        Assert.Empty(s.Fans);
        Assert.Contains("sensors-detect", s.StatusMessage);
        Assert.Equal(1, calls); // nvidia-smi missing: tried once, not every poll
    }
}

public class MacSmcDecodeTests
{
    [Fact]
    public void Decodes_Sp78_Fpe2_Flt_AndIntegers()
    {
        Assert.Equal(64.5, MacSensorReader.Smc.Decode("sp78", new byte[] { 0x40, 0x80 }));
        Assert.Equal(1180, MacSensorReader.Smc.Decode("fpe2", new byte[] { 0x12, 0x70 }));
        Assert.Equal(52.25, MacSensorReader.Smc.Decode("flt ", BitConverter.GetBytes(52.25f))!.Value, 4);
        Assert.Equal(2, MacSensorReader.Smc.Decode("ui8 ", new byte[] { 2 }));
        Assert.Null(MacSensorReader.Smc.Decode("ch8*", new byte[] { 1, 2 }));
        Assert.Null(MacSensorReader.Smc.Decode("sp78", new byte[] { 1 }));
    }

    [Fact]
    public void FourCc_RoundTrips()
    {
        uint key = MacSensorReader.Smc.ToFourCc("TC0P");
        Assert.Equal(0x54433050u, key);
        Assert.Equal("TC0P", MacSensorReader.Smc.FourCc(key));
    }
}

public class HardwareMonitorFactoryTests
{
    [Fact]
    public void PicksThePosixBackEnd_OffWindows()
    {
        // On Windows the factory opens LibreHardwareMonitor's ring-0 driver — not something a unit test should do.
        if (OperatingSystem.IsWindows()) return;
        using var service = HardwareMonitorFactory.Create();
        Assert.IsType<PosixHardwareMonitorService>(service);
    }

    [Fact]
    public void PosixEmptySnapshot_CarriesTheReason()
    {
        var s = PosixHardwareMonitorService.Empty(DateTimeOffset.UtcNow, "why");
        Assert.Equal("why", s.StatusMessage);
        Assert.Empty(s.Fans);
    }
}
