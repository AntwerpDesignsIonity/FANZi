using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Checks GitHub Releases for a newer FANZi IO-nity build and (optionally) downloads it.
/// Releases are published on <c>Ionity-Global-Pty-Ltd/FANZi-IO-nity</c> — the same repo the
/// companion launcher updates from. The source repo (<c>AntwerpDesignsIonity/FANZi</c>) carries
/// no releases, so pointing the check there always returned 404.
/// </summary>
public sealed class AutoUpdateService : IDisposable
{
    internal const string RepoOwner = "Ionity-Global-Pty-Ltd";
    internal const string RepoName = "FANZi-IO-nity";

    /// <summary>Preferred release asset: the single-file app published by build.ps1.</summary>
    internal const string PrimaryAssetName = "Fanzi.FanControl.exe";

    public static string ReleasesPageUrl { get; } = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    // Infinite client timeout: each call applies its own deadline. A fixed 15 s client timeout
    // previously aborted the ~140 MB download on all but the fastest connections.
    private static readonly HttpClient Http = CreateClient();

    private readonly string _currentVersion;

    public AutoUpdateService() : this(AppVersion.Display) { }

    internal AutoUpdateService(string currentVersion) => _currentVersion = currentVersion;

    public string? LatestVersion { get; private set; }
    public string? DownloadUrl { get; private set; }
    public long? DownloadSize { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public string ReleasePageUrl { get; private set; } = ReleasesPageUrl;
    public bool UpdateAvailable { get; private set; }
    public string Status { get; private set; } = "Not checked";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FANZi-IO-nity/{AppVersion.Display}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<bool> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            Status = "Checking for updates...";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ApiTimeout);

            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await Http.GetAsync(url, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Status = "No published releases found";
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                Status = $"Check failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var release = ParseRelease(json);

            LatestVersion = release.Version;
            ReleaseNotes = release.Notes;
            ReleasePageUrl = release.HtmlUrl ?? ReleasesPageUrl;
            DownloadUrl = release.Asset?.Url;
            DownloadSize = release.Asset?.Size;

            UpdateAvailable = IsNewerVersion(LatestVersion, _currentVersion);
            Status = UpdateAvailable
                ? $"Update available: v{LatestVersion}"
                : $"Up to date (v{_currentVersion})";
            return UpdateAvailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Status = "Check failed: timed out";
            return false;
        }
        catch (Exception ex)
        {
            Status = $"Check failed: {ex.Message}";
            AppLog.Warn("Update check failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Streams the release asset to <paramref name="destinationDir"/> via a <c>.part</c> file, verifies
    /// the byte count against the size GitHub reported, then renames it into place.
    /// Returns the final path, or null on failure (see <see cref="Status"/>).
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(string destinationDir, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DownloadUrl))
        {
            Status = "No downloadable asset in the latest release";
            return null;
        }

        string fileName = Path.GetFileName(new Uri(DownloadUrl).LocalPath);
        string filePath = Path.Combine(destinationDir, fileName);
        string partPath = filePath + ".part";

        try
        {
            Status = "Downloading update...";
            Directory.CreateDirectory(destinationDir);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DownloadTimeout);

            using var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? expected = response.Content.Headers.ContentLength ?? DownloadSize;
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    written += read;
                    if (expected is > 0) progress?.Report((double)written / expected.Value);
                }
            }

            if (expected is > 0 && written != expected.Value)
                throw new IOException($"Incomplete download: {written:N0} of {expected.Value:N0} bytes");

            File.Move(partPath, filePath, overwrite: true);
            progress?.Report(1.0);
            Status = "Download complete";
            return filePath;
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            Status = ex is OperationCanceledException ? "Download failed: timed out" : $"Download failed: {ex.Message}";
            AppLog.Warn("Update download failed", ex);
            return null;
        }
    }

    // ── Pure helpers (unit-tested) ────────────────────────────────────────────

    internal sealed record ReleaseAsset(string Name, string Url, long Size);

    internal sealed record ReleaseInfo(string Version, string Notes, string? HtmlUrl, ReleaseAsset? Asset);

    /// <summary>Parses a GitHub "release" JSON document into the fields FANZi needs.</summary>
    internal static ReleaseInfo ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = GetString(root, "tag_name") ?? "";
        string notes = GetString(root, "body") ?? "";
        string? htmlUrl = GetString(root, "html_url");

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                string? name = GetString(a, "name");
                string? url = GetString(a, "browser_download_url");
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long v) ? v : 0;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    assets.Add(new ReleaseAsset(name, url, size));
            }
        }

        return new ReleaseInfo(NormalizeTag(tag), notes, htmlUrl, SelectAsset(assets));
    }

    /// <summary>
    /// Picks the app binary: the exact <see cref="PrimaryAssetName"/>, then a "Setup" exe, then any exe.
    /// Launcher zips and other archives are never chosen.
    /// </summary>
    internal static ReleaseAsset? SelectAsset(IReadOnlyCollection<ReleaseAsset> assets)
    {
        static bool IsExe(ReleaseAsset a) => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        return assets.FirstOrDefault(a => a.Name.Equals(PrimaryAssetName, StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => IsExe(a) && a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(IsExe);
    }

    /// <summary>"v2.1.1" → "2.1.1"; "FANZi IO-nity v2.1.1" → "2.1.1".</summary>
    internal static string NormalizeTag(string tag)
    {
        string t = tag.Trim();
        int vAt = t.LastIndexOf(' ');
        if (vAt >= 0) t = t[(vAt + 1)..];
        return t.TrimStart('v', 'V');
    }

    /// <summary>
    /// Numeric comparison of the core version (build metadata "+…" and pre-release "-…" ignored),
    /// so "2.1.1+3d24044" vs "2.1.0" and "2.1" vs "2.1.0" compare correctly.
    /// </summary>
    internal static bool IsNewerVersion(string? latest, string current)
    {
        if (!TryParseCore(latest, out var latestVer) || !TryParseCore(current, out var currentVer))
            return false;
        return latestVer > currentVer;
    }

    internal static bool TryParseCore(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;

        string core = NormalizeTag(value).Split('+', 2)[0].Split('-', 2)[0];
        if (!Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed)) return false;

        // Normalise missing parts to 0 so 2.1 == 2.1.0 == 2.1.0.0.
        version = new Version(parsed.Major, Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose() { }
}
