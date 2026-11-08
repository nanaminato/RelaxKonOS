using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class MihomoTunDescriptorAcceptance
{
    private const string DeviceName = "rk-fd-proof";
    private static string Archive => LinuxMihomoArchiveHost.Root;
    private static RuntimeArchiveStateStore Store() => new(Path.Combine(Archive, "state"), RuntimeSourceProtection.Protect);

    [SupportedOSPlatform("linux")]
    internal static async Task RunAsync(string root)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK"))
            throw new InvalidOperationException("The descriptor test requires its private systemd work directory.");

        var journal = Path.Combine(root, "tun-operation.json");
        using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(journal));
        var operation = intent.RootElement.GetProperty("operation").GetString();
        if (!Guid.TryParseExact(operation, "N", out var operationId) || operationId == Guid.Empty
            || intent.RootElement.GetProperty("phase").GetString() != "prepared")
            throw new InvalidOperationException("The original descriptor operation is not prepared.");

        var intentRoot = intent.RootElement;
        var frozen = new MihomoTunIntent(operationId, intentRoot.GetProperty("revision").GetGuid(),
            intentRoot.GetProperty("credential").GetString()!.ToUpperInvariant(), LinuxMihomoTunCheckpoint.BootId(),
            LinuxMihomoTunCheckpoint.NetworkNamespace(), DeviceName, intentRoot.GetProperty("address").GetString()!,
            intentRoot.GetProperty("target").GetString()!, intentRoot.GetProperty("management").GetString()!, ManagementHash(intentRoot));
        var adapter = new ArchiveAdapter(root, frozen.RevisionId, frozen);
        var previous = MihomoArchiveAdapter.Hash(await File.ReadAllBytesAsync(adapter.Previous));
        await adapter.InstallConfigurationAsync(adapter.Previous, previous);
        var store = Store();
        var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
        var before = store.Initialize(new(Guid.NewGuid(), "test-host", "mihomo", 0, RuntimeOwnership.Absent, target, null, false), previous);
        var binary = await File.ReadAllBytesAsync(adapter.Binary);
        var digest = MihomoArchiveAdapter.Hash(binary);
        var artifact = new RuntimeArtifact("fixture", digest, binary.Length, "gz", "mihomo", "signed-manifest:fixture",
            new(binary.Length, 1, [new("mihomo", binary.Length, digest, true)]));
        var revision = new RuntimeStoredRevision(frozen.RevisionId, new("fixture-release", "mihomo", "v1.19.33", "1.19.33",
            RuntimeChannel.Stable, "fixture-source", target, artifact, "mihomo-v1", true, RuntimeTrust.Confirmed,
            RuntimeEvidence.NativeConfiguration, DateTimeOffset.UtcNow), "1.19.33", previous, 1, 1);
        using (var session = store.Acquire())
            session.Save(before with { Active = revision, Revisions = [revision], Installation = before.Installation with
                { StateRevision = 1, Ownership = RuntimeOwnership.Managed, ActiveReleaseId = revision.Release.ReleaseId } });
        var plan = new RuntimeConfigurationPlan(before.Installation.InstallationId, before.Installation.HostId, "mihomo",
            1, frozen.RevisionId, previous, frozen.ConfigurationHash, "tun-publication");
        await File.WriteAllBytesAsync(Path.Combine(root, "tun-plan.json"), RuntimeJson.Serialize(plan));
        var outcome = await new RuntimeArchiveConfiguration(store, adapter).ExecuteAsync(operationId, plan, CancellationToken.None);
        throw new InvalidOperationException($"The fixture exited before interruption: {outcome.Outcome}, {outcome.ProblemCode}, {store.Read()?.Configuration?.RecoveryProblemCode}.");
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
        else if (action is "retry" or "recover" or "recover-interrupt")
        {
            var plan = RuntimeJson.Read<RuntimeConfigurationPlan>(await File.ReadAllBytesAsync(Path.Combine(root, "tun-plan.json")));
            var adapter = new ArchiveAdapter(root, plan.ActiveRevisionId, intent: null, interruptStop: action == "recover-interrupt");
            var transaction = new RuntimeArchiveConfiguration(Store(), adapter);
            var result = action == "retry"
                ? await transaction.ExecuteAsync(Guid.NewGuid(), plan, CancellationToken.None)
                : await transaction.RecoverAsync(operation, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
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

    [SupportedOSPlatform("linux")]
    private sealed class ArchiveAdapter(string root, Guid revision, MihomoTunIntent? intent, bool interruptStop = false)
        : IRuntimeArchiveConfigurationAdapter
    {
        private readonly LinuxMihomoArchiveHost host = new();
        internal string Previous => Path.Combine(root, "previous.yaml");
        internal string Binary => Path.Combine(Archive, "versions", revision.ToString("N"), "mihomo");
        private string Data => Path.Combine(Archive, "data", revision.ToString("N"));
        private static string Configuration => Path.Combine(Archive, "active.yaml");

        internal async Task InstallConfigurationAsync(string source, string hash)
        {
            await CopyConfigurationAsync(source, hash);
            await host.ConfigureAsync(Binary, Data, Configuration, hash, false, CancellationToken.None);
        }

        private static async Task CopyConfigurationAsync(string source, string hash)
        {
            var content = await File.ReadAllBytesAsync(source);
            if (MihomoArchiveAdapter.Hash(content) != hash) throw new RuntimePolicyException("runtime.configuration_drift");
            await File.WriteAllBytesAsync(Configuration, content);
            File.SetUnixFileMode(Configuration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public Task PreflightAsync(RuntimeConfigurationPlan plan, RuntimeArchiveState current, CancellationToken ct) =>
            ValidateRestoreAsync(current.Configuration!, ct);
        public Task<RuntimeServiceSnapshot> CaptureAsync(Guid operation, RuntimeArchiveState current, CancellationToken ct) =>
            Task.FromResult(new RuntimeServiceSnapshot(false, false, current.ConfigurationIdentity, operation, operation));
        public Task PrepareAsync(RuntimePendingConfiguration pending, CancellationToken ct) =>
            RequireHashAsync(Path.Combine(root, "candidate.yaml"), pending.Plan.CandidateIdentity);
        public Task ValidateSwitchAsync(RuntimePendingConfiguration pending, CancellationToken ct) =>
            RequireHashAsync(Configuration, pending.Plan.PreviousIdentity);

        public async Task ActivateAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        {
            if (intent is null || intent.OperationId != pending.OperationId || intent.RevisionId != pending.Plan.ActiveRevisionId
                || intent.ConfigurationHash != pending.Plan.CandidateIdentity)
                throw new RuntimePolicyException("runtime.authorization_changed");
            await CopyConfigurationAsync(Path.Combine(root, "candidate.yaml"), pending.Plan.CandidateIdentity);
            MihomoTunEffect effect;
            try { effect = await host.ActivateTunAsync(intent, Binary, Data, ct); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                throw;
            }
            await RefusesAsync("runtime.network_checkpoint_conflict", () => host.DispatchTunStartAsync(pending.OperationId, ct));
            await NativeRuntimeProcess.RunAsync("/usr/bin/python3", ["-I", Path.Combine(root, "mihomo-tun-fd-proof.py"),
                "prepare", root, effect.InterfaceIndex.ToString(CultureInfo.InvariantCulture),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], 1024);
            Console.WriteLine(pending.OperationId.ToString("N"));
            Console.WriteLine($"PASS HELPER NATIVE TUN DESCRIPTOR: {pending.OperationId:N}; {intent.Device} ifindex={effect.InterfaceIndex}, receiver PID={effect.Process!.Pid}");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        public Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingConfiguration pending, CancellationToken ct) =>
            throw new InvalidOperationException("The test interrupts before configuration confirmation.");

        public async Task<bool> ObserveStoppedForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        {
            var checkpoint = new LinuxMihomoTunCheckpoint(Archive, pending.OperationId, revision, pending.Plan.CandidateIdentity);
            if (checkpoint.Read() is { Phase: MihomoTunPhase.DescriptorDispatched })
            {
                await host.VerifyTunAsync(pending.OperationId, ct);
                return false;
            }
            await host.CompleteTunStopAsync(pending.OperationId, ct);
            return true;
        }

        public async Task StopForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        {
            await host.DispatchTunStopAsync(pending.OperationId, ct);
            if (interruptStop) ProcessKill();
        }

        public Task ValidateRestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct) =>
            RequireHashAsync(Previous, pending.Plan.PreviousIdentity);

        public async Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        {
            await InstallConfigurationAsync(Previous, pending.Plan.PreviousIdentity);
            var observed = await host.ObserveAsync(ct);
            await RequireHashAsync(Configuration, pending.Plan.PreviousIdentity);
            return new("1.19.33", pending.Plan.PreviousIdentity, observed.Running, observed.BootEnabled!.Value,
                observed.Owned && observed.Executable == Binary, !observed.Running);
        }

        public Task CleanupPreparationAsync(RuntimePendingConfiguration pending, CancellationToken ct) => Task.CompletedTask;

        private static async Task RequireHashAsync(string path, string hash)
        {
            if (MihomoArchiveAdapter.Hash(await File.ReadAllBytesAsync(path)) != hash)
                throw new RuntimePolicyException("runtime.configuration_drift");
        }

        private static void ProcessKill() => System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
