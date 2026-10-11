using Fanzi.FanControl.Services.Rgb;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services.OpenRgb;
using OpenRGB.NET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// OpenRGB-backed implementation of <see cref="IRgbService"/>.
/// Connects to the OpenRGB SDK server (default port 6742) and, per device, picks the colour path
/// the hardware actually honours (<see cref="RgbControlPlanner"/>):
/// <list type="bullet">
/// <item>per-LED devices (Direct/Custom modes) are switched to that mode and driven with UpdateLeds;</item>
/// <item>single-colour devices with no per-LED mode (e.g. ZOTAC RTX 30-series GPUs) are driven by
/// writing the colour into their "Static" mode with UpdateMode — UpdateLeds is ignored by those.</item>
/// </list>
/// The device layout is cached to avoid per-frame SDK round-trips.
/// </summary>
public sealed class OpenRgbService : IRgbService
{
    /// <summary>Minimum gap between UpdateMode writes to one mode-colour device (I2C GPUs are slow).</summary>
    internal static readonly TimeSpan ModeColorMinInterval = TimeSpan.FromMilliseconds(120);

    private OpenRgbClient?      _client;
    private OpenRgbModeWriter?  _modeWriter;
    private OpenRGB.NET.Device[]? _deviceCache;
    private RgbDeviceControl[]  _controls = Array.Empty<RgbDeviceControl>();
    private readonly Dictionary<int, (OpenRGB.NET.Color Color, long Ticks)> _lastModeColor = new();
    private readonly HashSet<int> _modeColorActivated = new();
    private volatile bool       _disposed;
    private bool                _writeFailureLogged;
    private readonly object     _lock = new();

    /// <summary>How long Dispose waits for an in-flight SDK call before closing the socket anyway.</summary>
    private static readonly TimeSpan DisposeLockTimeout = TimeSpan.FromSeconds(2);

    public bool   IsConnected   { get; private set; }
    public string ServerVersion { get; private set; } = "Not connected";

    /// <summary>
    /// Summary of the last mode-switch pass — surfaced in the UI so users can see
    /// which devices were switched and which (if any) failed.
    /// </summary>
    public string ModeSwitchStatus { get; private set; } = "";

    /// <summary>
    /// True when the server accepted the connection but silently refused mode changes. OpenRGB 1.0
    /// rejects mode packets from protocol-4 SDK clients (FANZi/OpenRGB.NET) unless its
    /// Server → legacy_workaround setting is on; FANZi's own server launch turns it on.
    /// </summary>
    public bool ModeChangesRejected { get; private set; }

    /// <summary>Error from the last GetAllControllerData call; null when it succeeded.</summary>
    public string? LastDeviceQueryError { get; private set; }

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
                // A connect queued before shutdown must not create a client nobody will dispose.
                if (_disposed) return false;

                try
                {
                    _client?.Dispose();
                    _client = null;
                    ResetDeviceState();
                    _writeFailureLogged = false;
                    IsConnected   = false;
                    ServerVersion = "Not connected";
                    ModeSwitchStatus = "";
                    ModeChangesRejected = false;
                    LastDeviceQueryError = null;

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
                    _modeWriter   = new OpenRgbModeWriter(connectHost, port);
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
            ResetDeviceState();
            IsConnected   = false;
            ServerVersion = "Disconnected";
            ModeSwitchStatus = "";
        }
    }

    /// <summary>Caller holds _lock.</summary>
    private void ResetDeviceState()
    {
        _modeWriter?.Dispose();
        _modeWriter = null;
        _deviceCache = null;
        _controls = Array.Empty<RgbDeviceControl>();
        _lastModeColor.Clear();
        _modeColorActivated.Clear();
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
                    _lastModeColor.Clear();
                    _modeColorActivated.Clear();
                    LastDeviceQueryError = null;

                    _controls = devices.Select(d => RgbControlPlanner.Plan(Describe(d), d.Leds.Length)).ToArray();

                    // Put every per-LED device into its per-LED mode now; without this devices stay in
                    // their built-in effect (Rainbow, Spectrum, …) and silently ignore UpdateLeds().
                    // Mode-colour devices are switched on their first colour write.
                    ActivatePerLedModes(devices);

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
                                ControlLabel = _controls[i].Label,
                                IsControllable = _controls[i].Kind != RgbControlKind.Unsupported,
                                Vendor = d.Vendor ?? string.Empty,
                                Location = d.Location ?? string.Empty,
                            };
                        })
                        .ToArray();
                }
                catch (Exception ex)
                {
                    // Usually a protocol mismatch: the controller data could not be parsed.
                    LastDeviceQueryError = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
                    AppLog.Warn("OpenRGB: GetAllControllerData failed", ex);
                    ServerVersion = $"Error: {ex.Message.Split('\n')[0]}";
                    IsConnected = false;
                    return Array.Empty<RgbDeviceInfo>();
                }
            }
        }, cancellationToken);
    }

    internal static IReadOnlyList<RgbModeDescriptor> Describe(OpenRGB.NET.Device d) =>
        d.Modes.Select((m, idx) => new RgbModeDescriptor(
            idx, m.Name ?? "", (RgbModeColorMode)(int)m.ColorMode, m.ColorMin, m.ColorMax)).ToArray();

    /// <summary>
    /// Switches each per-LED device to its per-LED mode and verifies the server applied it.
    /// If UpdateMode is refused (OpenRGB 1.0 without legacy_workaround), SetCustomMode — which has
    /// no packet-size check — is tried before the device is reported as failed. Caller holds _lock.
    /// </summary>
    private void ActivatePerLedModes(OpenRGB.NET.Device[] devices)
    {
        int perLed = 0, modeColor = 0, unsupported = 0, failed = 0;
        bool rejected = false;

        for (int i = 0; i < devices.Length; i++)
        {
            var control = _controls[i];
            switch (control.Kind)
            {
                case RgbControlKind.ModeColor:
                    modeColor++;
                    continue;
                case RgbControlKind.Unsupported:
                    unsupported++;
                    continue;
            }

            try
            {
                if (devices[i].ActiveModeIndex == control.ModeIndex)
                {
                    perLed++;
                    continue;
                }

                ApplyMode(i, control.ModeIndex, colors: null);
                if (ActiveModeIs(i, control.ModeIndex))
                {
                    perLed++;
                    continue;
                }

                rejected = true;
                _client!.SetCustomMode(i);
                if (ActiveModeIsPerLed(i))
                {
                    perLed++;
                    continue;
                }

                failed++;
                AppLog.Warn($"OpenRGB: '{devices[i].Name}' stayed in its effect mode — mode change refused by the server");
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warn($"OpenRGB: switching '{devices[i].Name}' to {control.ModeName} failed", ex);
            }
        }

        ModeChangesRejected = rejected;

        var parts = new List<string>();
        if (perLed > 0)      parts.Add($"{perLed} per-LED");
        if (modeColor > 0)   parts.Add($"{modeColor} single-colour");
        if (unsupported > 0) parts.Add($"{unsupported} effects-only");
        if (failed > 0)      parts.Add($"{failed} failed");

        ModeSwitchStatus = parts.Count > 0 ? string.Join(", ", parts) : "No devices";
        AppLog.Info($"OpenRGB: device control — {ModeSwitchStatus}" + (rejected ? " (server refused UpdateMode)" : ""));
    }

    /// <summary>How long a queued UpdateMode may take before it counts as refused.</summary>
    internal static readonly TimeSpan ModeApplyTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Re-reads one controller until the server reports <paramref name="modeIndex"/> as active.
    /// OpenRGB 1.0 queues UpdateMode on a per-device worker thread, so an immediate read-back can
    /// still show the old mode — polling avoids reporting a false "refused". Caller holds _lock.
    /// </summary>
    private bool ActiveModeIs(int deviceIndex, int modeIndex)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(ModeApplyTimeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            try
            {
                if (_client!.GetControllerData(deviceIndex).ActiveModeIndex == modeIndex) return true;
            }
            catch
            {
                return false;
            }

            if (Stopwatch.GetTimestamp() >= deadline) return false;
            Thread.Sleep(100);
        }
    }

    private bool ActiveModeIsPerLed(int deviceIndex)
    {
        try { return _client!.GetControllerData(deviceIndex).ActiveMode.ColorMode == ColorMode.PerLed; }
        catch { return false; }
    }

    // ── Colour setting ────────────────────────────────────────────────────────

    public Task SetDeviceColorAsync(
        int      deviceIndex,
        RgbColor color,
        CancellationToken cancellationToken = default)
    {
        return RunWrite(cancellationToken, () =>
        {
            var devices = GetDevicesCached();
            if (deviceIndex < 0 || deviceIndex >= devices.Length) return;
            WriteFrame(deviceIndex, Enumerable.Repeat(color, Math.Max(devices[deviceIndex].Leds.Length, 1)).ToArray());
        });
    }

    public Task SetDeviceColorsAsync(
        int        deviceIndex,
        RgbColor[] colors,
        CancellationToken cancellationToken = default)
    {
        return RunWrite(cancellationToken, () =>
        {
            var devices = GetDevicesCached();
            if (deviceIndex < 0 || deviceIndex >= devices.Length) return;
            WriteFrame(deviceIndex, colors);
        });
    }

    public Task SetAllDevicesColorAsync(
        RgbColor  color,
        CancellationToken cancellationToken = default)
    {
        return RunWrite(cancellationToken, () =>
        {
            // Use cached device layout — critical for performance.
            var devices = GetDevicesCached();
            for (int i = 0; i < devices.Length; i++)
                WriteFrame(i, Enumerable.Repeat(color, Math.Max(devices[i].Leds.Length, 1)).ToArray());
        });
    }

    public Task SetDeviceZoneColorsAsync(
        int        deviceIndex,
        RgbColor[] zoneColors,
        CancellationToken cancellationToken = default)
    {
        return RunWrite(cancellationToken, () =>
        {
            var devices = GetDevicesCached();
            if (deviceIndex < 0 || deviceIndex >= devices.Length) return;
            WriteFrame(deviceIndex, ExpandZoneColors(devices[deviceIndex], zoneColors));
        });
    }

    /// <summary>
    /// Runs one write on the thread pool under the lock. A failure marks the link offline once
    /// (see <see cref="HandleWriteFailure"/>) instead of escaping as an unobserved exception.
    /// </summary>
    private Task RunWrite(CancellationToken cancellationToken, Action write)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_client is null || !IsConnected) return;
                try { write(); }
                catch (Exception ex) { HandleWriteFailure(ex); }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Sends one frame to a device using its planned control path. Caller holds _lock.
    /// </summary>
    private void WriteFrame(int deviceIndex, IReadOnlyList<RgbColor> frame)
    {
        var device  = _deviceCache![deviceIndex];
        var control = deviceIndex < _controls.Length ? _controls[deviceIndex] : RgbDeviceControl.None;

        switch (control.Kind)
        {
            case RgbControlKind.PerLed when device.Leds.Length > 0:
                // UpdateLeds needs exactly one colour per LED: a stale or short frame from an
                // effect would otherwise be rejected and wrongly drop the whole connection.
                _client!.UpdateLeds(deviceIndex, FitToLedCount(frame, device.Leds.Length));
                break;

            case RgbControlKind.ModeColor:
                WriteModeColor(deviceIndex, control, frame);
                break;

            default:
                // Effects-only device: nothing FANZi can colour.
                break;
        }
    }

    /// <summary>
    /// Single-colour devices: the frame's average colour is written into the planned mode
    /// (e.g. ZOTAC "Static"). Unchanged colours are skipped and writes are spaced by
    /// <see cref="ModeColorMinInterval"/> so I2C-attached GPUs aren't flooded by 30 fps effects.
    /// Caller holds _lock.
    /// </summary>
    private void WriteModeColor(int deviceIndex, RgbDeviceControl control, IReadOnlyList<RgbColor> frame)
    {
        var avg   = RgbControlPlanner.Average(frame.Select(c => (c.R, c.G, c.B)).ToArray());
        var color = new OpenRGB.NET.Color(avg.R, avg.G, avg.B);
        long now  = Stopwatch.GetTimestamp();
        bool activated = _modeColorActivated.Contains(deviceIndex);

        if (activated && _lastModeColor.TryGetValue(deviceIndex, out var last))
        {
            if (last.Color.R == color.R && last.Color.G == color.G && last.Color.B == color.B) return;
            if (Stopwatch.GetElapsedTime(last.Ticks, now) < ModeColorMinInterval) return;
        }

        var colors = Enumerable.Repeat((color.R, color.G, color.B), Math.Max(control.ModeColorCount, 1)).ToArray();
        ApplyMode(deviceIndex, control.ModeIndex, colors);
        _lastModeColor[deviceIndex] = (color, now);

        if (!activated)
        {
            _modeColorActivated.Add(deviceIndex);
            if (!ActiveModeIs(deviceIndex, control.ModeIndex))
            {
                ModeChangesRejected = true;
                AppLog.Warn($"OpenRGB: '{_deviceCache![deviceIndex].Name}' ignored the {control.ModeName} colour — " +
                            "the server refused UpdateMode (OpenRGB Server → legacy_workaround is off)");
            }
        }
    }

    /// <summary>Per-zone colours expanded to one colour per LED (trailing LEDs black).</summary>
    private static RgbColor[] ExpandZoneColors(OpenRGB.NET.Device dev, RgbColor[] zoneColors)
    {
        int ledTotal = dev.Leds.Length;
        var leds = new RgbColor[Math.Max(ledTotal, 1)];
        for (int k = 0; k < leds.Length; k++) leds[k] = RgbColor.Black;

        int offset = 0;
        for (int z = 0; z < dev.Zones.Length && offset < ledTotal; z++)
        {
            int count = (int)dev.Zones[z].LedCount;
            var col   = z < zoneColors.Length ? zoneColors[z] : RgbColor.Black;
            for (int k = 0; k < count && offset < ledTotal; k++)
                leds[offset++] = col;
        }

        // A device with no zones/LEDs reported still gets the first zone colour.
        if (ledTotal == 0 && zoneColors.Length > 0) leds[0] = zoneColors[0];
        return leds;
    }

    // ── Mode writes ───────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a mode (optionally with new mode colours) through <see cref="OpenRgbModeWriter"/>,
    /// which sends direction 0 for direction-less modes — OpenRGB 1.0 discards OpenRGB.NET's own
    /// UpdateMode (direction 0xFFFFFFFF). Caller holds _lock.
    /// </summary>
    private void ApplyMode(int deviceIndex, int modeIndex, IReadOnlyList<(byte R, byte G, byte B)>? colors)
    {
        var mode = GetDevicesCached()[deviceIndex].Modes[modeIndex];
        var snapshot = new OpenRgbModeSnapshot(
            mode.Name ?? "", mode.Value, (uint)mode.Flags,
            mode.SpeedMin, mode.SpeedMax, mode.BrightnessMin, mode.BrightnessMax,
            mode.ColorMin, mode.ColorMax, mode.Speed, mode.Brightness,
            (uint)mode.Direction, (uint)mode.ColorMode,
            colors ?? mode.Colors.Select(c => (c.R, c.G, c.B)).ToArray());

        (_modeWriter ?? throw new InvalidOperationException("OpenRGB mode writer not connected"))
            .UpdateMode(deviceIndex, modeIndex, snapshot);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OpenRGB.NET.Color ToOpenRgb(RgbColor c) =>
        new(c.R, c.G, c.B);

    /// <summary>Device layout from cache, refreshing (and caching) it once when empty. Caller holds _lock.</summary>
    private OpenRGB.NET.Device[] GetDevicesCached()
    {
        if (_deviceCache is null)
        {
            _deviceCache = _client!.GetAllControllerData();
            _controls = _deviceCache.Select(d => RgbControlPlanner.Plan(Describe(d), d.Leds.Length)).ToArray();
        }
        return _deviceCache;
    }

    /// <summary>
    /// Returns exactly <paramref name="ledCount"/> colours: extra entries are dropped, missing ones
    /// repeat the last supplied colour (black when none were supplied).
    /// </summary>
    internal static OpenRGB.NET.Color[] FitToLedCount(IReadOnlyList<RgbColor> colors, int ledCount)
    {
        if (ledCount <= 0) return Array.Empty<OpenRGB.NET.Color>();

        var result = new OpenRGB.NET.Color[ledCount];
        var fill = colors.Count > 0 ? ToOpenRgb(colors[^1]) : ToOpenRgb(RgbColor.Black);
        for (int i = 0; i < ledCount; i++)
            result[i] = i < colors.Count ? ToOpenRgb(colors[i]) : fill;
        return result;
    }

    /// <summary>
    /// A failed LED write means the SDK socket is gone (OpenRGB closed, crashed or restarted).
    /// Mark the link offline with a visible reason so the RGB watchdog reconnects, and log the
    /// first failure per connection instead of silently swallowing it every frame. Caller holds _lock.
    /// </summary>
    private void HandleWriteFailure(Exception ex)
    {
        IsConnected  = false;
        ResetDeviceState();
        ServerVersion = $"Connection lost — {ex.Message.Split('\n')[0]}";

        if (!_writeFailureLogged)
        {
            _writeFailureLogged = true;
            AppLog.Warn("OpenRGB: LED write failed; marking connection offline for reconnect", ex);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Wait briefly for an in-flight SDK call so its socket isn't closed underneath it;
        // never block shutdown on a hung OpenRGB server.
        bool locked = false;
        try
        {
            Monitor.TryEnter(_lock, DisposeLockTimeout, ref locked);
            try { _client?.Dispose(); }
            catch (Exception ex) { AppLog.Warn("OpenRGB: client dispose failed", ex); }
            _client      = null;
            ResetDeviceState();
            IsConnected  = false;
        }
        finally
        {
            if (locked) Monitor.Exit(_lock);
        }
    }
}
