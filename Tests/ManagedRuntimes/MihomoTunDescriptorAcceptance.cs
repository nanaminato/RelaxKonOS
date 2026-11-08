using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;

internal static class MihomoTunDescriptorAcceptance
{
    private const string DeviceName = "rk-fd-proof";
    private const int InheritedDescriptor = 198;

    [SupportedOSPlatform("linux")]
    internal static async Task RunAsync(string root)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK"))
            throw new InvalidOperationException("The descriptor test requires its private systemd work directory.");

        using var gate = new RuntimeArchiveStateStore(Path.Combine(root, "state"), RuntimeSourceProtection.Protect).Acquire();
        var journal = Path.Combine(root, "tun-operation.json");
        using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(journal));
        var operation = intent.RootElement.GetProperty("operation").GetString();
        if (!Guid.TryParseExact(operation, "N", out var operationId) || operationId == Guid.Empty
            || intent.RootElement.GetProperty("phase").GetString() != "prepared")
            throw new InvalidOperationException("The original descriptor operation is not prepared.");

        var intentRoot = intent.RootElement;
        var ledger = Ledger(root, operationId, intentRoot);
        var frozen = new MihomoTunIntent(operationId, intentRoot.GetProperty("revision").GetGuid(),
            intentRoot.GetProperty("credential").GetString()!, LinuxMihomoTunCheckpoint.BootId(),
            LinuxMihomoTunCheckpoint.NetworkNamespace(), DeviceName, intentRoot.GetProperty("address").GetString()!,
            intentRoot.GetProperty("target").GetString()!, ManagementHash(intentRoot));
        var initial = new MihomoTunEffect(MihomoTunPhase.Intent, frozen);
        await ledger.AppendAsync(initial);
        using var descriptor = LinuxMihomoTunDescriptor.Create(operationId, DeviceName);
        Refuses("runtime.network_device_unavailable", () =>
        {
            using var duplicate = LinuxMihomoTunDescriptor.Create(operationId, DeviceName);
        });
        var network = initial with { Phase = MihomoTunPhase.NetworkDispatched, InterfaceIndex = descriptor.InterfaceIndex };
        await ledger.AppendAsync(network);
        var routes = Routes(frozen);
        await routes.ApplyAsync(descriptor);
        try
        {
            await routes.ApplyAsync(descriptor);
            throw new InvalidOperationException("The owned TUN route was applied twice.");
        }
        catch (RuntimePolicyException error) when (error.ProblemCode == "runtime.network_route_conflict") { }

        using var broker = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = Path.Combine(root, "fd.sock");
        broker.Bind(new UnixDomainSocketEndPoint(endpoint));
        File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        broker.Listen(1);
        await NativeRuntimeProcess.RunAsync("/usr/bin/python3", ["-I", Path.Combine(root, "mihomo-tun-fd-proof.py"),
            "prepare", root, descriptor.InterfaceIndex.ToString(CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], 1024);

        using var dispatched = JsonDocument.Parse(await File.ReadAllBytesAsync(journal));
        var checkpoint = dispatched.RootElement;
        if (checkpoint.GetProperty("operation").GetString() != operation || checkpoint.GetProperty("phase").GetString() != "dispatching"
            || checkpoint.GetProperty("ifindex").GetUInt32() != descriptor.InterfaceIndex
            || checkpoint.GetProperty("broker").GetInt32() != Environment.ProcessId)
            throw new InvalidOperationException("The descriptor recipient and device identity are not in the original journal.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var connection = await broker.AcceptAsync(deadline.Token);
        var recipient = checkpoint.GetProperty("service")[0].GetInt32();
        await ledger.AppendAsync(network with { Phase = MihomoTunPhase.DescriptorDispatched, Process = Process(checkpoint) });
        Refuses("runtime.recovery_target_conflict", () => descriptor.SendTo(connection, Guid.NewGuid(), recipient, 0));
        Refuses("runtime.network_recipient_mismatch", () => descriptor.SendTo(connection, operationId, Environment.ProcessId, 0));
        descriptor.SendTo(connection, operationId, recipient, 0);
        Refuses("runtime.network_descriptor_already_dispatched", () => descriptor.SendTo(connection, operationId, recipient, 0));
        Console.WriteLine(operation);
        Console.WriteLine($"PASS HELPER NATIVE TUN DESCRIPTOR: {operation}; {descriptor.Name} ifindex={descriptor.InterfaceIndex}, receiver PID={recipient}, exclusive open and single authenticated dispatch");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    [SupportedOSPlatform("linux")]
    internal static async Task CheckpointAsync(string root, Guid operation, string action)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK"))
            throw new InvalidOperationException("The network check requires its private systemd work directory.");
        using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "tun-operation.json")));
        var state = intent.RootElement;
        if (operation == Guid.Empty || state.GetProperty("operation").GetString() != operation.ToString("N")
            || state.GetProperty("phase").GetString() != "dispatching")
            throw new InvalidOperationException("The network check requires the original dispatched operation.");
        var ledger = Ledger(root, operation, state);
        var current = ledger.Read() ?? throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        if (current.Intent.BootId != LinuxMihomoTunCheckpoint.BootId()
            || current.Intent.NetworkNamespace != LinuxMihomoTunCheckpoint.NetworkNamespace()
            || current.Intent.ManagementHash != ManagementHash(state) || current.Process != Process(state)
            || current.InterfaceIndex != state.GetProperty("ifindex").GetUInt32())
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        if (action == "state") Console.WriteLine(current.Phase);
        else if (action == "observe" && current.Phase == MihomoTunPhase.DescriptorDispatched)
            await Routes(current.Intent).VerifyAsync(operation, current.Intent.Device, current.InterfaceIndex);
        else if (action == "stop" && current.Phase == MihomoTunPhase.DescriptorDispatched)
        {
            await Routes(current.Intent).VerifyAsync(operation, current.Intent.Device, current.InterfaceIndex);
            await ledger.AppendAsync(current with { Phase = MihomoTunPhase.StopDispatched });
        }
        else if (action == "restored" && current.Phase == MihomoTunPhase.StopDispatched)
            await ledger.AppendAsync(current with { Phase = MihomoTunPhase.Restored });
        else throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
    }

    [SupportedOSPlatform("linux")]
    private static LinuxMihomoTunCheckpoint Ledger(string root, Guid operation, JsonElement intent) =>
        new(root, operation, intent.GetProperty("revision").GetGuid(), intent.GetProperty("credential").GetString()!);

    private static string ManagementHash(JsonElement intent) =>
        MihomoArchiveAdapter.Hash(JsonSerializer.SerializeToUtf8Bytes(intent.GetProperty("baseline")));

    private static MihomoTunProcess Process(JsonElement checkpoint)
    {
        var service = checkpoint.GetProperty("service");
        return new(service[0].GetInt32(), Guid.ParseExact(service[1].GetString()!, "N"),
            ulong.Parse(service[2].GetString()!, CultureInfo.InvariantCulture));
    }

    [SupportedOSPlatform("linux")]
    private static LinuxMihomoTunRoutes Routes(MihomoTunIntent intent) =>
        new(IPAddress.Parse(intent.Address), IPAddress.Parse(intent.Destination));

    [SupportedOSPlatform("linux")]
    internal static async Task LaunchAsync(string root)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK"))
            throw new InvalidOperationException("The descriptor receiver requires its private systemd work directory.");
        var credential = Path.Combine(Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY")
            ?? throw new InvalidOperationException("systemd credential is missing."), "active.yaml");
        using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "tun-operation.json")));
        if (!Guid.TryParseExact(intent.RootElement.GetProperty("operation").GetString(), "N", out var owner)
            || owner == Guid.Empty || intent.RootElement.GetProperty("phase").GetString() is not ("prepared" or "dispatching"))
            throw new InvalidOperationException("The descriptor receiver has no original prepared operation.");
        var broker = intent.RootElement.GetProperty("broker").GetInt32();
        var ifindex = intent.RootElement.GetProperty("ifindex").GetUInt32();
        using var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await connection.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(root, "fd.sock")));
        Refuses("runtime.network_broker_mismatch", () =>
        {
            using var rejected = LinuxMihomoTunDescriptor.ReceiveFrom(connection, owner, Environment.ProcessId, 0, DeviceName, ifindex);
        });
        using var descriptor = LinuxMihomoTunDescriptor.ReceiveFrom(connection, owner, broker, 0, DeviceName, ifindex);
        connection.Dispose();
        ExecMihomo(descriptor, Path.Combine(root, "mihomo"), Path.Combine(root, "mihomo-data"), credential);
    }

    private static void ExecMihomo(SafeFileHandle descriptor, string binary, string data, string credential)
    {
        var arguments = new[] { binary, "-d", data, "-f", credential };
        var pointers = new nint[arguments.Length];
        var vector = Marshal.AllocHGlobal((pointers.Length + 1) * nint.Size);
        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                pointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                Marshal.WriteIntPtr(vector, index * nint.Size, pointers[index]);
            }
            Marshal.WriteIntPtr(vector, pointers.Length * nint.Size, 0);
            const int duplicateDescriptor = 0; // F_DUPFD creates an inherited descriptor without clobbering an open slot.
            var inherited = Fcntl(descriptor, duplicateDescriptor, InheritedDescriptor);
            using var inheritedHandle = new SafeFileHandle(inherited, ownsHandle: true);
            if (inherited != InheritedDescriptor) throw new InvalidOperationException("The reserved Mihomo descriptor is unavailable.");
            Exec(pointers[0], vector);
            throw new InvalidOperationException("The isolated Mihomo executable did not start.");
        }
        finally
        {
            foreach (var pointer in pointers) Marshal.FreeCoTaskMem(pointer);
            Marshal.FreeHGlobal(vector);
        }
    }

    private static void Refuses(string code, Action action)
    {
        try { action(); }
        catch (RuntimePolicyException error) when (error.ProblemCode == code) { return; }
        throw new InvalidOperationException($"Expected native descriptor refusal {code}.");
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(SafeFileHandle descriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int Exec(nint path, nint arguments);
}
