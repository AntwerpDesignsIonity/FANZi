using Fanzi.FanControl.AI;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.RenderTests;

/// <summary>Realistic sensor values for one render (normal or hot).</summary>
internal sealed record SampleState(
    double CpuPackage, double CpuAverage, double CpuHotspot, double CpuLoad,
    double GpuCore, double GpuHotspot, double GpuLoad,
    double CpuFanRpm, double CpuFanPercent, double Health)
{
    public static SampleState Normal { get; } = new(72, 66.4, 78.5, 38, 64, 71.2, 47, 1180, 46, 88);
    public static SampleState Hot { get; } = new(96, 91.3, 99.1, 97, 88, 97.4, 99, 2650, 100, 41) { RunningGame = "Cyberpunk2077" };

    /// <summary>Process the fake game detector reports (null = desktop) — never the build machine's process list.</summary>
    public string? RunningGame { get; init; }
}

/// <summary>Returns a fixed, realistic hardware snapshot — no drivers, no ring-0 access.</summary>
internal sealed class FakeHardwareMonitorService : IHardwareMonitorService
{
    private readonly SampleState _s;

    public FakeHardwareMonitorService(SampleState state) => _s = state;

    public string? InitializationError => null;

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var cpuFan = new FanChannelSnapshot("mb/fan/0", "CPU_FAN", _s.CpuFanRpm, _s.CpuFanPercent, true,
            FanDeviceKind.Fan, "Software control available (Nuvoton NCT6798D).", "Motherboard", 0);
        var pump = new FanChannelSnapshot("mb/fan/1", "AIO_PUMP", 2400 + _s.CpuLoad * 4, 70, true,
            FanDeviceKind.Pump, "Pump header — keep above 60 %.", "Motherboard", 1);
        var fans = new[]
        {
            cpuFan,
            pump,
            new FanChannelSnapshot("mb/fan/2", "SYS_FAN1 (front intake)", _s.CpuFanRpm * 0.82, 40, true,
                FanDeviceKind.Fan, "Software control available.", "Motherboard", 2),
            new FanChannelSnapshot("mb/fan/3", "SYS_FAN2 (rear exhaust)", _s.CpuFanRpm * 0.9, 45, true,
                FanDeviceKind.Fan, "Software control available.", "Motherboard", 3),
            new FanChannelSnapshot("gpu/fan/0", "GPU Fan", 900 + _s.GpuLoad * 14, null, false,
                FanDeviceKind.Fan, "Controlled by the GPU driver (read only).", "GPU", 0),
        };

        var cpuReadings = new List<CpuReadingSnapshot>
        {
            new("CPU Package", $"{_s.CpuPackage:F1} °C", "Temperature"),
            new("Core Max", $"{_s.CpuHotspot:F1} °C", "Temperature"),
            new("Core Average", $"{_s.CpuAverage:F1} °C", "Temperature"),
        };
        for (int i = 0; i < 8; i++)
        {
            double wave = Math.Sin(i * 0.9) * 4.5;
            cpuReadings.Add(new($"CPU Core #{i + 1}", $"{_s.CpuAverage + wave:F1} °C", "Temperature"));
        }
        for (int i = 0; i < 8; i++)
            cpuReadings.Add(new($"CPU Core #{i + 1}", $"{Math.Clamp(_s.CpuLoad + Math.Cos(i) * 12, 0, 100):F0} %", "Load"));
        cpuReadings.Add(new("CPU Total", $"{_s.CpuLoad:F0} %", "Load"));
        cpuReadings.Add(new("Package", $"{65 + _s.CpuLoad * 0.9:F1} W", "Power"));
        cpuReadings.Add(new("Core (Avg)", "4725 MHz", "Clock"));

        var gpuReadings = new List<GpuReadingSnapshot>
        {
            new("GPU Core", $"{_s.GpuCore:F1} °C", "Temperature"),
            new("GPU Hot Spot", $"{_s.GpuHotspot:F1} °C", "Temperature"),
            new("GPU Memory Junction", $"{_s.GpuCore + 10:F1} °C", "Temperature"),
            new("GPU Core", $"{_s.GpuLoad:F0} %", "Load"),
            new("GPU Memory", "41 %", "Load"),
            new("GPU Package", $"{90 + _s.GpuLoad * 2.1:F1} W", "Power"),
            new("GPU Core", "2610 MHz", "Clock"),
        };

        var snapshot = new HardwareSnapshot(
            DateTimeOffset.Now,
            _s.CpuPackage, _s.CpuAverage, _s.CpuHotspot, _s.CpuLoad,
            4725, 65 + _s.CpuLoad * 0.9, 1.284,
            _s.GpuCore, _s.GpuHotspot, _s.GpuLoad, 2610, 90 + _s.GpuLoad * 2.1,
            cpuFan, cpuReadings, gpuReadings, fans,
            "Monitoring 5 fan/pump channels · 27 sensors",
            "AMD Ryzen 7 7800X3D 8-Core Processor",
            "NVIDIA GeForce RTX 4080 SUPER",
            6900, 16376);
        return Task.FromResult(snapshot);
    }

    public Task<SetFanControlResult> SetFanControlAsync(string channelId, double percent, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SetFanControlResult(true, $"{channelId} → {percent:F0}% (render harness)"));

    public Task<SetFanControlResult> RestoreAutomaticControlAsync(string channelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SetFanControlResult(true, $"{channelId} → auto (render harness)"));

    public void Dispose() { }
}

/// <summary>In-memory settings with a few realistic profiles; slow polling so renders stay deterministic.</summary>
internal sealed class FakeSettingsService : ISettingsService
{
    public Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var balanced = new FanProfile { Name = "Balanced", FanCurve = SmartFanCurveEngine.GenerateDefaultCurve(), UseFanCurve = true };
        var settings = new AppSettings
        {
            SettingsVersion = AppSettings.CurrentVersion,
            PollingIntervalSeconds = 60,
            ReducedPollingIntervalSeconds = 120,
            Profiles =
            {
                balanced,
                new FanProfile { Name = "Gaming", CpuFanDesiredPercent = 70, FanCurve = SmartFanCurveEngine.GenerateDefaultCurve() },
                new FanProfile { Name = "Silent", CpuFanDesiredPercent = 30, FanCurve = SmartFanCurveEngine.GenerateSilentCurve() },
            },
            ActiveProfileId = balanced.Id,
            AiAnomalyDetection = false, // keeps the anomaly banner out of the steady-state renders
        };
        return Task.FromResult(settings);
    }

    /// <summary>Last settings object handed to <see cref="SaveAsync"/> and how many saves happened.</summary>
    public AppSettings? LastSaved { get; private set; }
    public int SaveCount { get; private set; }

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        LastSaved = settings;
        SaveCount++;
        return Task.CompletedTask;
    }
}

/// <summary>OpenRGB SDK stand-in with a typical rig: RAM, motherboard, GPU and an ARGB fan hub.</summary>
internal sealed class FakeRgbService : IRgbService
{
    public bool IsConnected { get; private set; }
    public string ServerVersion => "OpenRGB 1.0rc1 (render harness)";
    public string ModeSwitchStatus => "";
    public string? LastDeviceQueryError => null;

    public Task<bool> TryConnectAsync(string host = "localhost", int port = 6742, CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<RgbDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RgbDeviceInfo>>(new[]
        {
            new RgbDeviceInfo(0, "ASUS ROG STRIX X670E-F", "Motherboard", 24, true)
            {
                Zones = new[] { new RgbZoneInfo(0, 0, "Aura Mainboard", 8), new RgbZoneInfo(0, 1, "ARGB Header 1", 16) },
            },
            new RgbDeviceInfo(1, "G.Skill Trident Z5 RGB", "DRAM", 16, true)
            {
                Zones = new[] { new RgbZoneInfo(1, 0, "DIMM A2", 8), new RgbZoneInfo(1, 1, "DIMM B2", 8) },
            },
            new RgbDeviceInfo(2, "NVIDIA GeForce RTX 4080 SUPER", "GPU", 12, true)
            {
                Zones = new[] { new RgbZoneInfo(2, 0, "Logo", 12) },
            },
        });

    public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetDeviceColorsAsync(int deviceIndex, RgbColor[] colors, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetAllDevicesColorAsync(RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetDeviceZoneColorsAsync(int deviceIndex, RgbColor[] zoneColors, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Disconnect() => IsConnected = false;
    public void Dispose() { }
}
