using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using OpenRGB.NET;

namespace Fanzi.FanControl.Tests;

/// <summary>
/// End-to-end check on real hardware: FANZi starts its own OpenRGB server (private config),
/// colours every device through <see cref="OpenRgbService"/>, and an independent SDK client reads
/// back what OpenRGB applied. Opt-in: set FANZI_HARDWARE_TESTS=1 (needs OpenRGB and RGB devices).
/// </summary>
[Trait("Category", "Hardware")]
public class RgbLiveHardwareTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FANZI_HARDWARE_TESTS") == "1";

    [Fact]
    public async Task Every_controllable_device_takes_the_colour()
    {
        if (!Enabled) return;

        using var manager = new OpenRgbServerManager();
        manager.DetectInstallation();
        Assert.True(manager.IsInstalled, "OpenRGB is not installed");

        bool started = await manager.RestartForCompatibilityAsync(6742);
        Assert.True(started, manager.Status);

        using var svc = new OpenRgbService();
        bool connected = false;
        for (int i = 0; i < 30 && !connected; i++)
        {
            connected = await svc.TryConnectAsync("127.0.0.1", 6742);
            if (!connected) await Task.Delay(1000);
        }
        Assert.True(connected, svc.ServerVersion);

        var devices = await svc.GetDevicesAsync();
        Assert.NotEmpty(devices);
        Assert.False(svc.ModeChangesRejected, "server refused mode changes: " + svc.ModeSwitchStatus);

        var report = new List<string> { $"control: {svc.ModeSwitchStatus}" };
        foreach (var color in new[] { new RgbColor(255, 0, 0), new RgbColor(0, 255, 0), new RgbColor(0, 0, 255) })
        {
            await svc.SetAllDevicesColorAsync(color);
            await Task.Delay(400);

            using var probe = new OpenRgbClient("127.0.0.1", 6742, "FANZI-VERIFY", autoConnect: true, timeoutMs: 5000, protocolVersionNumber: 4);
            foreach (var d in devices.Where(d => d.IsControllable))
            {
                var live = probe.GetControllerData(d.DeviceIndex);
                var shown = live.ActiveMode.ColorMode == ColorMode.ModeSpecific
                    ? live.ActiveMode.Colors[0]
                    : live.Colors[0];
                report.Add($"{d.Name} [{d.ControlLabel}] -> mode {live.ActiveMode.Name}, colour {shown.R},{shown.G},{shown.B}");

                Assert.True(shown.R == color.R && shown.G == color.G && shown.B == color.B,
                    $"{d.Name} shows {shown.R},{shown.G},{shown.B} in {live.ActiveMode.Name}, expected {color.R},{color.G},{color.B}");
            }
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "fanzi-rgb-live-report.txt"), report);
        manager.StopServer();
    }
}
