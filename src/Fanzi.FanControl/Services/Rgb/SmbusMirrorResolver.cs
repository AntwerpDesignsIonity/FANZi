using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>One OpenRGB device as the resolver sees it (library-independent, so this is unit-testable).</summary>
public sealed record SmbusDeviceSketch(int Index, string Name, string Type, string Location, int LedCount);

/// <summary>Where an SMBus device lives: host port (PIIX4 0 / 1, I801 …) and slave address.</summary>
public readonly record struct SmbusAddress(string Adapter, int Port, int Address)
{
    public string PortLabel => $"{Adapter} port {Port}";
}

/// <summary>Result for one device: drive it, or skip it because it mirrors another entry.</summary>
public sealed record SmbusMirrorDecision(int Index, bool Drive, int? MirrorOf, string? Note);

/// <summary>
/// 4.3 RAM fix. AMD FCH SMBus (PIIX4) exposes two host ports that reach the <em>same</em> physical bus on many
/// boards, so OpenRGB registers every ENE DRAM controller twice — once per port, same slave address
/// (e.g. "PawnIO SMBus PIIX4 0, address 0x71" and "PawnIO SMBus PIIX4 1, address 0x71"). FANZi used to stream
/// colours through both entries every frame: two host controllers interleaving register writes into one ENE
/// controller corrupt its register pointer, after which the stick ignores every colour until power-cycled, and
/// its LED count reads back as garbage (0, 249, 257 …).
///
/// This resolver keeps exactly one entry per physical stick and marks the others as mirrors that are never
/// written. Pick order: a user-forced port, else the entry with a plausible LED count, else the highest port
/// (OpenRGB's DRAM detector — which knows which bus the SPD EEPROMs answer on — runs after the generic I2C
/// detector and therefore registers later).
/// </summary>
public static class SmbusMirrorResolver
{
    /// <summary>LED counts outside this range are read errors, not RGB sticks.</summary>
    public const int MaxPlausibleRamLeds = 64;

    private static readonly Regex LocationPattern = new(
        @"I2C:\s*(?<adapter>.*?)\s+(?<port>\d+)\s*,\s*address\s*0x(?<addr>[0-9A-Fa-f]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses an OpenRGB I2C location string; null for HID / USB / unknown formats.</summary>
    public static SmbusAddress? Parse(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var m = LocationPattern.Match(location);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["port"].Value, out int port)) return null;
        int addr = Convert.ToInt32(m.Groups["addr"].Value, 16);
        return new SmbusAddress(m.Groups["adapter"].Value.Trim(), port, addr);
    }

    public static bool IsPlausibleRamLedCount(int leds) => leds is > 0 and <= MaxPlausibleRamLeds;

    /// <summary>
    /// Decides, per device, whether FANZi drives it. Only SMBus devices that share name + slave address with another
    /// SMBus device on a different port are affected; everything else is driven.
    /// </summary>
    /// <param name="forcedPort">User override: always drive this SMBus port's entry when it exists (null = auto).</param>
    public static IReadOnlyList<SmbusMirrorDecision> Resolve(IReadOnlyList<SmbusDeviceSketch> devices, int? forcedPort = null)
    {
        var decisions = new SmbusMirrorDecision[devices.Count];
        for (int i = 0; i < devices.Count; i++)
            decisions[i] = new SmbusMirrorDecision(devices[i].Index, Drive: true, MirrorOf: null, Note: null);

        var smbus = devices
            .Select(d => (Device: d, Addr: Parse(d.Location)))
            .Where(t => t.Addr is not null)
            .Select(t => (t.Device, Addr: t.Addr!.Value))
            .ToList();

        foreach (var group in smbus.GroupBy(t => (t.Device.Name, t.Addr.Adapter, t.Addr.Address)))
        {
            var members = group.ToList();
            if (members.Select(m => m.Addr.Port).Distinct().Count() < 2) continue; // same port twice = two real devices

            var chosen = Choose(members, forcedPort);
            foreach (var m in members)
            {
                int pos = IndexOf(devices, m.Device.Index);
                if (m.Device.Index == chosen.Device.Index)
                {
                    decisions[pos] = decisions[pos] with
                    {
                        Note = $"Driven via {chosen.Addr.PortLabel} (mirror on port " +
                               string.Join("/", members.Where(x => x.Device.Index != chosen.Device.Index).Select(x => x.Addr.Port)) + " ignored)",
                    };
                }
                else
                {
                    decisions[pos] = new SmbusMirrorDecision(
                        m.Device.Index, Drive: false, MirrorOf: chosen.Device.Index,
                        Note: $"Mirror of the same stick seen through {m.Addr.PortLabel} — driven via port {chosen.Addr.Port} instead");
                }
            }
        }

        return decisions;
    }

    private static (SmbusDeviceSketch Device, SmbusAddress Addr) Choose(
        List<(SmbusDeviceSketch Device, SmbusAddress Addr)> members, int? forcedPort)
    {
        if (forcedPort is int fp)
        {
            var forced = members.FirstOrDefault(m => m.Addr.Port == fp);
            if (forced.Device is not null) return forced;
        }

        // Plausible LED count beats garbage; among equals the highest port wins (registered last by the DRAM detector).
        return members
            .OrderByDescending(m => IsPlausibleRamLedCount(m.Device.LedCount) ? 1 : 0)
            .ThenByDescending(m => m.Addr.Port)
            .First();
    }

    private static int IndexOf(IReadOnlyList<SmbusDeviceSketch> devices, int deviceIndex)
    {
        for (int i = 0; i < devices.Count; i++)
            if (devices[i].Index == deviceIndex) return i;
        throw new InvalidOperationException($"device index {deviceIndex} not in list");
    }

    /// <summary>Distinct SMBus ports present among the given devices (for the "use other port" UI).</summary>
    public static IReadOnlyList<int> Ports(IEnumerable<SmbusDeviceSketch> devices) =>
        devices.Select(d => Parse(d.Location)).Where(a => a is not null).Select(a => a!.Value.Port).Distinct().OrderBy(p => p).ToArray();
}
