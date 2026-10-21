using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.Rgb;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace Fanzi.FanControl.Tests;

/// <summary>4.4: MAXSUN Sync RAM path (protocol taken from MAXSUN Sync 2 / MaxsunSyncService, 2026-09).</summary>
public class MaxsunSyncClientTests
{
    private static readonly Guid EneRgb = new("05B8E7A3-7D5D-41DE-B6CC-5E6205837FB7");
    private static readonly Guid EneAio = new("84D8717B-4E1F-4607-9C1A-14E1BDB7B675");

    private static string Capab(string name, int leds, int type = 458752) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\" ?>\n<root>\n<version>1</version>\n<type>" + type +
        "</type>\n<device>\n<name>" + name + "</name>\n<id>0</id>\n<layout>\n<led_count>" + leds +
        "</led_count>\n</layout>\n<manufacturer>Ene</manufacturer>\n</device>\n</root>\n";

    [Fact]
    public void Colours_are_alpha_blue_green_red()
    {
        Assert.Equal("FF0000FF", MaxsunSyncClient.FormatColor(new RgbColor(255, 0, 0)));
        Assert.Equal("FFFFFF00", MaxsunSyncClient.FormatColor(new RgbColor(0, 255, 255)));
        Assert.Equal("FF336699", MaxsunSyncClient.FormatColor(new RgbColor(0x99, 0x66, 0x33)));
    }

    [Fact]
    public void Frame_is_fitted_to_the_led_count()
    {
        Assert.Equal(string.Join(",", Enumerable.Repeat("FF0000FF", 8)), MaxsunSyncClient.FormatColors(new[] { RgbColor.Red }, 8));
        string two = MaxsunSyncClient.FormatColors(new[] { RgbColor.Red, new RgbColor(0, 0, 255) }, 4);
        Assert.Equal("FF0000FF,FF0000FF,FFFF0000,FFFF0000", two);
        Assert.Equal(3, MaxsunSyncClient.FormatColors(Array.Empty<RgbColor>(), 3).Split(',').Length);
    }

    [Fact]
    public void Static_command_matches_maxsun_sync()
    {
        var stick = new MaxsunLedDevice(EneRgb, 1, "ENE_RGB_For_ASUS1", 2, MaxsunLedDevice.DramDeviceType, "Ene");
        Assert.Equal("light /guids 05b8e7a3-7d5d-41de-b6cc-5e6205837fb7 /d 1 /m Static /colors FF0000FF,FF0000FF /brightness 100",
            MaxsunSyncClient.StaticCommand(stick, new[] { RgbColor.Red }, switchMode: true));
        Assert.StartsWith("update /guids", MaxsunSyncClient.StaticCommand(stick, new[] { RgbColor.Red }, switchMode: false));
    }

    [Fact]
    public void Capab_reply_is_parsed_and_errors_end_the_scan()
    {
        var d = MaxsunSyncClient.ParseCapab(Capab("ENE_RGB_For_ASUS0", 8), EneRgb, 0);
        Assert.NotNull(d);
        Assert.Equal(8, d!.LedCount);
        Assert.True(d.IsDram);
        Assert.Null(MaxsunSyncClient.ParseCapab("Error (HRESULT: 0x8007000e)", EneRgb, 2));
        Assert.Null(MaxsunSyncClient.ParseCapab("", EneRgb, 0));
        Assert.Null(MaxsunSyncClient.ParseCapab("<root><broken", EneRgb, 0));
        var board = MaxsunSyncClient.ParseCapab(Capab("MAXSUN MOTHERBOARD LED ENE", 390, 65545), MaxsunLedDevice.BoardHalGuid, 0);
        Assert.False(board!.IsDram);
    }

    [Fact]
    public void Ok_replies_are_recognised()
    {
        Assert.True(MaxsunSyncClient.IsOk("Set effect to device[0] with GUID: {05B8E7A3-7D5D-41DE-B6CC-5E6205837FB7}"));
        Assert.True(MaxsunSyncClient.IsOk("Update device[1] with GUID: {05B8E7A3-7D5D-41DE-B6CC-5E6205837FB7}"));
        Assert.False(MaxsunSyncClient.IsOk("Error (HRESULT: 0x80004005)"));
        Assert.False(MaxsunSyncClient.IsOk("ERROR: Missing /d parameter in command "));
        Assert.False(MaxsunSyncClient.IsOk(""));
    }

    [Fact]
    public void Only_the_first_dram_hal_drives_the_sticks()
    {
        var list = new[]
        {
            new MaxsunLedDevice(EneRgb, 0, "ENE0", 8, MaxsunLedDevice.DramDeviceType, ""),
            new MaxsunLedDevice(EneRgb, 1, "ENE1", 8, MaxsunLedDevice.DramDeviceType, ""),
            new MaxsunLedDevice(EneAio, 0, "AIO0", 8, MaxsunLedDevice.DramDeviceType, ""),
            new MaxsunLedDevice(MaxsunLedDevice.BoardHalGuid, 0, "Board", 390, 65545, ""),
        };
        var kept = MaxsunSyncClient.TrimDram(list, installedDimms: 0);
        Assert.Equal(new[] { "ENE0", "ENE1", "Board" }, kept.Select(d => d.Name));
        Assert.Equal(new[] { "ENE0", "Board" }, MaxsunSyncClient.TrimDram(list, installedDimms: 1).Select(d => d.Name));
    }

    [Fact]
    public async Task Discovery_and_frames_over_a_loopback_service()
    {
        using var server = new FakeMaxsunService(cmd =>
        {
            if (cmd.StartsWith("capab /guids 05b8e7a3", StringComparison.Ordinal))
                return cmd.EndsWith("/d 0") || cmd.EndsWith("/d 1") ? Capab("ENE_RGB_For_ASUS" + cmd[^1], 8) : "Error (HRESULT: 0x8007000e)";
            if (cmd.StartsWith("capab /guids 1995564f", StringComparison.Ordinal))
                return cmd.EndsWith("/d 0") ? Capab("MAXSUN MOTHERBOARD LED ENE", 390, 65545) : "Error (HRESULT: 0x8007000e)";
            if (cmd.StartsWith("capab", StringComparison.Ordinal)) return "Error (HRESULT: 0x80004005)";
            if (cmd.StartsWith("light", StringComparison.Ordinal)) return "Set effect to device[0] with GUID: {X}";
            if (cmd.StartsWith("update", StringComparison.Ordinal)) return "Update device[0] with GUID: {X}";
            return cmd == "resync" ? "Resynced" : "Unknown command";
        }, splitWrites: true);

        var client = new MaxsunSyncClient(server.Port, TimeSpan.FromSeconds(2));
        Assert.True(client.IsReachable());
        var devices = client.DiscoverDevices(MaxsunSyncRgbService.InstalledHalGuids(), 0);
        Assert.Equal(3, devices.Count);
        Assert.Equal(2, devices.Count(d => d.IsDram));
        Assert.Equal(390, devices.Single(d => !d.IsDram).LedCount);
        Assert.DoesNotContain(server.Commands, c => c.Contains("84d8717b")); // slower AIO HAL never asked once ENE_RGB answered

        var rgb = new MaxsunSyncRgbService(new NullRgbService(), client);
        Assert.True(await rgb.TryConnectAsync());
        var list = await rgb.GetDevicesAsync();
        Assert.Equal(3, list.Count);
        var ram = list.Where(d => d.Category == RgbDeviceCategory.Ram).ToList();
        Assert.Equal(2, ram.Count);
        Assert.Contains("MAXSUN Sync", rgb.RamBusStatus);

        await rgb.SetDeviceColorAsync(ram[0].DeviceIndex, RgbColor.Red);
        await rgb.SetDeviceColorAsync(ram[0].DeviceIndex, RgbColor.Red); // unchanged → not re-sent
        Assert.Single(server.Commands, c => c.StartsWith("light", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("update", StringComparison.Ordinal));

        await Task.Delay(MaxsunSyncRgbService.MinFrameInterval + TimeSpan.FromMilliseconds(30));
        await rgb.SetDeviceColorAsync(ram[0].DeviceIndex, RgbColor.Blue);
        Assert.Contains(server.Commands, c => c.StartsWith("update", StringComparison.Ordinal) && c.Contains("/m Static"));

        Assert.Equal(2, await rgb.ResyncSmbusDevicesAsync(RgbColor.Green));
        Assert.Contains("resync", server.Commands);
    }

    [Fact]
    public async Task Without_the_service_it_is_a_pass_through()
    {
        int freePort;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); freePort = ((IPEndPoint)l.LocalEndpoint).Port; }
        var rgb = new MaxsunSyncRgbService(new NullRgbService(), new MaxsunSyncClient(freePort, TimeSpan.FromMilliseconds(300)));
        Assert.False(await rgb.TryConnectAsync());
        Assert.Empty(await rgb.GetDevicesAsync());
        Assert.Equal("", rgb.RamBusStatus);
    }

    [Theory]
    [InlineData(HardwareType.SuperIO, HardwareType.Motherboard, true)]
    [InlineData(HardwareType.EmbeddedController, null, true)]
    [InlineData(HardwareType.GpuNvidia, null, false)]
    [InlineData(HardwareType.Cooler, null, false)]
    public void Bios_fan_control_covers_motherboard_headers_only(HardwareType type, HardwareType? parent, bool expected) =>
        Assert.Equal(expected, HardwareMonitorService.IsBoardHeader(type, parent));

    private sealed class NullRgbService : IRgbService
    {
        public bool IsConnected => false;
        public string ServerVersion => "offline";
        public string ModeSwitchStatus => "";
        public Task<bool> TryConnectAsync(string host = "localhost", int port = 6742, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<RgbDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RgbDeviceInfo>>(Array.Empty<RgbDeviceInfo>());
        public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default) => throw new InvalidOperationException("must not reach OpenRGB");
        public Task SetDeviceColorsAsync(int deviceIndex, RgbColor[] colors, CancellationToken cancellationToken = default) => throw new InvalidOperationException("must not reach OpenRGB");
        public Task SetAllDevicesColorAsync(RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDeviceZoneColorsAsync(int deviceIndex, RgbColor[] zoneColors, CancellationToken cancellationToken = default) => throw new InvalidOperationException("must not reach OpenRGB");
        public void Disconnect() { }
        public void Dispose() { }
    }

    /// <summary>One command per connection, like MaxsunSyncService; optionally answers in small TCP segments.</summary>
    private sealed class FakeMaxsunService : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _commands = new();
        public int Port { get; }
        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }

        public FakeMaxsunService(Func<string, string> handler, bool splitWrites)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            var s = c.GetStream();
                            var buf = new byte[65536];
                            var sb = new StringBuilder();
                            while (!sb.ToString().Contains('\n'))
                            {
                                int n = await s.ReadAsync(buf);
                                if (n <= 0) return; // probe connection (IsReachable)
                                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                            }
                            string cmd = sb.ToString().TrimEnd('\n');
                            lock (_commands) _commands.Add(cmd);
                            byte[] reply = Encoding.UTF8.GetBytes(handler(cmd));
                            if (!splitWrites) { await s.WriteAsync(reply); return; }
                            for (int off = 0; off < reply.Length; off += 700)
                            {
                                await s.WriteAsync(reply.AsMemory(off, Math.Min(700, reply.Length - off)));
                                await s.FlushAsync();
                                await Task.Delay(15);
                            }
                        }
                    });
                }
            });
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); }
    }
}
