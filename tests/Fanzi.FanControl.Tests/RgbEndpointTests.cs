using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.OpenRgb;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

/// <summary>4.1 OpenRGB endpoint: validation, saved settings, remote endpoints, control report and diagnostics.</summary>
[Collection("AppLog")]
public sealed class RgbEndpointTests : IDisposable
{
    private readonly TempAppLog _log = new();

    public void Dispose() => _log.Dispose();

    private static RgbControlViewModel CreateVm(out int launches)
    {
        int count = 0;
        var manager = new OpenRgbServerManager(
            () => { count++; return null; },
            (_, _) => Task.FromResult(OpenRgbInstallResult.Fail("not used")));
        launches = 0;
        return new RgbControlViewModel(new NoServerRgbService(), manager, () => DateTime.UtcNow, startBackgroundWork: false);
    }

    [Theory]
    [InlineData("127.0.0.1", 6742, null)]
    [InlineData("192.168.1.20", 6742, null)]
    [InlineData("gaming-pc", 6743, null)]
    [InlineData("gaming-pc.local", 1, null)]
    [InlineData("", 6742, "Enter the OpenRGB host")]
    [InlineData("http://192.168.1.20", 6742, "no http://")]
    [InlineData("pc one", 6742, "no http://")]
    [InlineData("127.0.0.1", 0, "1 to 65535")]
    [InlineData("127.0.0.1", 70000, "1 to 65535")]
    public void ValidateEndpoint_AcceptsHostsAndIps_RejectsUrlsAndBadPorts(string host, int port, string? error)
    {
        var result = RgbControlViewModel.ValidateEndpoint(host, port);
        if (error is null) Assert.Null(result);
        else Assert.Contains(error, result);
        Assert.NotNull(RgbControlViewModel.ValidateEndpoint("127.0.0.1", 6742.5m));
        Assert.NotNull(RgbControlViewModel.ValidateEndpoint("127.0.0.1", null));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("::1", true)]
    [InlineData("127.0.0.5", true)]
    [InlineData("", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("gaming-pc", false)]
    public void IsLocalHost(string host, bool local) => Assert.Equal(local, RgbControlViewModel.IsLocalHost(host));

    [Fact]
    public void SavedEndpoint_IsApplied_InvalidFallsBackToDefaults()
    {
        using var vm = CreateVm(out _);
        vm.ApplyEndpointSettings("192.168.1.20", 6750, (_, _) => { });
        Assert.Equal("192.168.1.20", vm.OpenRgbHost);
        Assert.Equal(6750, vm.OpenRgbPort);
        Assert.False(vm.EndpointIsLocal);
        Assert.Equal("192.168.1.20:6750", vm.EndpointLabel);
        Assert.Contains("Remote OpenRGB", vm.EndpointStatus);

        vm.ApplyEndpointSettings("http://bad", 99999, (_, _) => { });
        Assert.Equal("127.0.0.1", vm.OpenRgbHost);
        Assert.Equal(RgbControlViewModel.DefaultOpenRgbPort, vm.OpenRgbPort);

        vm.ApplyEndpointSettings(null, null, (_, _) => { });
        Assert.True(vm.EndpointIsLocal);
        Assert.Equal("127.0.0.1:6742", vm.EndpointLabel);
    }

    [Fact]
    public async Task ApplyEndpoint_InvalidInput_DoesNotChangeOrSave()
    {
        using var vm = CreateVm(out _);
        int saves = 0;
        vm.ApplyEndpointSettings(null, null, (_, _) => saves++);
        vm.EndpointHostInput = "https://pc";
        await vm.ApplyEndpointCommand.ExecuteAsync(null);
        Assert.Equal(0, saves);
        Assert.Equal("127.0.0.1", vm.OpenRgbHost);
        Assert.Contains("no http://", vm.EndpointStatus);
    }

    [Fact]
    public async Task RemoteEndpoint_IsSaved_UnreachableIsExplained_NoLocalServerManaged()
    {
        using var vm = CreateVm(out _);
        (string Host, int Port)? saved = null;
        vm.ApplyEndpointSettings(null, null, (h, p) => saved = (h, p));
        vm.EndpointHostInput = "192.0.2.1"; // TEST-NET-1: never routable
        vm.EndpointPortInput = 6742;
        await vm.ApplyEndpointCommand.ExecuteAsync(null);

        Assert.Equal(("192.0.2.1", 6742), saved);
        Assert.False(vm.IsConnected);
        Assert.False(vm.IsServerRunning);
        Assert.Contains("not reachable", vm.ConnectionStatus);
        Assert.Contains("SDK Server", vm.LastOpenRgbError);
        Assert.Contains("Not connected to 192.0.2.1:6742", vm.EndpointStatus);
    }

    [Fact]
    public void ControlReport_CountsControllableDevices_AndNamesTheRest()
    {
        var devices = new List<RgbDeviceInfo>
        {
            new(0, "ENE DRAM", "DRAM", 8, true),
            new(1, "ZOTAC RTX 3080", "GPU", 1, true) { IsControllable = true },
            new(2, "Logo Lamp", "Light", 1, true) { IsControllable = false },
        };
        string report = RgbControlViewModel.BuildControlReport(devices, "2 per-LED, 1 single colour", modeChangesRejected: true);
        Assert.StartsWith("2 of 3 device(s) under FANZi control — 2 per-LED, 1 single colour.", report);
        Assert.Contains("Logo Lamp: effects-only", report);
        Assert.Contains("legacy_workaround", report);
        Assert.Contains("no devices", RgbControlViewModel.BuildControlReport(Array.Empty<RgbDeviceInfo>(), "", false));
    }

    [Fact]
    public async Task TakeControl_WhenOffline_SaysHowToConnect()
    {
        using var vm = CreateVm(out _);
        vm.ApplyEndpointSettings(null, 6800, (_, _) => { });
        await vm.TakeControlCommand.ExecuteAsync(null);
        Assert.Contains("Not connected to OpenRGB (127.0.0.1:6800)", vm.ControlReport);
    }

    [Fact]
    public void Diagnostics_ListEndpointServerAndDevices()
    {
        using var vm = CreateVm(out _);
        vm.ApplyEndpointSettings("10.0.0.7", 6742, (_, _) => { });
        string text = vm.BuildDiagnostics();
        Assert.Contains("Endpoint: 10.0.0.7:6742 (remote)", text);
        Assert.Contains("OS: ", text);
        Assert.Contains("Elevated: ", text);
        Assert.Contains("Devices (0):", text);
    }

    private sealed class NoServerRgbService : IRgbService
    {
        public bool IsConnected => false;
        public string ServerVersion => "Not connected";
        public string ModeSwitchStatus => "";
        public string? LastDeviceQueryError => null;
        public Task<bool> TryConnectAsync(string host = "localhost", int port = 6742, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<RgbDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RgbDeviceInfo>>(Array.Empty<RgbDeviceInfo>());
        public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDeviceColorsAsync(int deviceIndex, RgbColor[] colors, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAllDevicesColorAsync(RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDeviceZoneColorsAsync(int deviceIndex, RgbColor[] zoneColors, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Disconnect() { }
        public void Dispose() { }
    }
}
