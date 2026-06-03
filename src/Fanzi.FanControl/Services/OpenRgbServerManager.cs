using System;
using System.Collections.Generic;
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

    /// <summary>Platform-specific OpenRGB executable name.</summary>
    private static string ExeName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "OpenRGB.exe" : "OpenRGB";

    /// <summary>
    /// All locations FANZI looks for an OpenRGB binary, in priority order. The first two
    /// entries point next to the running FANZI executable — this is where our installer
    /// drops the bundled OpenRGB (<installDir>\OpenRGB\), so a fresh install is found
    /// instantly without any download. The rest cover standard system installs and our
    /// fallback download directory.
    /// </summary>
    private static IEnumerable<string> SearchPaths
    {
        get
        {
            var exe = ExeName;
            var appDir = AppContext.BaseDirectory;

            // 1. Bundled next to FANZI (installer layout): <app>\OpenRGB\OpenRGB.exe
            yield return Path.Combine(appDir, "OpenRGB", exe);
            // 2. Bundled directly alongside FANZI
            yield return Path.Combine(appDir, exe);
            // 3. Our managed download location
            yield return Path.Combine(InstallDir, exe);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return @"C:\Program Files\OpenRGB\OpenRGB.exe";
                yield return @"C:\Program Files (x86)\OpenRGB\OpenRGB.exe";
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe");
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe");
            }
            else
            {
                // Common Linux/macOS install locations
                yield return "/usr/bin/openrgb";
                yield return "/usr/local/bin/openrgb";
                yield return "/opt/OpenRGB/openrgb";
            }
        }
    }

    public string? DetectedPath { get; private set; }

    public void DetectInstallation()
    {
        DetectedPath = SearchPaths.FirstOrDefault(File.Exists);

        if (DetectedPath is null)
        {
            var exe = ExeName;
            var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in envPath.Split(separator))
            {
                var candidate = Path.Combine(dir.Trim(), exe);
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

            // The only thing that actually matters is whether the SDK port is reachable.
            // If something is already listening (our own server from a previous launch, or
            // an external OpenRGB with its SDK server on), we're done — connect to it.
            if (IsPortListening(port))
            {
                Status = $"OpenRGB SDK server reachable on port {port}";
                return true;
            }

            if (IsRunning)
            {
                Status = $"Server already running (PID {_serverProcess!.Id})";
                return true;
            }

            // NOTE: we intentionally do NOT treat "an OpenRGB process exists" as success.
            // A plain OpenRGB GUI launched by the user has the SDK server OFF by default,
            // so the port never opens and the connection silently fails forever. We only
            // trust the port probe above. If a GUI instance is holding the hardware, our
            // own --server launch below will surface a clear, actionable status.

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

                // Our launched process exited without opening the port. The usual cause is
                // that an OpenRGB GUI was already running (single-instance), so our --server
                // invocation just forwarded to it and quit without enabling the SDK server.
                Status = IsOpenRgbAlreadyRunning()
                    ? "OpenRGB is already open without its SDK server — close it so FANZI can launch it in server mode (or enable Settings → SDK Server)"
                    : "Server process exited before port opened";
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
        Port = port;

        // Fast path: the SDK server is already reachable (ours from a prior launch, or an
        // external OpenRGB with the SDK server enabled). Nothing to do — just connect.
        if (IsPortListening(port))
        {
            Status = $"OpenRGB SDK server reachable on port {port}";
            progress?.Report(Status);
            return true;
        }

        DetectInstallation();

        if (!IsInstalled)
        {
            progress?.Report("OpenRGB not found — downloading...");
            bool installed = await InstallOpenRgbAsync(progress, ct);
            if (!installed) return false;
        }

        // Launch our own headless server. We deliberately rely on the port probe inside
        // StartServerAsync rather than the mere presence of an OpenRGB process, so a GUI
        // instance without the SDK server can't fool us into a broken "connected" state.
        return await StartServerAsync(port, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
    }
}
