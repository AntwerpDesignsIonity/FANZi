using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

public class AutoUpdateServiceTests
{
    [Fact]
    public void Repo_points_at_release_repository()
    {
        Assert.Equal("Ionity-Global-Pty-Ltd", AutoUpdateService.RepoOwner);
        Assert.Equal("FANZi-IO-nity", AutoUpdateService.RepoName);
        Assert.Equal("https://github.com/Ionity-Global-Pty-Ltd/FANZi-IO-nity/releases/latest", AutoUpdateService.ReleasesPageUrl);
    }

    [Theory]
    [InlineData("2.1.1", "2.1.0", true)]
    [InlineData("v2.1.1", "2.1.0", true)]
    [InlineData("2.2", "2.1.9", true)]
    [InlineData("2.1.0", "2.1.0", false)]
    [InlineData("2.1", "2.1.0", false)]
    [InlineData("2.1.0.0", "2.1.0", false)]
    [InlineData("2.1.0", "2.1.1", false)]
    [InlineData("2.1.1+3d24044", "2.1.0", true)]
    [InlineData("2.1.1-beta", "2.1.1", false)]
    [InlineData("10.0.0", "9.9.9", true)]
    [InlineData("FANZi IO-nity v2.1.1", "2.1.0", true)]
    [InlineData("", "2.1.0", false)]
    [InlineData(null, "2.1.0", false)]
    [InlineData("garbage", "2.1.0", false)]
    public void IsNewerVersion_compares_core_versions(string? latest, string current, bool expected)
    {
        Assert.Equal(expected, AutoUpdateService.IsNewerVersion(latest, current));
    }

    [Fact]
    public void SelectAsset_prefers_app_exe_over_launcher_zip()
    {
        var assets = new[]
        {
            new AutoUpdateService.ReleaseAsset("FANZi-IOnity-Launcher-v1.0.0.zip", "https://x/l.zip", 4969),
            new AutoUpdateService.ReleaseAsset("OpenRGB-helper.exe", "https://x/h.exe", 10),
            new AutoUpdateService.ReleaseAsset("Fanzi.FanControl.exe", "https://x/app.exe", 146777037),
        };

        var picked = AutoUpdateService.SelectAsset(assets);

        Assert.NotNull(picked);
        Assert.Equal("Fanzi.FanControl.exe", picked!.Name);
    }

    [Fact]
    public void SelectAsset_falls_back_to_setup_then_any_exe_and_never_zip()
    {
        Assert.Equal("FANZi-Setup.exe", AutoUpdateService.SelectAsset(new[]
        {
            new AutoUpdateService.ReleaseAsset("other.exe", "u1", 1),
            new AutoUpdateService.ReleaseAsset("FANZi-Setup.exe", "u2", 2),
        })!.Name);

        Assert.Equal("other.exe", AutoUpdateService.SelectAsset(new[]
        {
            new AutoUpdateService.ReleaseAsset("notes.zip", "u1", 1),
            new AutoUpdateService.ReleaseAsset("other.exe", "u2", 2),
        })!.Name);

        Assert.Null(AutoUpdateService.SelectAsset(new[] { new AutoUpdateService.ReleaseAsset("only.zip", "u", 1) }));
    }

    [Fact]
    public void ParseRelease_reads_real_github_shape()
    {
        const string json = """
        {
          "tag_name": "v2.1.0",
          "html_url": "https://github.com/Ionity-Global-Pty-Ltd/FANZi-IO-nity/releases/tag/v2.1.0",
          "body": "AI Thermal Intelligence",
          "assets": [
            { "name": "FANZi-IOnity-Launcher-v1.0.0.zip", "size": 4969, "browser_download_url": "https://example/l.zip" },
            { "name": "Fanzi.FanControl.exe", "size": 146777037, "browser_download_url": "https://example/Fanzi.FanControl.exe" }
          ]
        }
        """;

        var info = AutoUpdateService.ParseRelease(json);

        Assert.Equal("2.1.0", info.Version);
        Assert.Equal("AI Thermal Intelligence", info.Notes);
        Assert.EndsWith("/tag/v2.1.0", info.HtmlUrl);
        Assert.NotNull(info.Asset);
        Assert.Equal("https://example/Fanzi.FanControl.exe", info.Asset!.Url);
        Assert.Equal(146777037, info.Asset.Size);
    }

    [Fact]
    public void ParseRelease_tolerates_missing_fields()
    {
        var info = AutoUpdateService.ParseRelease("""{ "tag_name": "v3.0.0", "body": null }""");

        Assert.Equal("3.0.0", info.Version);
        Assert.Equal("", info.Notes);
        Assert.Null(info.HtmlUrl);
        Assert.Null(info.Asset);
    }

    [Fact]
    public async Task Download_without_asset_reports_status_and_returns_null()
    {
        var svc = new AutoUpdateService("2.1.0");

        string? path = await svc.DownloadUpdateAsync(Path.GetTempPath());

        Assert.Null(path);
        Assert.Equal("No downloadable asset in the latest release", svc.Status);
    }
}
