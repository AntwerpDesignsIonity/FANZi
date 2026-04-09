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
}
