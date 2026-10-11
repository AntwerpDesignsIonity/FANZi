using Fanzi.FanControl.Services.Rgb;
using Fanzi.FanControl.Services.OpenRgb;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public sealed class OpenRgbServerManager : IDisposable
{
    /// <summary>How long to wait for the SDK port after launching OpenRGB (device scan can be slow).</summary>
    private static readonly TimeSpan PortWaitTimeout = TimeSpan.FromSeconds(30);

    private Process? _serverProcess;
    private bool _disposed;
    private string _status = "Server not started";

    // Test seams: replace file-system/registry detection and the network install.
    private readonly Func<string?>? _locateOverride;
    private readonly Func<IProgress<string>?, CancellationToken, Task<OpenRgbInstallResult>>? _installOverride;

    public OpenRgbServerManager()
    {
    }

    /// <summary>Test constructor: <paramref name="locate"/> replaces folder/registry/PATH detection
    /// and <paramref name="install"/> replaces the download.</summary>
    internal OpenRgbServerManager(
        Func<string?> locate,
        Func<IProgress<string>?, CancellationToken, Task<OpenRgbInstallResult>> install)
    {
        _locateOverride = locate;
        _installOverride = install;
    }

    public bool IsRunning => _serverProcess is { HasExited: false };

    /// <summary>Latest human-readable state. Every change is written to the app log.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            AppLog.Info($"OpenRGB: {value}");
        }
    }

    public int Port { get; private set; } = 6742;
    public bool IsInstalled => DetectedPath is not null;

    /// <summary>User-selected OpenRGB executable (persisted as AppSettings.OpenRgbPath).</summary>
    public string? CustomPath { get; private set; }

    /// <summary>Precise reason the last download/install failed (null after a successful install).</summary>
    public string? LastInstallError { get; private set; }

    private static string InstallDir => OpenRgbInstallLocator.ManagedInstallDirectory;

    /// <summary>Platform-specific OpenRGB executable name.</summary>
    private static string ExeName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "OpenRGB.exe" : "OpenRGB";

    /// <summary>
    /// File-system locations FANZI looks for an OpenRGB binary, in priority order. The first
    /// entries point next to the running FANZI executable — this is where our installer drops
    /// the bundled OpenRGB (&lt;installDir&gt;\OpenRGB\). Then our managed download folder and
    /// the standard system install folders. The registry and PATH are checked afterwards.
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
                foreach (var path in OpenRgbInstallLocator.WellKnownExePaths())
                    yield return path;
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

    /// <summary>
    /// True only for a file literally named OpenRGB.exe (case-insensitive; "OpenRGB" on Linux/macOS).
    /// "OpenRGB_Setup.msi", "OpenRGB_1.0.zip", "OpenRGB.exe.lnk" or "OpenRGB-old.exe" are rejected.
    /// </summary>
    internal static bool IsOpenRgbExecutableName(string path) =>
        string.Equals(Path.GetFileName(path), ExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Uses <paramref name="exePath"/> as the OpenRGB executable (user picked it via
    /// "Locate OpenRGB.exe…" or it was restored from settings). Returns false when the file
    /// does not exist. Passing null/empty clears the custom path.
    /// </summary>
    public bool SetCustomPath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            CustomPath = null;
            DetectInstallation();
            return true;
        }

        string full;
        try { full = Path.GetFullPath(exePath.Trim().Trim('"')); }
        catch (Exception ex)
        {
            Status = $"Invalid OpenRGB path: {ex.Message}";
            return false;
        }

        if (!File.Exists(full))
        {
            Status = $"OpenRGB not found at {full}";
            return false;
        }

        if (!IsOpenRgbExecutableName(full))
        {
            Status = $"{Path.GetFileName(full)} is not OpenRGB — select the program file named {ExeName} (not an installer, archive or shortcut)";
            return false;
        }

        CustomPath = full;
        DetectedPath = full;
        Status = $"Using OpenRGB: {full}";
        return true;
    }

    public void DetectInstallation()
    {
        string? previous = DetectedPath;
        string? found = null;

        if (CustomPath is not null && File.Exists(CustomPath))
            found = CustomPath;

        if (_locateOverride is not null)
        {
            found ??= _locateOverride();
        }
        else
        {
            found ??= SearchPaths.FirstOrDefault(File.Exists);

            if (found is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                found = OpenRgbInstallLocator.FindInRegistry();

            found ??= OpenRgbInstallLocator.FindOnPath(ExeName);
        }

        DetectedPath = found;

        // Only report (and log) when the result changes — the watchdog calls this repeatedly.
        if (previous != found || Status == "Server not started")
        {
            Status = found is not null
                ? $"OpenRGB found: {Path.GetDirectoryName(found)}"
                : "OpenRGB not detected — install from openrgb.org";
        }
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

            // Nothing serves the SDK port: any OpenRGB still running is a zombie / GUI without the SDK that would
            // hold the hardware (and, being single-instance, swallow our --server launch). Stop it first.
            int zombies = StopOrphanedServers(port);
            if (zombies > 0) Thread.Sleep(500);

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
                // FANZi's private config turns on Server → legacy_workaround; without it OpenRGB 1.0
                // drops every mode change from FANZi's protocol-4 client (GPU colours never apply).
                string arguments = BuildServerArguments(port, PrepareConfigDirectory(port));

                AppLog.Info($"OpenRGB: launching {DetectedPath} {arguments}");
                _serverProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        // --server enables the SDK; we DON'T pass --noautoconnect so the
                        // server actually scans for devices on startup. WorkingDirectory
                        // is set so OpenRGB finds its runtime files relative to the exe.
                        FileName = DetectedPath,
                        Arguments = arguments,
                        WorkingDirectory = Path.GetDirectoryName(DetectedPath) ?? "",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    },
                    EnableRaisingEvents = true,
                };

                _serverProcess.Exited += (sender, _) =>
                {
                    int? exitCode = null;
                    try { exitCode = (sender as Process)?.ExitCode; } catch { }
                    Status = exitCode is int code ? $"Server stopped (exit code {code})" : "Server stopped";
                    _serverProcess = null;
                };

                _serverProcess.Start();

                // Wait for the SDK port to actually become reachable. OpenRGB needs time to
                // scan devices (SMBus/PawnIO probing can be slow) before binding the socket.
                if (WaitForPortOpen("127.0.0.1", port, PortWaitTimeout))
                {
                    StopOrphanedServers(port);
                    Status = $"Server running on port {port} (PID {_serverProcess?.Id})";
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
                    : $"OpenRGB exited before opening SDK port {port} ({DetectedPath})";
                return false;
            }
            catch (Exception ex)
            {
                // A Process that never started throws from HasExited — don't keep it around.
                try { _serverProcess?.Dispose(); } catch { }
                _serverProcess = null;
                Status = $"Start failed: {ex.GetType().Name}: {ex.Message}";
                AppLog.Warn("OpenRGB: server start failed", ex);
                return false;
            }
        }, ct);
    }

    public void StopServer()
    {
        // Snapshot: the Exited handler clears _serverProcess on another thread as soon as Kill lands.
        var process = _serverProcess;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and Kill — nothing left to stop.
        }
        catch (Exception ex)
        {
            AppLog.Warn("OpenRGB: failed to stop server process", ex);
        }

        _serverProcess = null;
        Status = "Server stopped";
    }

    /// <summary>Command line for FANZi's OpenRGB SDK server (config path quoted; omitted when null).</summary>
    internal static string BuildServerArguments(int port, string? configDir) =>
        configDir is null
            ? $"--server --server-port {port}"
            : $"--server --server-port {port} --config \"{configDir.TrimEnd('\\', '/')}\"";

    /// <summary>
    /// Writes FANZi's private OpenRGB config (legacy_workaround on) and also flips the flag in the
    /// user's own OpenRGB config. Returns the private folder, or null if it could not be written
    /// (OpenRGB then uses its default config).
    /// </summary>
    private static string? PrepareConfigDirectory(int port)
    {
        OpenRgbConfigWriter.TryPatchSharedConfig();
        try
        {
            return OpenRgbConfigWriter.EnsurePrivateConfig(port);
        }
        catch (Exception ex)
        {
            AppLog.Warn("OpenRGB: could not write FANZi's private OpenRGB config", ex);
            return null;
        }
    }

    /// <summary>
    /// Restarts the SDK server so it picks up a FANZi-compatible config: used when the running
    /// server refused mode changes (legacy_workaround off). Every OpenRGB process is stopped —
    /// FANZi runs elevated, so this includes a user-started OpenRGB holding the SDK port.
    /// </summary>
    public async Task<bool> RestartForCompatibilityAsync(int port = 6742, CancellationToken ct = default)
    {
        AppLog.Info("OpenRGB: restarting the SDK server with FANZi's compatible config");
        StopServer();
        foreach (var p in SafeGetOpenRgbProcesses())
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
            catch (Exception ex) { AppLog.Warn($"OpenRGB: could not stop PID {p.Id}", ex); }
            finally { p.Dispose(); }
        }

        // Give Windows a moment to release the socket before relaunching.
        for (int i = 0; i < 20 && IsPortListening(port); i++)
            await Task.Delay(250, ct).ConfigureAwait(false);

        return await StartServerAsync(port, ct).ConfigureAwait(false);
    }

    private static Process[] SafeGetOpenRgbProcesses()
    {
        try { return Process.GetProcessesByName("OpenRGB"); }
        catch { return Array.Empty<Process>(); }
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
            bool local = host is "127.0.0.1" or "localhost";
            if (local ? IsPortListening(port) : TryConnectOnce(host, port, 500)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>4.1: TCP probe of any SDK endpoint (local or another PC); false when unreachable or unresolvable.</summary>
    public static bool IsReachable(string host, int port, int timeoutMs) => TryConnectOnce(host, port, timeoutMs);

    /// <summary>4.1: local TCP ports that running OpenRGB processes listen on (Windows listener table; empty elsewhere).</summary>
    public static IReadOnlyList<int> ListOpenRgbListeningPorts()
    {
        var ports = new List<int>();
        foreach (var p in SafeGetOpenRgbProcesses())
        {
            using (p)
            {
                try { ports.AddRange(OpenRgb.TcpPortOwner.ListeningPorts(p.Id)); }
                catch { }
            }
        }
        return ports.Distinct().OrderBy(x => x).ToList();
    }

    /// <summary>True if a TCP probe succeeds against the SDK port right now.</summary>
    public static bool IsPortListening(int port)
        => OpenRgb.TcpPortOwner.IsListening(port) ?? TryConnectOnce("127.0.0.1", port, 1000);

    /// <summary>
    /// Single TCP probe. v4: loopback probes use a plain blocking connect on the calling thread. The old
    /// probe waited on ConnectAsync, whose completion needs a free thread-pool thread — when the pool was
    /// busy (RGB frames queued behind a slow OpenRGB call) every probe "timed out" although the server was
    /// listening, so FANZi never reconnected after "Restart OpenRGB". A loopback connect is answered by
    /// the kernel at once (accepted, or refused when nothing listens), so blocking is safe and bounded.
    /// </summary>
    private static bool TryConnectOnce(string host, int port, int timeoutMs)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp)
            {
                NoDelay = true,
                LingerState = new System.Net.Sockets.LingerOption(true, 0), // RST on close: no TIME_WAIT pile-up
            };

            // 4.1: host names (another PC on the LAN) resolve via DNS; IPv4 first, the socket is IPv4.
            var address = host is "localhost" or "127.0.0.1"
                ? System.Net.IPAddress.Loopback
                : System.Net.IPAddress.TryParse(host, out var literal)
                    ? literal
                    : System.Net.Dns.GetHostAddresses(host).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

            if (System.Net.IPAddress.IsLoopback(address))
            {
                socket.Connect(new System.Net.IPEndPoint(address, port));
                return socket.Connected;
            }

            // Remote hosts: bounded wait without depending on a pool thread for the result.
            var result = socket.BeginConnect(new System.Net.IPEndPoint(address, port), null, null);
            bool done = result.AsyncWaitHandle.WaitOne(timeoutMs);
            if (!done) return false;
            socket.EndConnect(result);
            return socket.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stops OpenRGB processes that are not the SDK server FANZi talks to: zombies left by an older FANZi
    /// (v3 → v4 handover), a second server whose port bind failed, or a GUI instance without the SDK. Two
    /// OpenRGB instances fight over the SMBus / USB devices (flicker, missed detections, slow start-up).
    /// The process listening on <paramref name="port"/> and FANZi's own server are kept.
    /// </summary>
    public int StopOrphanedServers(int port)
    {
        int? owner = OpenRgb.TcpPortOwner.GetListeningPid(port);
        int? ours = null;
        try { ours = _serverProcess is { HasExited: false } sp ? sp.Id : null; } catch { }

        int stopped = 0;
        foreach (var p in SafeGetOpenRgbProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == owner || p.Id == ours) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                    stopped++;
                    AppLog.Info($"OpenRGB: stopped orphaned OpenRGB instance (PID {p.Id}) — the SDK server is PID {owner?.ToString() ?? "none"}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"OpenRGB: could not stop orphaned instance PID {p.Id}", ex);
                }
            }
        }
        return stopped;
    }

    /// <summary>
    /// Downloads the current OpenRGB Windows portable package (releases page + pinned
    /// fallbacks), validates it and installs it to %LOCALAPPDATA%\FANZI\OpenRGB.
    /// On failure <see cref="Status"/> and <see cref="LastInstallError"/> carry the precise
    /// per-source reason (e.g. "All 3 sources failed: openrgb.org/…: HTTP 404 …").
    /// </summary>
    public async Task<bool> InstallOpenRgbAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // A running OpenRGB from our folder would lock its files mid-copy.
        StopServer();

        Status = "Downloading OpenRGB...";
        AppLog.Info($"OpenRGB: installing to {InstallDir}");

        try
        {
            var result = _installOverride is not null
                ? await _installOverride(progress, ct).ConfigureAwait(false)
                : await OpenRgbPackageInstaller.InstallLatestAsync(
                    InstallDir, progress, msg => AppLog.Info(msg), ct).ConfigureAwait(false);

            if (result.Success && result.ExePath is not null && File.Exists(result.ExePath))
            {
                DetectedPath = result.ExePath;
                LastInstallError = null;
                Status = $"OpenRGB installed: {Path.GetDirectoryName(result.ExePath)}";
                progress?.Report("OpenRGB installed successfully!");
                return true;
            }

            LastInstallError = result.Error ?? "OpenRGB install failed for an unknown reason";
            Status = LastInstallError;
            AppLog.Warn($"OpenRGB: install failed: {LastInstallError}");
            progress?.Report(LastInstallError);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastInstallError = "OpenRGB install cancelled";
            Status = LastInstallError;
            return false;
        }
        catch (Exception ex)
        {
            LastInstallError = $"Install failed: {ex.GetType().Name}: {ex.Message}";
            Status = LastInstallError;
            AppLog.Error("OpenRGB: install crashed", ex);
            progress?.Report(LastInstallError);
            return false;
        }
    }

    /// <summary>
    /// One-shot bootstrap called by FANZI on startup and by the watchdog:
    /// 1. Detect existing install
    /// 2. If not installed and <paramref name="allowInstall"/> permits it, download &amp; extract
    /// 3. If not running, launch the server
    /// <paramref name="allowInstall"/> is only evaluated when a download is actually needed
    /// (null = always allowed).
    /// </summary>
    public async Task<bool> EnsureRunningAsync(
        int port = 6742,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        Func<bool>? allowInstall = null)
    {
        Port = port;

        // Once per app run: clear backups/staging folders an interrupted install left next to our
        // managed OpenRGB folder (restoring a backup first if the install itself is missing).
        await CleanupStaleInstallsOnceAsync();

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
            if (allowInstall is not null && !allowInstall())
            {
                Status = LastInstallError is not null
                    ? $"OpenRGB not installed — last download failed: {LastInstallError}"
                    : "OpenRGB not installed — automatic download skipped (recently attempted)";
                return false;
            }

            progress?.Report("OpenRGB not found — downloading...");
            bool installed = await InstallOpenRgbAsync(progress, ct);
            if (!installed) return false;
        }

        // Launch our own headless server. We deliberately rely on the port probe inside
        // StartServerAsync rather than the mere presence of an OpenRGB process, so a GUI
        // instance without the SDK server can't fool us into a broken "connected" state.
        return await StartServerAsync(port, ct);
    }

    private int _staleCleanupStarted;

    /// <summary>
    /// Runs <see cref="OpenRgbPackageInstaller.CleanupStaleInstallFolders"/> on the managed install
    /// folder the first time it is called. Skipped for test instances (fake install seam) so tests
    /// never touch the user's %LOCALAPPDATA%.
    /// </summary>
    private Task CleanupStaleInstallsOnceAsync()
    {
        if (_installOverride is not null || Interlocked.Exchange(ref _staleCleanupStarted, 1) != 0)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                int removed = OpenRgbPackageInstaller.CleanupStaleInstallFolders(InstallDir, msg => AppLog.Info(msg));
                if (removed > 0)
                    AppLog.Info($"OpenRGB: removed {removed} stale install folder(s) next to {InstallDir}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("OpenRGB: stale install folder cleanup failed", ex);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
    }
}
