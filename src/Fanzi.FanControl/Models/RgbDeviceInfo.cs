namespace Fanzi.FanControl.Models;

/// <summary>Represents a connected OpenRGB device and its zone LEDs.</summary>
public sealed record RgbDeviceInfo(
    int    DeviceIndex,
    string Name,
    string Type,
    int    LedCount,
    bool   IsConnected);
