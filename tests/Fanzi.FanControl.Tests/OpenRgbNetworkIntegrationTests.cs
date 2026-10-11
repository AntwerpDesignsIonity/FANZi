using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Tests;

/// <summary>[Fact] that is skipped unless FANZI_NETWORK_TESTS=1 (downloads ~22 MB from the internet).</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "FANZI_NETWORK_TESTS";

    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
            Skip = $"Network integration test — set {EnvironmentVariable}=1 to run.";
    }
}

[Trait("Category", "Network")]
public sealed class OpenRgbNetworkIntegrationTests
{
    [NetworkFact]
    public async Task InstallLatest_DownloadsRealPackageIntoTempDirectory()
    {
        string target = Path.Combine(Path.GetTempPath(), "fanzi-openrgb-net-" + Guid.NewGuid().ToString("N"));
        var log = new List<string>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var result = await OpenRgbPackageInstaller.InstallLatestAsync(target, null, log.Add, cts.Token);

            Assert.True(result.Success, result.Error + Environment.NewLine + string.Join(Environment.NewLine, log));
            Assert.NotNull(result.ExePath);
            Assert.True(File.Exists(result.ExePath));
            Assert.Equal(Path.Combine(target, OpenRgbInstallLocator.ExeName), result.ExePath, ignoreCase: true);
            Assert.NotNull(result.SourceUrl);
            Assert.Equal(Uri.UriSchemeHttps, result.SourceUrl!.Scheme);
        }
        finally
        {
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }
}
