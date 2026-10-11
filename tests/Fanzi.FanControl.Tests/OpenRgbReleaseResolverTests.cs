using System.Net;
using System.Runtime.InteropServices;
using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Tests;

public sealed class OpenRgbReleaseResolverTests
{
    private static readonly Uri Base = new("https://openrgb.org/");

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "openrgb-releases.html"));

    [Fact]
    public void RealPage_FirstUrlIsRelease10WindowsX64Zip()
    {
        var urls = OpenRgbReleaseResolver.ParseWindowsPortableZipUrls(Fixture(), Base, Architecture.X64);

        Assert.NotEmpty(urls);
        Assert.Equal(
            "https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.0/OpenRGB_1.0_Windows_64_81bbe18.zip",
            urls[0].AbsoluteUri);
        // Only the stable 1.0 section: no msi, no 32-bit, no release candidates, no pipeline artifacts.
        Assert.All(urls, u => Assert.EndsWith(".zip", u.AbsolutePath));
        Assert.DoesNotContain(urls, u => u.AbsoluteUri.Contains("Windows_32"));
        Assert.DoesNotContain(urls, u => u.AbsoluteUri.Contains("rc"));
        Assert.DoesNotContain(urls, u => u.Host == "gitlab.com");
    }

    [Fact]
    public void RealPage_Arm64FallsBackToX64WhenNoArmBuild()
    {
        var urls = OpenRgbReleaseResolver.ParseWindowsPortableZipUrls(Fixture(), Base, Architecture.Arm64);
        Assert.Contains(urls, u => u.AbsoluteUri.EndsWith("OpenRGB_1.0_Windows_64_81bbe18.zip"));
    }

    [Fact]
    public void RelativeHrefs_ResolveAgainstBaseUri()
    {
        const string html = """
            <h3>Release 2.0 (Jan 1, 2027)</h3>
            <table><thead><tr><th>Platform<th>x86 64-bit<th>Arm 64-bit<th>x86 32-bit<th>Arm 32-bit<tbody>
            <tr><td class=platform>Windows<td><a href=releases/release_2.0/OpenRGB_2.0_Windows_64_abc1234.zip>Portable</a>
            <td><a href="/releases/release_2.0/OpenRGB_2.0_Windows_ARM64_abc1234.zip">Portable</a><td><td>
            </table>
            """;

        var x64 = OpenRgbReleaseResolver.ParseWindowsPortableZipUrls(html, Base, Architecture.X64);
        Assert.Equal("https://openrgb.org/releases/release_2.0/OpenRGB_2.0_Windows_64_abc1234.zip", Assert.Single(x64).AbsoluteUri);

        var arm = OpenRgbReleaseResolver.ParseWindowsPortableZipUrls(html, Base, Architecture.Arm64);
        Assert.Equal("https://openrgb.org/releases/release_2.0/OpenRGB_2.0_Windows_ARM64_abc1234.zip", arm[0].AbsoluteUri);
    }

    [Fact]
    public void ReleaseCandidate_UsedOnlyWhenNoStableRelease()
    {
        const string html = """
            <h3>OpenRGB Pipeline (Experimental)</h3>
            <table><tr><td>Windows<td><a href="https://gitlab.com/x/-/jobs/artifacts/master/download?job=Windows 64">Portable</a></table>
            <h3>Release Candidate 3.0rc1 (Jan 1, 2027)</h3>
            <table><tr><td>Windows<td><a href=https://example.org/OpenRGB_3.0rc1_Windows_64_x.zip>Portable</a></table>
            """;

        var urls = OpenRgbReleaseResolver.ParseWindowsPortableZipUrls(html, Base, Architecture.X64);
        Assert.Equal("https://example.org/OpenRGB_3.0rc1_Windows_64_x.zip", Assert.Single(urls).AbsoluteUri);
    }

    [Fact]
    public void PlainHttpHrefs_AreUpgradedToHttpsOrDropped()
    {
        const string html = """
            <h3>Release 2.0 (Jan 1, 2027)</h3>
            <table><tr><td>Windows<td>
            <a href="http://downloads.example.org/OpenRGB_2.0_Windows_64_a.zip">Portable</a>
            <a href="http://downloads.example.org:8080/OpenRGB_2.0_Windows_64_b.zip">Mirror</a>
            <a href="ftp://downloads.example.org/OpenRGB_2.0_Windows_64_c.zip">FTP</a>
            </table>
            """;

        var urls = OpenRgbReleaseResolver.ParseWindowsPortableZipUrls(html, Base, Architecture.X64);

        Assert.Equal("https://downloads.example.org/OpenRGB_2.0_Windows_64_a.zip", Assert.Single(urls).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://a.example/x.zip", "https://a.example/x.zip")]
    [InlineData("http://a.example/x.zip", "https://a.example/x.zip")]
    [InlineData("http://a.example:80/x.zip?q=1", "https://a.example/x.zip?q=1")]
    [InlineData("http://a.example:8080/x.zip", null)]
    [InlineData("ftp://a.example/x.zip", null)]
    [InlineData("file:///C:/x.zip", null)]
    public void ToHttps_OnlyEverReturnsHttps(string input, string? expected)
    {
        Assert.Equal(expected, OpenRgbReleaseResolver.ToHttps(new Uri(input))?.AbsoluteUri);
    }

    [Fact]
    public void EmptyOrUnrelatedHtml_ReturnsEmpty()
    {
        Assert.Empty(OpenRgbReleaseResolver.ParseWindowsPortableZipUrls("", Base, Architecture.X64));
        Assert.Empty(OpenRgbReleaseResolver.ParseWindowsPortableZipUrls("<html><body>Maintenance</body></html>", Base, Architecture.X64));
    }

    [Fact]
    public async Task ResolveAsync_PageFirstThenPinnedFallbacks_Deduped()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Fixture()),
        }));

        var urls = await OpenRgbReleaseResolver.ResolveAsync(http, arch: Architecture.X64);

        Assert.Equal(OpenRgbReleaseResolver.PinnedFallbackUrls[0], urls[0]); // page 1.0 == pinned 1.0, deduped
        Assert.Equal(urls.Count, urls.Distinct().Count());
        Assert.Contains(OpenRgbReleaseResolver.PinnedFallbackUrls[1], urls);
    }

    [Fact]
    public async Task ResolveAsync_PageUnavailable_ReturnsPinnedFallbacks()
    {
        var log = new List<string>();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var urls = await OpenRgbReleaseResolver.ResolveAsync(http, log: log.Add, arch: Architecture.X64);

        Assert.Equal(OpenRgbReleaseResolver.PinnedFallbackUrls, urls);
        Assert.Contains(log, l => l.Contains("503"));
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
