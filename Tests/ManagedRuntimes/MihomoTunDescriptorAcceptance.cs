using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoTunDescriptorAcceptance
{
    private const string DeviceName = "rk-fd-proof";
    private const string FixtureSecret = "mb04-private-fixture-controller";
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
        var revisionId = intentRoot.GetProperty("revision").GetGuid();
        var candidateHash = intentRoot.GetProperty("credential").GetString()!.ToUpperInvariant();
        var network = new MihomoTunNetworkIntent(DeviceName, intentRoot.GetProperty("address").GetString()!,
            intentRoot.GetProperty("target").GetString()!, intentRoot.GetProperty("management").GetString()!, ManagementHash(intentRoot));
        var previousPath = Path.Combine(root, "previous.yaml");
        var previousBytes = await File.ReadAllBytesAsync(previousPath);
        var previous = MihomoArchiveAdapter.Hash(previousBytes);
        var binaryPath = Path.Combine(Archive, "versions", revisionId.ToString("N"), "mihomo");
        var configuration = Path.Combine(Archive, "active.yaml");
        var host = new LinuxMihomoArchiveHost();
        await File.WriteAllBytesAsync(configuration, previousBytes);
        File.SetUnixFileMode(configuration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(Path.Combine(Archive, "seed-data"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await host.ConfigureAsync(binaryPath, Path.Combine(Archive, "data", revisionId.ToString("N")), configuration,
            previous, false, CancellationToken.None);
        await host.StartAsync(CancellationToken.None);
        var store = Store();
        var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
        var before = store.Initialize(new(Guid.NewGuid(), "test-host", "mihomo", 0, RuntimeOwnership.Absent, target, null, false), previous);
        var binary = await File.ReadAllBytesAsync(binaryPath);
        var digest = MihomoArchiveAdapter.Hash(binary);
        var artifact = new RuntimeArtifact("fixture", digest, binary.Length, "gz", "mihomo", "mihomo-acceptance-key",
            new(binary.Length, 1, [new("mihomo", binary.Length, digest, true)]));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sources, catalog) = MihomoArchiveAcceptance.Accept(key, [new("fixture-release", "mihomo", "v1.19.33", "1.19.33",
            RuntimeChannel.Stable, "mihomo-acceptance", target, artifact, "mihomo-v1", true, RuntimeTrust.Confirmed,
            RuntimeEvidence.NativeConfiguration, DateTimeOffset.UtcNow)], root);
        var revision = new RuntimeStoredRevision(revisionId, catalog.Releases.Single(), "1.19.33", previous, 1, 1);
        using (var session = store.Acquire())
            session.Save(before with { Active = revision, Revisions = [revision], Installation = before.Installation with
                { StateRevision = 1, Ownership = RuntimeOwnership.Managed, ActiveReleaseId = revision.Release.ReleaseId } });
        var plan = new RuntimeConfigurationPlan(before.Installation.InstallationId, before.Installation.HostId, "mihomo",
            1, revisionId, previous, candidateHash, "tun-publication");
        plan = plan with { NetworkIntentIdentity = MihomoRuntimeMaterial.ConfigurationTunIntentIdentity(operationId, plan, network) };
        var paths = new AuthorizationPaths(root);
        Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
        var publications = Publications(paths);
        var previousMaterial = MihomoConfigurationPolicy.Describe(await File.ReadAllTextAsync(previousPath)).Material;
        var candidate = MihomoConfigurationPolicy.Describe(await File.ReadAllTextAsync(Path.Combine(root, "candidate.yaml"))).Material;
        var bootstrap = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml"), previousMaterial.Yaml);
        await publications.BeginAsync(bootstrap, 0, previousMaterial, CancellationToken.None);
        var confirmed = await publications.ConfirmAsync(bootstrap, previous, CancellationToken.None);
        await RefusesAsync("runtime.authorization_changed", () => publications.BeginConfigurationAsync(operationId, confirmed.Revision,
            candidate, plan, CancellationToken.None, tunIntent: network with { Destination = "198.51.100.54" }));
        var autoRoute = MihomoConfigurationPolicy.Describe(candidate.Yaml.Replace("auto-route: false", "auto-route: true", StringComparison.Ordinal)).Material;
        var driftedPlan = plan with { CandidateIdentity = MihomoPublicationStore.Identity(autoRoute.Yaml) };
        driftedPlan = driftedPlan with { NetworkIntentIdentity = MihomoRuntimeMaterial.ConfigurationTunIntentIdentity(operationId, driftedPlan, network) };
        await RefusesAsync("runtime.network_intent_invalid", () => publications.BeginConfigurationAsync(operationId, confirmed.Revision,
            autoRoute, driftedPlan, CancellationToken.None, tunIntent: network));
        await publications.BeginConfigurationAsync(operationId, confirmed.Revision, candidate, plan, CancellationToken.None, tunIntent: network);
        await File.WriteAllBytesAsync(Path.Combine(root, "tun-plan.json"), RuntimeJson.Serialize(plan));
        var adapter = new MihomoConfigurationAdapter(Archive, store, sources, host, new Authorization(root, operationId), RuntimeSourceProtection.Protect);
        InstallationStage stage = default;
        var transaction = new RuntimeArchiveConfiguration(store, new FixtureAdapter(adapter), async (next, _) =>
        {
            stage = next;
            if (next != InstallationStage.HealthChecking) return;
            var effect = Ledger(operationId, intentRoot).Read() ?? throw new InvalidOperationException("Missing original native dispatch.");
            await RefusesAsync("runtime.network_lifecycle_unverified", () => adapter.VerifyAsync(store.Read()!.Configuration!, CancellationToken.None));
            await RefusesAsync("runtime.network_checkpoint_conflict", () => host.DispatchTunStartAsync(operationId, CancellationToken.None));
            await NativeRuntimeProcess.RunAsync("/usr/bin/python3", ["-I", Path.Combine(root, "mihomo-tun-fd-proof.py"),
                "prepare", root, effect.InterfaceIndex.ToString(CultureInfo.InvariantCulture),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], 1024);
            Console.WriteLine(operationId.ToString("N"));
            Console.WriteLine($"PASS HELPER NATIVE TUN DESCRIPTOR: {operationId:N}; {network.Device} ifindex={effect.InterfaceIndex}, receiver PID={effect.Process!.Pid}");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        });
        var outcome = await transaction.ExecuteAsync(operationId, plan, CancellationToken.None);
        throw new InvalidOperationException($"The fixture exited before interruption: {stage}, {outcome.Outcome}, {outcome.ProblemCode}, {store.Read()?.Configuration?.RecoveryProblemCode}.");
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
        else if (action == "ordinary-stop") await host.StopAsync(CancellationToken.None);
        else if (action is "retry" or "recover" or "recover-interrupt")
        {
            var plan = RuntimeJson.Read<RuntimeConfigurationPlan>(await File.ReadAllBytesAsync(Path.Combine(root, "tun-plan.json")));
            var store = Store();
            var sources = new RuntimeSourceStore(Path.Combine(root, "trust"), RuntimeSourceProtection.Protect);
            var adapter = new MihomoConfigurationAdapter(Archive, store, sources, host, new Authorization(root, operation), RuntimeSourceProtection.Protect);
            var transaction = new RuntimeArchiveConfiguration(store, new FixtureAdapter(adapter, action == "recover-interrupt"));
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

    private sealed class FixtureAdapter(MihomoConfigurationAdapter adapter, bool interruptStop = false) : IRuntimeArchiveConfigurationAdapter
    {
        private static async Task TraceAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                throw;
            }
        }
        public Task PreflightAsync(RuntimeConfigurationPlan plan, RuntimeArchiveState state, CancellationToken ct) =>
            TraceAsync(() => adapter.PreflightAsync(plan, state, ct));
        public Task<RuntimeServiceSnapshot> CaptureAsync(Guid id, RuntimeArchiveState state, CancellationToken ct) =>
            adapter.CaptureAsync(id, state, ct);
        public Task PrepareAsync(RuntimePendingConfiguration pending, CancellationToken ct) => TraceAsync(() => adapter.PrepareAsync(pending, ct));
        public Task ValidateSwitchAsync(RuntimePendingConfiguration pending, CancellationToken ct) => TraceAsync(() => adapter.ValidateSwitchAsync(pending, ct));
        public Task ActivateAsync(RuntimePendingConfiguration pending, CancellationToken ct) => TraceAsync(() => adapter.ActivateAsync(pending, ct));
        public Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingConfiguration pending, CancellationToken ct) => adapter.VerifyAsync(pending, ct);
        public Task<bool> ObserveStoppedForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct) =>
            adapter.ObserveStoppedForRecoveryAsync(pending, ct);
        public async Task StopForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        {
            await adapter.StopForRecoveryAsync(pending, ct);
            if (interruptStop) System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        public Task ValidateRestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct) => adapter.ValidateRestoreAsync(pending, ct);
        public Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct) => adapter.RestoreAsync(pending, ct);
        public Task CleanupPreparationAsync(RuntimePendingConfiguration pending, CancellationToken ct) => adapter.CleanupPreparationAsync(pending, ct);
    }

    private sealed class Authorization(string root, Guid operation) : IMihomoConfigurationAuthorization
    {
        public Task<MihomoConfigurationAuthorization> ReadAsync(CancellationToken ct) => ReadGrantAsync(root, operation, ct);
    }

    private static MihomoPublicationStore Publications(AuthorizationPaths paths) => new(paths,
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(paths.Root, "authorization-keys"))));

    private static async Task<MihomoConfigurationAuthorization> ReadGrantAsync(string root, Guid operation, CancellationToken ct)
    {
        var paths = new AuthorizationPaths(root);
        var provider = new MihomoRuntimeAuthorizationProvider(Publications(paths), paths, paths);
        await using var callback = new RuntimeAuthorizationServer<MihomoConfigurationAuthorization>(Guid.NewGuid(),
            token => provider.ReadConfigurationAsync(operation, token));
        var grant = await RuntimeAuthorizationClient.ReadAsync<MihomoConfigurationAuthorization>(
            callback.Channel, callback.Channel.OperationId, ct);
        if (grant.OperationId != operation) throw new RuntimePolicyException("runtime.authorization_unavailable");
        return grant;
    }

    private sealed record AuthorizationPaths(string Root) : IProxyPlatformPaths, IProxyControllerSecretStore
    {
        public string GetProtectedConfigurationDirectory() => Path.Combine(Root, "authorization");
        public string GetStateDirectory() => Path.Combine(Root, "authorization-state");
        public string GetEngineVersionsDirectory(string engineId) => throw new NotSupportedException();
        public string GetEngineDataDirectory(string engineId) => throw new NotSupportedException();
        public string GetSanitizedLogDirectory() => throw new NotSupportedException();
        public Task<string> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(FixtureSecret);
        public Task<string?> ReadExistingAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(FixtureSecret);
    }
}
