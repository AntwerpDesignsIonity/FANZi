using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fanzi.FanControl.Models;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.Services;

/// <summary>
/// 4.4: RGB service that drives RAM (and the MAXSUN board LEDs) the same way MAXSUN Sync 2 does — through
/// <c>MaxsunSyncService</c> and its ENE / MAXSUN HALs — while every other device keeps going through OpenRGB.
///
/// Why: on the MAXSUN MS-iCraft X870M the ENE DDR5 controllers already have a bus master (MaxsunSyncService runs as
/// LocalSystem and writes them through the ENE HAL). OpenRGB writing the same controllers over PIIX4 SMBus made two
/// masters interleave register writes — the "RAM stops taking colours" fault of 4.2 – 4.3.3. When the service is
/// present FANZi hands RAM to it and tells OpenRGB to leave every DRAM entry alone
/// (<see cref="OpenRgbService.SuppressDramWrites"/>). Without the service this class is a transparent pass-through.
/// </summary>
public sealed class MaxsunSyncRgbService : IRgbService
{
    /// <summary>First synthetic device index; far above anything OpenRGB hands out.</summary>
    internal const int IndexBase = 9000;

    /// <summary>Gap between frames to one HAL device: the service writes ENE DRAM over SMBus; 8 fps keeps it clean.</summary>
    internal static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(125);

    /// <summary>How often the service is re-probed when it was not found (installed / started later).</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(20);

    private readonly IRgbService _inner;
    private readonly OpenRgbService? _openRgb;
    private readonly MaxsunSyncClient _client;
    private readonly object _lock = new();

    private MaxsunLedDevice[] _devices = Array.Empty<MaxsunLedDevice>();
    private readonly Dictionary<int, (RgbColor[] Frame, long Ticks)> _lastFrame = new();
    private readonly HashSet<int> _staticArmed = new();
    private long _lastProbeTicks;
    private bool _online;
    private bool _failureLogged;
    private string _host = "localhost";
    private int _port = 6742;
    private long _lastInnerReconnectTicks;
    private volatile bool _disposed;

    public MaxsunSyncRgbService(IRgbService inner, MaxsunSyncClient? client = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _openRgb = inner as OpenRgbService;
        _client = client ?? new MaxsunSyncClient();
    }

    /// <summary>True while MaxsunSyncService answers and owns at least one LED device.</summary>
    public bool IsMaxsunOnline { get { lock (_lock) return _online && _devices.Length > 0; } }

    /// <summary>True when FANZi drives the RAM through MAXSUN Sync (OpenRGB is kept off the DRAM controllers).</summary>
    public bool OwnsRam { get { lock (_lock) return _online && _devices.Any(d => d.IsDram); } }

    // ── IRgbService: connection ──────────────────────────────────────────────

    public bool IsConnected => _inner.IsConnected || IsMaxsunOnline;

    public string ServerVersion => IsMaxsunOnline
        ? (_inner.IsConnected ? $"{_inner.ServerVersion} + MAXSUN Sync" : "MAXSUN Sync (OpenRGB offline)")
        : _inner.ServerVersion;

    public string ModeSwitchStatus => _inner.ModeSwitchStatus;
    public string? LastDeviceQueryError => _inner.IsConnected ? _inner.LastDeviceQueryError : null;
    public bool ModeChangesRejected => _inner.ModeChangesRejected;

    public async Task<bool> TryConnectAsync(string host = "localhost", int port = 6742, CancellationToken cancellationToken = default)
    {
        _host = host;
        _port = port;
        bool innerOk = await _inner.TryConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        bool maxsunOk = await Task.Run(() => ProbeMaxsun(force: true), cancellationToken).ConfigureAwait(false);
        return innerOk || maxsunOk;
    }

    public void Disconnect()
    {
        _inner.Disconnect();
        lock (_lock)
        {
            _lastFrame.Clear();
            _staticArmed.Clear();
        }
    }

    // ── IRgbService: devices ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<RgbDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => ProbeMaxsun(force: false), cancellationToken).ConfigureAwait(false);
        await ReconnectInnerIfDueAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<RgbDeviceInfo> openRgb = await _inner.GetDevicesAsync(cancellationToken).ConfigureAwait(false);

        MaxsunLedDevice[] devices;
        bool ownsRam;
        lock (_lock)
        {
            devices = _online ? _devices : Array.Empty<MaxsunLedDevice>();
            ownsRam = devices.Any(d => d.IsDram);
            _lastFrame.Clear();
            _staticArmed.Clear();
        }

        var list = new List<RgbDeviceInfo>(openRgb.Count + devices.Length);
        // OpenRGB's ENE DRAM entries (both SMBus ports) are the same sticks MAXSUN Sync drives: hide them.
        list.AddRange(ownsRam ? openRgb.Where(d => d.Category != RgbDeviceCategory.Ram) : openRgb);

        int dramOrdinal = 0;
        for (int i = 0; i < devices.Length; i++)
        {
            var d = devices[i];
            int index = IndexBase + i;
            string name = MaxsunSyncClient.FriendlyName(d, d.IsDram ? ++dramOrdinal : 0);
            list.Add(new RgbDeviceInfo(index, name, d.IsDram ? "Dram" : "Motherboard", d.LedCount, IsConnected: true)
            {
                Zones = new[] { new RgbZoneInfo(index, 0, d.IsDram ? "DIMM" : "LED chain", d.LedCount) },
                ControlLabel = "Per-LED · MAXSUN Sync (ENE HAL)",
                IsControllable = true,
                Vendor = string.IsNullOrWhiteSpace(d.Manufacturer) ? "MAXSUN" : d.Manufacturer,
                Location = $"MaxsunSyncService HAL {MaxsunSyncClient.FormatGuid(d.HalGuid)} /d {d.Index}",
                Layout = LedLayout.Linear(d.LedCount),
                LedNames = Enumerable.Range(1, d.LedCount).Select(n => $"LED {n}").ToArray(),
            });
        }
        return list;
    }

    public Task<int?> GetDeviceCountAsync(CancellationToken cancellationToken = default) =>
        _inner.IsConnected
            ? _inner.GetDeviceCountAsync(cancellationToken).ContinueWith(t =>
                t.Status == TaskStatus.RanToCompletion && t.Result is int n ? n - HiddenRamCount() + MaxsunCount() : (int?)null,
                cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            : Task.FromResult<int?>(null);

    // ── IRgbService: colour ──────────────────────────────────────────────────

    public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default)
    {
        if (!TryMaxsun(deviceIndex, out var d)) return _inner.SetDeviceColorAsync(deviceIndex, color, cancellationToken);
        return d is null ? Task.CompletedTask : WriteAsync(deviceIndex, d, Enumerable.Repeat(color, d.LedCount).ToArray(), cancellationToken);
    }

    public Task SetDeviceColorsAsync(int deviceIndex, RgbColor[] colors, CancellationToken cancellationToken = default)
    {
        if (!TryMaxsun(deviceIndex, out var d)) return _inner.SetDeviceColorsAsync(deviceIndex, colors, cancellationToken);
        return d is null ? Task.CompletedTask : WriteAsync(deviceIndex, d, colors ?? Array.Empty<RgbColor>(), cancellationToken);
    }

    /// <summary>MAXSUN HAL devices are one zone (the whole stick / LED chain): zone 0's colour fills it.</summary>
    public Task SetDeviceZoneColorsAsync(int deviceIndex, RgbColor[] zoneColors, CancellationToken cancellationToken = default)
    {
        if (!TryMaxsun(deviceIndex, out var d)) return _inner.SetDeviceZoneColorsAsync(deviceIndex, zoneColors, cancellationToken);
        if (d is null) return Task.CompletedTask;
        var colour = zoneColors is { Length: > 0 } ? zoneColors[0] : RgbColor.Black;
        return WriteAsync(deviceIndex, d, Enumerable.Repeat(colour, d.LedCount).ToArray(), cancellationToken);
    }

    public async Task SetAllDevicesColorAsync(RgbColor color, CancellationToken cancellationToken = default)
    {
        var innerTask = _inner.IsConnected ? _inner.SetAllDevicesColorAsync(color, cancellationToken) : Task.CompletedTask;
        MaxsunLedDevice[] devices;
        lock (_lock) devices = _online ? _devices : Array.Empty<MaxsunLedDevice>();
        var tasks = devices.Select((d, i) => WriteAsync(IndexBase + i, d, Enumerable.Repeat(color, d.LedCount).ToArray(), cancellationToken));
        await Task.WhenAll(tasks.Append(innerTask)).ConfigureAwait(false);
    }

    /// <summary>Re-sync RAM: re-arms Static on every HAL device, re-sends the colour and asks the service to resync.</summary>
    public async Task<int> ResyncSmbusDevicesAsync(RgbColor color, CancellationToken cancellationToken = default)
    {
        int viaInner = _inner.IsConnected ? await _inner.ResyncSmbusDevicesAsync(color, cancellationToken).ConfigureAwait(false) : 0;
        int viaMaxsun = await Task.Run(() =>
        {
            MaxsunLedDevice[] devices;
            lock (_lock)
            {
                devices = _online ? _devices : Array.Empty<MaxsunLedDevice>();
                _staticArmed.Clear();
                _lastFrame.Clear();
            }
            int done = 0;
            for (int i = 0; i < devices.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!devices[i].IsDram) continue;
                if (SendFrame(IndexBase + i, devices[i], Enumerable.Repeat(color, devices[i].LedCount).ToArray(), force: true)) done++;
            }
            if (done > 0) TrySend("resync");
            return done;
        }, cancellationToken).ConfigureAwait(false);
        return viaInner + viaMaxsun;
    }

    public int? RamSmbusPort
    {
        get => _inner.RamSmbusPort;
        set => _inner.RamSmbusPort = value;
    }

    public IReadOnlyList<int> MirroredSmbusPorts => OwnsRam ? Array.Empty<int>() : _inner.MirroredSmbusPorts;

    public string RamBusStatus
    {
        get
        {
            MaxsunLedDevice[] devices;
            lock (_lock) devices = _online ? _devices : Array.Empty<MaxsunLedDevice>();
            int sticks = devices.Count(d => d.IsDram);
            if (sticks == 0) return _inner.RamBusStatus;
            return $"RAM: {sticks} stick(s) driven through MAXSUN Sync (ENE HAL, the same path MAXSUN Sync 2 uses) — OpenRGB keeps off the SMBus RAM controllers.";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Dispose();
    }

    // ── internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="deviceIndex"/> belongs to MAXSUN Sync (never forwarded to OpenRGB). <paramref name="device"/>
    /// is null for a stale index (service went away since the last refresh) — the write is then dropped.
    /// </summary>
    private bool TryMaxsun(int deviceIndex, out MaxsunLedDevice? device)
    {
        device = null;
        int i = deviceIndex - IndexBase;
        if (i < 0) return false;
        lock (_lock)
        {
            if (_online && i < _devices.Length) device = _devices[i];
        }
        return true;
    }

    private Task WriteAsync(int deviceIndex, MaxsunLedDevice device, RgbColor[] frame, CancellationToken cancellationToken) =>
        Task.Run(() => { SendFrame(deviceIndex, device, frame, force: false); }, cancellationToken);

    /// <summary>Sends one frame (rate-limited, unchanged frames skipped). Returns true when the service accepted it.</summary>
    private bool SendFrame(int deviceIndex, MaxsunLedDevice device, RgbColor[] frame, bool force)
    {
        if (_disposed) return false;
        var fitted = Fit(frame, device.LedCount);
        bool switchMode;
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (!_online) return false;
            if (!force && _lastFrame.TryGetValue(deviceIndex, out var last))
            {
                if (last.Frame.AsSpan().SequenceEqual(fitted)) return true;
                if (Stopwatch.GetElapsedTime(last.Ticks, now) < MinFrameInterval) return false;
            }
            switchMode = !_staticArmed.Contains(deviceIndex);
            _lastFrame[deviceIndex] = (fitted, now); // claim the slot before the socket call (parallel frames)
        }

        string reply = TrySend(MaxsunSyncClient.StaticCommand(device, fitted, switchMode)) ?? "";
        bool ok = MaxsunSyncClient.IsOk(reply);
        lock (_lock)
        {
            if (ok) _staticArmed.Add(deviceIndex);
            else
            {
                _staticArmed.Remove(deviceIndex);
                _lastFrame.Remove(deviceIndex);
            }
        }
        if (!ok && !_failureLogged)
        {
            _failureLogged = true;
            AppLog.Warn($"MAXSUN Sync: '{device.Name}' refused a frame: {(reply.Length == 0 ? "no reply" : reply)}");
        }
        return ok;
    }

    private string? TrySend(string command)
    {
        try
        {
            return _client.Send(command);
        }
        catch (Exception ex)
        {
            lock (_lock) _online = false;
            AppLog.Warn("MAXSUN Sync: service stopped answering — RAM falls back to OpenRGB on the next rescan", ex);
            return null;
        }
    }

    private static RgbColor[] Fit(RgbColor[] frame, int count)
    {
        count = Math.Max(1, count);
        if (frame.Length == count) return (RgbColor[])frame.Clone();
        var leds = new RgbColor[count];
        for (int i = 0; i < count; i++)
            leds[i] = frame.Length == 0 ? RgbColor.Black : frame[(int)((long)i * frame.Length / count)];
        return leds;
    }

    /// <summary>Finds MaxsunSyncService and its devices (throttled unless <paramref name="force"/>). Returns online state.</summary>
    private bool ProbeMaxsun(bool force)
    {
        if (!OperatingSystem.IsWindows() || _disposed) return false;
        lock (_lock)
        {
            if (!force && _online) return true;
            long now = Stopwatch.GetTimestamp();
            if (!force && _lastProbeTicks != 0 && Stopwatch.GetElapsedTime(_lastProbeTicks, now) < ProbeInterval) return _online;
            _lastProbeTicks = now;
        }

        bool reachable = _client.IsReachable();
        MaxsunLedDevice[] devices = Array.Empty<MaxsunLedDevice>();
        if (reachable)
        {
            try { devices = _client.DiscoverDevices(InstalledHalGuids(), installedDimms: 0).ToArray(); }
            catch (Exception ex) { AppLog.Warn("MAXSUN Sync: device discovery failed", ex); }
        }

        bool online = reachable && devices.Length > 0;
        bool changed;
        lock (_lock)
        {
            changed = online != _online || devices.Length != _devices.Length;
            _online = online;
            _devices = devices;
            _lastFrame.Clear();
            _staticArmed.Clear();
            if (online) _failureLogged = false;
        }

        if (_openRgb is not null) _openRgb.SuppressDramWrites = online && devices.Any(d => d.IsDram);
        if (changed)
        {
            AppLog.Info(online
                ? $"MAXSUN Sync: {devices.Length} HAL device(s) — " + string.Join(", ", devices.Select(d => $"{d.Name} [{d.LedCount} LEDs]")) +
                  (devices.Any(d => d.IsDram) ? "; RAM is driven through MaxsunSyncService, OpenRGB stays off the DRAM SMBus controllers." : "")
                : reachable ? "MAXSUN Sync: service answers but reports no LED devices." : "MAXSUN Sync: service not running — RAM stays on OpenRGB.");
        }
        return online;
    }

    /// <summary>
    /// The HALs FANZi asks MaxsunSyncService about: the ENE DRAM HALs (in MAXSUN's priority order) and the MAXSUN board
    /// LED HAL (MaxSunNctLight — the 390-LED ARGB chain OpenRGB does not detect). Other Aura HALs (GPU, …) are left to
    /// OpenRGB so no device ever gets two writers.
    /// </summary>
    internal static IEnumerable<Guid> InstalledHalGuids() =>
        MaxsunLedDevice.DramHalGuids.Append(MaxsunLedDevice.BoardHalGuid);

    /// <summary>OpenRGB keeps reconnecting even while MAXSUN Sync alone keeps the service "connected".</summary>
    private async Task ReconnectInnerIfDueAsync(CancellationToken cancellationToken)
    {
        if (_inner.IsConnected || !IsMaxsunOnline) return;
        long now = Stopwatch.GetTimestamp();
        if (_lastInnerReconnectTicks != 0 && Stopwatch.GetElapsedTime(_lastInnerReconnectTicks, now) < ProbeInterval) return;
        _lastInnerReconnectTicks = now;
        try { await _inner.TryConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Warn("OpenRGB reconnect (behind MAXSUN Sync) failed", ex); }
    }

    private int MaxsunCount() { lock (_lock) return _online ? _devices.Length : 0; }

    private int HiddenRamCount() => OwnsRam && _openRgb is not null ? _openRgb.SuppressedDramCount : 0;
}
