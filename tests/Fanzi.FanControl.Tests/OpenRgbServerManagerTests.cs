using System.Net;
using System.Net.Sockets;
using Fanzi.FanControl.Services;
using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Tests;

[Collection("AppLog")] // OpenRgbServerManager logs every status change.
public sealed class OpenRgbServerManagerTests : IDisposable
{
    private readonly TempAppLog _log = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fanzi-orgb-mgr-" + Guid.NewGuid().ToString("N"));

    public OpenRgbServerManagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string FakeFile(string name)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a real executable");
        return path;
    }

    private sealed class Counters
    {
        public int Allow;
        public int Install;
    }

    private static OpenRgbServerManager Manager(Func<string?> locate, Counters counters, OpenRgbInstallResult? installResult = null) =>
        new(locate, (_, _) =>
        {
            counters.Install++;
            return Task.FromResult(installResult ?? OpenRgbInstallResult.Fail("All 3 sources failed: a: HTTP 404; b: HTTP 404; c: HTTP 404"));
        });

    [Fact]
    public async Task EnsureRunning_WhenInstalled_NeverConsultsThrottleOrDownloads()
    {
        var counters = new Counters();
        string exe = FakeFile("OpenRGB.exe");
        using var manager = Manager(() => exe, counters);

        // The fake exe can't start — what matters is that no download was considered.
        await manager.EnsureRunningAsync(FreePort(), allowInstall: () => { counters.Allow++; return true; });

        Assert.Equal(0, counters.Allow);
        Assert.Equal(0, counters.Install);
        Assert.True(manager.IsInstalled);
    }

    [Fact]
    public async Task EnsureRunning_WhenMissingAndThrottled_SkipsDownload()
    {
        var counters = new Counters();
        using var manager = Manager(() => null, counters);

        bool ok = await manager.EnsureRunningAsync(FreePort(), allowInstall: () => { counters.Allow++; return false; });

        Assert.False(ok);
        Assert.Equal(1, counters.Allow);
        Assert.Equal(0, counters.Install);
        Assert.Contains("automatic download skipped", manager.Status);
    }

    [Fact]
    public async Task EnsureRunning_WhenMissingAndAllowed_DownloadsOnceAndRecordsPreciseError()
    {
        var counters = new Counters();
        using var manager = Manager(() => null, counters);

        bool ok = await manager.EnsureRunningAsync(FreePort(), allowInstall: () => { counters.Allow++; return true; });

        Assert.False(ok);
        Assert.Equal(1, counters.Allow);
        Assert.Equal(1, counters.Install);
        Assert.StartsWith("All 3 sources failed", manager.LastInstallError);
        Assert.Equal(manager.LastInstallError, manager.Status);

        // Next (throttled) attempt keeps the precise reason visible.
        await manager.EnsureRunningAsync(FreePort(), allowInstall: () => false);
        Assert.Contains("last download failed: All 3 sources failed", manager.Status);
        Assert.Equal(1, counters.Install);
    }

    [Fact]
    public async Task Install_Success_SetsDetectedPathAndClearsError()
    {
        var counters = new Counters();
        string exe = FakeFile("OpenRGB.exe");
        using var manager = Manager(() => null, counters, OpenRgbInstallResult.Ok(exe, new Uri("https://a.example/x.zip")));

        bool ok = await manager.InstallOpenRgbAsync();

        Assert.True(ok);
        Assert.Equal(exe, manager.DetectedPath);
        Assert.Null(manager.LastInstallError);
        Assert.Contains("OpenRGB installed", manager.Status);
    }

    [Fact]
    public void SetCustomPath_MissingFile_Rejected()
    {
        using var manager = Manager(() => null, new Counters());
        string missing = Path.Combine(_root, "nope", "OpenRGB.exe");

        Assert.False(manager.SetCustomPath(missing));
        Assert.Null(manager.CustomPath);
        Assert.False(manager.IsInstalled);
        Assert.Contains("OpenRGB not found at", manager.Status);
    }

    [Fact]
    public void SetCustomPath_WrongExecutable_Rejected()
    {
        using var manager = Manager(() => null, new Counters());

        Assert.False(manager.SetCustomPath(FakeFile("notepad.exe")));
        Assert.Null(manager.CustomPath);
        Assert.Contains("is not OpenRGB", manager.Status);
    }

    [Theory]
    [InlineData("OpenRGB.msi")]
    [InlineData("OpenRGB_Windows_64.zip")]
    [InlineData("OpenRGB.exe.lnk")]
    [InlineData("OpenRGB.exe.bak")]
    [InlineData("OpenRGB-old.exe")]
    [InlineData("OpenRGB_Setup.exe")]
    [InlineData("OpenRGB")]
    public void SetCustomPath_FileNamedLikeOpenRgbButNotOpenRgbExe_RejectedWithClearMessage(string fileName)
    {
        using var manager = Manager(() => null, new Counters());

        Assert.False(manager.SetCustomPath(FakeFile(fileName)));
        Assert.Null(manager.CustomPath);
        Assert.False(manager.IsInstalled);
        Assert.Contains($"{fileName} is not OpenRGB", manager.Status);
        Assert.Contains("OpenRGB.exe", manager.Status);
    }

    [Theory]
    [InlineData("OpenRGB.exe")]
    [InlineData("openrgb.exe")]
    [InlineData("OPENRGB.EXE")]
    public void SetCustomPath_OpenRgbExe_AcceptedCaseInsensitive(string fileName)
    {
        using var manager = Manager(() => null, new Counters());
        string exe = FakeFile(fileName);

        Assert.True(manager.SetCustomPath(exe));
        Assert.Equal(exe, manager.CustomPath);
        Assert.True(manager.IsInstalled);
    }

    [Theory]
    [InlineData(@"C:\Tools\OpenRGB\OpenRGB.exe", true)]
    [InlineData(@"C:\Tools\OpenRGB\openrgb.EXE", true)]
    [InlineData(@"C:\Downloads\OpenRGB_1.0_Windows_64.zip", false)]
    [InlineData(@"C:\Downloads\OpenRGB.msi", false)]
    [InlineData(@"C:\OpenRGB.exe\readme.txt", false)]
    public void IsOpenRgbExecutableName_OnlyExactExeName(string path, bool expected)
    {
        Assert.Equal(expected, OpenRgbServerManager.IsOpenRgbExecutableName(path));
    }

    [Fact]
    public void SetCustomPath_ValidQuotedPath_UsedAndClearedAgain()
    {
        using var manager = Manager(() => null, new Counters());
        string exe = FakeFile("OpenRGB.exe");

        Assert.True(manager.SetCustomPath($"  \"{exe}\" "));
        Assert.Equal(exe, manager.CustomPath);
        Assert.Equal(exe, manager.DetectedPath);
        Assert.True(manager.IsInstalled);

        // Custom path wins over detection.
        manager.DetectInstallation();
        Assert.Equal(exe, manager.DetectedPath);

        Assert.True(manager.SetCustomPath(null));
        Assert.Null(manager.CustomPath);
        Assert.False(manager.IsInstalled);
    }
}
