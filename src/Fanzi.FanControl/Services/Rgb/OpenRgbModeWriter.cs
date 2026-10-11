using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Fanzi.FanControl.Services.Rgb;

/// <summary>Everything OpenRGB needs to re-apply one mode (mirrors OpenRGB's <c>mode</c> struct).</summary>
public sealed record OpenRgbModeSnapshot(
    string Name,
    int Value,
    uint Flags,
    uint SpeedMin,
    uint SpeedMax,
    uint BrightnessMin,
    uint BrightnessMax,
    uint ColorMin,
    uint ColorMax,
    uint Speed,
    uint Brightness,
    uint Direction,
    uint ColorMode,
    IReadOnlyList<(byte R, byte G, byte B)> Colors);

/// <summary>
/// Sends RGBController::UpdateMode packets over its own SDK connection.
/// <para>
/// Why this exists: OpenRGB.NET 3.1.1 stores "no direction" as 0xFFFFFFFF and echoes it back in
/// UpdateMode, while OpenRGB 1.0 accepts a mode only when a direction-less mode carries direction 0
/// (RGBController::SetModeValuesFromMode). Every mode change therefore failed silently — per-LED
/// devices stayed in their effect and single-colour devices (e.g. ZOTAC RTX 30 GPUs) never changed.
/// This writer serialises the packet itself (protocol 4) with the direction normalised.
/// </para>
/// Not thread-safe: OpenRgbService calls it under its own lock.
/// </summary>
public sealed class OpenRgbModeWriter : IDisposable
{
    internal const uint ProtocolVersion = 4;
    internal const uint PacketRequestProtocolVersion = 40;
    internal const uint PacketSetClientName = 50;
    internal const uint PacketUpdateMode = 1101;

    // OpenRGB mode flags (RGBController.h).
    internal const uint FlagHasDirectionLr = 1u << 1;
    internal const uint FlagHasDirectionUd = 1u << 2;
    internal const uint FlagHasDirectionHv = 1u << 3;

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public OpenRgbModeWriter(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>Sends UpdateMode for <paramref name="deviceIndex"/>, connecting (once) on demand.</summary>
    public void UpdateMode(int deviceIndex, int modeIndex, OpenRgbModeSnapshot mode)
    {
        EnsureConnected();
        DrainIncoming();
        byte[] packet = BuildUpdateModePacket(deviceIndex, modeIndex, mode);
        try
        {
            _stream!.Write(packet, 0, packet.Length);
        }
        catch
        {
            Close();
            throw;
        }
    }

    /// <summary>
    /// Direction OpenRGB 1.0 accepts: the mode's own value when it has a direction flag,
    /// otherwise 0 (OpenRGB.NET reports 0xFFFFFFFF for those).
    /// </summary>
    internal static uint NormalizeDirection(uint flags, uint direction)
    {
        bool hasDirection = (flags & (FlagHasDirectionLr | FlagHasDirectionUd | FlagHasDirectionHv)) != 0;
        if (!hasDirection) return 0;
        return direction == uint.MaxValue ? 0 : direction;
    }

    /// <summary>Full UpdateMode packet (header + body) for SDK protocol 4.</summary>
    internal static byte[] BuildUpdateModePacket(int deviceIndex, int modeIndex, OpenRgbModeSnapshot m)
    {
        byte[] name = Encoding.UTF8.GetBytes(m.Name ?? string.Empty);
        int colorCount = m.Colors?.Count ?? 0;

        using var body = new MemoryStream();
        using var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);
        w.Write(0u);                                 // data_size placeholder
        w.Write((uint)modeIndex);
        w.Write((ushort)(name.Length + 1));          // name length incl. terminator
        w.Write(name);
        w.Write((byte)0);
        w.Write(m.Value);
        w.Write(m.Flags);
        w.Write(m.SpeedMin);
        w.Write(m.SpeedMax);
        w.Write(m.BrightnessMin);                    // protocol >= 3
        w.Write(m.BrightnessMax);
        w.Write(m.ColorMin);
        w.Write(m.ColorMax);
        w.Write(m.Speed);
        w.Write(m.Brightness);                       // protocol >= 3
        w.Write(NormalizeDirection(m.Flags, m.Direction));
        w.Write(m.ColorMode);
        w.Write((ushort)colorCount);
        for (int i = 0; i < colorCount; i++)
        {
            var c = m.Colors![i];
            w.Write(c.R); w.Write(c.G); w.Write(c.B); w.Write((byte)0);
        }
        w.Flush();

        byte[] data = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)data.Length);
        return Packet((uint)deviceIndex, PacketUpdateMode, data);
    }

    internal static byte[] Packet(uint deviceIndex, uint packetId, byte[] data)
    {
        var packet = new byte[16 + data.Length];
        packet[0] = (byte)'O'; packet[1] = (byte)'R'; packet[2] = (byte)'G'; packet[3] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)data.Length);
        data.CopyTo(packet, 16);
        return packet;
    }

    private void EnsureConnected()
    {
        if (_tcp is { Connected: true } && _stream is not null) return;
        Close();

        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 3000, SendTimeout = 3000 };
        tcp.Connect(_host, _port);
        var stream = tcp.GetStream();

        // The server parses mode packets by the client's protocol version (0 until negotiated).
        var version = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, ProtocolVersion);
        var request = Packet(0, PacketRequestProtocolVersion, version);
        stream.Write(request, 0, request.Length);
        ReadReply(stream, PacketRequestProtocolVersion);

        var name = Encoding.UTF8.GetBytes("FANZI-MODE\0");
        var hello = Packet(0, PacketSetClientName, name);
        stream.Write(hello, 0, hello.Length);

        _tcp = tcp;
        _stream = stream;
    }

    /// <summary>Reads packets until one with <paramref name="packetId"/> arrives (others are skipped).</summary>
    private static void ReadReply(NetworkStream stream, uint packetId)
    {
        var header = new byte[16];
        for (int guard = 0; guard < 16; guard++)
        {
            ReadExactly(stream, header);
            if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
                throw new IOException("OpenRGB: bad packet magic");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            var payload = new byte[size];
            ReadExactly(stream, payload);
            if (id == packetId) return;
        }
        throw new IOException("OpenRGB: no protocol version reply");
    }

    private static void ReadExactly(NetworkStream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new IOException("OpenRGB: connection closed");
            read += n;
        }
    }

    /// <summary>Discards server notifications (e.g. device list updated) so the socket never backs up.</summary>
    private void DrainIncoming()
    {
        try
        {
            var buf = new byte[4096];
            while (_tcp is { Available: > 0 } && _stream is not null)
            {
                int n = _stream.Read(buf, 0, Math.Min(buf.Length, _tcp.Available));
                if (n <= 0) break;
            }
        }
        catch
        {
            Close();
            EnsureConnected();
        }
    }

    private void Close()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}
