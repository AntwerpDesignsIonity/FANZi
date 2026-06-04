namespace Fanzi.FanControl.Models;

/// <summary>
/// A single addressable zone within an OpenRGB device (e.g. a RAM stick, a fan
/// ring, a GPU segment). Zones are how OpenRGB exposes the individually-addressable
/// regions of a device — controlling them separately is what lets FANZI paint each
/// box / endpoint its own colour instead of one colour for the whole device.
/// </summary>
public sealed record RgbZoneInfo(
    int    DeviceIndex,
    int    ZoneIndex,
    string Name,
    int    LedCount);
