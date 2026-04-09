using System;
using System.Collections.Generic;

namespace Fanzi.FanControl.Models;

public sealed record HardwareSnapshot(
    DateTimeOffset Timestamp,
    double? CpuPackageTemperature,
    double? CpuAverageTemperature,
    double? CpuHotspotTemperature,
    double? CpuTotalLoadPercent,
    double? CpuAverageClockMhz,
    double? CpuPackagePowerWatts,
    double? CpuCoreVoltage,
    double? GpuCoreTemperature,
    double? GpuHotspotTemperature,
    double? GpuLoadPercent,
    double? GpuCoreClockMhz,
    double? GpuPowerWatts,
    FanChannelSnapshot? CpuFan,
    IReadOnlyList<CpuReadingSnapshot> CpuReadings,
    IReadOnlyList<GpuReadingSnapshot> GpuReadings,
    IReadOnlyList<FanChannelSnapshot> Fans,
    string StatusMessage,
    string? CpuName,
    string? GpuName,
    double? GpuMemoryUsedMb,
    double? GpuMemoryTotalMb);