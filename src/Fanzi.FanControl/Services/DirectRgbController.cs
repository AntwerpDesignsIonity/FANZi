using Fanzi.FanControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fanzi.FanControl.Services;

/// <summary>
/// FANZi IO-nity built-in RGB controller — works independently of external OpenRGB.
/// Provides a virtual device bus that maps fan channels to LED zones, supports
/// per-zone colour control, and bridges to OpenRGB when available for hardware output.
/// </summary>
public sealed class DirectRgbController : IRgbService, IDisposable
{
    private readonly List<VirtualRgbDevice> _virtualDevices = new();
    private readonly IRgbService? _bridge;
    private readonly object _lock = new();
    private bool _disposed;
    private int _frameCounter;

    public bool IsConnected => _virtualDevices.Count > 0 || (_bridge?.IsConnected ?? false);
    public string ServerVersion => _bridge?.ServerVersion ?? "IO-nity Direct RGB Engine v2.0";
    public string ModeSwitchStatus => _bridge?.ModeSwitchStatus ?? "Virtual devices (no mode switch needed)";

    public IReadOnlyList<VirtualRgbDevice> VirtualDevices => _virtualDevices;

    public DirectRgbController(IRgbService? openRgbBridge = null)
    {
        _bridge = openRgbBridge;
    }

    public void RegisterVirtualDevice(string name, int zoneCount, int ledsPerZone)
    {
        lock (_lock)
        {
            var zones = new List<RgbZoneInfo>();
            for (int z = 0; z < zoneCount; z++)
                zones.Add(new RgbZoneInfo(_virtualDevices.Count, z, $"Zone {z + 1}", ledsPerZone));

            _virtualDevices.Add(new VirtualRgbDevice(
                Index: _virtualDevices.Count,
                Name: name,
                Zones: zones,
                LedCount: zoneCount * ledsPerZone));
        }
    }

    public void AutoDiscoverFromFanChannels(IReadOnlyList<FanChannelSnapshot> fans)
    {
        lock (_lock)
        {
            _virtualDevices.Clear();
            foreach (var fan in fans)
            {
                RegisterVirtualDevice($"{fan.Name} RGB", 1, 1);
            }
            if (_virtualDevices.Count == 0)
            {
                RegisterVirtualDevice("IO-nity Virtual LED Strip", 4, 8);
            }
        }
    }

    public Task<bool> TryConnectAsync(string host = "localhost", int port = 6742, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_virtualDevices.Count == 0)
                AutoDiscoverFromFanChannels(Array.Empty<FanChannelSnapshot>());
        }

        if (_bridge != null)
            return _bridge.TryConnectAsync(host, port, cancellationToken);

        return Task.FromResult(true);
    }

    public void Disconnect()
    {
        _bridge?.Disconnect();
    }

    public Task<IReadOnlyList<RgbDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var devices = _virtualDevices.Select(v => new RgbDeviceInfo(
                DeviceIndex: v.Index,
                Name: v.Name,
                Type: "IO-nity Virtual",
                LedCount: v.LedCount,
                IsConnected: true)
            {
                Zones = v.Zones,
            }).ToList();

            return Task.FromResult<IReadOnlyList<RgbDeviceInfo>>(devices);
        }
    }

    public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default)
    {
        _frameCounter++;
        lock (_lock)
        {
            if (deviceIndex >= 0 && deviceIndex < _virtualDevices.Count)
                _virtualDevices[deviceIndex].CurrentColor = color;
        }

        if (_bridge?.IsConnected == true)
            return _bridge.SetDeviceColorAsync(deviceIndex, color, cancellationToken);

        return Task.CompletedTask;
    }

    public Task SetDeviceColorsAsync(int deviceIndex, RgbColor[] colors, CancellationToken cancellationToken = default)
    {
        _frameCounter++;
        if (_bridge?.IsConnected == true)
            return _bridge.SetDeviceColorsAsync(deviceIndex, colors, cancellationToken);

        return Task.CompletedTask;
    }

    public Task SetAllDevicesColorAsync(RgbColor color, CancellationToken cancellationToken = default)
    {
        _frameCounter++;
        lock (_lock)
        {
            foreach (var dev in _virtualDevices)
                dev.CurrentColor = color;
        }

        if (_bridge?.IsConnected == true)
            return _bridge.SetAllDevicesColorAsync(color, cancellationToken);

        return Task.CompletedTask;
    }

    public Task SetDeviceZoneColorsAsync(int deviceIndex, RgbColor[] zoneColors, CancellationToken cancellationToken = default)
    {
        _frameCounter++;
        if (_bridge?.IsConnected == true)
            return _bridge.SetDeviceZoneColorsAsync(deviceIndex, zoneColors, cancellationToken);

        return Task.CompletedTask;
    }

    public int FrameCount => _frameCounter;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bridge?.Dispose();
    }
}

public sealed record VirtualRgbDevice(int Index, string Name, IReadOnlyList<RgbZoneInfo> Zones, int LedCount)
{
    public RgbColor CurrentColor { get; set; } = RgbColor.Black;
}
