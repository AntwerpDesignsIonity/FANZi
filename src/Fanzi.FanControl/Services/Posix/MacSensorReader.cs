using Fanzi.FanControl.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Fanzi.FanControl.Services.Posix;

/// <summary>
/// macOS sensors without root: CPU name and clock via <c>sysctl</c>, CPU load via <c>host_statistics</c>, and
/// temperatures + fan RPM from the System Management Controller (AppleSMC user client — the same unprivileged
/// read path that menu-bar monitors use) on Intel and Apple-silicon Macs. Writing fan speeds needs root, so fans
/// are reported read-only. Every native call is guarded: a Mac (or VM) without an SMC simply reports no sensors.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacSensorReader : IDisposable
{
    private (ulong Busy, ulong Total)? _lastCpu;
    private readonly Smc? _smc;
    private string[]? _cpuKeys, _gpuKeys;

    public MacSensorReader()
    {
        try { _smc = Smc.Open(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _smc = null;
        }
    }

    /// <summary>True when the AppleSMC user client opened.</summary>
    public bool HasSmc => _smc is not null;

    /// <summary>Intel and Apple-silicon CPU die / cluster keys (whichever exist on this Mac are used).</summary>
    internal static readonly string[] CpuKeyCandidates =
    {
        "TC0D", "TC0E", "TC0F", "TC0P", "TCXC", "TC1C", "TC2C", "TC3C", "TC4C",                  // Intel
        "Tp09", "Tp0T", "Tp01", "Tp05", "Tp0D", "Tp0H", "Tp0L", "Tp0P", "Tp0X", "Tp0b",            // M1
        "Tp1h", "Tp1t", "Tp1p", "Tp1l", "Tp0f", "Tp0j", "Te05", "Te0L", "Te0P", "Te0S",            // M2 / M3
        "Tp0V", "Tp0Y", "Tp0e", "Tp0n", "Tp0q", "Tp0u",                                              // M3 / M4
    };

    internal static readonly string[] GpuKeyCandidates =
    {
        "TG0D", "TG0P", "TG0H",                                                                      // Intel / dGPU
        "Tg05", "Tg0D", "Tg0L", "Tg0T", "Tg0f", "Tg0j", "Tg1U", "Tg1k", "Tg0K", "Tg0S",              // Apple silicon
    };

    public HardwareSnapshot Read(DateTimeOffset now)
    {
        var cpuReadings = new List<CpuReadingSnapshot>();
        var gpuReadings = new List<GpuReadingSnapshot>();

        var cpuTemps = new List<double>();
        var gpuTemps = new List<double>();
        var fans = new List<FanChannelSnapshot>();
        if (_smc is not null)
        {
            _cpuKeys ??= CpuKeyCandidates.Where(k => _smc.ReadTemperature(k) is not null).ToArray();
            _gpuKeys ??= GpuKeyCandidates.Where(k => _smc.ReadTemperature(k) is not null).ToArray();
            foreach (var key in _cpuKeys)
            {
                if (_smc.ReadTemperature(key) is not double t) continue;
                cpuTemps.Add(t);
                cpuReadings.Add(new CpuReadingSnapshot($"CPU {key}", $"{t:F1} C", "Temperature"));
            }
            foreach (var key in _gpuKeys)
            {
                if (_smc.ReadTemperature(key) is not double t) continue;
                gpuTemps.Add(t);
                gpuReadings.Add(new GpuReadingSnapshot($"GPU {key}", $"{t:F1} C", "Temperature"));
            }
            int count = _smc.ReadNumber("FNum") is double n ? (int)n : 0;
            for (int i = 0; i < Math.Min(count, 8); i++)
            {
                double? rpm = _smc.ReadNumber($"F{i}Ac");
                double? min = _smc.ReadNumber($"F{i}Mn"), max = _smc.ReadNumber($"F{i}Mx");
                double? percent = rpm is double r && min is double lo && max is double hi && hi > lo
                    ? Math.Clamp((r - lo) / (hi - lo) * 100, 0, 100) : null;
                fans.Add(new FanChannelSnapshot($"macos/smc/fan{i}", i == 0 ? "CPU Fan" : $"Fan {i + 1}", rpm, percent, false,
                    FanDeviceKind.Fan, "macOS fans are read-only here: writing SMC fan targets needs root.", "AppleSMC", i));
            }
        }

        double? load = CpuLoad();
        if (load is double l) cpuReadings.Add(new CpuReadingSnapshot("CPU Total", $"{l:F0}%", "Load"));
        double? clock = SysctlUInt64("hw.cpufrequency") is ulong hz and > 0 ? hz / 1e6 : null; // Intel only
        if (clock is double mhz) cpuReadings.Add(new CpuReadingSnapshot("CPU Clock", $"{mhz:F0} MHz", "Clock"));

        double? package = cpuTemps.Count > 0 ? cpuTemps.Max() : null;
        bool appleSilicon = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        string status = (_smc, cpuTemps.Count) switch
        {
            (null, _) => "macOS: the System Management Controller is not reachable (virtual machine?) — load and clock only.",
            (_, 0) => "macOS: no known SMC temperature keys on this Mac model — load and clock only.",
            _ => "macOS: temperatures and fan speeds are live from the SMC. Fan control is read-only on macOS.",
        };

        return new HardwareSnapshot(
            now,
            package,
            cpuTemps.Count > 0 ? cpuTemps.Average() : null,
            package,
            load,
            clock,
            null,
            null,
            gpuTemps.Count > 0 ? gpuTemps.Average() : null,
            gpuTemps.Count > 0 ? gpuTemps.Max() : null,
            null,
            null,
            null,
            fans.FirstOrDefault(),
            cpuReadings,
            gpuReadings,
            fans,
            status,
            SysctlString("machdep.cpu.brand_string") ?? (appleSilicon ? "Apple silicon" : "Mac CPU"),
            appleSilicon ? "Apple GPU (integrated)" : null,
            null,
            null);
    }

    private double? CpuLoad()
    {
        var ticks = new uint[4]; // user, system, idle, nice
        uint count = 4;
        try
        {
            if (host_statistics(mach_host_self(), HostCpuLoadInfo, ticks, ref count) != 0) return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        ulong busy = (ulong)ticks[0] + ticks[1] + ticks[3];
        ulong total = busy + ticks[2];
        var last = _lastCpu;
        _lastCpu = (busy, total);
        if (last is not { } prev || total <= prev.Total) return null;
        return Math.Clamp((double)(busy - Math.Min(busy, prev.Busy)) / (total - prev.Total) * 100, 0, 100);
    }

    public void Dispose() => _smc?.Dispose();

    // ── sysctl ───────────────────────────────────────────────────────────

    internal static string? SysctlString(string name)
    {
        try
        {
            nuint size = 0;
            if (sysctlbyname(name, IntPtr.Zero, ref size, IntPtr.Zero, 0) != 0 || size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, 0) != 0) return null;
                return Marshal.PtrToStringUTF8(buffer)?.Trim();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    internal static ulong? SysctlUInt64(string name)
    {
        try
        {
            nuint size = 8;
            var buffer = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(buffer, 0);
                if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, 0) != 0) return null;
                return size == 4 ? (ulong)(uint)Marshal.ReadInt32(buffer) : (ulong)Marshal.ReadInt64(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private const int HostCpuLoadInfo = 3;
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    [DllImport(LibSystem)] private static extern uint mach_host_self();
    [DllImport(LibSystem)] private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);
    [DllImport(LibSystem)] private static extern int sysctlbyname(string name, IntPtr oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    // ── SMC (AppleSMC IOKit user client) ─────────────────────────────────

    /// <summary>Minimal unprivileged AppleSMC reader (key info + read bytes).</summary>
    internal sealed class Smc : IDisposable
    {
        private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const int KernelIndexSmc = 2, CmdReadBytes = 5, CmdReadKeyInfo = 9, StructSize = 80;
        private uint _connection;
        private readonly Dictionary<string, (uint Size, string Type)?> _info = new(StringComparer.Ordinal);

        private Smc(uint connection) => _connection = connection;

        public static Smc? Open()
        {
            IntPtr matching = IOServiceMatching("AppleSMC");
            if (matching == IntPtr.Zero) return null;
            uint service = IOServiceGetMatchingService(0, matching); // consumes the matching dictionary
            if (service == 0) return null;
            try
            {
                return IOServiceOpen(service, TaskSelf(), 0, out uint connection) == 0 && connection != 0
                    ? new Smc(connection) : null;
            }
            finally { IOObjectRelease(service); }
        }

        public double? ReadTemperature(string key)
            => ReadNumber(key) is double t && t is > 5 and < 125 ? t : null;

        public double? ReadNumber(string key)
        {
            if (Info(key) is not { } info) return null;
            var bytes = Call(key, CmdReadBytes, info.Size);
            if (bytes is null) return null;
            return Decode(info.Type, bytes.AsSpan(0, (int)Math.Min(info.Size, 32u)));
        }

        /// <summary>Decodes an SMC value: sp78 / fpe2 fixed point (big-endian), flt (little-endian float), ui8/16/32.</summary>
        internal static double? Decode(string type, ReadOnlySpan<byte> b) => type switch
        {
            "sp78" when b.Length >= 2 => BinaryPrimitives.ReadInt16BigEndian(b) / 256.0,
            "fpe2" when b.Length >= 2 => BinaryPrimitives.ReadUInt16BigEndian(b) / 4.0,
            "flt " when b.Length >= 4 => BinaryPrimitives.ReadSingleLittleEndian(b),
            "ui8 " when b.Length >= 1 => b[0],
            "ui16" when b.Length >= 2 => BinaryPrimitives.ReadUInt16BigEndian(b),
            "ui32" when b.Length >= 4 => BinaryPrimitives.ReadUInt32BigEndian(b),
            _ => null,
        };

        private (uint Size, string Type)? Info(string key)
        {
            if (_info.TryGetValue(key, out var cached)) return cached;
            (uint, string)? result = null;
            var output = Call(key, CmdReadKeyInfo, 0, wantRaw: true);
            if (output is not null)
            {
                uint size = BitConverter.ToUInt32(output, 28);
                uint type = BitConverter.ToUInt32(output, 32);
                if (size is > 0 and <= 32) result = (size, FourCc(type));
            }
            _info[key] = result;
            return result;
        }

        /// <summary>One SMC call; returns the 32 data bytes (or the raw 80-byte struct when <paramref name="wantRaw"/>).</summary>
        private byte[]? Call(string key, byte command, uint dataSize, bool wantRaw = false)
        {
            if (_connection == 0 || key.Length != 4) return null;
            var input = new byte[StructSize];
            var output = new byte[StructSize];
            BitConverter.GetBytes(ToFourCc(key)).CopyTo(input, 0);
            BitConverter.GetBytes(dataSize).CopyTo(input, 28); // keyInfo.dataSize
            input[42] = command;                               // data8
            nuint outSize = StructSize;
            int kr = IOConnectCallStructMethod(_connection, KernelIndexSmc, input, StructSize, output, ref outSize);
            if (kr != 0 || output[40] != 0) return null;        // result byte
            if (wantRaw) return output;
            return output.AsSpan(48, 32).ToArray();
        }

        internal static uint ToFourCc(string key)
            => (uint)(key[0] << 24 | key[1] << 16 | key[2] << 8 | key[3]);

        internal static string FourCc(uint value)
            => Encoding.ASCII.GetString(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

        public void Dispose()
        {
            if (_connection != 0)
            {
                try { IOServiceClose(_connection); } catch { }
                _connection = 0;
            }
        }

        [DllImport(IoKit)] private static extern IntPtr IOServiceMatching(string name);
        [DllImport(IoKit)] private static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
        [DllImport(IoKit)] private static extern int IOServiceOpen(uint service, uint owningTask, uint type, out uint connect);
        [DllImport(IoKit)] private static extern int IOServiceClose(uint connect);
        [DllImport(IoKit)] private static extern int IOObjectRelease(uint obj);
        [DllImport(IoKit)]
        private static extern int IOConnectCallStructMethod(uint connection, uint selector, byte[] input, nuint inputSize,
            byte[] output, ref nuint outputSize);
        /// <summary><c>mach_task_self()</c> is a C macro over the exported global <c>mach_task_self_</c>.</summary>
        private static uint TaskSelf()
        {
            var lib = NativeLibrary.Load(LibSystem);
            return (uint)Marshal.ReadInt32(NativeLibrary.GetExport(lib, "mach_task_self_"));
        }
    }
}
