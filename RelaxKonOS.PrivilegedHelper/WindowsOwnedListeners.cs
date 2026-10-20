using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed record WindowsOwnedListener(int ProcessId, bool Udp, IPAddress Address, int Port);

/// <summary>PID-owned TCP LISTEN and UDP bindings from bounded Windows IP Helper tables.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsOwnedListeners
{
    internal static IReadOnlyList<WindowsOwnedListener> Read()
    {
        var result = new List<WindowsOwnedListener>();
        foreach (var ipv6 in new[] { false, true })
            foreach (var udp in new[] { false, true }) result.AddRange(Parse(ReadTable(ipv6, udp), ipv6, udp));
        return result;
    }

    internal static IReadOnlyList<WindowsOwnedListener> Parse(byte[] table, bool ipv6, bool udp)
    {
        var rowSize = ipv6 ? udp ? 28 : 56 : udp ? 12 : 24;
        if (table.Length < 4) throw new RuntimePolicyException("runtime.listener_query_failed");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > 100000 || count > (table.Length - 4) / rowSize) throw new RuntimePolicyException("runtime.listener_query_failed");
        var result = new List<WindowsOwnedListener>();
        for (var i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + i * rowSize, rowSize);
            if (!udp && BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv6 ? 48 : 0)..]) != 2) continue;
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv6 ? udp ? 24 : 52 : udp ? 8 : 20)..]);
            if (pid == 0 || pid > int.MaxValue) continue;
            var port = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv6 ? 20 : udp ? 4 : 8)..]);
            var address = ipv6 ? new IPAddress(row[..16], BinaryPrimitives.ReadUInt32BigEndian(row[16..]))
                : new IPAddress(row.Slice(udp ? 0 : 4, 4));
            result.Add(new((int)pid, udp, address, port));
        }
        return result;
    }

    private static byte[] ReadTable(bool ipv6, bool udp)
    {
        uint size = 0;
        var family = ipv6 ? 23u : 2u;
        uint Query(IntPtr pointer, ref uint length) => udp
            ? GetExtendedUdpTable(pointer, ref length, false, family, 1, 0)
            : GetExtendedTcpTable(pointer, ref length, false, family, 3, 0);
        var error = Query(IntPtr.Zero, ref size);
        if (error is not (0 or 122)) throw new RuntimePolicyException("runtime.listener_query_failed");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size is < 4 or > 8 * 1024 * 1024) throw new RuntimePolicyException("runtime.listener_query_failed");
            var allocated = size;
            var pointer = Marshal.AllocHGlobal((int)allocated);
            try
            {
                error = Query(pointer, ref size);
                if (error == 122) continue;
                if (error != 0 || size > allocated) throw new RuntimePolicyException("runtime.listener_query_failed");
                var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        throw new RuntimePolicyException("runtime.listener_query_failed");
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
}
