using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public sealed class AutoUpdateService : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "FANZI/2.0" } }
    };

    private const string RepoOwner = "AntwerpDesignsIonity";
    private const string RepoName = "Fanzy";
    private const string CurrentVersion = "2.0.0";

    public string? LatestVersion { get; private set; }
    public string? DownloadUrl { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public bool UpdateAvailable { get; private set; }
    public string Status { get; private set; } = "Not checked";

    public async Task<bool> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            Status = "Checking for updates...";
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var response = await Http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                Status = $"Check failed: {response.StatusCode}";
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            LatestVersion = tagName.TrimStart('v', 'V');
            ReleaseNotes = root.GetProperty("body").GetString() ?? "";

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                        || name.Contains(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        DownloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            UpdateAvailable = IsNewerVersion(LatestVersion, CurrentVersion);
            Status = UpdateAvailable
                ? $"Update available: v{LatestVersion}"
                : $"Up to date (v{CurrentVersion})";

            return UpdateAvailable;
        }
        catch (Exception ex)
        {
            Status = $"Check failed: {ex.Message}";
            return false;
        }
    }

    public async Task<string?> DownloadUpdateAsync(string destinationDir, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DownloadUrl))
            return null;

        try
        {
            Status = "Downloading update...";
            Directory.CreateDirectory(destinationDir);

            var response = await Http.GetAsync(DownloadUrl, ct);
            response.EnsureSuccessStatusCode();

            string fileName = Path.GetFileName(new Uri(DownloadUrl).LocalPath);
            string filePath = Path.Combine(destinationDir, fileName);

            await using var fs = File.Create(filePath);
            await response.Content.CopyToAsync(fs, ct);

            Status = "Download complete";
            return filePath;
        }
        catch (Exception ex)
        {
            Status = $"Download failed: {ex.Message}";
            return null;
        }
    }

    private static bool IsNewerVersion(string? latest, string current)
    {
        if (string.IsNullOrEmpty(latest)) return false;
        if (Version.TryParse(latest, out var latestVer) && Version.TryParse(current, out var currentVer))
            return latestVer > currentVer;
        return false;
    }

    public void Dispose() { }
}
