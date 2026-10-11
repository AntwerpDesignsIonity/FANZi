using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Fanzi.FanControl.Services.OpenRgb;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>
/// Detects and installs the PawnIO kernel driver (https://pawnio.eu). OpenRGB 1.0 needs it —
/// plus Administrator, which FANZi already runs as — to reach SMBus/I2C devices: RGB RAM and
/// most motherboard lighting. GPUs on NVIDIA's NvAPI I2C work without it.
/// </summary>
public static class PawnIoInstaller
{
    public const string ReleaseApiUrl = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";

    /// <summary>Pinned fallback when the GitHub API is unavailable (rate limit, proxy).</summary>
    public const string FallbackDownloadUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";

    /// <summary>Expected Authenticode signer of PawnIO_setup.exe.</summary>
    internal const string ExpectedSignerCn = "CN=namazso.eu";

    private const int ErrorSuccessRebootRequired = 3010;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"FANZi-IO-nity/{AppVersion.Display}");
        return c;
    }

    /// <summary>True when the PawnIO driver service is registered on this PC.</summary>
    public static bool IsInstalled
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true; // not applicable
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                if (key is not null) return true;
            }
            catch { /* fall through to the file check */ }

            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(sys, "drivers", "PawnIO.sys"));
        }
    }

    /// <summary>Outcome of an install attempt.</summary>
    public sealed record Result(bool Success, bool RebootRequired, string Message);

    /// <summary>
    /// Downloads the latest signed PawnIO_setup.exe, verifies its signature and runs
    /// <c>-install -silent</c>. FANZi runs elevated, so no extra UAC prompt is shown.
    /// </summary>
    public static async Task<Result> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new Result(true, false, "PawnIO is only needed on Windows");
        if (IsInstalled)
            return new Result(true, false, "PawnIO is already installed");

        string dir = Path.Combine(Path.GetTempPath(), "fanzi-pawnio");
        string setup = Path.Combine(dir, "PawnIO_setup.exe");
        try
        {
            Directory.CreateDirectory(dir);
            progress?.Report("Finding the latest PawnIO release…");
            string url = await ResolveDownloadUrlAsync(ct).ConfigureAwait(false);

            progress?.Report("Downloading PawnIO…");
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(setup);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            if (!IsTrustedSigner(setup, out string signer))
                return new Result(false, false, $"PawnIO installer signature not trusted ({signer}) — not run");

            progress?.Report("Installing the PawnIO driver…");
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = setup,
                Arguments = "-install -silent",
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("PawnIO setup did not start");

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            int code = proc.ExitCode;
            AppLog.Info($"PawnIO: setup exited with code {code}");

            if (code == ErrorSuccessRebootRequired)
                return new Result(true, true, "PawnIO installed — restart Windows to finish");
            if (code != 0)
                return new Result(false, false, $"PawnIO setup failed (code {code})");

            return IsInstalled
                ? new Result(true, false, "PawnIO installed")
                : new Result(false, false, "PawnIO setup finished but the driver is not registered");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("PawnIO: install failed", ex);
            return new Result(false, false, $"PawnIO install failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(setup)) File.Delete(setup); } catch { /* best effort */ }
        }
    }

    private static async Task<string> ResolveDownloadUrlAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (SelectSetupAsset(json) is { } url) return url;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            AppLog.Warn("PawnIO: release lookup failed, using pinned 2.2.0", ex);
        }
        return FallbackDownloadUrl;
    }

    /// <summary>Finds the PawnIO_setup.exe download URL in a GitHub release JSON document.</summary>
    internal static string? SelectSetupAsset(string releaseJson)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is not null && string.Equals(name, "PawnIO_setup.exe", StringComparison.OrdinalIgnoreCase)
                && url.StartsWith("https://github.com/namazso/", StringComparison.OrdinalIgnoreCase))
                return url;
        }
        return null;
    }

    /// <summary>
    /// True when Windows verifies the file's Authenticode signature (content intact, trusted chain)
    /// and the signer is namazso.eu.
    /// </summary>
    internal static bool IsTrustedSigner(string path, out string signer)
    {
        signer = "unsigned";
        if (!WinTrust.Verify(path, out int trustResult))
        {
            signer = $"WinVerifyTrust 0x{trustResult:X8}";
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile: reads the embedded signer certificate
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = cert.Subject;
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            bool chainOk = chain.Build(cert);
            return chainOk && cert.Subject.Contains(ExpectedSignerCn, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            signer = ex.Message;
            return false;
        }
    }
}

/// <summary>Minimal WinVerifyTrust (Authenticode) wrapper.</summary>
internal static class WinTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WintrustFileInfo
    {
        public uint cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>();
        public string? pcwszFilePath;
        public IntPtr hFile = IntPtr.Zero;
        public IntPtr pgKnownSubject = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class WintrustData : IDisposable
    {
        public uint cbStruct = (uint)Marshal.SizeOf<WintrustData>();
        public IntPtr pPolicyCallbackData = IntPtr.Zero;
        public IntPtr pSIPClientData = IntPtr.Zero;
        public uint dwUIChoice = WTD_UI_NONE;
        public uint fdwRevocationChecks = WTD_REVOKE_NONE;
        public uint dwUnionChoice = WTD_CHOICE_FILE;
        public IntPtr pFile;
        public uint dwStateAction = WTD_STATEACTION_VERIFY;
        public IntPtr hWVTStateData = IntPtr.Zero;
        public IntPtr pwszURLReference = IntPtr.Zero;
        public uint dwProvFlags = 0;
        public uint dwUIContext = 0;
        public IntPtr pSignatureSettings = IntPtr.Zero;

        public WintrustData(WintrustFileInfo file)
        {
            pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(file, pFile, false);
        }

        public void Dispose()
        {
            if (pFile != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WintrustFileInfo>(pFile);
                Marshal.FreeCoTaskMem(pFile);
                pFile = IntPtr.Zero;
            }
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, WintrustData pWVTData);

    /// <summary>Returns true when WinVerifyTrust reports a valid, trusted signature.</summary>
    public static bool Verify(string path, out int result)
    {
        result = -1;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

        using var data = new WintrustData(new WintrustFileInfo { pcwszFilePath = path });
        try
        {
            result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, data);
            return result == 0;
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, data);
        }
    }
}
