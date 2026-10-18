using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.OpenRgb;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

[Collection("AppLog")] // The view model logs banner changes.
public sealed class RgbControlViewModelOpenRgbTests : IDisposable
{
    private readonly TempAppLog _log = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fanzi-rgbvm-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    public RgbControlViewModelOpenRgbTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string FakeFile(string name)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stub");
        return path;
    }

    private RgbControlViewModel CreateVm(string? detectedExe = null)
    {
        var manager = new OpenRgbServerManager(
            () => detectedExe,
            (_, _) => Task.FromResult(OpenRgbInstallResult.Fail("not used")));
        return new RgbControlViewModel(new FakeRgbService(), manager, () => _now, startBackgroundWork: false);
    }

    [Fact]
    public void Picking_a_colour_or_effect_pauses_Smart_Ambient()
    {
        using var vm = CreateVm();
        vm.SmartAmbientEnabled = true;

        vm.SetPrimarySwatchCommand.Execute(Fanzi.FanControl.Models.RgbColor.Red);

        Assert.False(vm.SmartAmbientEnabled);
        Assert.Equal(Fanzi.FanControl.Models.RgbColor.Red, vm.PrimaryColor);
        Assert.Contains("Smart Ambient paused", vm.AiLightingStatus);

        vm.SmartAmbientEnabled = true;
        vm.SelectEffectCommand.Execute(Fanzi.FanControl.Models.RgbEffectType.Static);
        Assert.False(vm.SmartAmbientEnabled);
    }

    [Fact]
    public void AllowDownload_AutomaticThrottled_UserActionsBypassAndRestartWindow()
    {
        using var vm = CreateVm();

        Assert.True(vm.AllowDownload(userInitiated: false));      // startup
        _now += TimeSpan.FromMinutes(1);
        Assert.False(vm.AllowDownload(userInitiated: false));     // watchdog inside the window
        Assert.True(vm.AllowDownload(userInitiated: true));       // Retry / Install / RGB on
        _now += TimeSpan.FromMinutes(9);
        Assert.False(vm.AllowDownload(userInitiated: false));     // window restarted by the user attempt
        _now += TimeSpan.FromMinutes(1);
        Assert.True(vm.AllowDownload(userInitiated: false));
    }

    [Fact]
    public void AllowDownload_ThrottledSkips_LoggedOncePerWindow()
    {
        using var vm = CreateVm();
        vm.AllowDownload(userInitiated: false);

        for (int i = 0; i < 5; i++)
        {
            _now += TimeSpan.FromSeconds(15);
            vm.AllowDownload(userInitiated: false);
        }

        Assert.Equal(1, CountOccurrences(_log.ReadAll(), "automatic download skipped"));
    }

    [Fact]
    public void Banner_NotInstalled_ShowsInstallPrompt()
    {
        using var vm = CreateVm(detectedExe: null);

        Assert.False(vm.IsOpenRgbInstalled);
        Assert.True(vm.ShowOpenRgbBanner);
        Assert.Equal("OpenRGB Not Installed", vm.OpenRgbBannerTitle);
        Assert.Contains("Click Install", vm.OpenRgbBannerMessage);
        Assert.False(vm.HasOpenRgbError);
    }

    [Fact]
    public void Banner_InstalledWithoutError_Hidden()
    {
        using var vm = CreateVm(detectedExe: FakeFile("OpenRGB.exe"));

        Assert.True(vm.IsOpenRgbInstalled);
        Assert.False(vm.ShowOpenRgbBanner);
    }

    [Fact]
    public void Banner_InstalledWithError_StaysVisibleWithExactError()
    {
        using var vm = CreateVm(detectedExe: FakeFile("OpenRGB.exe"));

        vm.LastOpenRgbError = RgbControlViewModel.DeviceQueryBannerError(0, null);

        Assert.True(vm.HasOpenRgbError);
        Assert.True(vm.ShowOpenRgbBanner);
        Assert.Equal("OpenRGB Needs Attention", vm.OpenRgbBannerTitle);
        Assert.Equal(RgbControlViewModel.NoDevicesStatus, vm.OpenRgbBannerMessage);

        vm.LastOpenRgbError = null;
        Assert.False(vm.ShowOpenRgbBanner);
    }

    [Fact]
    public void DeviceQueryBannerError_MapsProtocolErrorZeroDevicesAndSuccess()
    {
        Assert.Equal(
            "OpenRGB protocol error — try updating OpenRGB/FANZI (bad controller data)",
            RgbControlViewModel.DeviceQueryBannerError(0, "bad controller data"));
        Assert.Equal(
            "Connected to OpenRGB but no devices found — RAM/motherboard RGB on OpenRGB 1.0 requires the PawnIO driver (pawnio.eu) and Administrator.",
            RgbControlViewModel.DeviceQueryBannerError(0, null));
        Assert.Null(RgbControlViewModel.DeviceQueryBannerError(3, null));
        // A protocol error wins even if a partial list came back.
        Assert.StartsWith(RgbControlViewModel.ProtocolErrorStatus, RgbControlViewModel.DeviceQueryBannerError(2, "x"));
    }

    [Fact]
    public void BannerError_RetryCountdownChanges_LoggedOnlyWhenUnderlyingErrorChanges()
    {
        using var vm = CreateVm();
        const string error = "All 3 sources failed: a: HTTP 404";

        vm.LastOpenRgbError = $"{error}{RgbControlViewModel.RetryCountdownMarker}9 min, or use Retry.";
        vm.LastOpenRgbError = $"{error}{RgbControlViewModel.RetryCountdownMarker}8 min, or use Retry.";
        vm.LastOpenRgbError = $"{error}{RgbControlViewModel.RetryCountdownMarker}7 min, or use Retry.";
        Assert.Equal(1, CountOccurrences(_log.ReadAll(), "OpenRGB banner:"));

        vm.LastOpenRgbError = "OpenRGB download failed: a: network error";
        Assert.Equal(2, CountOccurrences(_log.ReadAll(), "OpenRGB banner:"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("plain error", "plain error")]
    [InlineData("install failed — automatic retry in 4 min, or use Retry", "install failed")]
    public void OpenRgbErrorLogKey_StripsCountdown(string? message, string? expected)
    {
        Assert.Equal(expected, RgbControlViewModel.OpenRgbErrorLogKey(message));
    }

    [Fact]
    public async Task LocateOpenRgb_WrongFile_ShowsErrorAndDoesNotPersist()
    {
        using var vm = CreateVm();
        var persisted = new List<string?>();
        vm.ApplyOpenRgbSettings(null, persisted.Add);
        string notepad = FakeFile("notepad.exe");
        vm.PickOpenRgbExecutable = () => Task.FromResult<string?>(notepad);

        await vm.LocateOpenRgbCommand.ExecuteAsync(null);

        Assert.Contains("is not OpenRGB", vm.LastOpenRgbError);
        Assert.True(vm.ShowOpenRgbBanner);
        Assert.Empty(persisted);
        Assert.False(vm.IsOpenRgbInstalled);
    }

    [Theory]
    [InlineData("OpenRGB.msi")]
    [InlineData("OpenRGB_Windows_64.zip")]
    public async Task LocateOpenRgb_InstallerOrArchiveNamedOpenRgb_ShowsErrorAndDoesNotPersist(string fileName)
    {
        using var vm = CreateVm();
        var persisted = new List<string?>();
        vm.ApplyOpenRgbSettings(null, persisted.Add);
        string picked = FakeFile(fileName);
        vm.PickOpenRgbExecutable = () => Task.FromResult<string?>(picked);

        await vm.LocateOpenRgbCommand.ExecuteAsync(null);

        Assert.Contains($"{fileName} is not OpenRGB", vm.LastOpenRgbError);
        Assert.Contains("OpenRGB.exe", vm.LastOpenRgbError);
        Assert.True(vm.ShowOpenRgbBanner);
        Assert.Empty(persisted);
        Assert.False(vm.IsOpenRgbInstalled);
    }

    [Fact]
    public async Task LocateOpenRgb_Cancelled_ChangesNothing()
    {
        using var vm = CreateVm();
        var persisted = new List<string?>();
        vm.ApplyOpenRgbSettings(null, persisted.Add);
        vm.PickOpenRgbExecutable = () => Task.FromResult<string?>(null);

        await vm.LocateOpenRgbCommand.ExecuteAsync(null);

        Assert.Null(vm.LastOpenRgbError);
        Assert.Empty(persisted);
    }

    [Fact]
    public void ApplyOpenRgbSettings_SavedPath_ValidUsed_InvalidIgnored()
    {
        string exe = FakeFile("OpenRGB.exe");
        using (var vm = CreateVm())
        {
            vm.ApplyOpenRgbSettings(exe, _ => { });
            Assert.True(vm.IsOpenRgbInstalled);
            Assert.False(vm.ShowOpenRgbBanner);
        }

        using (var vm = CreateVm())
        {
            vm.ApplyOpenRgbSettings(Path.Combine(_root, "gone", "OpenRGB.exe"), _ => { });
            Assert.False(vm.IsOpenRgbInstalled);
            Assert.Contains("saved path is no longer valid", _log.ReadAll());
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private sealed class FakeRgbService : IRgbService
    {
        public bool IsConnected => false;
        public string ServerVersion => "fake";
        public string ModeSwitchStatus => "";
        public string? LastDeviceQueryError => null;

        public Task<bool> TryConnectAsync(string host = "localhost", int port = 6742, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

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
