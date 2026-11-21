using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Fanzi.FanControl.Services;

/// <summary>
/// OAuth client for "Sign in with Google" (Google Cloud project <c>fanzi-io-nity</c>, Desktop-app client).
/// <para>
/// The client ID is public and ships in the app. Google still issues a client secret to Desktop clients
/// (not confidential for installed apps, but kept out of source control), so it is read from the JSON
/// downloaded from the Google Cloud console ("Download JSON"), saved as <c>google_oauth.json</c> in one of
/// <see cref="SearchPaths"/>. Both Google's own layout (<c>{"installed": {...}}</c>) and a flat
/// <c>{"client_id", "client_secret"}</c> object are accepted.
/// </para>
/// </summary>
public sealed record GoogleOAuthConfig(string ClientId, string? ClientSecret, string? SourcePath)
{
    /// <summary>FANZi IO-nity Desktop client (Google Cloud project fanzi-io-nity).</summary>
    public const string DefaultClientId = "965905510128-ens959bf325e101re8a6o3jj0aji63ar.apps.googleusercontent.com";

    public const string FileName = "google_oauth.json";

    /// <summary>True when a secret was found — Google's token endpoint requires it for Desktop clients.</summary>
    public bool HasSecret => !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Where <c>google_oauth.json</c> is looked for, in order.</summary>
    public static IReadOnlyList<string> SearchPaths { get; } = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FANZI", FileName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FANZI", FileName),
        Path.Combine(AppContext.BaseDirectory, FileName),
    };

    /// <summary>The preferred location shown to the user (%APPDATA%\FANZI\google_oauth.json).</summary>
    public static string PreferredPath => SearchPaths[0];

    /// <summary>Loads the first valid config file; falls back to the built-in client ID without a secret.</summary>
    public static GoogleOAuthConfig Load(IEnumerable<string>? paths = null)
    {
        foreach (var path in paths ?? SearchPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var parsed = Parse(File.ReadAllText(path), path);
                if (parsed is not null) return parsed;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Google OAuth config {path} unreadable", ex);
            }
        }

        // 4.6: official builds carry the Desktop client, so "Sign in with Google" works without the user fetching
        // a JSON file from the Google Cloud console. Only for the default search — an explicit path list means "these".
        if (paths is null && LoadEmbedded(typeof(GoogleOAuthConfig).Assembly) is { } embedded)
            return embedded;
        return new GoogleOAuthConfig(DefaultClientId, null, null);
    }

    /// <summary>Logical name of the client JSON embedded at build time (see Fanzi.FanControl.csproj).</summary>
    internal const string EmbeddedResourceName = "Fanzi.FanControl.google_oauth.json";

    /// <summary>Shown as <see cref="SourcePath"/> when the config came from inside the application.</summary>
    internal const string EmbeddedSource = "(built in)";

    /// <summary>
    /// The client JSON compiled into the assembly, or null when this build has none (a build from source without
    /// <c>google_oauth.json</c> next to FANZI.slnx). A Desktop-app client secret is not confidential — Google's
    /// installed-app flow (RFC 8252) says so and sign-in is protected by PKCE — but it still stays out of source
    /// control: the file is git-ignored locally and written from a repository secret in CI.
    /// </summary>
    internal static GoogleOAuthConfig? LoadEmbedded(System.Reflection.Assembly assembly)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
            return stream is null ? null : ParseStream(stream, EmbeddedSource);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Built-in Google OAuth client unreadable", ex);
            return null;
        }
    }

    internal static GoogleOAuthConfig? ParseStream(Stream stream, string? sourcePath)
    {
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), sourcePath);
    }

    /// <summary>Parses Google's client JSON ("installed" / "web" wrapper) or a flat object; null when invalid.</summary>
    public static GoogleOAuthConfig? Parse(string json, string? sourcePath = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("installed", out var installed)) root = installed;
        else if (root.TryGetProperty("web", out var web)) root = web;

        string? id = root.TryGetProperty("client_id", out var i) ? i.GetString() : null;
        string? secret = root.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            return null;
        return new GoogleOAuthConfig(id.Trim(), string.IsNullOrWhiteSpace(secret) ? null : secret.Trim(), sourcePath);
    }
}
