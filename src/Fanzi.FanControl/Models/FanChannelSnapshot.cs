namespace Fanzi.FanControl.Models;

public enum FanDeviceKind
{
    Fan,
    Pump,
    AioCooler
}

public sealed record FanChannelSnapshot(
    string Id,
    string Name,
    double? SpeedRpm,
    double? CurrentControlPercent,
    bool CanControl,
    FanDeviceKind DeviceKind,
    string CapabilityMessage,
    string HardwareSource,
    int SensorIndex);