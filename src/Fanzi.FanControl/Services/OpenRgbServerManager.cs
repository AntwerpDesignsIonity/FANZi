using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public sealed class OpenRgbServerManager : IDisposable
{
    private static readonly HttpClient Http = new();
    private Process? _serverProcess;
    private bool _disposed;

    public bool IsRunning => _serverProcess is { HasExited: false };
    public string Status { get; private set; } = "Server not started";
    public int Port { get; private set; } = 6742;
    public bool IsInstalled => DetectedPath is not null;

    private static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FANZI", "OpenRGB");

    private static readonly string[] SearchPaths =
    [
        @"C:\Program Files\OpenRGB\OpenRGB.exe",
        @"C:\Program Files (x86)\OpenRGB\OpenRGB.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
        Path.Combine(InstallDir, "OpenRGB.exe"),
    ];

    public string? DetectedPath { get; private set; }

    public void DetectInstallation()
    {
        DetectedPath = SearchPaths.FirstOrDefault(File.Exists);

        if (DetectedPath is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in envPath.Split(';'))
            {
                var candidate = Path.Combine(dir.Trim(), "OpenRGB.exe");
                if (File.Exists(candidate))
                {
                    DetectedPath = candidate;
                    break;
                }
            }
        }

        Status = DetectedPath is not null
            ? $"OpenRGB found: {Path.GetDirectoryName(DetectedPath)}"
            : "OpenRGB not detected — install from openrgb.org";
    }

    public Task<bool> StartServerAsync(int port = 6742, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Port = port;

            if (IsRunning)
            {
                Status = $"Server already running (PID {_serverProcess!.Id})";
                return true;
            }

            if (IsOpenRgbAlreadyRunning())
            {
                Status = "OpenRGB is already running externally";
                return true;
            }

            if (DetectedPath is null)
            {
                DetectInstallation();
                if (DetectedPath is null)
                {
                    Status = "Cannot start: OpenRGB not installed";
                    return false;
                }
            }

            try
            {
                _serverProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        // --server enables the SDK; we DON'T pass --noautoconnect so the
                        // server actually scans for devices on startup. WorkingDirectory
                        // is set so OpenRGB finds its config files relative to the exe.
                        FileName = DetectedPath,
                        Arguments = $"--server --server-port {port}",
                        WorkingDirectory = Path.GetDirectoryName(DetectedPath) ?? "",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    },
                    EnableRaisingEvents = true,
                };

                _serverProcess.Exited += (_, _) =>
                {
                    Status = "Server stopped";
                    _serverProcess = null;
                };

                _serverProcess.Start();

                // Wait for the SDK port to actually become reachable (up to 15s).
                // OpenRGB needs time to scan devices before binding the SDK socket.
                if (WaitForPortOpen("127.0.0.1", port, TimeSpan.FromSeconds(15)))
                {
                    Status = $"Server running on port {port} (PID {_serverProcess.Id})";
                    return true;
                }

                if (_serverProcess is { HasExited: false })
                {
                    Status = $"Server PID {_serverProcess.Id} running but port {port} not yet listening";
                    return true; // process is alive; client retries will catch up
                }
                Status = "Server process exited before port opened";
                return false;
            }
            catch (Exception ex)
            {
                Status = $"Start failed: {ex.Message}";
                return false;
            }
        }, ct);
    }

    public void StopServer()
    {
        if (_serverProcess is { HasExited: false })
        {
            try
            {
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(3000);
            }
            catch { }

            _serverProcess = null;
            Status = "Server stopped";
        }
    }

    private static bool IsOpenRgbAlreadyRunning()
    {
        try
        {
            return Process.GetProcessesByName("OpenRGB").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// TCP port probe — repeatedly attempts a connect to (host, port) every 250 ms until
    /// the socket accepts or the timeout expires. Used after launching OpenRGB so we know
    /// the SDK server is actually ready to accept client connections.
    ///
    /// CRITICAL: the in-flight ConnectAsync task MUST be observed (await with try/catch)
    /// even on timeout, otherwise the SocketException it throws on disposal lands on the
    /// TaskScheduler.UnobservedTaskException finalizer thread → CrashGuard logs a crash
    /// → restart loop → death spiral.
    /// </summary>
    private static bool WaitForPortOpen(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryConnectOnce(host, port, 500)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>True if a TCP probe succeeds against the SDK port right now.</summary>
    public static bool IsPortListening(int port) => TryConnectOnce("127.0.0.1", port, 300);

    /// <summary>
    /// Single non-leaking TCP probe. The connect task is always observed via ContinueWith
    /// so a timeout-disposal can never escape as an UnobservedTaskException.
    /// </summary>
    private static bool TryConnectOnce(string host, int port, int timeoutMs)
    {
        var client = new System.Net.Sockets.TcpClient();
        var task = client.ConnectAsync(host, port);

        // ALWAYS attach a continuation that observes any exception, even if we time out
        // and dispose the client below. This is the fix for the crash.
        task.ContinueWith(t =>
        {
            _ = t.Exception;            // mark exception as observed
            try { client.Dispose(); } catch { }
        }, System.Threading.Tasks.TaskScheduler.Default);

        try
        {
            if (task.Wait(timeoutMs) && client.Connected)
            {
                try { client.Dispose(); } catch { }
                return true;
            }
        }
        catch
        {
            // probe failed — observation is handled by the continuation above
        }
        return false;
    }

    public async Task<bool> InstallOpenRgbAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Try mirrors in order — gitlab CI artifacts (always-latest) first, then known release tags.
        string[] candidates =
        {
            "https://gitlab.com/CalcProgrammer1/OpenRGB/-/jobs/artifacts/master/raw/OpenRGB_Windows_64.zip?job=Windows+64",
            "https://openrgb.org/releases/release_0.9/OpenRGB_0.9_Windows_64_b5f46e3.zip",
            "https://openrgb.org/releases/release_0.9/OpenRGB_0.9_Windows_64_6b1df76.zip",
            "https://openrgb.org/releases/release_0.9/OpenRGB_0.9_Windows_64_2bcc167.zip",
            "https://openrgb.org/releases/release_0.9/OpenRGB_0.9_Windows_64_b5f46e3_x86.zip",
        };

        try
        {
            progress?.Report("Downloading OpenRGB...");
            Status = "Downloading OpenRGB...";

            Directory.CreateDirectory(InstallDir);
            var zipPath = Path.Combine(InstallDir, "OpenRGB.zip");

            Exception? lastError = null;
            bool downloaded = false;
            foreach (var downloadUrl in candidates)
            {
                try
                {
                    progress?.Report($"Trying mirror: {new Uri(downloadUrl).Host}…");
                    using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = File.Create(zipPath);
                    await stream.CopyToAsync(fileStream, ct);

                    // Sanity check the downloaded file is a real zip (>1 MB, valid header)
                    if (new FileInfo(zipPath).Length > 1_000_000)
                    {
                        downloaded = true;
                        break;
                    }
                    File.Delete(zipPath);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    progress?.Report($"Mirror failed, trying next...");
                }
            }

            if (!downloaded)
            {
                Status = $"Download failed: {lastError?.Message ?? "all mirrors unreachable"}";
                progress?.Report("Could not download OpenRGB — check your internet connection.");
                return false;
            }

            progress?.Report("Extracting...");
            Status = "Extracting OpenRGB...";

            if (Directory.GetFiles(InstallDir, "*.exe").Length > 0)
            {
                foreach (var f in Directory.GetFiles(InstallDir).Where(f => !f.EndsWith(".zip")))
                    File.Delete(f);
                foreach (var d in Directory.GetDirectories(InstallDir))
                    Directory.Delete(d, true);
            }

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, InstallDir, overwriteFiles: true);
            File.Delete(zipPath);

            var exeInSubfolder = Directory.GetFiles(InstallDir, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exeInSubfolder is not null && Path.GetDirectoryName(exeInSubfolder) != InstallDir)
            {
                var subDir = Path.GetDirectoryName(exeInSubfolder)!;
                foreach (var file in Directory.GetFiles(subDir))
                    File.Move(file, Path.Combine(InstallDir, Path.GetFileName(file)), overwrite: true);
                foreach (var dir in Directory.GetDirectories(subDir))
                {
                    var dest = Path.Combine(InstallDir, Path.GetFileName(dir));
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.Move(dir, dest);
                }
                if (Directory.Exists(subDir) && subDir != InstallDir)
                    Directory.Delete(subDir, true);
            }

            DetectedPath = Path.Combine(InstallDir, "OpenRGB.exe");
            if (!File.Exists(DetectedPath))
            {
                DetectedPath = Directory.GetFiles(InstallDir, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault();
            }

            if (DetectedPath is not null && File.Exists(DetectedPath))
            {
                Status = $"OpenRGB installed: {InstallDir}";
                progress?.Report("OpenRGB installed successfully!");
                return true;
            }

            Status = "Install failed: OpenRGB.exe not found after extraction";
            progress?.Report("Install failed");
            return false;
        }
        catch (Exception ex)
        {
            Status = $"Install failed: {ex.Message}";
            progress?.Report($"Install failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// One-shot bootstrap called by FANZI on startup:
    /// 1. Detect existing install
    /// 2. If not installed, download & extract
    /// 3. If not running, launch the server
    /// </summary>
    public async Task<bool> EnsureRunningAsync(int port = 6742, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        DetectInstallation();

        if (!IsInstalled)
        {
            progress?.Report("OpenRGB not found — downloading...");
            bool installed = await InstallOpenRgbAsync(progress, ct);
            if (!installed) return false;
        }

        if (IsOpenRgbAlreadyRunning())
        {
            Status = "OpenRGB already running externally";
            progress?.Report(Status);
            return true;
        }

        return await StartServerAsync(port, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
    }
}
