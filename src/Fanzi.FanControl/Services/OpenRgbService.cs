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

    // 4.3: last frame written per per-LED device, so unchanged frames are not re-sent (SMBus RAM, slow HID).
    private readonly Dictionary<int, (OpenRGB.NET.Color[] Frame, long Ticks)> _lastLedFrame = new();

    /// <summary>
    /// Minimum gap between UpdateLeds writes to one SMBus-attached device (RAM, onboard Aura). ENE DRAM is driven
    /// over SMBus at ~100 kHz: 8 LEDs × 3 bytes plus register addressing is ~5 ms per stick per write, and the
    /// controller has no input buffer — 10 fps per stick on several sticks is enough to make it drop writes.
    /// </summary>
    internal static readonly TimeSpan SmbusMinInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// User override for which SMBus host port drives mirrored RAM entries (null = automatic, see
    /// <see cref="SmbusMirrorResolver"/>). Takes effect on the next device refresh.
    /// </summary>
    public int? RamSmbusPort { get; set; }

    /// <summary>SMBus ports that currently show RAM mirrors (empty when no mirroring was detected).</summary>
    public IReadOnlyList<int> MirroredSmbusPorts { get; private set; } = Array.Empty<int>();

    /// <summary>Human-readable summary of the RAM mirror decision ("RAM: 2 sticks driven via PIIX4 port 1 …").</summary>
    public string RamBusStatus { get; private set; } = "";

    /// <summary>
    /// 4.4: when true every DRAM entry is planned as "not driven" — no Direct-mode switch, no frames, no re-sync.
    /// Set by <see cref="MaxsunSyncRgbService"/> while MaxsunSyncService owns the RAM (ENE HAL), so the sticks never
    /// see OpenRGB's PIIX4 writes interleaved with the service's. Takes effect on the next device refresh.
    /// </summary>
    public bool SuppressDramWrites { get; set; }

    /// <summary>4.4: DRAM entries (both SMBus ports) skipped by the last refresh because of <see cref="SuppressDramWrites"/>.</summary>
    public int SuppressedDramCount { get; private set; }
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
        _lastLedFrame.Clear();
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
                    var devices = QueryControllersWithTimeout(_client);
                    _deviceCache = devices;
                    _lastModeColor.Clear();
                    _modeColorActivated.Clear();
                    _lastLedFrame.Clear();
                    LastDeviceQueryError = null;

                    _controls = devices.Select(d => RgbControlPlanner.Plan(Describe(d), d.Leds.Length)).ToArray();
                    var mirrors = ApplyMirrorResolution(devices);
                    ApplyDramSuppression(devices);

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
                                IsControllable = _controls[i].IsControllable,
                                Vendor = d.Vendor ?? string.Empty,
                                Location = d.Location ?? string.Empty,
                                IsMirror = _controls[i].Kind == RgbControlKind.Mirror,
                                MirrorOf = _controls[i].MirrorIndex,
                                Layout = BuildLayout(d),
                                LedNames = d.Leds.Select(l => l.Name ?? "").ToArray(),
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

    public Task<int?> GetDeviceCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<int?>(() =>
        {
            bool taken = false;
            try
            {
                Monitor.TryEnter(_lock, WriteLockTimeout, ref taken);
                if (!taken || _client is null || !IsConnected) return null;
                return _client.GetControllerCount();
            }
            catch (Exception ex)
            {
                AppLog.Warn("OpenRGB: controller-count query failed", ex);
                return null;
            }
            finally
            {
                if (taken) Monitor.Exit(_lock);
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
        int perLed = 0, modeColor = 0, unsupported = 0, failed = 0, mirror = 0;
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
                case RgbControlKind.Mirror:
                    mirror++;
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
        if (mirror > 0)      parts.Add($"{mirror} SMBus mirror(s) skipped");
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
            // v4: never park a pool thread behind a slow OpenRGB call (device query, mode switch). The next
            // frame comes 100 ms later anyway, so a busy service simply skips this one.
            bool taken = false;
            try
            {
                Monitor.TryEnter(_lock, WriteLockTimeout, ref taken);
                if (!taken) return;
                if (_client is null || !IsConnected) return;
                try { write(); }
                catch (Exception ex) { HandleWriteFailure(ex); }
            }
            finally
            {
                if (taken) Monitor.Exit(_lock);
            }
        }, cancellationToken);
    }

    /// <summary>Longest a colour write waits for the service lock before the frame is dropped.</summary>
    internal static readonly TimeSpan WriteLockTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>A device query that takes longer than this is abandoned (socket closed) instead of hanging FANZi.</summary>
    internal static readonly TimeSpan DeviceQueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// GetAllControllerData on a dedicated thread with a deadline. OpenRGB answers late (or never) while it is
    /// still detecting hardware; the SDK client has no read timeout, so v3 could hold the service lock forever.
    /// On timeout the client is disposed, which unblocks the read. Caller holds _lock.
    /// </summary>
    private OpenRGB.NET.Device[] QueryControllersWithTimeout(OpenRgbClient client)
    {
        var query = Task.Factory.StartNew(client.GetAllControllerData, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (query.Wait(DeviceQueryTimeout)) return query.Result;

        _ = query.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        try { client.Dispose(); } catch { }
        _client = null;
        IsConnected = false;
        throw new TimeoutException($"OpenRGB did not return its device list within {DeviceQueryTimeout.TotalSeconds:F0} s (still detecting hardware?)");
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
            {
                // UpdateLeds needs exactly one colour per LED: a stale or short frame from an
                // effect would otherwise be rejected and wrongly drop the whole connection.
                var leds = FitToLedCount(frame, device.Leds.Length);
                long now = Stopwatch.GetTimestamp();
                if (_lastLedFrame.TryGetValue(deviceIndex, out var last))
                {
                    // 4.3: identical frame → nothing to send (a static colour costs zero bus traffic).
                    if (SameFrame(last.Frame, leds)) return;
                    // 4.3: SMBus devices (RAM, onboard Aura) are rate-limited; USB/HID devices stream at full rate.
                    if (IsSmbusDevice(device) && Stopwatch.GetElapsedTime(last.Ticks, now) < SmbusMinInterval) return;
                }
                _client!.UpdateLeds(deviceIndex, leds);
                _lastLedFrame[deviceIndex] = (leds, now);
                break;
            }

            case RgbControlKind.Mirror:
                // Same physical controller as another entry — writing here corrupts it.
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

    // ── 4.3 RAM: SMBus mirrors, throttling, re-sync ───────────────────────────

    /// <summary>
    /// Replaces the control plan of SMBus mirror entries with <see cref="RgbControlKind.Mirror"/> and records a
    /// status line. Caller holds _lock; <see cref="_controls"/> must already be planned.
    /// </summary>
    private IReadOnlyList<SmbusMirrorDecision> ApplyMirrorResolution(OpenRGB.NET.Device[] devices)
    {
        var sketches = devices.Select((d, i) => new SmbusDeviceSketch(i, d.Name ?? "", d.Type.ToString(), d.Location ?? "", d.Leds.Length)).ToArray();
        var decisions = SmbusMirrorResolver.Resolve(sketches, RamSmbusPort);

        int mirrors = 0;
        var drivenPorts = new SortedSet<int>();
        for (int i = 0; i < decisions.Count; i++)
        {
            var d = decisions[i];
            if (!d.Drive)
            {
                mirrors++;
                _controls[i] = RgbDeviceControl.MirrorOf(d.MirrorOf!.Value, d.Note!);
            }
            else if (d.Note is not null && SmbusMirrorResolver.Parse(devices[i].Location) is { } addr)
            {
                // Driven entry of a mirrored pair: its plan stays as is; only the port is recorded for the status line.
                drivenPorts.Add(addr.Port);
            }
        }

        MirroredSmbusPorts = mirrors > 0
            ? SmbusMirrorResolver.Ports(sketches.Where(s => SmbusMirrorResolver.Parse(s.Location) is not null && s.Type.Equals("Dram", StringComparison.OrdinalIgnoreCase)))
            : Array.Empty<int>();

        int ramDriven = devices.Where((d, i) => _controls[i].IsControllable && d.Type.ToString().Equals("Dram", StringComparison.OrdinalIgnoreCase)).Count();
        RamBusStatus = mirrors == 0
            ? (ramDriven > 0 ? $"RAM: {ramDriven} stick(s) driven" : "")
            : $"RAM: {ramDriven} stick(s) driven via SMBus port {string.Join("/", drivenPorts)}; {mirrors} mirror entr{(mirrors == 1 ? "y" : "ies")} on the other port skipped" +
              (RamSmbusPort is int fp ? $" (port {fp} forced)" : " (auto)");
        if (mirrors > 0) AppLog.Info("OpenRGB: " + RamBusStatus);
        return decisions;
    }

    /// <summary>
    /// 4.3: LED geometry for Typing FX. The first matrix zone (keyboards, some mice / pads) gives real 2-D positions;
    /// a device without one is a line of LEDs. Zone LED offsets are honoured so a multi-zone keyboard maps correctly.
    /// </summary>
    internal static LedLayout BuildLayout(OpenRGB.NET.Device d)
    {
        int total = d.Leds.Length;
        int offset = 0;
        foreach (var z in d.Zones)
        {
            var map = z.MatrixMap;
            if (map is not null && map.Width > 0 && map.Height > 0)
            {
                int w = (int)map.Width, h = (int)map.Height, zoneOffset = offset;
                object matrix = map.Matrix;
                return LedLayout.FromMatrix(total, w, h, (r, c) =>
                {
                    long v = matrix switch
                    {
                        uint[,] m2 => m2[r, c],
                        uint[][] mj => mj[r][c],
                        Array a when a.Rank == 2 => Convert.ToInt64(a.GetValue(r, c)),
                        _ => 0xFFFFFFFF,
                    };
                    return v == 0xFFFFFFFF ? -1 : (int)v + zoneOffset;
                });
            }
            offset += (int)z.LedCount;
        }
        return LedLayout.Linear(total);
    }

    /// <summary>4.4: hands every DRAM entry to MAXSUN Sync (see <see cref="SuppressDramWrites"/>). Caller holds _lock.</summary>
    private void ApplyDramSuppression(OpenRGB.NET.Device[] devices)
    {
        int skipped = 0;
        if (SuppressDramWrites)
        {
            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i].Type != DeviceType.Dram) continue;
                _controls[i] = RgbDeviceControl.None with { Note = "Driven by MAXSUN Sync (ENE HAL) — OpenRGB stays off this stick" };
                skipped++;
            }
            if (skipped > 0)
            {
                MirroredSmbusPorts = Array.Empty<int>();
                RamBusStatus = $"RAM: {skipped} OpenRGB DRAM entr{(skipped == 1 ? "y" : "ies")} left to MAXSUN Sync";
            }
        }
        SuppressedDramCount = skipped;
    }

    private static bool IsSmbusDevice(OpenRGB.NET.Device d) =>
        d.Type == DeviceType.Dram || (d.Location?.StartsWith("I2C:", StringComparison.OrdinalIgnoreCase) ?? false);

    internal static bool SameFrame(OpenRGB.NET.Color[] a, OpenRGB.NET.Color[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].R != b[i].R || a[i].G != b[i].G || a[i].B != b[i].B) return false;
        return true;
    }

    /// <summary>
    /// Re-arms every driven RAM / SMBus device: switches it back into its per-LED mode (an ENE controller that was
    /// kicked into an effect by another app or a confused register pointer accepts Direct again after this), clears
    /// the frame cache so the next frame is sent unconditionally, and re-sends <paramref name="color"/> immediately.
    /// Returns the number of devices re-armed.
    /// </summary>
    public Task<int> ResyncSmbusDevicesAsync(RgbColor color, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_client is null || !IsConnected || _deviceCache is null) return 0;
                int done = 0;
                for (int i = 0; i < _deviceCache.Length; i++)
                {
                    var dev = _deviceCache[i];
                    var control = _controls[i];
                    if (control.Kind != RgbControlKind.PerLed || !IsSmbusDevice(dev)) continue;
                    try
                    {
                        // Static-with-colour then back to the per-LED mode: the ENE firmware reloads its colour
                        // table on a mode change, which is what un-sticks a controller that stopped applying LEDs.
                        var staticMode = Describe(dev).FirstOrDefault(m => m.Name.Equals("Static", StringComparison.OrdinalIgnoreCase));
                        if (staticMode is not null && staticMode.Index != control.ModeIndex)
                        {
                            var cols = Enumerable.Repeat(((byte)color.R, (byte)color.G, (byte)color.B), Math.Max(1, (int)Math.Max(staticMode.ColorMin, 1u))).ToArray();
                            ApplyMode(i, staticMode.Index, staticMode.ColorMode == RgbModeColorMode.ModeSpecific ? cols : null);
                            Thread.Sleep(60);
                        }
                        ApplyMode(i, control.ModeIndex, colors: null);
                        Thread.Sleep(40);
                        _lastLedFrame.Remove(i);
                        _client.UpdateLeds(i, FitToLedCount(Enumerable.Repeat(color, dev.Leds.Length).ToArray(), dev.Leds.Length));
                        _lastLedFrame[i] = (FitToLedCount(Enumerable.Repeat(color, dev.Leds.Length).ToArray(), dev.Leds.Length), Stopwatch.GetTimestamp());
                        done++;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"OpenRGB: re-sync of '{dev.Name}' failed", ex);
                    }
                }
                AppLog.Info($"OpenRGB: re-synced {done} SMBus device(s)");
                return done;
            }
        }, cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OpenRGB.NET.Color ToOpenRgb(RgbColor c) =>
        new(c.R, c.G, c.B);

    /// <summary>Device layout from cache, refreshing (and caching) it once when empty. Caller holds _lock.</summary>
    private OpenRGB.NET.Device[] GetDevicesCached()
    {
        if (_deviceCache is null)
        {
            _deviceCache = QueryControllersWithTimeout(_client!);
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
