using System;
using System.Runtime.InteropServices;

namespace Fanzi.FanControl.Services;

/// <summary>Physical memory in use, read with GlobalMemoryStatusEx (no WMI, sub-millisecond).</summary>
public static class SystemMemory
{
    /// <summary>One reading; <see cref="UsedPercent"/> is 0–100.</summary>
    public readonly record struct Reading(double TotalGb, double UsedGb, double UsedPercent)
    {
        public string Display => TotalGb <= 0 ? "—" : $"{UsedGb:F1} / {TotalGb:F0} GB";
    }

    /// <summary>Current usage, or null when the call is unavailable (non-Windows, or it failed).</summary>
    public static Reading? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return null;
            double total = status.TotalPhys / 1024d / 1024d / 1024d;
            double used = (status.TotalPhys - status.AvailPhys) / 1024d / 1024d / 1024d;
            return new Reading(total, used, Math.Clamp(used / total * 100d, 0, 100));
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
