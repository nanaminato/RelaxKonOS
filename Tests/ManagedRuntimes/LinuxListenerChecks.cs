using System.Net;
using System.Net.Sockets;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;

internal static class LinuxListenerChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Linux listener evidence mismatch"); count++; }
        const string header = "sl local_address rem_address st tx_queue rx_queue tr tm->when retrnsmt uid timeout inode";
        string Row(string address, string state = "0A", string inode = "123") =>
            $"0: {address}:1F90 {new string('0', address.Length)}:0000 {state} 00000000:00000000 00:00000000 00000000 0 0 {inode}";
        LinuxOwnedListener Parse(string address, bool v6, bool little) => LinuxOwnedListeners.Parse([header, Row(address)], v6, false, little).Single();
        Check(Parse("0100007F", false, true).Address.Equals(IPAddress.Loopback));
        Check(Parse("7F000001", false, false).Address.Equals(IPAddress.Loopback));
        Check(Parse("00000000000000000000000001000000", true, true).Address.Equals(IPAddress.IPv6Loopback));
        Check(Parse("00000000000000000000000000000001", true, false).Address.Equals(IPAddress.IPv6Loopback));
        Check(Parse("0100007F", false, true) is { Port: 8080, Inode: 123, Udp: false });
        Check(LinuxOwnedListeners.Parse([header, Row("0100007F", "01")], false, false, true).Count == 0);
        Check(LinuxOwnedListeners.Parse([header, Row("0100007F", "07")], false, true, true).Single().Udp);
        Check(LinuxOwnedListeners.Parse([header, Row("0100007F", "01")], false, true, true).Count == 0);
        Check(LinuxOwnedListeners.Parse([header, Row("0100007F", inode: "0")], false, false, true).Count == 0);
        foreach (var lines in new[] { new[] { "bad header" }, new[] { header, "truncated" }, new[] { header, Row("GG00007F") }, new[] { header, Row("0100007F", inode: "-1") } })
        {
            try { LinuxOwnedListeners.Parse(lines, false, false, true); throw new Exception("Malformed proc table accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.listener_query_failed"); }
        }
        if (OperatingSystem.IsLinux())
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var owned = LinuxOwnedListeners.Read(process.Id, process.StartTime);
            Check(owned.Any(s => !s.Udp && s.Port == ((IPEndPoint)tcp.LocalEndpoint).Port));
            Check(owned.Any(s => s.Udp && s.Port == ((IPEndPoint)udp.Client.LocalEndPoint!).Port));
            try { LinuxOwnedListeners.Read(process.Id, process.StartTime.AddSeconds(-1)); throw new Exception("Wrong process identity accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.listener_query_failed"); }
            foreach (var pair in new[] { (Udp: false, Port: ((IPEndPoint)tcp.LocalEndpoint).Port), (Udp: true, Port: ((IPEndPoint)udp.Client.LocalEndPoint!).Port) })
            {
                var sockets = Directory.EnumerateFiles("/proc/self/fd").Select(p => new FileInfo(p).LinkTarget).ToHashSet();
                var table = LinuxOwnedListeners.Parse(File.ReadLines(pair.Udp ? "/proc/self/net/udp" : "/proc/self/net/tcp"), false, pair.Udp, BitConverter.IsLittleEndian);
                Check(table.Any(s => s.Port == pair.Port && s.Address.Equals(IPAddress.Loopback) && sockets.Contains($"socket:[{s.Inode}]")));
            }
        }
        return count;
    }
}
