using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>SettingsService logs through AppLog, so these join the non-parallel AppLog collection.</summary>
[Collection("AppLog")]
public sealed class SettingsServiceTests : IDisposable
{
    private readonly TempAppLog _log = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fanzi-settings-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultConstructor_UsesAppDataPath()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FANZI", "settings.json");

        Assert.Equal(expected, new SettingsService().SettingsFilePath);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsDefaults()
    {
        var settings = await new SettingsService(SettingsPath).LoadAsync();

        Assert.Equal(3, settings.PollingIntervalSeconds);
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var service = new SettingsService(SettingsPath);

        await service.SaveAsync(new AppSettings { PollingIntervalSeconds = 7, OpenRgbPath = @"C:\x\OpenRGB.exe" });
        var loaded = await service.LoadAsync();

        Assert.Equal(7, loaded.PollingIntervalSeconds);
        Assert.Equal(@"C:\x\OpenRGB.exe", loaded.OpenRgbPath);
    }

    [Fact]
    public async Task Load_CorruptJson_BacksUpOriginalAndLogs()
    {
        Directory.CreateDirectory(_dir);
        const string corrupt = "{ \"PollingIntervalSeconds\": 5, \"Profiles\": [ { \"Name\": ";
        await File.WriteAllTextAsync(SettingsPath, corrupt);

        var settings = await new SettingsService(SettingsPath).LoadAsync();

        Assert.Equal(3, settings.PollingIntervalSeconds); // defaults
        string backup = Assert.Single(Directory.GetFiles(_dir, "settings.corrupt-*.json"));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(backup));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(SettingsPath)); // not touched by Load
        Assert.Contains("could not be read", _log.ReadAll());
    }

    [Fact]
    public async Task Load_CorruptJson_ThenSave_KeepsBackup()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(SettingsPath, "not json at all");
        var service = new SettingsService(SettingsPath);

        var settings = await service.LoadAsync();
        await service.SaveAsync(settings);

        string backup = Assert.Single(Directory.GetFiles(_dir, "settings.corrupt-*.json"));
        Assert.Equal("not json at all", await File.ReadAllTextAsync(backup));
        Assert.Equal(3, (await service.LoadAsync()).PollingIntervalSeconds);
    }

    [Fact]
    public async Task ConcurrentSaves_LastCallWins_AndNoTempFilesLeft()
    {
        var service = new SettingsService(SettingsPath);

        var saves = Enumerable.Range(1, 25)
            .Select(i => service.SaveAsync(new AppSettings { PollingIntervalSeconds = i }))
            .ToArray();
        await Task.WhenAll(saves);

        Assert.Equal(25, (await service.LoadAsync()).PollingIntervalSeconds);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSaves_FromThreadPool_LeaveValidFileAndNoTempFiles()
    {
        var service = new SettingsService(SettingsPath);

        await Task.WhenAll(Enumerable.Range(1, 40).Select(i =>
            Task.Run(() => service.SaveAsync(new AppSettings { PollingIntervalSeconds = 1 + (i % 50) }))));

        var loaded = await service.LoadAsync();
        Assert.InRange(loaded.PollingIntervalSeconds, 1, 50);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_dir, "settings.corrupt-*.json"));
    }

    [Fact]
    public async Task Save_SnapshotsSettingsAtCallTime()
    {
        var service = new SettingsService(SettingsPath);
        var settings = new AppSettings { PollingIntervalSeconds = 4 };

        Task save = service.SaveAsync(settings);
        settings.PollingIntervalSeconds = 9; // mutated after the call — must not leak into that save
        await save;

        Assert.Equal(4, (await service.LoadAsync()).PollingIntervalSeconds);
    }
}
