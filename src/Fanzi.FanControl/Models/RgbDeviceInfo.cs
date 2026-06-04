namespace Fanzi.FanControl.Models;

using System;
using System.Collections.Generic;

/// <summary>Represents a connected OpenRGB device and its zone LEDs.</summary>
public sealed record RgbDeviceInfo(
    int    DeviceIndex,
    string Name,
    string Type,
    int    LedCount,
    bool   IsConnected)
{
    /// <summary>Addressable zones within this device (never null).</summary>
    public IReadOnlyList<RgbZoneInfo> Zones { get; init; } = Array.Empty<RgbZoneInfo>();
}
