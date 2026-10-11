using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services.OpenRgb;

/// <summary>
/// Finds download URLs for the Windows portable OpenRGB package by reading
/// https://openrgb.org/releases.html, with pinned (verified) fallbacks.
/// Shared with Fanzi.Installer (linked source) — no app-specific dependencies.
/// </summary>
public static class OpenRgbReleaseResolver
{
    public static readonly Uri ReleasesPageUri = new("https://openrgb.org/releases.html");
    public static readonly Uri BaseUri = new("https://openrgb.org/");

    /// <summary>
    /// Pinned fallbacks, verified HTTP 200 against codeberg.org on 2026-10-09
    /// (1.0: 22,012,179 bytes; 0.9: 11,849,808 bytes). Windows x64 portable zips.
    /// </summary>
    public static readonly IReadOnlyList<Uri> PinnedFallbackUrls = new[]
    {
        new Uri("https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.0/OpenRGB_1.0_Windows_64_81bbe18.zip"),
        new Uri("https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_0.9/OpenRGB_0.9_Windows_64_b5f46e3.zip"),
    };

    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(20);

    private static readonly Regex HeadingRegex = new(
        @"<h3[^>]*>(?<title>.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RowRegex = new(
        @"<tr[^>]*>(?<row>.*?)(?=<tr[\s>]|</tbody|</table|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HeaderCellRegex = new(
        @"<th(?=[\s>])[^>]*>(?<text>.*?)(?=<th[\s>]|</th|</tr|<tbody|</thead|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CellRegex = new(
        @"<td[^>]*>(?<cell>.*?)(?=<td[\s>]|</tr|<tr[\s>]|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HrefRegex = new(
        @"href\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    private enum SectionKind { Stable, ReleaseCandidate, Pipeline, Other }

    /// <summary>
    /// Parses the OpenRGB releases page and returns Windows portable .zip URLs for
    /// <paramref name="arch"/> from the first stable "Release" section. Release
    /// candidates are used only when no stable section yields a URL, and pipeline
    /// builds only when neither does. Relative hrefs resolve against <paramref name="baseUri"/>.
    /// </summary>
    public static IReadOnlyList<Uri> ParseWindowsPortableZipUrls(string html, Uri baseUri, Architecture arch)
    {
        if (string.IsNullOrEmpty(html)) return Array.Empty<Uri>();

        var sections = SplitSections(html);
        foreach (var kind in new[] { SectionKind.Stable, SectionKind.ReleaseCandidate, SectionKind.Pipeline })
        {
            foreach (var (sectionKind, body) in sections)
            {
                if (sectionKind != kind) continue;
                var urls = ExtractWindowsZips(body, baseUri, arch);
                if (urls.Count > 0) return urls;
            }
        }
        return Array.Empty<Uri>();
    }

    /// <summary>
    /// Downloads the releases page, parses it and appends the pinned fallbacks
    /// (de-duplicated, page results first). Never throws for network/parse errors —
    /// on failure the pinned fallbacks alone are returned.
    /// </summary>
    public static async Task<IReadOnlyList<Uri>> ResolveAsync(
        HttpClient http,
        CancellationToken ct = default,
        Action<string>? log = null,
        Architecture? arch = null)
    {
        var architecture = arch ?? RuntimeInformation.OSArchitecture;
        var result = new List<Uri>();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PageTimeout);
            using var response = await http.GetAsync(ReleasesPageUri, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"OpenRGB resolver: {ReleasesPageUri} returned HTTP {(int)response.StatusCode} {response.StatusCode}");
            }
            else
            {
                string html = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var parsed = ParseWindowsPortableZipUrls(html, BaseUri, architecture);
                log?.Invoke(parsed.Count > 0
                    ? $"OpenRGB resolver: releases page lists {string.Join(", ", parsed)}"
                    : "OpenRGB resolver: no Windows portable zip found on releases page");
                result.AddRange(parsed);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"OpenRGB resolver: could not read {ReleasesPageUri}: {ex.GetType().Name}: {ex.Message}");
        }

        // Pinned fallbacks are x64 builds (also run on Windows ARM64 via emulation).
        if (architecture is Architecture.X64 or Architecture.Arm64)
            result.AddRange(PinnedFallbackUrls);

        return result.Distinct().ToList();
    }

    private static List<(SectionKind Kind, string Body)> SplitSections(string html)
    {
        var list = new List<(SectionKind, string)>();
        var headings = HeadingRegex.Matches(html);
        for (int i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            int start = h.Index + h.Length;
            int end = i + 1 < headings.Count ? headings[i + 1].Index : html.Length;
            string title = WebUtility.HtmlDecode(TagRegex.Replace(h.Groups["title"].Value, "")).Trim();
            list.Add((Classify(title), html.Substring(start, end - start)));
        }
        return list;
    }

    private static SectionKind Classify(string title)
    {
        if (title.Contains("Pipeline", StringComparison.OrdinalIgnoreCase)) return SectionKind.Pipeline;
        if (title.Contains("Candidate", StringComparison.OrdinalIgnoreCase)) return SectionKind.ReleaseCandidate;
        if (title.StartsWith("Release", StringComparison.OrdinalIgnoreCase)) return SectionKind.Stable;
        return SectionKind.Other;
    }

    private static List<Uri> ExtractWindowsZips(string section, Uri baseUri, Architecture arch)
    {
        var columns = ColumnPreference(arch);
        var found = new List<Uri>();

        // Column headers ("x86 64-bit", "Arm 64-bit", ...) → cell index (platform column excluded).
        var headers = new List<string>();
        foreach (Match th in HeaderCellRegex.Matches(section))
            headers.Add(WebUtility.HtmlDecode(TagRegex.Replace(th.Groups["text"].Value, "")).Trim());

        foreach (Match row in RowRegex.Matches(section))
        {
            var cells = CellRegex.Matches(row.Groups["row"].Value).Select(m => m.Groups["cell"].Value).ToList();
            if (cells.Count == 0) continue;
            string platform = TagRegex.Replace(cells[0], " ");
            bool isWindows = platform.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                             || cells[0].Contains("alt=Windows", StringComparison.OrdinalIgnoreCase)
                             || cells[0].Contains("alt=\"Windows\"", StringComparison.OrdinalIgnoreCase);
            if (!isWindows) continue;

            foreach (var column in columns)
            {
                int cellIndex = FindColumn(headers, column);
                if (cellIndex < 1 || cellIndex >= cells.Count) continue;
                foreach (var uri in ExtractZipHrefs(cells[cellIndex], baseUri))
                    if (!found.Contains(uri)) found.Add(uri);
            }
            if (found.Count > 0) return found;
        }

        // Table layout not recognised — fall back to file-name matching within the section.
        foreach (var column in columns)
        {
            string token = column switch
            {
                ArchColumn.X64 => "Windows_64",
                ArchColumn.Arm64 => "Windows_ARM64",
                _ => "Windows_32",
            };
            foreach (var uri in ExtractZipHrefs(section, baseUri))
                if (uri.AbsolutePath.Contains(token, StringComparison.OrdinalIgnoreCase) && !found.Contains(uri))
                    found.Add(uri);
        }
        return found;
    }

    private enum ArchColumn { X64, Arm64, X86 }

    private static ArchColumn[] ColumnPreference(Architecture arch) => arch switch
    {
        // Windows on ARM runs x64 binaries under emulation; prefer native when published.
        Architecture.Arm64 => new[] { ArchColumn.Arm64, ArchColumn.X64 },
        Architecture.X86 => new[] { ArchColumn.X86 },
        _ => new[] { ArchColumn.X64 },
    };

    /// <summary>Returns the &lt;td&gt; index (0 = platform) for an arch column.</summary>
    private static int FindColumn(List<string> headers, ArchColumn column)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            string h = headers[i].ToLowerInvariant();
            bool match = column switch
            {
                ArchColumn.X64 => h.Contains("x86") && h.Contains("64"),
                ArchColumn.Arm64 => h.Contains("arm") && h.Contains("64"),
                ArchColumn.X86 => h.Contains("x86") && h.Contains("32"),
                _ => false,
            };
            if (match) return i;
        }

        // Default openrgb.org layout: Platform | x86 64-bit | Arm 64-bit | x86 32-bit | Arm 32-bit
        return column switch
        {
            ArchColumn.X64 => 1,
            ArchColumn.Arm64 => 2,
            _ => 3,
        };
    }

    private static IEnumerable<Uri> ExtractZipHrefs(string fragment, Uri baseUri)
    {
        foreach (Match m in HrefRegex.Matches(fragment))
        {
            string href = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
            if (href.Length == 0) continue;
            if (!Uri.TryCreate(baseUri, href, out var parsed)) continue;
            // The extracted OpenRGB.exe is launched elevated — never fetch it over plain http.
            Uri? uri = ToHttps(parsed);
            if (uri is null) continue;
            if (!uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            yield return uri;
        }
    }

    /// <summary>
    /// Returns <paramref name="uri"/> when it is https, the https equivalent of a plain-http
    /// URL on the default port, and null for anything else (other schemes, http on a custom
    /// port). Downloads must only ever happen over https.
    /// </summary>
    internal static Uri? ToHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme != Uri.UriSchemeHttp || !uri.IsDefaultPort) return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
    }
}
