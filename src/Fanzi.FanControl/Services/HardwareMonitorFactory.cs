using Fanzi.FanControl.Services.Posix;
using System;
using System.Runtime.CompilerServices;

namespace Fanzi.FanControl.Services;

/// <summary>
/// Picks the sensor back-end for this OS (4.1). <see cref="HardwareMonitorService"/> holds LibreHardwareMonitor types,
/// whose assembly only ships for Windows, so it is constructed in its own non-inlined method that Linux / macOS never
/// reach — loading it there would crash the app at start-up.
/// </summary>
public static class HardwareMonitorFactory
{
    public static IHardwareMonitorService Create()
        => OperatingSystem.IsWindows() ? CreateWindows() : new PosixHardwareMonitorService();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IHardwareMonitorService CreateWindows() => new HardwareMonitorService();
}
