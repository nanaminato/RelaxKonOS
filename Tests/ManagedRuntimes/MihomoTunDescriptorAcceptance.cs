using System.Globalization;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;

internal static class MihomoTunDescriptorAcceptance
{
    [SupportedOSPlatform("linux")]
    internal static async Task RunAsync(string root)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK"))
            throw new InvalidOperationException("The descriptor test requires its private systemd work directory.");

        using var gate = new FileStream(Path.Combine(root, "tun-operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var journal = Path.Combine(root, "tun-operation.json");
        using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(journal));
        var operation = intent.RootElement.GetProperty("operation").GetString();
        if (!Guid.TryParseExact(operation, "N", out var operationId) || operationId == Guid.Empty
            || intent.RootElement.GetProperty("phase").GetString() != "prepared")
            throw new InvalidOperationException("The original descriptor operation is not prepared.");

        const string deviceName = "rk-fd-proof";
        using var descriptor = LinuxMihomoTunDescriptor.Create(operationId, deviceName);
        Refuses("runtime.network_device_unavailable", () =>
        {
            using var duplicate = LinuxMihomoTunDescriptor.Create(operationId, deviceName);
        });

        using var broker = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = Path.Combine(root, "fd.sock");
        broker.Bind(new UnixDomainSocketEndPoint(endpoint));
        File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        broker.Listen(1);
        await NativeRuntimeProcess.RunAsync("/usr/bin/python3", ["-I", Path.Combine(root, "mihomo-tun-fd-proof.py"),
            "prepare", root, descriptor.InterfaceIndex.ToString(CultureInfo.InvariantCulture)], 1024);

        using var dispatched = JsonDocument.Parse(await File.ReadAllBytesAsync(journal));
        var checkpoint = dispatched.RootElement;
        if (checkpoint.GetProperty("operation").GetString() != operation || checkpoint.GetProperty("phase").GetString() != "dispatching"
            || checkpoint.GetProperty("ifindex").GetUInt32() != descriptor.InterfaceIndex)
            throw new InvalidOperationException("The descriptor recipient and device identity are not in the original journal.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var connection = await broker.AcceptAsync(deadline.Token);
        var recipient = checkpoint.GetProperty("service")[0].GetInt32();
        Refuses("runtime.recovery_target_conflict", () => descriptor.SendTo(connection, Guid.NewGuid(), recipient, 0));
        Refuses("runtime.network_recipient_mismatch", () => descriptor.SendTo(connection, operationId, Environment.ProcessId, 0));
        descriptor.SendTo(connection, operationId, recipient, 0);
        Refuses("runtime.network_descriptor_already_dispatched", () => descriptor.SendTo(connection, operationId, recipient, 0));
        Console.WriteLine(operation);
        Console.WriteLine($"PASS HELPER NATIVE TUN DESCRIPTOR: {operation}; {descriptor.Name} ifindex={descriptor.InterfaceIndex}, receiver PID={recipient}, exclusive open and single authenticated dispatch");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static void Refuses(string code, Action action)
    {
        try { action(); }
        catch (RuntimePolicyException error) when (error.ProblemCode == code) { return; }
        throw new InvalidOperationException($"Expected native descriptor refusal {code}.");
    }
}
