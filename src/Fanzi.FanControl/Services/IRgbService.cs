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
    /// Summary of the last mode-switch pass (e.g. "3 switched to Direct, 1 already Direct").
    /// Empty string if no mode switching has occurred yet.
    /// </summary>
    string ModeSwitchStatus { get; }

    /// <summary>
    /// Error from the last device query (e.g. OpenRGB controller data could not be parsed —
    /// a protocol mismatch). Null when the last query succeeded.
    /// </summary>
    string? LastDeviceQueryError => null;

    /// <summary>
    /// True when the connected server silently refused mode changes (OpenRGB 1.0 with
    /// Server → legacy_workaround off). Restarting FANZi's own OpenRGB server fixes it.
    /// </summary>
    bool ModeChangesRejected => false;

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

    /// <summary>
    /// Paints each zone of a device its own colour in a single hardware update.
    /// <paramref name="zoneColors"/> is indexed by zone (entry <c>i</c> fills every LED
    /// of zone <c>i</c>); shorter arrays leave trailing zones black. This is what drives
    /// per-zone / per-endpoint control (e.g. individual RAM stick colours).
    /// </summary>
    Task SetDeviceZoneColorsAsync(
        int        deviceIndex,
        RgbColor[] zoneColors,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cheap device-count query (no controller data). OpenRGB keeps adding devices for several seconds
    /// after its SDK port opens, so FANZi polls this to pick up late devices. Null when unsupported.
    /// </summary>
    Task<int?> GetDeviceCountAsync(CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);

    /// <summary>
    /// 4.3: re-arms SMBus-attached per-LED devices (RAM) — mode re-applied, frame cache cleared, colour re-sent.
    /// Returns how many devices were re-armed (0 when unsupported / not connected).
    /// </summary>
    Task<int> ResyncSmbusDevicesAsync(RgbColor color, CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <summary>4.3: forced SMBus host port for mirrored RAM entries (null = automatic).</summary>
    int? RamSmbusPort { get => null; set { } }

    /// <summary>4.3: SMBus ports among which RAM mirrors were found (empty = no mirroring).</summary>
    IReadOnlyList<int> MirroredSmbusPorts => Array.Empty<int>();

    /// <summary>4.3: one-line RAM bus summary for the UI ("" when there is nothing to say).</summary>
    string RamBusStatus => "";

    /// <summary>Closes the OpenRGB connection.</summary>
    void Disconnect();
}
