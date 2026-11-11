namespace Fanzi.FanControl.Models;

public enum FanDeviceKind
{
    Fan,
    Pump,
    AioCooler,
    Watercooler,
    IoPump
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
    int SensorIndex)
{
    /// <summary>4.5: what the channel is (CPU fan, system fan, pump, GPU fan) — decided by <c>CoolingChannelClassifier</c>.</summary>
    public Fanzi.FanControl.Services.CoolingRole Role { get; init; }

    /// <summary>4.5: how <see cref="Role"/> / <see cref="DeviceKind"/> were decided (name, hardware, RPM signature, user).</summary>
    public Fanzi.FanControl.Services.CoolingEvidence Evidence { get; init; } = Fanzi.FanControl.Services.CoolingEvidence.Default;

    /// <summary>4.5: why Apply / Auto are disabled for this channel.</summary>
    public FanLockReason LockReason { get; init; }

    public bool IsPump => DeviceKind is FanDeviceKind.Pump or FanDeviceKind.AioCooler or FanDeviceKind.IoPump or FanDeviceKind.Watercooler;
}

/// <summary>Why a fan channel cannot be set from FANZi right now.</summary>
public enum FanLockReason { None, BiosControl, PumpProtected, ReadOnlyHardware }