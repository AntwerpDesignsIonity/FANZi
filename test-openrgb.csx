#!/usr/bin/env dotnet-script
// Quick OpenRGB protocol test
using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

var tcp = new TcpClient();
tcp.Connect("127.0.0.1", 6742);
Console.WriteLine("TCP connected OK");

var stream = tcp.GetStream();
stream.ReadTimeout = 3000;

// Send OpenRGB protocol handshake: "OPEN" + uint32 protocol version
var magic = Encoding.ASCII.GetBytes("OPEN");
var version = BitConverter.GetBytes((uint)4);
var handshake = new byte[magic.Length + version.Length];
Buffer.BlockCopy(magic, 0, handshake, 0, magic.Length);
Buffer.BlockCopy(version, 0, handshake, magic.Length, version.Length);
stream.Write(handshake, 0, handshake.Length);
Console.WriteLine($"Sent handshake: OPEN v4 ({handshake.Length} bytes)");

Thread.Sleep(1000);

// Read response
var buf = new byte[256];
try
{
    int read = stream.Read(buf, 0, 256);
    Console.WriteLine($"Received {read} bytes");

    // Check for "ORGB" magic in response
    if (read >= 4)
    {
        string responseMagic = Encoding.ASCII.GetString(buf, 0, 4);
        Console.WriteLine($"Response magic: '{responseMagic}'");

        if (read >= 8)
        {
            uint serverVersion = BitConverter.ToUInt32(buf, 4);
            Console.WriteLine($"Server protocol version: {serverVersion}");
        }
    }

    // Print hex dump
    Console.Write("Hex: ");
    for (int i = 0; i < Math.Min(read, 32); i++)
        Console.Write($"{buf[i]:X2} ");
    Console.WriteLine();
}
catch (Exception ex)
{
    Console.WriteLine($"Read failed: {ex.Message}");
}

tcp.Close();
Console.WriteLine("Done");
