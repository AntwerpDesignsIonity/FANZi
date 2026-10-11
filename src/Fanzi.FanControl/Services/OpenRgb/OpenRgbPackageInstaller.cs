using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services.OpenRgb;

/// <summary>Outcome of an OpenRGB install attempt.</summary>
/// <param name="Success">True when OpenRGB.exe exists in the target directory.</param>
/// <param name="ExePath">Full path of the installed OpenRGB.exe (on success).</param>
/// <param name="SourceUrl">The URL the package was installed from (on success).</param>
/// <param name="Error">Human-readable failure reason, one entry per mirror (on failure).</param>
public sealed record OpenRgbInstallResult(bool Success, string? ExePath, Uri? SourceUrl, string? Error)
{
    public static OpenRgbInstallResult Ok(string exePath, Uri? source) => new(true, exePath, source, null);
    public static OpenRgbInstallResult Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// Downloads, validates and extracts the portable OpenRGB package.
/// Shared with Fanzi.Installer (linked source) — must only depend on the BCL
/// (System.IO.Compression); logging is via the optional <c>log</c> callback.
/// </summary>
public static class OpenRgbPackageInstaller
{
    public const string ExeName = "OpenRGB.exe";

    /// <summary>Real OpenRGB Windows packages are 10+ MB; anything under 1 MB is an error page.</summary>
    internal const long MinPackageBytes = 1024 * 1024;

    internal static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 }; // "PK\x03\x04"

    /// <summary>Shared client (one per process). Timeouts are enforced per download instead.</summary>
    public static HttpClient SharedHttpClient { get; } = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        // Linked into both the app and the installer: each reports its own assembly version.
        Version? version = typeof(OpenRgbPackageInstaller).Assembly.GetName().Version;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FANZI", version?.ToString(3) ?? "2"));
        return client;
    }

    /// <summary>Resolves the current download URLs (releases page + pinned fallbacks) and installs.</summary>
    public static async Task<OpenRgbInstallResult> InstallLatestAsync(
        string targetDirectory,
        IProgress<string>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        progress?.Report("Looking up the latest OpenRGB release…");
        var urls = await OpenRgbReleaseResolver.ResolveAsync(SharedHttpClient, ct, log).ConfigureAwait(false);
        return await InstallAsync(urls, targetDirectory, progress, log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tries each URL in order until one produces a valid install in <paramref name="targetDirectory"/>.
    /// Only https URLs are downloaded (plain http on the default port is upgraded, anything else
    /// is refused). All downloads share one <see cref="OverallTimeout"/> budget.
    /// </summary>
    public static Task<OpenRgbInstallResult> InstallAsync(
        IEnumerable<Uri> urls,
        string targetDirectory,
        IProgress<string>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default,
        HttpClient? http = null)
        => InstallCoreAsync(urls, targetDirectory, progress, log, ct, http, OverallTimeout, StallTimeout);

    /// <summary><see cref="InstallAsync"/> with explicit timeouts (tests).</summary>
    internal static async Task<OpenRgbInstallResult> InstallCoreAsync(
        IEnumerable<Uri> urls,
        string targetDirectory,
        IProgress<string>? progress,
        Action<string>? log,
        CancellationToken ct,
        HttpClient? http,
        TimeSpan overallTimeout,
        TimeSpan stallTimeout)
    {
        http ??= SharedHttpClient;
        var list = urls.Distinct().ToList();
        if (list.Count == 0)
            return Fail("No OpenRGB download sources available.", log);

        // One time budget for the whole install, not per mirror.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(overallTimeout);

        var errors = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Uri url = list[i];
            string label = $"{url.Host}/{Path.GetFileName(url.AbsolutePath)}";

            if (budget.IsCancellationRequested)
            {
                errors.Add($"{label}: skipped — overall time limit of {Describe(overallTimeout)} reached");
                continue;
            }

            Uri? secureUrl = OpenRgbReleaseResolver.ToHttps(url);
            if (secureUrl is null)
            {
                errors.Add($"{label}: refused — only https downloads are allowed");
                log?.Invoke($"OpenRGB install: refused non-https source {url}");
                continue;
            }
            url = secureUrl;

            progress?.Report($"Downloading OpenRGB ({i + 1}/{list.Count}) from {url.Host}…");
            log?.Invoke($"OpenRGB install: trying {url}");

            string tempZip = Path.Combine(Path.GetTempPath(), $"fanzi-openrgb-{Guid.NewGuid():N}.zip");
            try
            {
                string? downloadError = await DownloadAsync(
                    http, url, tempZip, progress, ct, budget.Token, overallTimeout, stallTimeout).ConfigureAwait(false);
                if (downloadError != null)
                {
                    errors.Add($"{label}: {downloadError}");
                    log?.Invoke($"OpenRGB install: {url} failed: {downloadError}");
                    continue;
                }

                string? validationError = ValidatePackage(tempZip);
                if (validationError != null)
                {
                    errors.Add($"{label}: {validationError}");
                    log?.Invoke($"OpenRGB install: {url} rejected: {validationError}");
                    continue;
                }

                progress?.Report("Extracting OpenRGB…");
                var result = ExtractAndInstall(tempZip, targetDirectory, log);
                if (!result.Success)
                {
                    errors.Add($"{label}: {result.Error}");
                    continue;
                }

                progress?.Report("OpenRGB installed.");
                log?.Invoke($"OpenRGB install: installed {result.ExePath} from {url}");
                return result with { SourceUrl = url };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
                log?.Invoke($"OpenRGB install: {url} failed: {ex}");
            }
            finally
            {
                TryDeleteFile(tempZip);
            }
        }

        string summary = list.Count == 1
            ? $"OpenRGB download failed: {errors[0]}"
            : $"All {list.Count} sources failed: {string.Join("; ", errors)}";
        return Fail(summary, log);
    }

    /// <summary>
    /// Streams <paramref name="url"/> to <paramref name="destinationPath"/> within the install's
    /// overall time <paramref name="budget"/> and an idle-stall timeout. Returns null on success,
    /// otherwise the reason. The file is fully closed before this method returns.
    /// <paramref name="ct"/> is the caller's token (cancellation propagates); <paramref name="budget"/>
    /// is linked to it and additionally expires after <paramref name="overallTimeout"/>.
    /// </summary>
    internal static async Task<string?> DownloadAsync(
        HttpClient http, Uri url, string destinationPath, IProgress<string>? progress, CancellationToken ct,
        CancellationToken budget, TimeSpan overallTimeout, TimeSpan stallTimeout)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(budget);
        stall.CancelAfter(stallTimeout);

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();

            long? expected = response.Content.Headers.ContentLength;
            long total = 0;
            int lastReportedMb = -1;

            using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            using (var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    stall.CancelAfter(stallTimeout); // reset idle timer for each chunk
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    total += read;

                    int mb = (int)(total / (1024 * 1024));
                    if (mb != lastReportedMb)
                    {
                        lastReportedMb = mb;
                        progress?.Report(expected is > 0
                            ? $"Downloading OpenRGB… {mb} / {expected.Value / (1024 * 1024)} MB"
                            : $"Downloading OpenRGB… {mb} MB");
                    }
                }
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (expected is > 0 && total != expected.Value)
                return $"incomplete download ({total:N0} of {expected.Value:N0} bytes)";
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return budget.IsCancellationRequested
                ? $"timed out (overall install time limit of {Describe(overallTimeout)} reached)"
                : $"download stalled (no data for {Describe(stallTimeout)})";
        }
        catch (HttpRequestException ex)
        {
            return $"network error: {ex.Message}";
        }
    }

    /// <summary>Checks size and the ZIP local-file-header magic. Returns null when valid.</summary>
    internal static string? ValidatePackage(string zipPath, long minBytes = MinPackageBytes)
    {
        var info = new FileInfo(zipPath);
        if (!info.Exists) return "downloaded file is missing";
        if (info.Length < minBytes)
            return $"file too small ({info.Length:N0} bytes) — not an OpenRGB package";

        var header = new byte[4];
        using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int n = 0;
            while (n < header.Length)
            {
                int r = fs.Read(header, n, header.Length - n);
                if (r == 0) break;
                n += r;
            }
            if (n < header.Length || !header.AsSpan().SequenceEqual(ZipMagic))
                return "not a ZIP archive (server returned a web page or other content)";
        }
        return null;
    }

    /// <summary>
    /// Extracts the archive to a staging folder next to <paramref name="targetDirectory"/>,
    /// locates OpenRGB.exe at any depth and swaps its folder in atomically: the existing
    /// target is renamed to a backup, the new folder is moved into place and the backup is
    /// deleted. If the swap fails the backup is restored, so the target is never left
    /// half-written. <paramref name="moveDirectory"/> is a test seam (defaults to Directory.Move).
    /// </summary>
    internal static OpenRgbInstallResult ExtractAndInstall(
        string zipPath, string targetDirectory, Action<string>? log = null,
        Action<string, string>? moveDirectory = null)
    {
        moveDirectory ??= Directory.Move;
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        string parent = Path.GetDirectoryName(target) ?? target;
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        // Siblings of the target so every move stays on the same volume (Directory.Move can't cross volumes).
        string staging = $"{target}.staging-{suffix}";
        string backup = $"{target}.backup-{suffix}";

        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(staging);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            }
            catch (InvalidDataException ex)
            {
                return Fail($"archive is corrupt: {ex.Message}", log);
            }

            string? exe = Directory.EnumerateFiles(staging, ExeName, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                })
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
                .FirstOrDefault();
            if (exe == null)
                return Fail($"{ExeName} not found in the downloaded archive", log);

            string newDir = Path.GetDirectoryName(exe)!;

            // 1. Move the current install out of the way (fails cleanly if OpenRGB is running from it).
            bool hadPrevious = Directory.Exists(target);
            if (hadPrevious)
            {
                try
                {
                    moveDirectory(target, backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Fail($"could not replace {target} (is OpenRGB running from it?): {ex.Message}", log);
                }
            }

            // 2. Move the new folder in; on failure put the previous install back.
            try
            {
                moveDirectory(newDir, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (hadPrevious)
                {
                    try
                    {
                        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                        moveDirectory(backup, target);
                        log?.Invoke($"OpenRGB install: restored previous install at {target}");
                    }
                    catch (Exception restoreEx)
                    {
                        log?.Invoke($"OpenRGB install: could not restore previous install from {backup}: {restoreEx.Message}");
                    }
                }
                return Fail($"could not install to {target}: {ex.Message}", log);
            }

            // 3. Drop the old install.
            if (hadPrevious)
                TryDeleteDirectory(backup, log);

            string installedExe = Path.Combine(target, Path.GetFileName(exe));
            if (!File.Exists(installedExe))
                return Fail($"{ExeName} missing from {target} after install", log);

            // 4. A healthy install is in place: sweep backups/staging left by earlier failed attempts.
            CleanupStaleInstallFolders(target, log, moveDirectory);

            return OpenRgbInstallResult.Ok(installedExe, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"could not install to {target}: {ex.Message}", log);
        }
        finally
        {
            TryDeleteDirectory(staging, log: null);
        }
    }

    /// <summary>Sibling-folder suffixes left behind by interrupted installs ("OpenRGB.backup-1a2b3c4d" …).</summary>
    private static readonly string[] StaleSuffixes = { ".staging-", ".backup-", ".bak" };

    /// <summary>
    /// Removes leftovers of interrupted installs next to <paramref name="targetDirectory"/>:
    /// <c>&lt;name&gt;.staging-*</c>, <c>&lt;name&gt;.backup-*</c> and <c>&lt;name&gt;.bak*</c> folders.
    /// Called after every successful install and once at app start. If the target has no
    /// OpenRGB.exe but a backup does (the restore after a failed swap did not complete), the newest
    /// such backup is moved back into place first, so a previous working install is never deleted.
    /// Returns the number of stale folders removed. Never throws for I/O problems (logged instead).
    /// </summary>
    internal static int CleanupStaleInstallFolders(
        string targetDirectory, Action<string>? log = null, Action<string, string>? moveDirectory = null)
    {
        moveDirectory ??= Directory.Move;
        string target, parent, name;
        List<DirectoryInfo> stale;
        try
        {
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            parent = Path.GetDirectoryName(target) ?? target;
            name = Path.GetFileName(target);
            if (name.Length == 0 || !Directory.Exists(parent)) return 0;

            stale = new DirectoryInfo(parent)
                .EnumerateDirectories(name + ".*")
                .Where(d => StaleSuffixes.Any(s =>
                    d.Name.Length > name.Length + s.Length - 1
                    && d.Name.AsSpan(name.Length).StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log?.Invoke($"OpenRGB install: could not scan for stale install folders: {ex.Message}");
            return 0;
        }

        if (stale.Count == 0) return 0;

        // A failed restore leaves the only good copy in a backup: put the newest one back first.
        if (!File.Exists(Path.Combine(target, ExeName)))
        {
            var restore = stale
                .Where(d => !d.Name.AsSpan(name.Length).StartsWith(".staging-", StringComparison.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(d.FullName, ExeName)))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .FirstOrDefault();
            if (restore is not null)
            {
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    moveDirectory(restore.FullName, target);
                    stale.Remove(restore);
                    log?.Invoke($"OpenRGB install: restored previous install from {restore.FullName}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep every backup if the restore failed — deleting them could lose the only copy.
                    log?.Invoke($"OpenRGB install: could not restore {restore.FullName}: {ex.Message}");
                    return 0;
                }
            }
        }

        int removed = 0;
        foreach (var dir in stale)
        {
            if (TryDeleteDirectory(dir.FullName, log))
            {
                removed++;
                log?.Invoke($"OpenRGB install: removed stale folder {dir.FullName}");
            }
        }
        return removed;
    }

    private static bool TryDeleteDirectory(string path, Action<string>? log)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"OpenRGB install: could not delete {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>"5 minutes" / "30 s" style text for timeout messages.</summary>
    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} min" : $"{Math.Max(0, span.TotalSeconds):0.#} s";

    private static OpenRgbInstallResult Fail(string error, Action<string>? log)
    {
        log?.Invoke($"OpenRGB install: {error}");
        return OpenRgbInstallResult.Fail(error);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
