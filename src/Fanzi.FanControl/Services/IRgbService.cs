using Fanzi.FanControl.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Abstraction over an RGB bus (currently backed by OpenRGB).
/// When OpenRGB server is not running, implementations return graceful no-ops.
/// </summary>
public interface IRgbService : IDisposable
{
    /// <summary>True if actively connected to the OpenRGB server.</summary>
    bool IsConnected { get; }

    /// <summary>Server version string returned after handshake.</summary>
    string ServerVersion { get; }

    /// <summary>
    /// Attempts to connect to the OpenRGB server.
    /// Returns true on success; false if server is not reachable.
    /// </summary>
    Task<bool> TryConnectAsync(
        string host = "localhost",
        int    port = 6742,
        CancellationToken cancellationToken = default);

    /// <summary>Returns all RGB devices detected by OpenRGB.</summary>
    Task<IReadOnlyList<RgbDeviceInfo>> GetDevicesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Sets every LED on a specific device to the same colour.</summary>
    Task SetDeviceColorAsync(
        int      deviceIndex,
        RgbColor color,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets individual LED colours on a device.
    /// If the array is shorter than the number of LEDs, the remainder stays unchanged.
    /// </summary>
    Task SetDeviceColorsAsync(
        int        deviceIndex,
        RgbColor[] colors,
        CancellationToken cancellationToken = default);

    /// <summary>Broadcasts the same colour to every LED on every device.</summary>
    Task SetAllDevicesColorAsync(
        RgbColor  color,
        CancellationToken cancellationToken = default);

    /// <summary>Closes the OpenRGB connection.</summary>
    void Disconnect();
}
