using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

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
        var serverCase = intentRoot.GetProperty("case").GetString();
        var commit = intentRoot.GetProperty("case").GetString() is
            "committed" or "committed-stop" or "committed-stop-before-delete" or "committed-start" or "committed-remove-keep" or "committed-remove-purge";
        var revisionId = intentRoot.GetProperty("revision").GetGuid();
        var previousPath = Path.Combine(root, "previous.yaml");
        var previousBytes = await File.ReadAllBytesAsync(previousPath);
        var previous = MihomoArchiveAdapter.Hash(previousBytes);
        var binaryPath = Path.Combine(Archive, "versions", revisionId.ToString("N"), "mihomo");
        var configuration = Path.Combine(Archive, "active.yaml");
        var host = new LinuxMihomoArchiveHost();
        var store = Store();
        var lifecycle = new MihomoRuntimeLifecycle(Archive, store,
            new RuntimeSourceStore(Path.Combine(root, "trust"), RuntimeSourceProtection.Protect), host, RuntimeSourceProtection.Protect);
        var baseline = await lifecycle.QueryAsync(CancellationToken.None);
        var before = store.Read()!;
        var target = baseline.Installation.Target;
        await File.WriteAllBytesAsync(configuration, previousBytes);
        File.SetUnixFileMode(configuration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(Path.Combine(Archive, "seed-data"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await host.ConfigureAsync(binaryPath, Path.Combine(Archive, "data", revisionId.ToString("N")), configuration,
            previous, false, CancellationToken.None);
        await host.StartAsync(CancellationToken.None);
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
            session.Save(before with { Active = revision, ConfigurationIdentity = previous, Revisions = [revision], Installation = before.Installation with
                { StateRevision = 1, Ownership = RuntimeOwnership.Managed, ActiveReleaseId = revision.Release.ReleaseId } });
        var draft = await File.ReadAllTextAsync(Path.Combine(root, "candidate.yaml"));
        var inputPlan = new RuntimeConfigurationPlan(before.Installation.InstallationId, before.Installation.HostId, "mihomo",
            1, revisionId, previous, MihomoPublicationStore.Identity(draft), "tun-publication");
        var scope = new LinuxMihomoTunScope(DeviceName, intentRoot.GetProperty("address").GetString()!,
            intentRoot.GetProperty("target").GetString()!, intentRoot.GetProperty("uid").GetUInt32(), "tun.fixture.test");
        await RefusesAsync("runtime.network_scope_unavailable", () => Task.FromResult(LinuxMihomoTunScope.Read()));
        await WriteScopeAsync(scope);
        File.SetUnixFileMode(LinuxMihomoTunScope.Path, File.GetUnixFileMode(LinuxMihomoTunScope.Path) | UnixFileMode.GroupWrite);
        await RefusesAsync("runtime.network_scope_unavailable", () => Task.FromResult(LinuxMihomoTunScope.Read()));
        var scopeTarget = LinuxMihomoTunScope.Path + ".target";
        File.Move(LinuxMihomoTunScope.Path, scopeTarget);
        File.SetUnixFileMode(scopeTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        File.CreateSymbolicLink(LinuxMihomoTunScope.Path, scopeTarget);
        await RefusesAsync("runtime.network_scope_unavailable", () => Task.FromResult(LinuxMihomoTunScope.Read()));
        File.Delete(LinuxMihomoTunScope.Path);
        File.Move(scopeTarget, LinuxMihomoTunScope.Path);
        var planInput = Path.Combine(root, "tun-plan-input.json");
        await File.WriteAllBytesAsync(planInput, RuntimeJson.Serialize(new FreezeInput(operationId, inputPlan, draft,
            scope.Address, intentRoot.GetProperty("management").GetString()!)));
        File.SetUnixFileMode(planInput, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var frozen = RuntimeJson.Read<MihomoManagedTunPlan>(System.Text.Encoding.UTF8.GetBytes(
            await NativeRuntimeProcess.RunAsync("/usr/sbin/runuser", ["-u", "nobody", "--", "/usr/bin/dotnet",
                intentRoot.GetProperty("fixture").GetString()!, "runtime-mihomo-tun-freeze", planInput], 32768)));
        File.Delete(planInput);
        var (plan, candidate, network) = frozen;
        if (!scope.Matches(network) || network.ManagementHash != ManagementHash(intentRoot))
            throw new InvalidOperationException("Server policy or management route observations differ.");
        var paths = new AuthorizationPaths(root);
        Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
        var publications = Publications(paths);
        var previousMaterial = MihomoConfigurationPolicy.Describe(await File.ReadAllTextAsync(previousPath)).Material;
        if (serverCase is "ipc-http-full" or "ipc-http-joint" or "ipc-http-admission" or "ipc-http-handoff" or "ipc-http-retained" or "ipc-http-retained-installation" or "ipc-http-retained-administrator" or "ipc-http-squid" or "ipc-http-squid-lifecycle-unknown" or "ipc-http-squid-account" or "ipc-http-zero-dns" or "ipc-http-early-owner" or "ipc-http-initial-stop" or "ipc-http-initial-stop-replaced" or "ipc-http-restored-process-replaced" or "ipc-http-restored-job-replaced" or "ipc-http-restored-job-cut" or "ipc-http-restored-dispatch-unknown" or "ipc-http-named-start-dns" or "ipc-http-named-start-dns-settled" or "ipc-http-named-start-recovery-cut" or "ipc-http-partial-dns" or "ipc-http-unknown-dns")
        {
            await MihomoTunFullServerAcceptance.SeedAsync(root, previousMaterial, previous,
                serverCase is "ipc-http-joint" or "ipc-http-handoff" or "ipc-http-retained" or "ipc-http-retained-installation" or "ipc-http-retained-administrator" or "ipc-http-squid");
            await WriteScopeAsync(scope with { AllowManagedPublication = true });
            return;
        }
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
        if (serverCase is "ipc-server-commit" or "ipc-server-pending" or "ipc-http-commit" or "ipc-http-pending" or "ipc-http-action")
        {
            var input = IpcPlanPath(paths);
            await File.WriteAllBytesAsync(input, RuntimeJson.Serialize(new IpcPublicationInput(operationId, serverCase, frozen)));
            File.SetUnixFileMode(input, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await WriteScopeAsync(scope with { AllowManagedPublication = true });
            var gid = intentRoot.GetProperty("gid").GetUInt32();
            await NativeRuntimeProcess.RunAsync("/usr/bin/chown", ["-R", $"{scope.ServerUserId}:{gid}", "--",
                paths.GetStateDirectory(), paths.GetProtectedConfigurationDirectory(), Path.Combine(root, "authorization-keys")], 1024);
            return;
        }
        if (serverCase is "server-commit" or "server-pending")
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "tun-server-plan.json"), RuntimeJson.Serialize(frozen));
            await RunServerPublicationAsync(root, operationId, frozen, confirmed.Revision, scope, serverCase);
            return;
        }
        await publications.BeginConfigurationAsync(operationId, confirmed.Revision, candidate, plan, CancellationToken.None, tunIntent: network);
        await File.WriteAllBytesAsync(Path.Combine(root, "tun-plan.json"), RuntimeJson.Serialize(plan));
        var adapter = new MihomoConfigurationAdapter(Archive, store, sources, host, new Authorization(root, operationId), RuntimeSourceProtection.Protect);
        var pending = new RuntimePendingConfiguration(operationId, "", "", plan, default, null, false, false, null, null);
        var oldService = await host.ObserveAsync(CancellationToken.None);
        var oldPid = await ReadPidAsync();
        var operationDirectory = Path.Combine(Archive, "operations", operationId.ToString("N"));
        await WriteScopeAsync(scope with { Destination = "198.51.100.54" });
        await RefusesAsync("runtime.network_scope_changed", () => adapter.PreflightAsync(plan,
            store.Read()! with { Configuration = pending }, CancellationToken.None));
        await RefusesAsync("runtime.network_scope_changed", () => adapter.ActivateAsync(pending, CancellationToken.None));
        var unchanged = await File.ReadAllBytesAsync(configuration);
        if (await host.ObserveAsync(CancellationToken.None) != oldService || await ReadPidAsync() != oldPid
            || !previousBytes.AsSpan().SequenceEqual(unchanged)
            || store.Read()?.Configuration is not null || Directory.EnumerateFileSystemEntries(operationDirectory).Any())
            throw new InvalidOperationException("Helper touched the old service or original operation with a different host scope.");
        await WriteScopeAsync(scope);
        Console.Error.WriteLine("PASS MANAGED TUN HOST SCOPE: protected policy, unprivileged planner, Helper preflight and activation drift refusal");
        InstallationStage stage = default;
        var transaction = new RuntimeArchiveConfiguration(store, new FixtureAdapter(adapter, commit: commit), async (next, _) =>
        {
            stage = next;
            if (next != InstallationStage.HealthChecking) return;
            var effect = new LinuxMihomoTunCheckpoint(Archive, operationId, revisionId, plan.CandidateIdentity).Read()
                ?? throw new InvalidOperationException("Missing original native dispatch.");
            await RefusesAsync("runtime.network_lifecycle_unverified", () => adapter.VerifyAsync(store.Read()!.Configuration!, CancellationToken.None));
            await RefusesAsync("runtime.network_checkpoint_conflict", () => host.DispatchTunStartAsync(operationId, CancellationToken.None));
            await NativeRuntimeProcess.RunAsync("/usr/bin/python3", ["-I", Path.Combine(root, "mihomo-tun-fd-proof.py"),
                "prepare", root, effect.InterfaceIndex.ToString(CultureInfo.InvariantCulture),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], 1024);
            if (commit) return;
            Console.WriteLine(operationId.ToString("N"));
            Console.WriteLine($"PASS HELPER NATIVE TUN DESCRIPTOR: {operationId:N}; {network.Device} ifindex={effect.InterfaceIndex}, receiver PID={effect.Process!.Pid}");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        });
        var outcome = await transaction.ExecuteAsync(operationId, plan, CancellationToken.None);
        if (commit && outcome.Outcome == RuntimeSwitchOutcome.Succeeded)
            await Task.Delay(Timeout.InfiniteTimeSpan); // The parent observes disk and kills us before receiving a result.
        throw new InvalidOperationException($"The fixture exited before interruption: {stage}, {outcome.Outcome}, {outcome.ProblemCode}, {store.Read()?.Configuration?.RecoveryProblemCode}.");

        static Task<string> ReadPidAsync() => NativeRuntimeProcess.RunAsync("/usr/bin/systemctl",
            ["show", LinuxMihomoArchiveHost.Unit, "--property=MainPID", "--value"], 64);
    }

    [SupportedOSPlatform("linux")]
    internal static async Task RunIpcServerAsync(string root, string phase)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost(rootRequired: false);
        if (Environment.IsPrivilegedProcess || root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK")
            || phase is not ("publish" or "recover" or "lifecycle" or "http" or "http-recover"))
            throw new InvalidOperationException("The managed publication IPC Server requires the isolated unprivileged worker.");
        var paths = new AuthorizationPaths(root);
        var input = RuntimeJson.Read<IpcPublicationInput>(await File.ReadAllBytesAsync(IpcPlanPath(paths)));
        if (input.Scenario is ("ipc-http-commit" or "ipc-http-pending" or "ipc-http-action") && phase is ("http" or "http-recover"))
        {
            await MihomoTunHttpAcceptance.RunAsync(root, Publications(paths), paths, paths, paths, IpcSocketPath,
                phase == "http", input.Scenario == "ipc-http-pending");
            return;
        }
        if (input.Scenario is not ("ipc-server-commit" or "ipc-server-pending"))
            throw new InvalidOperationException("The IPC publication scenario is unavailable.");
        var revision = (await Publications(paths).ReadAsync(CancellationToken.None)).Revision;
        if (phase == "publish")
            await File.WriteAllTextAsync(Path.Combine(paths.GetStateDirectory(), "ipc-server.pid"),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        await RunServerPublicationAsync(root, input.OperationId, input.Frozen, revision, null, phase == "publish" ? input.Scenario : null,
            new UnixSocketPrivilegedOperationTransport(new() { SocketPath = IpcSocketPath }, NullLogger<UnixSocketPrivilegedOperationTransport>.Instance),
            runLifecycle: phase == "lifecycle");
    }

    private const string IpcSocketPath = "/run/relaxkonos-tun-ipc/helper.sock";
    private static string IpcPlanPath(AuthorizationPaths paths) => Path.Combine(paths.GetStateDirectory(), "tun-ipc-plan.json");
    private sealed record IpcPublicationInput(Guid OperationId, string Scenario, MihomoManagedTunPlan Frozen);

    internal static async Task FreezeAsync(string inputPath)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost(rootRequired: false);
        if (Environment.IsPrivilegedProcess) throw new InvalidOperationException("The Server planner must run unprivileged.");
        var input = RuntimeJson.Read<FreezeInput>(await File.ReadAllBytesAsync(inputPath));
        var platform = new HostProxyNetworkSafetyPlatform();
        var snapshot = await platform.CaptureManagementRouteAsync(IPAddress.Parse(input.Management), CancellationToken.None)
            ?? throw new InvalidOperationException("The Server did not observe a management route.");
        var draft = MihomoConfigurationPolicy.Describe(input.Yaml).Material;
        var settings = new ProxyTunSettingsDto("gvisor", DeviceName, false, false, false, "", 1500);
        await RefusesAsync("runtime.network_transaction_required", () => MihomoManagedTunPlan.FreezeAsync(
            input.Operation, input.Plan, draft, ProxyTunSettingsDto.Default, snapshot, CancellationToken.None));
        await RefusesAsync("runtime.network_scope_changed", () => MihomoManagedTunPlan.FreezeAsync(
            input.Operation, input.Plan, draft, settings with { DeviceName = "other-device" }, snapshot, CancellationToken.None));
        await RefusesAsync("runtime.management_route_unknown", () => MihomoManagedTunPlan.FreezeAsync(
            input.Operation, input.Plan, draft, settings, snapshot with { DefaultGateway = input.Address }, CancellationToken.None));
        var frozen = await MihomoManagedTunPlan.FreezeAsync(input.Operation, input.Plan, draft, settings, snapshot, CancellationToken.None);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(RuntimeJson.Serialize(frozen)));
    }

    private sealed record FreezeInput(Guid Operation, RuntimeConfigurationPlan Plan, string Yaml, string Address, string Management);

    [SupportedOSPlatform("linux")]
    private static async Task WriteScopeAsync(LinuxMihomoTunScope scope)
    {
        await File.WriteAllBytesAsync(LinuxMihomoTunScope.Path, RuntimeJson.Serialize(scope));
        File.SetUnixFileMode(LinuxMihomoTunScope.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
    }

    [SupportedOSPlatform("linux")]
    internal static async Task CheckpointAsync(string root, Guid operation, string action)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK"))
            throw new InvalidOperationException("The network check requires its private systemd work directory.");
        using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "tun-operation.json")));
        var state = intent.RootElement;
        var earlyPhase = state.GetProperty("case").GetString() switch
        {
            "registration-before-network" => MihomoTunPhase.Intent,
            "network-before-launch" => MihomoTunPhase.NetworkDispatched,
            "launch-before-descriptor" => MihomoTunPhase.LaunchDispatched,
            _ => (MihomoTunPhase?)null
        };
        var prelaunch = earlyPhase is not null && state.GetProperty("phase").GetString() == "prepared";
        if (operation == Guid.Empty || state.GetProperty("operation").GetString() != operation.ToString("N")
            || !prelaunch && state.GetProperty("phase").GetString() != "dispatching")
            throw new InvalidOperationException("The network check requires the original dispatched operation.");
        var ledger = prelaunch
            ? new LinuxMihomoTunCheckpoint(Archive, operation, state.GetProperty("revision").GetGuid(),
                RuntimeJson.Read<RuntimeConfigurationPlan>(await File.ReadAllBytesAsync(Path.Combine(root, "tun-plan.json"))).CandidateIdentity)
            : Ledger(operation, state);
        var current = ledger.Read();
        if (current is null ? action != "recover" : !LinuxMihomoTunCheckpoint.MatchesCurrentNetwork(current.Intent)
            || current.Intent.ManagementHash != ManagementHash(state) || current.Intent.ManagementUserId != state.GetProperty("uid").GetUInt32()
            || (prelaunch ? current.Phase != earlyPhase
                : current.Process != Process(state) || current.InterfaceIndex != state.GetProperty("ifindex").GetUInt32()))
            throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
        var host = new LinuxMihomoArchiveHost();
        if (action == "state") Console.WriteLine(current!.Phase);
        else if (action == "observe") await host.VerifyTunAsync(operation, CancellationToken.None);
        else if (action == "ordinary-stop") await host.StopAsync(CancellationToken.None);
        else if (action.StartsWith("committed-stop-interrupt:", StringComparison.Ordinal))
        {
            var stop = Guid.Parse(action["committed-stop-interrupt:".Length..]);
            var result = await MihomoRuntimeOperations.ExecuteAsync(
                new(PrivilegedOperationKind.MihomoRuntimeTunStop, OperationId: stop,
                    RuntimeLifecycle: new(MihomoTunPublicationId: operation)), report: frame =>
                {
                    if (frame.Stage != InstallationStage.Verifying || frame.Cancellable)
                        throw new InvalidOperationException("Unexpected committed stop boundary.");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return Task.CompletedTask;
                });
            throw new InvalidOperationException($"Committed stop missed its dispatch boundary: {result.Error}.");
        }
        else if (action == "observe-commit" || action.StartsWith("observe-action:", StringComparison.Ordinal))
        {
            var store = Store();
            var sources = new RuntimeSourceStore(Path.Combine(root, "trust"), RuntimeSourceProtection.Protect);
            var lifecycle = new MihomoRuntimeLifecycle(Archive, store, sources, host, RuntimeSourceProtection.Protect);
            var receiptId = action == "observe-commit" ? operation : Guid.Parse(action["observe-action:".Length..]);
            var observed = await lifecycle.QueryAsync(CancellationToken.None, receiptId);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(RuntimeJson.Serialize(observed)));
        }
        else if (action == "server-recover")
        {
            var frozen = RuntimeJson.Read<MihomoManagedTunPlan>(await File.ReadAllBytesAsync(Path.Combine(root, "tun-server-plan.json")));
            await RunServerPublicationAsync(root, operation, frozen, 0, null, null);
        }
        else if (action.Split(':') is [var verb, var id] && Guid.TryParseExact(id, "N", out var execution))
        {
            if (verb.StartsWith("remove-", StringComparison.Ordinal))
            {
                await RemovalFixtureAsync(root, host, verb, execution);
                return;
            }
            var (kind, cut) = verb switch
            {
                "start" => (PrivilegedOperationKind.MihomoRuntimeTunStart, (InstallationStage?)null),
                "start-interrupt" => (PrivilegedOperationKind.MihomoRuntimeTunStart, InstallationStage.HealthChecking),
                "start-undispatched" => (PrivilegedOperationKind.MihomoRuntimeTunStart, InstallationStage.Activating),
                "restart" => (PrivilegedOperationKind.MihomoRuntimeTunRestart, (InstallationStage?)null),
                "restart-stop-interrupt" => (PrivilegedOperationKind.MihomoRuntimeTunRestart, InstallationStage.Verifying),
                _ => throw new InvalidOperationException("Unexpected TUN launch fixture action.")
            };
            var material = (await ReadGrantAsync(root, operation, CancellationToken.None)).Candidate!;
            await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(execution, _ =>
                Task.FromResult(new MihomoCurrentAuthorization(DateTimeOffset.UtcNow.AddMinutes(1), material)));
            var result = await MihomoRuntimeOperations.ExecuteAsync(new(kind, OperationId: execution,
                RuntimeLifecycle: new(Authorization: callback.Channel, MihomoTunPublicationId: operation)),
                () => new(Archive, Store(), new RuntimeSourceStore(Path.Combine(root, "trust"), RuntimeSourceProtection.Protect),
                    host, RuntimeSourceProtection.Protect), report: frame =>
                {
                    if (frame.Stage == cut && cut is not null) System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return Task.CompletedTask;
                });
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        else if (action is "retry" or "recover" or "recover-interrupt" or "recover-cleanup-interrupt" or "recover-restored-interrupt")
        {
            var plan = RuntimeJson.Read<RuntimeConfigurationPlan>(await File.ReadAllBytesAsync(Path.Combine(root, "tun-plan.json")));
            var store = Store();
            var sources = new RuntimeSourceStore(Path.Combine(root, "trust"), RuntimeSourceProtection.Protect);
            var adapter = new MihomoConfigurationAdapter(Archive, store, sources, host, new Authorization(root, operation), RuntimeSourceProtection.Protect);
            var transaction = new RuntimeArchiveConfiguration(store, new FixtureAdapter(adapter,
                interruptStop: action == "recover-interrupt", interruptCleanup: action == "recover-cleanup-interrupt",
                interruptRestored: action == "recover-restored-interrupt"));
            var result = action == "retry"
                ? await transaction.ExecuteAsync(Guid.NewGuid(), plan, CancellationToken.None)
                : await transaction.RecoverAsync(operation, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        else throw new RuntimePolicyException("runtime.network_checkpoint_conflict");
    }

    [SupportedOSPlatform("linux")]
    private static async Task RunServerPublicationAsync(string root, Guid operation, MihomoManagedTunPlan frozen,
        long expectedRevision, LinuxMihomoTunScope? scope, string? cut, IPrivilegedOperationTransport? ipc = null, bool runLifecycle = false)
    {
        var paths = new AuthorizationPaths(root);
        var store = Publications(paths);
        var authorization = new MihomoRuntimeAuthorizationProvider(store, paths, paths);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        await using var services = builder.Services.BuildServiceProvider();
        var ids = new HashSet<string> { ServerModules.ProxyId };
        await using var coordinator = new ModuleResidencyCoordinator([new ProxyWorkRuntimeFactory()],
            ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), services,
            ServerModuleContext.Create(builder.Configuration, builder.Environment, "private-systemd"),
            NullLogger<ModuleResidencyCoordinator>.Instance, residentWorkModuleIds: ids);
        void RequireLease()
        {
            if (coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases != 1)
                throw new InvalidOperationException("The Server did not retain its resident Proxy work lease.");
        }
        async Task PauseAsync()
        {
            RequireLease();
            await File.WriteAllTextAsync(Path.Combine(ipc is null ? root : paths.GetStateDirectory(), "server-cut-ready"), operation.ToString("N"));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        var transport = new ServerPublicationTransport(ipc ?? new InProcessTransport(new(Archive, Store(),
            new RuntimeSourceStore(Path.Combine(root, "trust"), RuntimeSourceProtection.Protect),
            new LinuxMihomoArchiveHost(), RuntimeSourceProtection.Protect), async (request, frame) =>
        {
            if (cut is null || request != PrivilegedOperationKind.MihomoRuntimeConfigure || frame.Stage != InstallationStage.HealthChecking)
                return;
            var effect = new LinuxMihomoTunCheckpoint(Archive, operation, frozen.Plan.ActiveRevisionId, frozen.Plan.CandidateIdentity).Read()!;
            await NativeRuntimeProcess.RunAsync("/usr/bin/python3", ["-I", Path.Combine(root, "mihomo-tun-fd-proof.py"),
                "prepare", root, effect.InterfaceIndex.ToString(CultureInfo.InvariantCulture),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], 1024);
            if (cut == "server-pending") await PauseAsync();
        }), RequireLease, cut is "server-commit" or "ipc-server-commit" ? PauseAsync : null);
        var runtime = new MihomoRuntimeService(transport, store, authorization, paths, new DirectDownloads());
        var leases = new ProxyWorkLeaseSource(coordinator);
        var service = new MihomoConfigurationPublicationService(store, authorization, runtime, paths,
            workLeases: leases);
        if (runLifecycle)
        {
            var result = await RunIpcLifecycleAsync(store, runtime, leases, operation);
            if (coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases != 0)
                throw new InvalidOperationException("Managed TUN lifecycle retained a completed work lease.");
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(RuntimeJson.Serialize(result)));
            return;
        }
        if (cut is not null)
        {
            await RefusesAsync("runtime.network_transaction_required", () =>
                service.PublishAsync(operation, expectedRevision, frozen.Plan, frozen.Candidate, CancellationToken.None));
            if (ipc is null)
            {
                await RefusesAsync("runtime.network_lifecycle_unverified", () =>
                    service.PublishManagedTunAsync(operation, expectedRevision, frozen, CancellationToken.None));
                if ((await store.ReadAsync(CancellationToken.None)).Pending is not null || transport.ConfigureCalls != 0)
                    throw new InvalidOperationException("A closed publication gate changed state.");
                await WriteScopeAsync(scope! with { AllowManagedPublication = true });
            }
            await RefusesAsync("runtime.network_transaction_required", () =>
                service.PublishAsync(operation, expectedRevision, frozen.Plan, frozen.Candidate, CancellationToken.None));
            var unexpected = await service.PublishManagedTunAsync(operation, expectedRevision, frozen, CancellationToken.None);
            throw new InvalidOperationException($"The Server missed its expected cut: {unexpected.Outcome} {unexpected.Receipt?.ProblemCode}.");
        }
        var resolved = await service.RecoverAsync(operation, CancellationToken.None);
        var replay = await service.PublishManagedTunAsync(operation, expectedRevision, frozen, CancellationToken.None);
        var state = await store.ReadAsync(CancellationToken.None);
        if (resolved != replay || transport.ConfigureCalls != 0 || state.Pending is not null
            || coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases != 0
            || await new MihomoPublicationFiles(paths).IdentityAsync(CancellationToken.None) != state.Identity)
            throw new InvalidOperationException("Managed TUN publication did not resolve its original receipt and release its work lease.");
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(RuntimeJson.Serialize(new
        {
            resolved.OperationId, Outcome = resolved.Outcome.ToString(), resolved.Receipt, state.Identity,
            transport.ConfigureCalls, transport.RecoverCalls
        })));
    }

    private static async Task<object> RunIpcLifecycleAsync(MihomoPublicationStore store, MihomoRuntimeService runtime,
        IProxyWorkLeaseSource leases, Guid publicationId)
    {
        await store.Gate.WaitAsync();
        try
        {
            using var work = await leases.AcquireAsync(CancellationToken.None);
            var start = Guid.NewGuid();
            var restart = Guid.NewGuid();
            var stopped = await runtime.StopManagedTunUnderGateAsync(Guid.NewGuid(), publicationId, CancellationToken.None);
            var started = await runtime.StartManagedTunUnderGateAsync(start, publicationId, restart: false, CancellationToken.None);
            var restarted = await runtime.StartManagedTunUnderGateAsync(restart, publicationId, restart: true, CancellationToken.None);
            var historical = await runtime.StartManagedTunUnderGateAsync(start, publicationId, restart: false, CancellationToken.None);
            var observed = await runtime.QueryHelperAsync(CancellationToken.None, publicationId);
            if (stopped.Outcome != RuntimeSwitchOutcome.Succeeded || started.Outcome != RuntimeSwitchOutcome.Succeeded
                || restarted.Outcome != RuntimeSwitchOutcome.Succeeded || historical != started
                || observed.MihomoTunPublicationId != publicationId || observed.Receipt is not { Outcome: RuntimeSwitchOutcome.Succeeded })
                throw new InvalidOperationException("The managed lifecycle did not preserve the original publication and execution receipts.");
            return new { start, restart, observed.ConfigurationIdentity };
        }
        finally { store.Gate.Release(); }
    }

    private sealed class InProcessTransport(MihomoRuntimeLifecycle lifecycle, Func<PrivilegedOperationKind, PrivilegedOperationFrame, Task> progress)
        : IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default) =>
            MihomoRuntimeOperations.ExecuteAsync(request, () => lifecycle, frame => progress(request.Operation, frame));
    }

    private sealed class ServerPublicationTransport(IPrivilegedOperationTransport inner, Action requireLease, Func<Task>? hold)
        : IPrivilegedOperationTransport
    {
        internal int ConfigureCalls;
        internal int RecoverCalls;

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            requireLease();
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure) ConfigureCalls++;
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover) RecoverCalls++;
            var result = await inner.ExecuteAsync(request, cancellationToken);
            requireLease();
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure && result.Success && hold is not null)
                await hold();
            return result;
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task RemovalFixtureAsync(string root, LinuxMihomoArchiveHost host, string verb, Guid operation)
    {
        var store = Store();
        var sources = new RuntimeSourceStore(Path.Combine(root, "trust"), RuntimeSourceProtection.Protect);
        MihomoRuntimeLifecycle Lifecycle() => new(Archive, store, sources, host, RuntimeSourceProtection.Protect);
        if (verb == "remove-observe")
        {
            var snapshot = await Lifecycle().QueryAsync(CancellationToken.None, operation);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(RuntimeJson.Serialize(snapshot)));
            return;
        }
        var planPath = Path.Combine(root, "tun-removal-plan.json");
        if (verb is "remove-plan-active" or "remove-plan-keep" or "remove-plan-purge")
        {
            var state = store.Read()!.Installation;
            var result = await Lifecycle().PlanAsync("mihomo-acceptance", new(state.InstallationId, state.HostId,
                RuntimeChange.Uninstall, state.StateRevision, 0, 0, null, operation.ToString("N"),
                KeepData: verb != "remove-plan-purge"), CancellationToken.None);
            if (result.Plan is { } plan) await File.WriteAllBytesAsync(planPath, RuntimeJson.Serialize(plan));
            Console.WriteLine(JsonSerializer.Serialize(result));
            return;
        }
        var frozen = RuntimeJson.Read<RuntimeChangePlan>(await File.ReadAllBytesAsync(planPath));
        await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(operation, _ =>
            Task.FromResult(new MihomoCurrentAuthorization(DateTimeOffset.UtcNow.AddMinutes(1), null)));
        var (recover, cut) = verb switch
        {
            "remove-interrupt" => (false, InstallationStage.Verifying),
            "remove-cleanup-interrupt" => (false, InstallationStage.HealthChecking),
            "remove-retry" => (false, (InstallationStage?)null),
            "remove-recover" => (true, (InstallationStage?)null),
            _ => throw new InvalidOperationException("Unexpected TUN removal fixture action.")
        };
        var response = await MihomoRuntimeOperations.ExecuteAsync(new(
            recover ? PrivilegedOperationKind.MihomoRuntimeRecover : PrivilegedOperationKind.MihomoRuntimeExecute,
            OperationId: operation, RuntimeLifecycle: recover
                ? new(Authorization: callback.Channel, RecoveryOperationId: operation)
                : new(Authorization: callback.Channel, FrozenPlan: frozen)), Lifecycle, frame =>
        {
            if (frame.Stage == cut && cut is not null) System.Diagnostics.Process.GetCurrentProcess().Kill();
            return Task.CompletedTask;
        });
        Console.WriteLine(JsonSerializer.Serialize(response));
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

    private sealed class FixtureAdapter(MihomoConfigurationAdapter adapter, bool interruptStop = false,
        bool commit = false, bool interruptCleanup = false, bool interruptRestored = false) : IRuntimeArchiveConfigurationAdapter
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
        public Task<RuntimeActivationEvidence> VerifyAsync(RuntimePendingConfiguration pending, CancellationToken ct) =>
            commit ? adapter.VerifyCandidateAsync(pending, ct) : adapter.VerifyAsync(pending, ct);
        public Task<bool> ObserveStoppedForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct) =>
            adapter.ObserveStoppedForRecoveryAsync(pending, ct);
        public async Task StopForRecoveryAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        {
            await adapter.StopForRecoveryAsync(pending, ct);
            if (interruptStop) System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        public Task ValidateRestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct) => adapter.ValidateRestoreAsync(pending, ct);
        public async Task<RuntimeRestorationEvidence> RestoreAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        {
            var restored = await adapter.RestoreAsync(pending, ct);
            if (interruptRestored) System.Diagnostics.Process.GetCurrentProcess().Kill();
            return restored;
        }
        public Task<RuntimeRestorationEvidence> VerifyRestoredAsync(RuntimePendingConfiguration pending, CancellationToken ct) =>
            adapter.VerifyRestoredAsync(pending, ct);
        public Task CleanupPreparationAsync(RuntimePendingConfiguration pending, CancellationToken ct)
        {
            if (interruptCleanup && pending.PreviousRestored) System.Diagnostics.Process.GetCurrentProcess().Kill();
            return adapter.CleanupPreparationAsync(pending, ct);
        }
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

    private sealed record AuthorizationPaths(string Root) : IProxyPlatformPaths, IProxyControllerSecretStore, IServerModeResolver
    {
        public string GetProtectedConfigurationDirectory() => Path.Combine(Root, "authorization");
        public string GetStateDirectory() => Path.Combine(Root, "authorization-state");
        public string GetEngineVersionsDirectory(string engineId) => throw new NotSupportedException();
        public string GetEngineDataDirectory(string engineId) => throw new NotSupportedException();
        public string GetSanitizedLogDirectory() => throw new NotSupportedException();
        public Task<string> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(FixtureSecret);
        public Task<string?> ReadExistingAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(FixtureSecret);
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
