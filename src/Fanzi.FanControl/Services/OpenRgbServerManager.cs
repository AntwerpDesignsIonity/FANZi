using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

public sealed class OpenRgbServerManager : IDisposable
{
    private Process? _serverProcess;
    private bool _disposed;

    public bool IsRunning => _serverProcess is { HasExited: false };
    public string Status { get; private set; } = "Server not started";
    public int Port { get; private set; } = 6742;

    private static readonly string[] SearchPaths =
    [
        @"C:\Program Files\OpenRGB\OpenRGB.exe",
        @"C:\Program Files (x86)\OpenRGB\OpenRGB.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
    }
}
