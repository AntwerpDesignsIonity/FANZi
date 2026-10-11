using Fanzi.FanControl.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services.Posix;

/// <summary>
/// <see cref="IHardwareMonitorService"/> for Linux and macOS (4.1). LibreHardwareMonitor is Windows-only, so these
/// platforms read their own kernel interfaces: hwmon / procfs / nvidia-smi on Linux (<see cref="LinuxSensorReader"/>),
/// sysctl / host_statistics / AppleSMC on macOS (<see cref="MacSensorReader"/>). Reads never throw: a failure becomes
/// an empty snapshot with the reason in its status message.
/// </summary>
public sealed class PosixHardwareMonitorService : IHardwareMonitorService
{
    private readonly object _sync = new();
    private readonly LinuxSensorReader? _linux;
    private readonly MacSensorReader? _mac;
    private bool _disposed;

    public PosixHardwareMonitorService()
    {
        if (OperatingSystem.IsLinux())
        {
            _linux = new LinuxSensorReader();
            AppLog.Info("Hardware monitor: Linux hwmon / procfs reader.");
        }
        else if (OperatingSystem.IsMacOS())
        {
            _mac = new MacSensorReader();
            AppLog.Info($"Hardware monitor: macOS reader (SMC {(_mac.HasSmc ? "open" : "unavailable")}).");
        }
        else
        {
            InitializationError = $"Hardware sensors are not supported on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.";
        }
    }

    public string? InitializationError { get; }

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                try
                {
                    if (_linux is not null) return _linux.Read(now);
                    if (_mac is not null && OperatingSystem.IsMacOS()) return _mac.Read(now);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Sensor read failed", ex);
                    return Empty(now, $"Sensor read failed: {ex.Message}");
                }
                return Empty(now, InitializationError ?? "No sensors on this platform.");
            }
        }, cancellationToken);
    }

    public Task<SetFanControlResult> SetFanControlAsync(string channelId, double percent, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(_linux?.SetDuty(channelId, percent)
                ?? new SetFanControlResult(false, "Fan control is read-only on this platform."));
        }
    }

    public Task<SetFanControlResult> RestoreAutomaticControlAsync(string channelId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(_linux?.RestoreAutomatic(channelId)
                ?? new SetFanControlResult(false, "Fan control is read-only on this platform."));
        }
    }

    internal static HardwareSnapshot Empty(DateTimeOffset now, string status) => new(
        now, null, null, null, null, null, null, null, null, null, null, null, null, null,
        Array.Empty<CpuReadingSnapshot>(), Array.Empty<GpuReadingSnapshot>(), Array.Empty<FanChannelSnapshot>(),
        status, null, null, null, null);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (OperatingSystem.IsMacOS()) _mac?.Dispose();
    }
}
