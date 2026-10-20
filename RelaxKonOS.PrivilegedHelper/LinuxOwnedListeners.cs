using System.Globalization;
using System.Net;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed record LinuxOwnedListener(ulong Inode, bool Udp, IPAddress Address, int Port);

/// <summary>Parses Linux proc socket tables; callers must join socket inodes to a verified process's descriptors.</summary>
internal static class LinuxOwnedListeners
{
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static IReadOnlyList<LinuxOwnedListener> Read(int processId, DateTime expectedStartTime)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime() != expectedStartTime.ToUniversalTime()) Fail();
            var proc = $"/proc/{processId}";
            HashSet<string?> Sockets() => Directory.EnumerateFiles(proc + "/fd")
                .Select(path => new FileInfo(path).LinkTarget).Where(target => target?.StartsWith("socket:[", StringComparison.Ordinal) == true).ToHashSet();
            var before = Sockets();
            var result = new List<LinuxOwnedListener>();
            foreach (var ipv6 in new[] { false, true })
                foreach (var udp in new[] { false, true })
                {
                    var path = proc + "/net/" + (udp ? "udp" : "tcp") + (ipv6 ? "6" : "");
                    // IPv6 tables may be absent on kernels built without IPv6.
                    if (ipv6 && !File.Exists(path)) continue;
                    result.AddRange(Parse(File.ReadLines(path), ipv6, udp, BitConverter.IsLittleEndian));
                }
            var after = Sockets();
            using var current = System.Diagnostics.Process.GetProcessById(processId);
            if (current.StartTime.ToUniversalTime() != expectedStartTime.ToUniversalTime()) Fail();
            return result.Where(endpoint => before.Contains($"socket:[{endpoint.Inode}]")
                && after.Contains($"socket:[{endpoint.Inode}]")).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new RuntimePolicyException("runtime.listener_query_failed"); }
    }

    internal static IReadOnlyList<LinuxOwnedListener> Parse(IEnumerable<string> lines, bool ipv6, bool udp, bool littleEndian)
    {
        var result = new List<LinuxOwnedListener>();
        using var rows = lines.GetEnumerator();
        if (!rows.MoveNext() || !rows.Current.Contains("local_address", StringComparison.Ordinal)
            || !rows.Current.Contains("inode", StringComparison.Ordinal)) Fail();
        var count = 0;
        while (rows.MoveNext())
        {
            if (++count > 100000 || rows.Current.Length > 4096) Fail();
            var fields = rows.Current.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10) Fail();
            var local = fields[1].Split(':');
            var remote = fields[2].Split(':');
            var length = ipv6 ? 32 : 8;
            ushort port = 0; byte state = 0; ulong inode = 0;
            if (local.Length != 2 || local[0].Length != length || local[1].Length != 4
                || remote.Length != 2 || remote[0].Length != length || remote[1].Length != 4
                || !local[0].All(Uri.IsHexDigit) || !remote[0].All(Uri.IsHexDigit)
                || !ushort.TryParse(local[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out port)
                || !ushort.TryParse(remote[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
                || !byte.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out state)
                || !ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out inode)) Fail();
            // UDP state 07 is an unconnected binding. Connected UDP sockets do not prove an inbound listener.
            if (state != (udp ? 7 : 10) || inode == 0 || port == 0) continue;
            var bytes = Convert.FromHexString(local[0]);
            // proc prints each native-endian 32-bit address word, including IPv6.
            if (littleEndian)
                for (var offset = 0; offset < bytes.Length; offset += 4)
                    Array.Reverse(bytes, offset, 4);
            result.Add(new(inode, udp, new IPAddress(bytes), port));
        }
        return result;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail() => throw new RuntimePolicyException("runtime.listener_query_failed");
}


