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
                        FileName = DetectedPath,
                        Arguments = $"--server --server-port {port} --noautoconnect",
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
                Thread.Sleep(1500);

                if (_serverProcess is { HasExited: false })
                {
                    Status = $"Server running on port {port} (PID {_serverProcess.Id})";
                    return true;
                }
                else
                {
                    Status = "Server failed to start";
                    return false;
                }
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

    public async Task<bool> InstallOpenRgbAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        const string downloadUrl = "https://openrgb.org/releases/release_0.9/OpenRGB_0.9_Windows_64_6b1df76.zip";

        try
        {
            progress?.Report("Downloading OpenRGB...");
            Status = "Downloading OpenRGB...";

            Directory.CreateDirectory(InstallDir);
            var zipPath = Path.Combine(InstallDir, "OpenRGB.zip");

            using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(zipPath);
                await stream.CopyToAsync(fileStream, ct);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
    }
}
