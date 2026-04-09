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
    private OpenRgbClient?    _client;
    private bool              _disposed;
    private readonly object   _lock = new();

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
                    return devices
                        .Select((d, i) => new RgbDeviceInfo(
                            DeviceIndex: i,
                            Name: d.Name,
                            Type: d.Type.ToString(),
                            LedCount: d.Leds.Length,
                            IsConnected: true))
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
