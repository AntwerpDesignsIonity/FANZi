using System.Buffers.Binary;
using System.Text;
using Fanzi.FanControl.Services.Rgb;

namespace Fanzi.FanControl.Tests;

public class OpenRgbModeWriterTests
{
    // ZOTAC RTX 3080 "Static" exactly as OpenRGB.NET reports it (direction 0xFFFFFFFF = "None").
    private static readonly OpenRgbModeSnapshot ZotacStatic = new(
        Name: "Static", Value: 0, Flags: 592,
        SpeedMin: 0, SpeedMax: 0, BrightnessMin: 0, BrightnessMax: 100,
        ColorMin: 1, ColorMax: 1, Speed: 0, Brightness: 100,
        Direction: uint.MaxValue, ColorMode: 2,
        Colors: new (byte, byte, byte)[] { (255, 0, 180) });

    [Theory]
    [InlineData(592u, uint.MaxValue, 0u)]   // no direction flags + OpenRGB.NET "None" → 0 (the v3 fix)
    [InlineData(592u, 3u, 0u)]              // no direction flags → always 0
    [InlineData(2u, 1u, 1u)]                // HasDirectionLR keeps Right
    [InlineData(8u, 5u, 5u)]                // HasDirectionHV keeps Vertical
    [InlineData(4u, uint.MaxValue, 0u)]     // flagged but unset → 0
    public void Direction_is_normalised_for_openrgb_1_0(uint flags, uint direction, uint expected)
    {
        Assert.Equal(expected, OpenRgbModeWriter.NormalizeDirection(flags, direction));
    }

    [Fact]
    public void UpdateMode_packet_matches_openrgb_protocol_4_layout()
    {
        byte[] p = OpenRgbModeWriter.BuildUpdateModePacket(deviceIndex: 0, modeIndex: 0, ZotacStatic);
        var s = p.AsSpan();

        // Header: "ORGB", device index, packet id 1101, payload size.
        Assert.Equal("ORGB", Encoding.ASCII.GetString(p, 0, 4));
        Assert.Equal(0u, U32(s, 4));
        Assert.Equal(1101u, U32(s, 8));
        uint payload = U32(s, 12);
        Assert.Equal((uint)(p.Length - 16), payload);

        int o = 16;
        Assert.Equal(payload, U32(s, o)); o += 4;              // data_size == payload (passes the size check)
        Assert.Equal(0u, U32(s, o)); o += 4;                   // mode index
        ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o)); o += 2;
        Assert.Equal(7, nameLen);                              // "Static" + NUL
        Assert.Equal("Static\0", Encoding.UTF8.GetString(p, o, nameLen)); o += nameLen;
        Assert.Equal(0u, U32(s, o)); o += 4;                   // value
        Assert.Equal(592u, U32(s, o)); o += 4;                 // flags
        o += 8;                                                // speed min/max
        Assert.Equal(0u, U32(s, o)); Assert.Equal(100u, U32(s, o + 4)); o += 8;   // brightness min/max
        Assert.Equal(1u, U32(s, o)); Assert.Equal(1u, U32(s, o + 4)); o += 8;     // colours min/max
        o += 4;                                                // speed
        Assert.Equal(100u, U32(s, o)); o += 4;                 // brightness
        Assert.Equal(0u, U32(s, o)); o += 4;                   // direction normalised from 0xFFFFFFFF
        Assert.Equal(2u, U32(s, o)); o += 4;                   // colour mode: mode-specific
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o))); o += 2;
        Assert.Equal(new byte[] { 255, 0, 180, 0 }, p.Skip(o).Take(4).ToArray()); o += 4;
        Assert.Equal(p.Length, o);
    }

    [Fact]
    public void Packet_header_is_little_endian()
    {
        byte[] p = OpenRgbModeWriter.Packet(3, 40, new byte[] { 4, 0, 0, 0 });
        Assert.Equal(new byte[] { (byte)'O', (byte)'R', (byte)'G', (byte)'B', 3, 0, 0, 0, 40, 0, 0, 0, 4, 0, 0, 0, 4, 0, 0, 0 }, p);
    }

    private static uint U32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset));
}
