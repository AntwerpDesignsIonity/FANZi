using Fanzi.FanControl.Models;
using OpenRGB.NET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// OpenRGB-backed implementation of <see cref="IRgbService"/>.
/// Requires the OpenRGB application to be running with its SDK server enabled
/// (Settings → SDK Server, default port 6742).
/// </summary>
public sealed class OpenRgbService : IRgbService
{
    private OpenRgbClient?      _client;
    private OpenRGB.NET.Device[]? _deviceCache;   // zone/LED layout, refreshed on each device scan
    private bool                _disposed;
    private readonly object     _lock = new();

    public bool   IsConnected   { get; private set; }
    public string ServerVersion { get; private set; } = "Not connected";

    // ── Connection ────────────────────────────────────────────────────────────

    public Task<bool> TryConnectAsync(
        string host = "localhost",
        int    port = 6742,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                try
                {
                    _client?.Dispose();
                    _client = null;
                    _deviceCache = null;
                    IsConnected   = false;
                    ServerVersion = "Not connected";

                    var client = new OpenRgbClient(
                        ip:                     host,
                        port:                   port,
                        name:                   "FANZI",
                        autoConnect:            false,
                        protocolVersionNumber:  4);

                    client.Connect();
                    _client       = client;
                    IsConnected   = true;
                    ServerVersion = "OpenRGB (connected)";
                    return true;
                }
                catch (Exception ex)
                {
                    ServerVersion = $"Offline — {ex.Message.Split('\n')[0]}";
                    IsConnected   = false;
                    return false;
                }
            }
        }, cancellationToken);
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _client?.Dispose();
            _client       = null;
            _deviceCache  = null;
            IsConnected   = false;
            ServerVersion = "Disconnected";
        }
    }

    // ── Device discovery ──────────────────────────────────────────────────────

    public Task<IReadOnlyList<RgbDeviceInfo>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<RgbDeviceInfo>>(() =>
        {
            lock (_lock)
            {
                if (_client is null || !IsConnected)
                    return Array.Empty<RgbDeviceInfo>();

                try
                {
                    var devices = _client.GetAllControllerData();
                    _deviceCache = devices;   // cache layout so per-zone sends don't round-trip each frame

                    return devices
                        .Select((d, i) =>
                        {
                            var zones = new List<RgbZoneInfo>(d.Zones.Length);
                            for (int z = 0; z < d.Zones.Length; z++)
                            {
                                var zoneName = string.IsNullOrWhiteSpace(d.Zones[z].Name)
                                    ? $"Zone {z + 1}"
                                    : d.Zones[z].Name;
                                zones.Add(new RgbZoneInfo(i, z, zoneName, (int)d.Zones[z].LedCount));
                            }

                            return new RgbDeviceInfo(
                                DeviceIndex: i,
                                Name: d.Name,
                                Type: d.Type.ToString(),
                                LedCount: d.Leds.Length,
                                IsConnected: true)
                            {
                                Zones = zones,
                            };
                        })
                        .ToArray();
                }
                catch
                {
                    IsConnected = false;
                    return Array.Empty<RgbDeviceInfo>();
                }
            }
        }, cancellationToken);
    }

    // ── Colour setting ────────────────────────────────────────────────────────

    public Task SetDeviceColorAsync(
        int      deviceIndex,
        RgbColor color,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_client is null || !IsConnected) return;
                try
                {
                    var devices = _client.GetAllControllerData();
                    if (deviceIndex < 0 || deviceIndex >= devices.Length) return;

                    int count  = devices[deviceIndex].Leds.Length;
                    var colors = Enumerable.Repeat(ToOpenRgb(color), count).ToArray();
                    _client.UpdateLeds(deviceIndex, colors);
                }
                catch { IsConnected = false; }
            }
        }, cancellationToken);
    }

    public Task SetDeviceColorsAsync(
        int        deviceIndex,
        RgbColor[] colors,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_client is null || !IsConnected) return;
                try
                {
                    var openRgbColors = colors.Select(ToOpenRgb).ToArray();
                    _client.UpdateLeds(deviceIndex, openRgbColors);
                }
                catch { IsConnected = false; }
            }
        }, cancellationToken);
    }

    public Task SetAllDevicesColorAsync(
        RgbColor  color,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_client is null || !IsConnected) return;
                try
                {
                    var devices = _client.GetAllControllerData();
                    for (int i = 0; i < devices.Length; i++)
                    {
                        var colors = Enumerable
                            .Repeat(ToOpenRgb(color), devices[i].Leds.Length)
                            .ToArray();
                        _client.UpdateLeds(i, colors);
                    }
                }
                catch { IsConnected = false; }
            }
        }, cancellationToken);
    }

    public Task SetDeviceZoneColorsAsync(
        int        deviceIndex,
        RgbColor[] zoneColors,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_client is null || !IsConnected) return;
                try
                {
                    // Use the cached layout where possible so we don't hit the SDK every frame.
                    var devices = _deviceCache ?? _client.GetAllControllerData();
                    if (deviceIndex < 0 || deviceIndex >= devices.Length) return;

                    var dev = devices[deviceIndex];
                    int ledTotal = dev.Leds.Length;

                    // Build the full per-LED buffer for the device, expanding each zone's
                    // colour across its LED span. Pre-fill black so unzoned/trailing LEDs
                    // are always initialised (safe whether Color is a struct or class).
                    var black = ToOpenRgb(RgbColor.Black);
                    var leds  = new OpenRGB.NET.Color[ledTotal];
                    for (int k = 0; k < ledTotal; k++) leds[k] = black;

                    int offset = 0;
                    for (int z = 0; z < dev.Zones.Length && offset < ledTotal; z++)
                    {
                        int count = (int)dev.Zones[z].LedCount;
                        var col   = z < zoneColors.Length ? ToOpenRgb(zoneColors[z]) : black;
                        for (int k = 0; k < count && offset < ledTotal; k++)
                            leds[offset++] = col;
                    }

                    _client.UpdateLeds(deviceIndex, leds);
                }
                catch { IsConnected = false; }
            }
        }, cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OpenRGB.NET.Color ToOpenRgb(RgbColor c) =>
        new(c.R, c.G, c.B);

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
        _client = null;
    }
}
