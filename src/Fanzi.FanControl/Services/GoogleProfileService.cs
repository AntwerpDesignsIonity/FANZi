using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Google Profile Service — OAuth2 login + Google Drive sync for FANZi profiles.
/// Uses Google Identity Platform (installed app flow) with a local redirect listener.
/// Profiles are stored as JSON in the user's Google Drive app folder.
/// </summary>
public sealed class GoogleProfileService : IDisposable
{
    private static readonly HttpClient Http = new();
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";
    private const string DriveUploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";
    private const string DriveListEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string RedirectUri = "http://localhost:19876/auth";
    private const int LocalPort = 19876;

    // FANZi Google OAuth Client (Installed App)
    // This is a public client ID for desktop apps — safe to ship in source.
    private const string ClientId = "764089528038-EXAMPLE.apps.googleusercontent.com";

    private readonly string _tokenPath;
    private GoogleToken? _token;
    private HttpListener? _listener;

    public bool IsLoggedIn => _token is not null && !string.IsNullOrEmpty(_token.AccessToken);
    public string? UserName { get; private set; }
    public string? UserEmail { get; private set; }
    public string? UserPicture { get; private set; }
    public string Status { get; private set; } = "Not signed in";

    public GoogleProfileService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FANZI");
        Directory.CreateDirectory(appData);
        _tokenPath = Path.Combine(appData, "google_token.json");
        LoadSavedToken();
    }

    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        try
        {
            Status = "Opening Google sign-in...";

            // Start local listener for OAuth redirect
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{LocalPort}/");
            _listener.Start();

            // Build OAuth URL
            var scopes = Uri.EscapeDataString("openid email profile https://www.googleapis.com/auth/drive.appdata");
            var authUrl = $"{AuthEndpoint}?client_id={ClientId}" +
                          $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                          $"&response_type=code" +
                          $"&scope={scopes}" +
                          $"&access_type=offline" +
                          $"&prompt=consent";

            // Open browser
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authUrl)
            {
                UseShellExecute = true
            });

            // Wait for redirect with auth code
            Status = "Waiting for Google sign-in...";
            var context = await WaitForCallbackAsync(ct);
            if (context is null)
            {
                Status = "Sign-in cancelled";
                return false;
            }

            // Extract auth code from query string
            string? code = null;
            var query = context.Request.Url?.Query ?? "";
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "code")
                    code = Uri.UnescapeDataString(kv[1]);
            }

            if (string.IsNullOrEmpty(code))
            {
                Status = "No auth code received";
                return false;
            }

            // Exchange code for tokens
            Status = "Completing sign-in...";
            var tokenResult = await ExchangeCodeAsync(code, ct);
            if (tokenResult is null)
            {
                Status = "Token exchange failed";
                return false;
            }

            _token = tokenResult;
            SaveToken();

            // Fetch user info
            await FetchUserInfoAsync(ct);

            Status = $"Signed in as {UserEmail}";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Login failed: {ex.Message}";
            return false;
        }
        finally
        {
            try { _listener?.Stop(); } catch { }
        }
    }

    public void Logout()
    {
        _token = null;
        UserName = null;
        UserEmail = null;
        UserPicture = null;
        Status = "Signed out";
        try { File.Delete(_tokenPath); } catch { }
    }

    public async Task<bool> UploadProfileAsync(string profileJson, string fileName, CancellationToken ct = default)
    {
        if (!IsLoggedIn || _token is null) return false;

        try
        {
            Status = $"Uploading {fileName}...";

            // First, search for existing file
            string? existingFileId = await FindFileAsync(fileName, ct);

            var metadata = new
            {
                name = fileName,
                parents = new[] { "appDataFolder" }
            };

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json"), "metadata");
            content.Add(new StringContent(profileJson, Encoding.UTF8, "application/json"), "file");

            var url = existingFileId is not null
                ? $"{DriveUploadEndpoint}/{existingFileId}?uploadType=multipart"
                : $"{DriveUploadEndpoint}?uploadType=multipart";

            var request = new HttpRequestMessage(existingFileId is not null ? HttpMethod.Patch : HttpMethod.Post, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token.AccessToken);
            request.Content = content;

            var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                Status = $"Uploaded {fileName} to Google Drive";
                return true;
            }

            Status = $"Upload failed: {response.StatusCode}";
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
        if (!IsLoggedIn || _token is null) return null;

        try
        {
            Status = $"Downloading {fileName}...";
            string? fileId = await FindFileAsync(fileName, ct);
            if (fileId is null)
            {
                Status = $"{fileName} not found in Google Drive";
                return null;
            }

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token.AccessToken);

            var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                Status = $"Downloaded {fileName}";
                return json;
            }

            Status = $"Download failed: {response.StatusCode}";
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
        if (!IsLoggedIn || _token is null) return Array.Empty<string>();

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{DriveListEndpoint}?spaces=appDataFolder&fields=files(id,name)&pageSize=50");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token.AccessToken);

            var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var names = new List<string>();

            if (doc.RootElement.TryGetProperty("files", out var files))
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.TryGetProperty("name", out var name))
                        names.Add(name.GetString() ?? "");
                }
            }

            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private async Task<HttpListenerContext?> WaitForCallbackAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            var context = await _listener!.GetContextAsync();

            // Send success page to browser
            var response = context.Response;
            string html = "<html><body style='background:#050A12;color:#22C55E;font-family:sans-serif;text-align:center;padding:60px'>" +
                          "<h1>FANZi IO-nity</h1><p>Sign-in successful! You can close this tab.</p>" +
                          "<script>setTimeout(()=>window.close(),2000)</script></body></html>";
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();

            return context;
        }
        catch { return null; }
    }

    private async Task<GoogleToken?> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["grant_type"] = "authorization_code",
        });

        var response = await Http.PostAsync(TokenEndpoint, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode) return null;

        var doc = JsonDocument.Parse(json);
        return new GoogleToken(
            doc.RootElement.GetProperty("access_token").GetString() ?? "",
            doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            doc.RootElement.GetProperty("expires_in").GetInt32(),
            DateTimeOffset.UtcNow);
    }

    private async Task FetchUserInfoAsync(CancellationToken ct)
    {
        if (_token is null) return;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token.AccessToken);

            var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);

            UserName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            UserEmail = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
            UserPicture = doc.RootElement.TryGetProperty("picture", out var p) ? p.GetString() : null;
        }
        catch { }
    }

    private async Task<string?> FindFileAsync(string fileName, CancellationToken ct)
    {
        if (_token is null) return null;

        try
        {
            var query = Uri.EscapeDataString($"name='{fileName}' and trashed=false");
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{DriveListEndpoint}?spaces=appDataFolder&q={query}&fields=files(id,name)");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token.AccessToken);

            var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);

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
            var json = File.ReadAllText(_tokenPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _token = new GoogleToken(
                root.GetProperty("access_token").GetString() ?? "",
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                root.GetProperty("expires_in").GetInt32(),
                root.GetProperty("created_at").GetDateTime());

            // Check if token is expired
            if (DateTimeOffset.UtcNow > _token.CreatedAt.AddSeconds(_token.ExpiresIn - 60))
            {
                _token = null;
                return;
            }

            // Try to restore user info
            _ = FetchUserInfoAsync(CancellationToken.None).ContinueWith(_ => { });
        }
        catch { _token = null; }
    }

    private void SaveToken()
    {
        try
        {
            if (_token is null) return;
            var json = JsonSerializer.Serialize(new
            {
                _token.AccessToken,
                _token.RefreshToken,
                _token.ExpiresIn,
                created_at = _token.CreatedAt
            });
            File.WriteAllText(_tokenPath, json);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _listener?.Stop(); } catch { }
    }
}

internal sealed record GoogleToken(string AccessToken, string? RefreshToken, int ExpiresIn, DateTimeOffset CreatedAt);
