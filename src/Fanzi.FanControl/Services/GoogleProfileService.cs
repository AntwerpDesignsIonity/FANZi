using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Google Profile Service — "Sign in with Google" + Google Drive sync for FANZi profiles.
/// <para>
/// OAuth 2.0 for installed apps (RFC 8252): the system browser opens Google's consent page, Google redirects
/// to a one-shot loopback listener on <c>http://127.0.0.1:{free port}/</c>, and the code is exchanged with
/// PKCE (S256) + a CSRF <c>state</c>. The client comes from <see cref="GoogleOAuthConfig"/>. Tokens live in
/// %LOCALAPPDATA%\FANZI\google_token.json (per user) and are refreshed with the refresh token, so a sign-in
/// survives restarts. Profiles are stored as JSON in the Drive app-data folder (scope drive.appdata).
/// </para>
/// </summary>
public sealed class GoogleProfileService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    private const string DriveUploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";
    private const string DriveListEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string Scopes = "openid email profile https://www.googleapis.com/auth/drive.appdata";

    /// <summary>How long the browser consent may take before sign-in is abandoned.</summary>
    internal static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private readonly string _tokenPath;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private GoogleToken? _token;
    private HttpListener? _listener;

    public bool IsLoggedIn => _token is not null && !string.IsNullOrEmpty(_token.AccessToken);
    public string? UserName { get; private set; }
    public string? UserEmail { get; private set; }
    public string? UserPicture { get; private set; }
    public string Status { get; private set; } = "Not signed in";

    /// <summary>The OAuth client in use (re-read on every sign-in so a newly saved google_oauth.json applies).</summary>
    public GoogleOAuthConfig Config { get; private set; }

    /// <summary>True when google_oauth.json with a client secret was found.</summary>
    public bool IsConfigured => Config.HasSecret;

    public GoogleProfileService() : this(null) { }

    internal GoogleProfileService(string? tokenPath)
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FANZI");
        Directory.CreateDirectory(appData);
        _tokenPath = tokenPath ?? Path.Combine(appData, "google_token.json");
        Config = GoogleOAuthConfig.Load();
        LoadSavedToken();
    }

    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        Config = GoogleOAuthConfig.Load();
        if (!Config.HasSecret)
        {
            Status = $"Google sign-in not set up: save the OAuth client JSON as {GoogleOAuthConfig.PreferredPath}";
            AppLog.Warn(Status);
            return false;
        }

        try
        {
            Status = "Opening Google sign-in...";

            // Loopback redirect on a free port (RFC 8252 §7.3) — no fixed port to collide with.
            int port = FreeLoopbackPort();
            string redirectUri = $"http://127.0.0.1:{port}/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(redirectUri);
            _listener.Start();

            string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(16));

            var authUrl = BuildAuthUrl(Config.ClientId, redirectUri, challenge, state);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });

            Status = "Waiting for Google sign-in in your browser...";
            var query = await WaitForCallbackAsync(ct);
            if (query is null)
            {
                Status = "Sign-in cancelled or timed out";
                return false;
            }

            if (query.TryGetValue("error", out var error))
            {
                Status = error == "access_denied"
                    ? "Sign-in denied (while the app is in testing, only added test users can sign in)"
                    : $"Google sign-in error: {error}";
                return false;
            }
            if (!query.TryGetValue("state", out var returnedState) || returnedState != state)
            {
                Status = "Sign-in rejected: state mismatch";
                return false;
            }
            if (!query.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            {
                Status = "No auth code received";
                return false;
            }

            Status = "Completing sign-in...";
            var tokenResult = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            }, previousRefreshToken: null, ct);
            if (tokenResult is null) return false; // Status set by RequestTokenAsync

            _token = tokenResult;
            SaveToken();
            await FetchUserInfoAsync(ct);

            Status = $"Signed in as {UserEmail ?? UserName ?? "Google user"}";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Login failed: {ex.Message}";
            AppLog.Error("Google sign-in failed", ex);
            return false;
        }
        finally
        {
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }
    }

    /// <summary>
    /// Re-validates a saved sign-in at start-up (refreshing the access token if needed) and loads the profile.
    /// True when the user is still signed in.
    /// </summary>
    public async Task<bool> RestoreSessionAsync(CancellationToken ct = default)
    {
        if (_token is null) return false;
        await FetchUserInfoAsync(ct);
        if (_token is null || UserEmail is null)
        {
            if (_token is null) Status = "Google session expired - please sign in again";
            return false;
        }
        return true;
    }

    /// <summary>Google consent URL for the installed-app flow with PKCE (exposed for tests).</summary>
    internal static string BuildAuthUrl(string clientId, string redirectUri, string codeChallenge, string state)
        => $"{AuthEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
           $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
           "&response_type=code" +
           $"&scope={Uri.EscapeDataString(Scopes)}" +
           $"&code_challenge={codeChallenge}&code_challenge_method=S256" +
           $"&state={Uri.EscapeDataString(state)}" +
           "&access_type=offline&prompt=consent";

    public void Logout()
    {
        var token = _token;
        _token = null;
        UserName = null;
        UserEmail = null;
        UserPicture = null;
        Status = "Signed out";
        try { File.Delete(_tokenPath); } catch { }

        // Revoke server-side too (best effort, fire and forget).
        var revoke = token?.RefreshToken ?? token?.AccessToken;
        if (!string.IsNullOrEmpty(revoke))
            _ = Http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = revoke }))
                .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public async Task<bool> UploadProfileAsync(string profileJson, string fileName, CancellationToken ct = default)
    {
        if (!IsLoggedIn) return false;

        try
        {
            Status = $"Uploading {fileName}...";
            string? existingFileId = await FindFileAsync(fileName, ct);

            // Metadata may only name the parent on create; an update keeps the file where it is.
            object metadata = existingFileId is null
                ? new { name = fileName, parents = new[] { "appDataFolder" } }
                : new { name = fileName };

            var url = existingFileId is not null
                ? $"{DriveUploadEndpoint}/{existingFileId}?uploadType=multipart"
                : $"{DriveUploadEndpoint}?uploadType=multipart";

            using var response = await SendAuthorizedAsync(() =>
            {
                var content = new MultipartContent("related")
                {
                    new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json"),
                    new StringContent(profileJson, Encoding.UTF8, "application/json"),
                };
                return new HttpRequestMessage(existingFileId is not null ? HttpMethod.Patch : HttpMethod.Post, url) { Content = content };
            }, ct);

            if (response?.IsSuccessStatusCode == true)
            {
                Status = $"Uploaded {fileName} to Google Drive";
                return true;
            }

            Status = $"Upload failed: {await DescribeErrorAsync(response, ct)}";
            return false;
        }
        catch (Exception ex)
        {
            Status = $"Upload error: {ex.Message}";
            return false;
        }
    }

    public async Task<string?> DownloadProfileAsync(string fileName, CancellationToken ct = default)
    {
        if (!IsLoggedIn) return null;

        try
        {
            Status = $"Downloading {fileName}...";
            string? fileId = await FindFileAsync(fileName, ct);
            if (fileId is null)
            {
                Status = $"{fileName} not found in Google Drive";
                return null;
            }

            using var response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"{DriveListEndpoint}/{fileId}?alt=media"), ct);
            if (response?.IsSuccessStatusCode == true)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                Status = $"Downloaded {fileName}";
                return json;
            }

            Status = $"Download failed: {await DescribeErrorAsync(response, ct)}";
            return null;
        }
        catch (Exception ex)
        {
            Status = $"Download error: {ex.Message}";
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListCloudProfilesAsync(CancellationToken ct = default)
    {
        if (!IsLoggedIn) return Array.Empty<string>();

        try
        {
            using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get,
                $"{DriveListEndpoint}?spaces=appDataFolder&fields=files(id,name)&pageSize=100"), ct);
            if (response?.IsSuccessStatusCode != true) return Array.Empty<string>();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("files", out var files))
                foreach (var file in files.EnumerateArray())
                    if (file.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n)
                        names.Add(n);
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>Waits (max <see cref="LoginTimeout"/>) for Google's redirect and answers the browser tab.</summary>
    private async Task<Dictionary<string, string>?> WaitForCallbackAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var context = await _listener!.GetContextAsync().WaitAsync(LoginTimeout, ct);
                var query = ParseQuery(context.Request.Url?.Query);

                // Browsers also ask for /favicon.ico etc.; only the redirect carries code / error.
                bool isRedirect = query.ContainsKey("code") || query.ContainsKey("error");
                bool ok = query.ContainsKey("code");
                string html = !isRedirect ? "" :
                    "<html><head><meta charset='utf-8'><title>FANZi IO-nity</title></head>" +
                    "<body style='background:#050A12;color:#E8F0FF;font-family:Segoe UI,sans-serif;text-align:center;padding:60px'>" +
                    "<h1 style='background:linear-gradient(90deg,#38BDF8,#8B5CF6,#EF4444);-webkit-background-clip:text;color:transparent'>FANZi IO-nity</h1>" +
                    (ok ? "<p style='color:#22C55E'>Signed in with Google. You can close this tab and return to FANZi.</p>"
                        : "<p style='color:#EF4444'>Sign-in was not completed. You can close this tab.</p>") +
                    "</body></html>";
                byte[] buffer = Encoding.UTF8.GetBytes(html);
                context.Response.StatusCode = isRedirect ? 200 : 404;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, ct);
                context.Response.OutputStream.Close();

                if (isRedirect) return query;
            }
        }
        catch (TimeoutException) { return null; }
        catch (OperationCanceledException) { return null; }
        catch (HttpListenerException) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>
    /// POSTs a token request (code exchange or refresh). Returns null and sets <see cref="Status"/> with Google's
    /// error description on failure. A refresh response carries no new refresh token, so the old one is kept.
    /// </summary>
    private async Task<GoogleToken?> RequestTokenAsync(Dictionary<string, string> form, string? previousRefreshToken, CancellationToken ct)
    {
        form["client_id"] = Config.ClientId;
        if (Config.HasSecret) form["client_secret"] = Config.ClientSecret!;

        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Status = $"Google token error: {DescribeError(json, response.StatusCode)}";
            AppLog.Warn(Status);
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new GoogleToken(
            root.GetProperty("access_token").GetString() ?? "",
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : previousRefreshToken,
            root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600,
            DateTimeOffset.UtcNow);
    }

    /// <summary>Returns a valid access token, refreshing it (once, serialized) when it is about to expire.</summary>
    private async Task<string?> EnsureAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var token = _token;
            if (token is null) return null;
            if (!forceRefresh && !token.IsExpired) return token.AccessToken;
            if (string.IsNullOrEmpty(token.RefreshToken))
            {
                Status = "Google session expired - please sign in again";
                return null;
            }

            var refreshed = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["refresh_token"] = token.RefreshToken!,
                ["grant_type"] = "refresh_token",
            }, token.RefreshToken, ct);
            if (refreshed is null)
            {
                // invalid_grant = revoked / expired refresh token (7 days while the app is in testing).
                if (Status.Contains("invalid_grant", StringComparison.Ordinal))
                {
                    _token = null;
                    try { File.Delete(_tokenPath); } catch { }
                    Status = "Google session expired - please sign in again";
                }
                return null;
            }

            _token = refreshed;
            SaveToken();
            return refreshed.AccessToken;
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>Sends an authorized request; on 401 refreshes the token once and retries.</summary>
    private async Task<HttpResponseMessage?> SendAuthorizedAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var access = await EnsureAccessTokenAsync(ct, forceRefresh: attempt > 0);
            if (access is null) return null;
            var request = makeRequest();
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            var response = await Http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
        }
        return null;
    }

    private static async Task<string> DescribeErrorAsync(HttpResponseMessage? response, CancellationToken ct)
    {
        if (response is null) return "not signed in";
        string body = "";
        try { body = await response.Content.ReadAsStringAsync(ct); } catch { }
        return DescribeError(body, response.StatusCode);
    }

    /// <summary>"error: error_description" from a Google error body, else the HTTP status (exposed for tests).</summary>
    internal static string DescribeError(string? body, HttpStatusCode status)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String)
                    {
                        var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                        return string.IsNullOrEmpty(desc) ? err.GetString()! : $"{err.GetString()}: {desc}";
                    }
                    if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                        return m.GetString() ?? status.ToString();
                }
            }
        }
        catch (JsonException) { }
        return $"{(int)status} {status}";
    }

    private async Task FetchUserInfoAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint), ct);
            if (response?.IsSuccessStatusCode != true) return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            UserName = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            UserEmail = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            UserPicture = root.TryGetProperty("picture", out var p) ? p.GetString() : null;
            if (UserEmail is not null) Status = $"Signed in as {UserEmail}";
        }
        catch (Exception ex) { AppLog.Warn("Google user info failed", ex); }
    }

    private async Task<string?> FindFileAsync(string fileName, CancellationToken ct)
    {
        try
        {
            var escaped = fileName.Replace("\\", "\\\\").Replace("'", "\\'");
            var query = Uri.EscapeDataString($"name='{escaped}' and trashed=false");
            using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get,
                $"{DriveListEndpoint}?spaces=appDataFolder&q={query}&fields=files(id,name)"), ct);
            if (response?.IsSuccessStatusCode != true) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                return files[0].GetProperty("id").GetString();
        }
        catch { }

        return null;
    }

    private void LoadSavedToken()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return;
            _token = JsonSerializer.Deserialize<GoogleToken>(File.ReadAllText(_tokenPath));
            if (_token is null || string.IsNullOrEmpty(_token.AccessToken)) { _token = null; return; }

            // Expired access tokens are kept: the refresh token renews them on first use (RestoreSessionAsync).
            Status = "Restoring Google session...";
        }
        catch { _token = null; }
    }

    private void SaveToken()
    {
        try
        {
            if (_token is null) return;
            File.WriteAllText(_tokenPath, JsonSerializer.Serialize(_token));
        }
        catch (Exception ex) { AppLog.Warn("Saving Google token failed", ex); }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0])] = kv.Length == 2 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    public void Dispose()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _tokenLock.Dispose();
    }
}

/// <summary>Persisted OAuth tokens (property names are the on-disk format).</summary>
internal sealed record GoogleToken(string AccessToken, string? RefreshToken, int ExpiresIn, DateTimeOffset CreatedAt)
{
    /// <summary>Expired, or within a minute of expiring.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow >= CreatedAt.AddSeconds(ExpiresIn - 60);
}
