using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;

internal static class WindowsHostChecks
{
    internal static Task<int> RunAsync() => OperatingSystem.IsWindows() ? RunWindowsAsync() : Task.FromResult(0);

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Reject(string code, Action action)
        {
            try { action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected Windows host rejection: " + code);
        }
        foreach (var ipv6 in new[] { false, true })
            foreach (var udp in new[] { false, true })
            {
                var size = ipv6 ? udp ? 28 : 56 : udp ? 12 : 24;
                var bytes = new byte[4 + size];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
                var row = bytes.AsSpan(4);
                var address = ipv6 ? IPAddress.Parse("fe80::1%7") : IPAddress.Loopback;
                address.GetAddressBytes().CopyTo(row[(ipv6 || udp ? 0 : 4)..]);
                if (ipv6) BinaryPrimitives.WriteUInt32BigEndian(row[16..], 7);
                if (!udp) BinaryPrimitives.WriteUInt32LittleEndian(row[(ipv6 ? 48 : 0)..], 2);
                BinaryPrimitives.WriteUInt16BigEndian(row[(ipv6 ? 20 : udp ? 4 : 8)..], 24443);
                BinaryPrimitives.WriteUInt32LittleEndian(row[(ipv6 ? udp ? 24 : 52 : udp ? 8 : 20)..], 1234);
                var listener = WindowsOwnedListeners.Parse(bytes, ipv6, udp).Single();
                Check(listener.ProcessId == 1234 && listener.Udp == udp && listener.Port == 24443 && listener.Address.Equals(address),
                    "PID, network-order port and address/scope parsed: " + ipv6 + "/" + udp);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
                Reject("listener_query_failed", () => WindowsOwnedListeners.Parse(bytes, ipv6, udp));
            }
        Reject("listener_query_failed", () => WindowsOwnedListeners.Parse([], false, false));
        var established = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(established, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(established.AsSpan(4), 5);
        Check(WindowsOwnedListeners.Parse(established, false, false).Count == 0, "Established connections cannot satisfy listener health");
        using (var tcp = new TcpListener(IPAddress.Loopback, 0))
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            tcp.Start();
            var tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
            var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var actual = WindowsOwnedListeners.Read();
            Check(actual.Any(l => l.ProcessId == Environment.ProcessId && !l.Udp && l.Port == tcpPort && l.Address.Equals(IPAddress.Loopback)),
                "Actual Windows TCP table binds temporary listener to this process");
            Check(actual.Any(l => l.ProcessId == Environment.ProcessId && l.Udp && l.Port == udpPort && l.Address.Equals(IPAddress.Loopback)),
                "Actual Windows UDP table binds temporary endpoint to this process");
        }
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-windows-host-policy-" + Guid.NewGuid().ToString("N"));
        var revision = Guid.NewGuid().ToString("N");
        var binary = Path.Combine(root, "versions", revision, "package", "sing-box.exe");
        var config = Path.Combine(root, "config.json");
        var identity = new string('a', 64);
        WindowsSingBoxArchiveHost.ValidatePaths(root, binary, config, identity);
        Check(true, "Only fixed configuration and scoped immutable binary paths accepted");
        foreach (var path in new[] { Path.Combine(root, "sing-box.exe"), Path.Combine(root + "-neighbor", "versions", revision, "sing-box.exe"),
            Path.Combine(root, "versions", Guid.Empty.ToString("N"), "sing-box.exe"), Path.Combine(root, "versions", revision, "other.exe") })
            Reject("service_scope_invalid", () => WindowsSingBoxArchiveHost.ValidatePaths(root, path, config, identity));
        Reject("service_scope_invalid", () => WindowsSingBoxArchiveHost.ValidatePaths(root, binary, Path.Combine(root, "previous.json"), identity));
        Reject("service_scope_invalid", () => WindowsSingBoxArchiveHost.ValidatePaths(root, binary, config, "malformed"));
        Reject("service_scope_invalid", () => WindowsSingBoxArchiveHost.ValidatePaths(Path.GetPathRoot(root)!, binary, config, identity));
        var state = new ComponentProcessState(1234, DateTime.UtcNow, false, false, [], identity);
        using (var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(state)))
            Check(WindowsComponentService.ReadProcessState(input).AppliedIdentity == identity,
                "Process-start proof survives current state contract serialization");
        var old = JsonSerializer.SerializeToNode(state)!.AsObject(); old.Remove("AppliedIdentity");
        try
        {
            using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(old));
            WindowsComponentService.ReadProcessState(input);
            throw new Exception("Missing process proof accepted");
        }
        catch (JsonException) { count++; }
        var launchRoot = Path.Combine(Path.GetTempPath(), "relaxkonos-component-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(launchRoot);
        try
        {
            var executable = Path.Combine(launchRoot, "sing-box.exe");
            var configuration = Path.Combine(launchRoot, "config.json");
            await File.WriteAllBytesAsync(executable, [1, 2, 3]);
            await File.WriteAllBytesAsync(configuration, [4, 5, 6]);
            var binaryHash = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
            var configHash = Convert.ToHexString(SHA256.HashData([4, 5, 6]));
            var registration = new ComponentServiceConfiguration(ComponentKind.SingBox, WindowsComponentService.Name(ComponentKind.SingBox, launchRoot),
                launchRoot, executable, configuration, null, binaryHash, configHash);
            await WindowsComponentService.ValidateLaunchAsync(registration, default);
            Check(true, "Worker accepts matching program/configuration proof before launching");
            await File.WriteAllBytesAsync(configuration, [7, 8, 9]);
            try { await WindowsComponentService.ValidateLaunchAsync(registration, default); throw new Exception("Configuration drift accepted"); }
            catch (InvalidDataException) { count++; }
            registration = registration with { AppliedIdentity = Convert.ToHexString(SHA256.HashData([7, 8, 9])) };
            await WindowsComponentService.ValidateLaunchAsync(registration, default);
            Check(true, "Only explicitly reconfigured content can be launched with its new identity");
            try { await WindowsComponentService.ValidateLaunchAsync(registration with { AppliedIdentity = null }, default); throw new Exception("Missing identity accepted"); }
            catch (InvalidDataException) { count++; }
            await File.WriteAllBytesAsync(executable, [9, 9, 9]);
            try { await WindowsComponentService.ValidateLaunchAsync(registration, default); throw new Exception("Binary drift accepted"); }
            catch (InvalidDataException) { count++; }
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(launchRoot)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Launch fixture cleanup escaped temp root");
            Directory.Delete(launchRoot, true);
        }
        return count;
    }
}
