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
/// Connects to the OpenRGB SDK server (default port 6742), auto-switches all
/// discovered devices to Direct/Custom mode so per-LED colours actually reach hardware,
/// and caches the device layout to avoid per-frame SDK round-trips.
/// </summary>
public sealed class OpenRgbService : IRgbService
{
    private OpenRgbClient?      _client;
    private OpenRGB.NET.Device[]? _deviceCache;
    private bool                _disposed;
    private readonly object     _lock = new();

    public bool   IsConnected   { get; private set; }
    public string ServerVersion { get; private set; } = "Not connected";

    /// <summary>
    /// Summary of the last mode-switch pass — surfaced in the UI so users can see
    /// which devices were switched to Direct mode and which (if any) failed.
    /// </summary>
    public string ModeSwitchStatus { get; private set; } = "";

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
                    ModeSwitchStatus = "";

                    // Force IPv4 — OpenRGB server only binds 0.0.0.0, and
                    // "localhost" resolves to ::1 (IPv6) on Windows which fails.
                    string connectHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        ? "127.0.0.1" : host;

                    var client = new OpenRgbClient(
                        ip:                     connectHost,
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
            ModeSwitchStatus = "";
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
                    _deviceCache = devices;

                    // Auto-switch every device to Direct/Custom mode so per-LED
                    // colour updates actually reach the hardware.  Without this,
                    // devices stay in their built-in effect (Rainbow, Spectrum,
                    // etc.) and silently ignore UpdateLeds() calls.
                    SwitchAllDevicesToDirectMode(devices);

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
                catch (Exception ex)
                {
                    ServerVersion = $"Error: {ex.Message.Split('\n')[0]}";
                    IsConnected = false;
                    return Array.Empty<RgbDeviceInfo>();
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Iterates every discovered device and switches it to "Direct" or "Custom"
    /// mode — the mode that lets FANZI drive each LED individually.
    /// Devices already in Direct mode are left alone.
    /// </summary>
    private void SwitchAllDevicesToDirectMode(OpenRGB.NET.Device[] devices)
    {
        int switched = 0, already = 0, failed = 0;

        for (int i = 0; i < devices.Length; i++)
        {
            try
            {
                var dev = devices[i];
                var activeMode = dev.ActiveMode;

                // Already in Direct/Custom/Static-per-LED? Nothing to do.
                string modeName = activeMode.Name ?? "";
                bool isDirect = modeName.Contains("Direct", StringComparison.OrdinalIgnoreCase)
                             || modeName.Contains("Custom", StringComparison.OrdinalIgnoreCase)
                             || modeName.Contains("Static", StringComparison.OrdinalIgnoreCase);

                if (isDirect)
                {
                    already++;
                    continue;
                }

                // Find the Direct or Custom mode index on this device.
                int directIndex = -1;
                for (int m = 0; m < dev.Modes.Length; m++)
                {
                    string name = dev.Modes[m].Name ?? "";
                    if (name.Equals("Direct", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                    {
                        directIndex = m;
                        break;
                    }
                }

                // Prefer "Direct" — if not found, try "Custom" via SetCustomMode.
                if (directIndex >= 0)
                {
                    _client!.UpdateMode(i, directIndex);
                    switched++;
                }
                else
                {
                    // SetCustomMode sends the RGBController::SetCustomMode() command
                    // which on most devices activates the per-LED direct control path.
                    _client!.SetCustomMode(i);
                    switched++;
                }
            }
            catch
            {
                failed++;
            }
        }

        var parts = new List<string>();
        if (switched > 0) parts.Add($"{switched} switched to Direct");
        if (already > 0)  parts.Add($"{already} already Direct");
        if (failed > 0)   parts.Add($"{failed} failed");

        ModeSwitchStatus = parts.Count > 0
            ? string.Join(", ", parts)
            : "No devices";
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
                    // Use cached device layout — avoids a full SDK round-trip every frame.
                    var devices = _deviceCache ?? _client.GetAllControllerData();
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
                    // Use cached device layout — critical for performance.
                    var devices = _deviceCache ?? _client.GetAllControllerData();
                    var openColor = ToOpenRgb(color);
                    for (int i = 0; i < devices.Length; i++)
                    {
                        var colors = Enumerable
                            .Repeat(openColor, devices[i].Leds.Length)
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
                    var devices = _deviceCache ?? _client.GetAllControllerData();
                    if (deviceIndex < 0 || deviceIndex >= devices.Length) return;

                    var dev = devices[deviceIndex];
                    int ledTotal = dev.Leds.Length;

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
