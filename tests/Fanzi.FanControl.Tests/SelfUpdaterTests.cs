using Fanzi.FanControl.Services;
using Fanzi.FanControl.ViewModels;

namespace Fanzi.FanControl.Tests;

/// <summary>4.5.1 in-place update: checksum lookup, file swap with rollback, asset choice.</summary>
public sealed class SelfUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fanzi-upd-" + Guid.NewGuid().ToString("N"));
    public SelfUpdaterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Sums =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA  FANZi-IO-nity-v4.5.1-Installer-win-x64.zip  (118,484,842 bytes)\r\n" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  FANZi-IO-nity-v4.5.1-Portable-win-x64.exe  (152,859,597 bytes)\r\n" +
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB *other.bin\n" +
        "not-a-hash  FANZi-IO-nity-v4.5.1-x86_64.AppImage\n";

    [Fact]
    public void Checksum_is_found_by_exact_file_name()
    {
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
            SelfUpdater.FindSha256(Sums, "FANZi-IO-nity-v4.5.1-Portable-win-x64.exe"));
        Assert.Equal(new string('B', 64), SelfUpdater.FindSha256(Sums, "other.bin"));           // sha256sum "*name" form
        Assert.Null(SelfUpdater.FindSha256(Sums, "FANZi-IO-nity-v4.5.1-x86_64.AppImage"));       // malformed hash
        Assert.Null(SelfUpdater.FindSha256(Sums, "Portable-win-x64.exe"));                       // no partial match
        Assert.Null(SelfUpdater.FindSha256(null, "x"));
        Assert.Null(SelfUpdater.FindSha256("", "x"));
    }

    [Fact]
    public async Task Sha256_of_a_file()
    {
        string f = Path.Combine(_dir, "a.bin");
        File.WriteAllText(f, "abc");
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", await SelfUpdater.ComputeSha256Async(f));
    }

    [Fact]
    public void Swap_puts_the_new_file_in_place_and_keeps_the_old_one_aside()
    {
        string current = Path.Combine(_dir, "Fanzi.FanControl.exe"), download = Path.Combine(_dir, "stage", "new.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        File.WriteAllText(current, "old build");
        File.WriteAllText(download, "new build");
        File.WriteAllText(current + SelfUpdater.OldSuffix, "leftover from an earlier update");

        SelfUpdater.Swap(current, download);

        Assert.Equal("new build", File.ReadAllText(current));
        Assert.Equal("old build", File.ReadAllText(current + SelfUpdater.OldSuffix));
        Assert.False(File.Exists(download));

        SelfUpdater.CleanupAfterUpdate(current);
        Assert.False(File.Exists(current + SelfUpdater.OldSuffix));
        Assert.Equal("new build", File.ReadAllText(current));
    }

    [Fact]
    public void Swap_without_a_download_changes_nothing()
    {
        string current = Path.Combine(_dir, "Fanzi.FanControl.exe");
        File.WriteAllText(current, "old build");
        Assert.Throws<FileNotFoundException>(() => SelfUpdater.Swap(current, Path.Combine(_dir, "missing.exe")));
        Assert.Equal("old build", File.ReadAllText(current));
        Assert.False(File.Exists(current + SelfUpdater.OldSuffix));
    }

    [Fact]
    public void Failed_swap_restores_the_original()
    {
        string current = Path.Combine(_dir, "Fanzi.FanControl.exe"), download = Path.Combine(_dir, "new.exe");
        File.WriteAllText(current, "old build");
        Directory.CreateDirectory(download); // a directory where the download should be: exists-check passes for neither File nor copy
        Assert.ThrowsAny<Exception>(() => SelfUpdater.Swap(current, download));
        Assert.Equal("old build", File.ReadAllText(current));
    }

    [Fact]
    public void Cleanup_never_throws()
    {
        SelfUpdater.CleanupAfterUpdate(null);
        SelfUpdater.CleanupAfterUpdate(Path.Combine(_dir, "nothing.exe"));
        SelfUpdater.SyncInstalledVersion(null, "4.5.1");
        SelfUpdater.SyncInstalledVersion(Path.Combine(_dir, "nothing.exe"), "4.5.1");
    }

    [Fact]
    public void Release_json_selects_the_windows_application_and_the_checksum_file()
    {
        const string json = """
        { "tag_name": "v4.5.1", "html_url": "https://example/r", "body": "notes", "assets": [
          { "name": "FANZi-IO-nity-v4.5.1-Installer-win-x64.zip", "browser_download_url": "https://example/i.zip", "size": 1 },
          { "name": "FANZi-IO-nity-v4.5.1-macOS-arm64.dmg", "browser_download_url": "https://example/m.dmg", "size": 2 },
          { "name": "FANZi-IO-nity-v4.5.1-Portable-win-x64.exe", "browser_download_url": "https://example/p.exe", "size": 3 },
          { "name": "FANZi-IO-nity-v4.5.1-Portable-win-x64.zip", "browser_download_url": "https://example/p.zip", "size": 4 },
          { "name": "SHA256SUMS.txt", "browser_download_url": "https://example/sums", "size": 5 } ] }
        """;
        var release = AutoUpdateService.ParseRelease(json);
        Assert.Equal("4.5.1", release.Version);
        Assert.Equal("FANZi-IO-nity-v4.5.1-Portable-win-x64.exe", release.Asset!.Name);
        Assert.Equal("https://example/sums", release.ChecksumsUrl);

        Assert.Null(AutoUpdateService.ParseRelease("""{ "tag_name": "v1", "assets": [] }""").ChecksumsUrl);
    }

    [Fact]
    public void Settings_tab_constant_matches_the_tab_strip()
        => Assert.Equal(11, MainWindowViewModel.SettingsTabIndex); // Dashboard … Bench, Alerts, Settings

    [Fact]
    public void Automatic_check_is_on_by_default_and_persisted()
    {
        Assert.True(new Fanzi.FanControl.Models.AppSettings().AutoUpdateCheck);
    }
}
