using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;

internal static class MihomoTunDescriptorAcceptance
{
    private const string DeviceName = "rk-fd-proof";

    [SupportedOSPlatform("linux")]
    internal static async Task RunAsync(string root)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK"))
            throw new InvalidOperationException("The descriptor test requires its private systemd work directory.");

        using var gate = new RuntimeArchiveStateStore(Path.Combine(LinuxMihomoArchiveHost.Root, "state"), RuntimeSourceProtection.Protect).Acquire();
        var journal = Path.Combine(root, "tun-operation.json");
        using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(journal));
        var operation = intent.RootElement.GetProperty("operation").GetString();
        if (!Guid.TryParseExact(operation, "N", out var operationId) || operationId == Guid.Empty
            || intent.RootElement.GetProperty("phase").GetString() != "prepared")
            throw new InvalidOperationException("The original descriptor operation is not prepared.");

        var intentRoot = intent.RootElement;
        var frozen = new MihomoTunIntent(operationId, intentRoot.GetProperty("revision").GetGuid(),
            intentRoot.GetProperty("credential").GetString()!, LinuxMihomoTunCheckpoint.BootId(),
            LinuxMihomoTunCheckpoint.NetworkNamespace(), DeviceName, intentRoot.GetProperty("address").GetString()!,
            intentRoot.GetProperty("target").GetString()!, intentRoot.GetProperty("management").GetString()!, ManagementHash(intentRoot));
        var archive = LinuxMihomoArchiveHost.Root;
        var binary = Path.Combine(archive, "versions", frozen.RevisionId.ToString("N"), "mihomo");
        var data = Path.Combine(archive, "data", frozen.RevisionId.ToString("N"));
        var host = new LinuxMihomoArchiveHost();
        var effect = await host.ActivateTunAsync(frozen, binary, data, CancellationToken.None);
        await RefusesAsync("runtime.network_checkpoint_conflict", () => host.DispatchTunStartAsync(operationId, CancellationToken.None));
        await RefusesAsync("runtime.network_checkpoint_conflict", () => host.ActivateTunAsync(frozen, binary, data, CancellationToken.None));
        await NativeRuntimeProcess.RunAsync("/usr/bin/python3", ["-I", Path.Combine(root, "mihomo-tun-fd-proof.py"),
            "prepare", root, effect.InterfaceIndex.ToString(CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], 1024);
        Console.WriteLine(operation);
        Console.WriteLine($"PASS HELPER NATIVE TUN DESCRIPTOR: {operation}; {frozen.Device} ifindex={effect.InterfaceIndex}, receiver PID={effect.Process!.Pid}, authenticated single dispatch and duplicate activation refused");
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
        var ledger = Ledger(operation, state);
        var current = ledger.Read() ?? throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        if (current.Intent.BootId != LinuxMihomoTunCheckpoint.BootId()
            || current.Intent.NetworkNamespace != LinuxMihomoTunCheckpoint.NetworkNamespace()
            || current.Intent.ManagementHash != ManagementHash(state) || current.Process != Process(state)
            || current.InterfaceIndex != state.GetProperty("ifindex").GetUInt32())
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        var host = new LinuxMihomoArchiveHost();
        if (action == "state") Console.WriteLine(current.Phase);
        else if (action == "observe") await host.VerifyTunAsync(operation, CancellationToken.None);
        else if (action == "stop") await host.DispatchTunStopAsync(operation, CancellationToken.None);
        else if (action == "restored") await host.CompleteTunStopAsync(operation, CancellationToken.None);
        else throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
    }

    [SupportedOSPlatform("linux")]
    private static LinuxMihomoTunCheckpoint Ledger(Guid operation, JsonElement intent) =>
        new(LinuxMihomoArchiveHost.Root, operation, intent.GetProperty("revision").GetGuid(), intent.GetProperty("credential").GetString()!);

    private static string ManagementHash(JsonElement intent) =>
        MihomoArchiveAdapter.Hash(JsonSerializer.SerializeToUtf8Bytes(intent.GetProperty("baseline")));

    private static MihomoTunProcess Process(JsonElement checkpoint)
    {
        var service = checkpoint.GetProperty("service");
        return new(service[0].GetInt32(), Guid.ParseExact(service[1].GetString()!, "N"),
            ulong.Parse(service[2].GetString()!, CultureInfo.InvariantCulture));
    }

    private static async Task RefusesAsync(string code, Func<Task> action)
    {
        try { await action(); }
        catch (RuntimePolicyException error) when (error.ProblemCode == code) { return; }
        throw new InvalidOperationException($"Expected native TUN refusal {code}.");
    }
}