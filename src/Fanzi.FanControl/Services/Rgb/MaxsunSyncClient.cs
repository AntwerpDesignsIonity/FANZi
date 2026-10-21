using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using Fanzi.FanControl.Models;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>One LED device that MaxsunSyncService exposes through an Aura-style HAL (ENE DRAM, MAXSUN board LEDs, …).</summary>
public sealed record MaxsunLedDevice(Guid HalGuid, int Index, string Name, int LedCount, int DeviceType, string Manufacturer)
{
    /// <summary>ENE DRAM HALs MAXSUN Sync 2 treats as memory (same list and order as its DeviceHelper.DramGuidPriority).</summary>
    public static readonly IReadOnlyList<Guid> DramHalGuids = new[]
    {
        new Guid("05B8E7A3-7D5D-41DE-B6CC-5E6205837FB7"), // ENE_RGB (AacHal) — Patriot / ENE DDR5 sticks
        new Guid("136A7445-3013-4FA5-AE40-B5CDBC67F425"),
        new Guid("F4A7CF47-482E-48A3-BEF8-B8364F13D3F2"),
        new Guid("84D8717B-4E1F-4607-9C1A-14E1BDB7B675"), // ENE_DRAM_AIO
    };

    /// <summary>MAXSUN board LED HAL (MaxSunNctLight, "MAXSUN MOTHERBOARD LED ENE").</summary>
    public static readonly Guid BoardHalGuid = new("1995564F-DA1A-4FFD-97F2-F412A631E99C");

    /// <summary>HAL device type MAXSUN reports for DRAM (0x70000).</summary>
    public const int DramDeviceType = 0x70000;

    public bool IsDram => DeviceType == DramDeviceType || DramHalGuids.Contains(HalGuid);
}

/// <summary>
/// 4.4: talks to <c>MaxsunSyncService</c> — the LocalSystem service behind MAXSUN Sync 2 — exactly the way MAXSUN's own
/// utility does: one UTF-8 text command per TCP connection to 127.0.0.1:5150, newline terminated, reply read until the
/// service closes or sends less than 1 KiB. The service owns the ENE / MAXSUN HAL DLLs and is the only SMBus master for
/// the RAM, so routing FANZi's colours through it means the sticks never see two writers.
///
/// Commands (from MAXSUN Sync 2 2026-09): <c>capab /guids G /d N</c> → XML layout; <c>light /guids G /d N /m Mode …</c>
/// (switch effect); <c>update …</c> (same effect, new colours); <c>resync</c>. Colours are <c>AABBGGRR</c> hex.
/// </summary>
public sealed class MaxsunSyncClient
{
    public const string Host = "127.0.0.1";
    public const int DefaultPort = 5150;
    public const string ServiceName = "MaxsunSyncService";

    private readonly int _port;
    private readonly TimeSpan _timeout;

    public MaxsunSyncClient(int port = DefaultPort, TimeSpan? timeout = null)
    {
        _port = port;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(1500);
    }

    /// <summary>True when the service accepts a TCP connection right now (cheap: no command is sent).</summary>
    public bool IsReachable()
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(Host, _port);
            return connect.Wait(_timeout) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sends one command and returns the service's reply ("" when it answered nothing). Throws on socket errors.</summary>
    public string Send(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Empty command", nameof(command));
        using var tcp = new TcpClient { NoDelay = true };
        var connect = tcp.ConnectAsync(Host, _port);
        if (!connect.Wait(_timeout) || !tcp.Connected)
            throw new IOException($"{ServiceName} did not accept a connection on {Host}:{_port}");

        using NetworkStream stream = tcp.GetStream();
        stream.ReadTimeout = (int)_timeout.TotalMilliseconds;
        stream.WriteTimeout = (int)_timeout.TotalMilliseconds;
        byte[] payload = Encoding.UTF8.GetBytes(command.EndsWith('\n') ? command : command + "\n");
        stream.Write(payload, 0, payload.Length);

        using var reply = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int n;
            try { n = stream.Read(buffer, 0, buffer.Length); }
            catch (IOException) { break; } // read timeout: the service is done talking
            if (n <= 0) break;
            reply.Write(buffer, 0, n);
            if (stream.DataAvailable) continue;
            // A capab reply is XML (20 KB for the board's 390 LEDs) and can arrive in several TCP segments: keep reading
            // until the closing tag. Every other reply is one short line written at once.
            if (!IsIncompleteXml(reply)) break;
        }
        return Encoding.UTF8.GetString(reply.ToArray()).Trim('\0', '\r', '\n', ' ');
    }

    /// <summary>
    /// Enumerates every LED device behind the given HALs the way MAXSUN Sync 2 does (capab /d 0, 1, … until an error),
    /// then applies <see cref="TrimDram"/>.
    /// </summary>
    public IReadOnlyList<MaxsunLedDevice> DiscoverDevices(IEnumerable<Guid> halGuids, int installedDimms)
    {
        var found = new List<MaxsunLedDevice>();
        foreach (Guid guid in halGuids.Distinct())
        {
            // Several DRAM HALs answer for the same sticks (ENE_RGB and ENE_DRAM_AIO here); the first one that
            // reports devices wins, exactly like MAXSUN Sync 2 — and the slower ones are not even asked.
            if (MaxsunLedDevice.DramHalGuids.Contains(guid) && found.Any(d => d.IsDram)) continue;
            for (int index = 0; index < 16; index++)
            {
                string reply;
                try { reply = Send(CapabCommand(guid, index)); }
                catch { break; }
                var device = ParseCapab(reply, guid, index);
                if (device is null) break;
                found.Add(device);
            }
        }
        return TrimDram(found, installedDimms);
    }

    private static bool IsIncompleteXml(MemoryStream reply)
    {
        var buf = reply.GetBuffer().AsSpan(0, (int)reply.Length);
        int start = 0;
        while (start < buf.Length && (buf[start] == (byte)' ' || buf[start] == (byte)'\r' || buf[start] == (byte)'\n')) start++;
        if (start >= buf.Length || buf[start] != (byte)'<') return false;
        return buf.IndexOf("</root>"u8) < 0;
    }

    // ── Pure protocol helpers (unit-tested) ───────────────────────────────────

    public static string FormatGuid(Guid g) => g.ToString("D", CultureInfo.InvariantCulture);

    public static string CapabCommand(Guid hal, int index) => $"capab /guids {FormatGuid(hal)} /d {index}";

    /// <summary>MAXSUN colour word: alpha, blue, green, red (<c>FFBBGGRR</c>).</summary>
    public static string FormatColor(RgbColor c) =>
        string.Create(8, c, static (span, col) =>
        {
            const string hex = "0123456789ABCDEF";
            span[0] = 'F'; span[1] = 'F';
            span[2] = hex[col.B >> 4]; span[3] = hex[col.B & 0xF];
            span[4] = hex[col.G >> 4]; span[5] = hex[col.G & 0xF];
            span[6] = hex[col.R >> 4]; span[7] = hex[col.R & 0xF];
        });

    /// <summary>One colour per LED, comma separated; the frame is stretched / repeated to exactly <paramref name="ledCount"/>.</summary>
    public static string FormatColors(IReadOnlyList<RgbColor> frame, int ledCount)
    {
        ledCount = Math.Max(1, ledCount);
        var sb = new StringBuilder(ledCount * 9);
        for (int i = 0; i < ledCount; i++)
        {
            RgbColor c = frame.Count == 0 ? RgbColor.Black
                : frame.Count >= ledCount ? frame[i]
                : frame[(int)((long)i * frame.Count / ledCount)];
            if (i > 0) sb.Append(',');
            sb.Append(FormatColor(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Static frame command. <c>light</c> switches the HAL into Static (first frame / after another app changed it);
    /// <c>update</c> only replaces the colours and is what MAXSUN Sync 2 sends while you drag its colour picker.
    /// </summary>
    public static string StaticCommand(MaxsunLedDevice d, IReadOnlyList<RgbColor> frame, bool switchMode, int brightness = 100) =>
        $"{(switchMode ? "light" : "update")} /guids {FormatGuid(d.HalGuid)} /d {d.Index} /m Static /colors {FormatColors(frame, d.LedCount)} /brightness {Math.Clamp(brightness, 0, 100)}";

    /// <summary>True for a reply the service sends on success ("Set effect to device[…]", "Update device[…]", "Resynced").</summary>
    public static bool IsOk(string reply) =>
        !string.IsNullOrWhiteSpace(reply)
        && !reply.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
        && !reply.StartsWith("ERROR", StringComparison.Ordinal)
        && !reply.Contains("Unknown command", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a <c>capab</c> XML reply; null for an error / empty reply (no device at that index).</summary>
    public static MaxsunLedDevice? ParseCapab(string reply, Guid hal, int index)
    {
        if (string.IsNullOrWhiteSpace(reply) || !reply.TrimStart().StartsWith('<')) return null;
        try
        {
            var xml = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(reply), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                xml.Load(reader);
            string name = xml.SelectSingleNode("//device/name")?.InnerText.Trim() ?? $"MAXSUN device {index}";
            if (!int.TryParse(xml.SelectSingleNode("//device/layout/led_count")?.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int leds) || leds <= 0)
                return null;
            int.TryParse(xml.SelectSingleNode("//type")?.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int type);
            string maker = xml.SelectSingleNode("//device/manufacturer")?.InnerText.Trim() ?? "";
            return new MaxsunLedDevice(hal, index, name, leds, type, maker);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// MAXSUN's ExcludeDramDevices, made independent of WMI: several DRAM HALs can answer for the same sticks, so only
    /// the highest-priority DRAM HAL that reported devices is kept (one writer per stick); when the DIMM count is known
    /// (&gt; 0) the kept entries are also capped to it, because the ENE HAL can answer for empty slots.
    /// </summary>
    public static IReadOnlyList<MaxsunLedDevice> TrimDram(IReadOnlyList<MaxsunLedDevice> devices, int installedDimms)
    {
        Guid? winner = MaxsunLedDevice.DramHalGuids.Cast<Guid?>().FirstOrDefault(g => devices.Any(d => d.HalGuid == g));
        var result = new List<MaxsunLedDevice>(devices.Count);
        int dram = 0;
        foreach (var d in devices)
        {
            bool isDramHal = MaxsunLedDevice.DramHalGuids.Contains(d.HalGuid);
            if (!isDramHal) { result.Add(d); continue; }
            if (d.HalGuid != winner) continue;
            if (installedDimms > 0 && ++dram > installedDimms) continue;
            result.Add(d);
        }
        return result;
    }

    /// <summary>
    /// Friendly name: ENE's "ENE_RGB_For_ASUS0" becomes "RAM stick 1 (ENE via MAXSUN Sync)", the board keeps its name.
    /// </summary>
    public static string FriendlyName(MaxsunLedDevice d, int dramOrdinal) =>
        d.IsDram ? $"RAM stick {dramOrdinal} (ENE · MAXSUN Sync)" : $"{d.Name} (MAXSUN Sync)";
}
