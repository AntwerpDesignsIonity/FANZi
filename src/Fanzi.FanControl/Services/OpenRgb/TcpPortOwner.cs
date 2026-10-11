using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Fanzi.FanControl.Services.OpenRgb;

/// <summary>Finds which process listens on a local IPv4 TCP port (GetExtendedTcpTable, no netstat).</summary>
internal static class TcpPortOwner
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;

    /// <summary>PID listening on <paramref name="port"/> (any IPv4 address), or null when none / unknown.</summary>
    public static int? GetListeningPid(int port) => Query(port, out _);

    /// <summary>
    /// True / false when the IPv4 listener table could be read, null when it could not (non-Windows, API error).
    /// Instant and connection-free: no thread pool, no TIME_WAIT, no 2 s refused-loopback retry delay.
    /// </summary>
    public static bool? IsListening(int port)
    {
        int? pid = Query(port, out bool ok);
        return ok ? pid is not null : null;
    }

    private static int? Query(int port, out bool ok)
    {
        ok = false;
        if (!OperatingSystem.IsWindows()) return null;
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0) return null;
            ok = true;
            int count = Marshal.ReadInt32(buffer);
            const int rowSize = 24; // state, localAddr, localPort, remoteAddr, remotePort, pid (6 × DWORD)
            for (int i = 0; i < count; i++)
            {
                IntPtr row = buffer + 4 + i * rowSize;
                int rawPort = Marshal.ReadInt32(row + 8);
                // Port is stored in network byte order in the low 16 bits.
                int localPort = BinaryPrimitives.ReverseEndianness((ushort)(rawPort & 0xFFFF));
                if (localPort == port) return Marshal.ReadInt32(row + 20);
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);
}
