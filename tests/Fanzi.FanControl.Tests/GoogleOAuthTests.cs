using System.Net;
using Fanzi.FanControl.Services;

namespace Fanzi.FanControl.Tests;

/// <summary>Sign in with Google: client config, PKCE consent URL, Google error parsing (no network).</summary>
public class GoogleOAuthTests
{
    private const string Id = "123-abc.apps.googleusercontent.com";

    [Fact]
    public void Parse_AcceptsGoogleDownloadAndFlatFormats()
    {
        var google = GoogleOAuthConfig.Parse($$$"""{"installed":{"client_id":"{{{Id}}}","client_secret":"s3cret","redirect_uris":["http://localhost"]}}""");
        Assert.NotNull(google);
        Assert.Equal(Id, google!.ClientId);
        Assert.True(google.HasSecret);

        var flat = GoogleOAuthConfig.Parse($$"""{"client_id":"{{Id}}","client_secret":"x"}""");
        Assert.Equal(Id, flat!.ClientId);

        Assert.Null(GoogleOAuthConfig.Parse("""{"installed":{"client_id":"not-a-google-id"}}"""));
        Assert.False(GoogleOAuthConfig.Parse($$"""{"client_id":"{{Id}}"}""")!.HasSecret);
    }

    [Fact]
    public void Load_FallsBackToBuiltInClientWithoutSecret_AndReadsFirstValidFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fanzi-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var missing = Path.Combine(dir, "missing.json");
            var bad = Path.Combine(dir, "bad.json");
            var good = Path.Combine(dir, "good.json");
            File.WriteAllText(bad, "{ not json");
            File.WriteAllText(good, $$$"""{"installed":{"client_id":"{{{Id}}}","client_secret":"s"}}""");

            var fallback = GoogleOAuthConfig.Load(new[] { missing });
            Assert.Equal(GoogleOAuthConfig.DefaultClientId, fallback.ClientId);
            Assert.False(fallback.HasSecret);

            var loaded = GoogleOAuthConfig.Load(new[] { missing, bad, good });
            Assert.Equal(Id, loaded.ClientId);
            Assert.Equal(good, loaded.SourcePath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AuthUrl_UsesPkceStateLoopbackAndOfflineAccess()
    {
        var url = GoogleProfileService.BuildAuthUrl(Id, "http://127.0.0.1:50123/", "CHAL", "ST8");
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=" + Uri.EscapeDataString(Id), url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A50123%2F", url);
        Assert.Contains("code_challenge=CHAL&code_challenge_method=S256", url);
        Assert.Contains("state=ST8", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/drive.appdata"), url);
    }

    [Fact]
    public void Base64Url_And_QueryParsing()
    {
        Assert.Equal("-_8", GoogleProfileService.Base64Url(new byte[] { 0xFB, 0xFF }));
        var q = GoogleProfileService.ParseQuery("?code=4%2F0Ab&state=x+y&scope=email");
        Assert.Equal("4/0Ab", q["code"]);
        Assert.Equal("x y", q["state"]);
        Assert.Empty(GoogleProfileService.ParseQuery(null));
    }

    [Fact]
    public void DescribeError_ReadsOAuthAndApiErrors()
    {
        Assert.Equal("invalid_grant: Token has been expired or revoked.",
            GoogleProfileService.DescribeError("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest));
        Assert.Equal("Insufficient Permission",
            GoogleProfileService.DescribeError("""{"error":{"code":403,"message":"Insufficient Permission"}}""", HttpStatusCode.Forbidden));
        Assert.Equal("502 BadGateway", GoogleProfileService.DescribeError("<html>", HttpStatusCode.BadGateway));
    }

    [Fact]
    public void SavedToken_RoundTripsAndKnowsWhenExpired()
    {
        var fresh = new GoogleToken("a", "r", 3600, DateTimeOffset.UtcNow);
        var old = fresh with { CreatedAt = DateTimeOffset.UtcNow.AddHours(-2) };
        Assert.False(fresh.IsExpired);
        Assert.True(old.IsExpired);
        var json = System.Text.Json.JsonSerializer.Serialize(fresh);
        Assert.Equal(fresh, System.Text.Json.JsonSerializer.Deserialize<GoogleToken>(json));
    }
}
