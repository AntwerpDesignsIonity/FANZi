using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.Models;

public sealed class FanProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public double CpuFanDesiredPercent { get; set; } = 50;
    public double CpuWarningThresholdDegrees { get; set; } = 95;
    public string NotificationEmail { get; set; } = string.Empty;
    public Dictionary<string, double> FanChannelPercents { get; set; } = new();
    public List<FanCurvePoint> FanCurve { get; set; } = new();
    public bool UseFanCurve { get; set; }
    public string? LinkedApplication { get; set; }
    public RgbProfileState? RgbState { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FanCurvePoint
{
    public double TemperatureC { get; set; }
    public double FanPercent { get; set; }
}

public sealed class RgbProfileState
{
    public string Effect { get; set; } = "Pulse";
    public string PrimaryColorHex { get; set; } = "#0064FF";
    public string SecondaryColorHex { get; set; } = "#00C8FF";
    public double SpeedMultiplier { get; set; } = 1.0;
    public double Brightness { get; set; } = 1.0;
    public bool HardwareReactive { get; set; } = true;
}
