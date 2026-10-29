using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.ProxyServer;

internal static class InstallationChecks
{
    internal const string FixtureFile = "runtime-progress-fixture";

    internal static async Task RunHelperFixtureAsync()
    {
        var request = await JsonSerializer.DeserializeAsync<PrivilegedOperationRequest>(Console.OpenStandardInput())
            ?? throw new Exception("Missing fixture request");
        var channel = request.RuntimeLifecycle!.Authorization!;
        await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(channel, request.OperationId!.Value, default);
        foreach (var stage in new[] { InstallationStage.Downloading, InstallationStage.Extracting,
            InstallationStage.Verifying, InstallationStage.Activating, InstallationStage.HealthChecking })
        {
            Console.WriteLine(JsonSerializer.Serialize(PrivilegedOperationFrame.Report(stage)));
            await Console.Out.FlushAsync();
            if (stage == InstallationStage.Downloading) await Task.Delay(200);
        }
        Console.WriteLine(JsonSerializer.Serialize(PrivilegedOperationFrame.Completed(Json(new RuntimeSwitchResult(request.OperationId!.Value, RuntimeSwitchOutcome.Succeeded, null)))));
    }

    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        void Reject(string code, Action action)
        {
            try { action(); throw new Exception("Installation unexpectedly accepted: " + code); }
            catch (InstallationException ex) when (ex.ProblemCode == code) { count++; }
        }
        async Task RejectAsync(string code, Func<Task> action)
        {
            try { await action(); throw new Exception("Installation unexpectedly accepted: " + code); }
            catch (InstallationException ex) when (ex.ProblemCode == code) { count++; }
        }
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-runtime-installation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ProxyServerStore(new Paths(root), DataProtectionProvider.Create(new DirectoryInfo(root + "/keys")));
            var authorization = new ProxyServerRuntimeAuthorizationProvider(store, new NoCertificates());
            var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
            var installation = new RuntimeInstallation(Guid.NewGuid(), "fixture-host", "sing-box", 0, RuntimeOwnership.Absent, target, null, false);
            var artifact = new RuntimeArtifact("fixture", new string('a', 64), 1024, "zip", "sing-box", "key", new(1024, 8, [new("sing-box", 4, new string('b', 64), true)]));
            var release = new RuntimeRelease("fixture-release", "sing-box", "v1.14.3", "1.14.3", RuntimeChannel.Stable, "fixture-source", target,
                artifact, SingBoxRuntimeSupport.Capability, true, RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, null);
            var plan = new RuntimeChangePlan(new(installation.InstallationId, installation.HostId, RuntimeChange.Install, 0, 1, 1, release.ReleaseId, "install-fixture"), "sing-box", release, null);
            var input = new ManagedRuntimeInstallationRequest(true, plan);
            var entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executeCalls = 0;
            var recoveryCalls = 0;
            var cancelRequests = 0;
            var busy = 0;
            RuntimeAuthorizationChannel? observedChannel = null;
            var snapshot = new RuntimeInstallationSnapshot(installation, null, null, null, null, null, null);
            var transport = new Transport(async (request, ct) =>
            {
                switch (request.Operation)
                {
                    case PrivilegedOperationKind.SingBoxRuntimeExecute:
                        executeCalls++;
                        observedChannel = request.RuntimeLifecycle!.Authorization!;
                        Check(request.OperationId == observedChannel.OperationId && request.RuntimeLifecycle.FrozenPlan!.Request.IdempotencyKey == "install-fixture",
                            "Coordinator operation identity and frozen key reach the Helper composition");
                        await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(observedChannel, request.OperationId!.Value, ct);
                        entered.TrySetResult(request.OperationId!.Value);
                        await finish.Task;
                        return Json(new RuntimeSwitchResult(request.OperationId!.Value, RuntimeSwitchOutcome.Succeeded, null));
                    case PrivilegedOperationKind.SingBoxRuntimeCancel:
                        cancelRequests++;
                        return Json(new RuntimeCancelRequestResult(request.RuntimeLifecycle!.CancelOperationId!.Value, true));
                    case PrivilegedOperationKind.SingBoxRuntimeQuery: return Json(snapshot);
                    case PrivilegedOperationKind.SingBoxRuntimeRecover:
                        recoveryCalls++;
                        if (busy-- > 0) return new(false, Error: "runtime.instance_busy", ProblemCode: PrivilegedProblemCode.Conflict);
                        return Json(new RuntimeSwitchResult(request.RuntimeLifecycle!.RecoveryOperationId!.Value, RuntimeSwitchOutcome.Interrupted, "runtime.operation_interrupted"));
                    default: throw new Exception("Unexpected fixture operation");
                }
            });
            var runtime = new SingBoxRuntimeService(transport, store, authorization, new TestMode(), new DirectDownloads());
            var domain = new SingBoxInstallationService(runtime);
            var operations = new InstallationOperationStore(new Environment(root));
            using var lifetime = new Lifetime();
            var coordinator = new InstallationCoordinator(operations, [domain], lifetime);
            await coordinator.StartAsync(default);
            JsonElement Options(ManagedRuntimeInstallationRequest request) => JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Reject(InstallationProblemCodes.ConfirmationRequired, () => coordinator.Start(domain.Id, InstallationOperationKind.Install, Options(input with { Confirmed = false }), "actor", "install-fixture"));
            Reject(InstallationProblemCodes.IdempotencyConflict, () => coordinator.Start(domain.Id, InstallationOperationKind.Install, Options(input), "actor", "different-key"));
            Reject(InstallationProblemCodes.InvalidRequest, () => coordinator.Start(domain.Id, InstallationOperationKind.Rollback, Options(input), "actor", "install-fixture"));
            // Removal is supported, but it is the one change that names no release; a release-bearing removal is not a removal.
            Reject(InstallationProblemCodes.InvalidRequest, () => coordinator.Start(domain.Id, InstallationOperationKind.Uninstall, Options(input), "actor", "install-fixture"));
            Reject(InstallationProblemCodes.InvalidRequest, () => coordinator.Start(domain.Id, InstallationOperationKind.Install,
                JsonSerializer.SerializeToElement(new { confirmed = true, plan, shell = "forbidden" }), "actor", "install-fixture"));
            Check(operations.Read().Length == 0 && executeCalls == 0, "Invalid submissions never create a ledger job or call Helper");
            var job = coordinator.Start(domain.Id, InstallationOperationKind.Install, Options(input), "actor", "install-fixture");
            Check(await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)) == job.OperationId, "Existing coordinator supplies the authoritative operation ID");
            var replay = coordinator.Start(domain.Id, InstallationOperationKind.Install, Options(input), "actor", "install-fixture");
            Check(replay.OperationId == job.OperationId && executeCalls == 1, "Retry joins one persisted job without a second root operation");
            Reject(InstallationProblemCodes.IdempotencyConflict, () => coordinator.Start(domain.Id, InstallationOperationKind.Install,
                Options(input with { Plan = plan with { Release = release with { Artifact = artifact with { Sha256 = new string('c', 64) } } } }), "actor", "install-fixture"));
            Reject(InstallationProblemCodes.ResourceConflict, () => coordinator.Start(domain.Id, InstallationOperationKind.Install, Options(input), "another-actor", "install-fixture"));
            // The root transaction outlives the worker token, so the coordinator records the request instead of
            // publishing a cancellation the Host never performed.
            var accepted = await coordinator.CancelAsync(job.OperationId, default);
            Check(!accepted.Cancellable && cancelRequests == 1,
                "A runtime cancellation is delivered to the Host instead of aborting the local worker");
            Check(coordinator.Get(job.OperationId)!.Operation.State == InstallationOperationState.Running,
                "Only the root transaction may acknowledge a cancellation, so the ledger stays Running");
            await RejectAsync(InstallationProblemCodes.NotCancellable, () => coordinator.CancelAsync(job.OperationId, default));
            finish.SetResult();
            await UntilAsync(() => !InstallationOperationStore.Active(coordinator.Get(job.OperationId)!.Operation));
            Check(coordinator.Get(job.OperationId)!.Operation.State == InstallationOperationState.Succeeded, "Job success requires a matching Helper receipt");
            var ledger = File.ReadAllText(root + "/data/installation-operations.json");
            Check(!ledger.Contains(observedChannel!.Secret, StringComparison.Ordinal) && !ledger.Contains(release.ReleaseId, StringComparison.Ordinal),
                "Ledger stores fingerprints and outcomes, never callback secrets or the raw frozen request");
            Check(InstallationEndpoints.Capability(InstallationServiceId.SingBox) == HostElevationCapability.SingBoxInstall
                && InstallationEndpoints.Target(InstallationServiceId.SingBox) == "singbox", "Sing-box uses an explicit exact-resource installation capability");
            var wire = JsonSerializer.Serialize(job with { Service = InstallationServiceId.SingBox, Kind = InstallationOperationKind.Rollback }, RelaxKonOSJsonOptions.Default);
            Check(wire.Contains("\"service\":\"singBox\"", StringComparison.Ordinal) && wire.Contains("\"kind\":\"rollback\"", StringComparison.Ordinal),
                "Current REST enum spelling matches the Android sing-box/rollback observation contract");
            Reject(InstallationProblemCodes.NotSupported, () => new FrpInstallationService(null!, null!, null!, null!, null!).Validate(InstallationOperationKind.Rollback, Options(input), "install-fixture"));
            // The Mihomo ledger entry now validates a frozen plan, so a plan-less rollback body is never accepted.
            Reject(InstallationProblemCodes.InvalidRequest, () => new MihomoRuntimeInstallationService(null!, null!, null!, null!, null!, null!, null!)
                .Validate(InstallationOperationKind.Rollback, JsonSerializer.SerializeToElement(new { confirmed = true }), "install-fixture"));

            snapshot = snapshot with { Receipt = new(job.OperationId, RuntimeSwitchOutcome.Succeeded, null) };
            Check((await domain.RecoverAsync(coordinator.Get(job.OperationId)!.Operation, default)).State == InstallationOperationState.Succeeded,
                "Startup reads the exact durable Helper receipt");
            snapshot = snapshot with { Receipt = null, ActiveRevisionId = Guid.NewGuid(), MeasuredVersion = "1.14.3" };
            Check((await domain.RecoverAsync(coordinator.Get(job.OperationId)!.Operation, default)).State == InstallationOperationState.Interrupted,
                "Program presence and version cannot substitute for an operation receipt");
            snapshot = snapshot with { PendingOperationId = Guid.NewGuid() };
            Check((await domain.RecoverAsync(coordinator.Get(job.OperationId)!.Operation, default)).ProblemCode == "runtime.recovery_target_conflict" && recoveryCalls == 0,
                "Startup cannot compensate another operation");
            snapshot = snapshot with { PendingOperationId = job.OperationId };
            busy = 2;
            Check((await domain.RecoverAsync(coordinator.Get(job.OperationId)!.Operation, default)).State == InstallationOperationState.Interrupted
                && recoveryCalls == 3 && executeCalls == 1, "An OS-held instance lock is waited out without restarting the original worker");
            // A cancellation the Host actually performed is an outcome of its own, not a failure.
            var cancelTransport = new Transport((request, ct) => Task.FromResult(request.Operation switch
            {
                PrivilegedOperationKind.SingBoxRuntimeExecute => Json(new RuntimeSwitchResult(request.OperationId!.Value,
                    RuntimeSwitchOutcome.Cancelled, "runtime.operation_cancelled")),
                PrivilegedOperationKind.SingBoxRuntimeCancel => Json(new RuntimeCancelRequestResult(
                    request.RuntimeLifecycle!.CancelOperationId!.Value, true)),
                _ => throw new Exception("Unexpected fixture operation")
            }));
            var cancelledDomain = new SingBoxInstallationService(new SingBoxRuntimeService(cancelTransport, store, authorization, new TestMode(), new DirectDownloads()));
            var cancelledCoordinator = new InstallationCoordinator(new InstallationOperationStore(new Environment(root)), [cancelledDomain], lifetime);
            await cancelledCoordinator.StartAsync(default);
            var cancelledJob = cancelledCoordinator.Start(cancelledDomain.Id, InstallationOperationKind.Install,
                Options(input with { Plan = plan with { Request = plan.Request with { IdempotencyKey = "cancel-fixture" } } }),
                "actor", "cancel-fixture");
            await UntilAsync(() => !InstallationOperationStore.Active(cancelledCoordinator.Get(cancelledJob.OperationId)!.Operation));
            var cancelledEntry = cancelledCoordinator.Get(cancelledJob.OperationId)!.Operation;
            Check(cancelledEntry.State == InstallationOperationState.Cancelled && cancelledEntry.Stage == InstallationStage.Cancelled
                && cancelledEntry.ProblemCode == "runtime.operation_cancelled",
                "A cancellation the Host performed is recorded as Cancelled, never as a failed installation");
            await cancelledCoordinator.StopAsync(default);

            await coordinator.StopAsync(default);

            var interrupted = operations.Create(domain.Id, InstallationOperationKind.Upgrade, "actor", "startup-recovery", new string('d', 64), domain.Resources, out _);
            operations.Update(interrupted.Operation.OperationId, value => value with { State = InstallationOperationState.Running, Stage = InstallationStage.Activating }, "started");
            snapshot = snapshot with { PendingOperationId = interrupted.Operation.OperationId, Receipt = null };
            var restarted = new InstallationCoordinator(new InstallationOperationStore(new Environment(root)), [domain], lifetime);
            await restarted.StartAsync(default);
            Check(restarted.Get(interrupted.Operation.OperationId)!.Operation.State == InstallationOperationState.Interrupted,
                "A new coordinator reconciles a persisted running job through target-bound compensation");
            await restarted.StopAsync(default);

            var rollback = input with { Plan = plan with { Request = plan.Request with { Change = RuntimeChange.Rollback, IdempotencyKey = "rollback-fixture" } } };
            Check(domain.Validate(InstallationOperationKind.Rollback, Options(rollback), "rollback-fixture") is ManagedRuntimeInstallationRequest,
                "Rollback retains its own installation operation kind");
            var removal = input with { Plan = plan with { Release = null, Request = plan.Request with { Change = RuntimeChange.Uninstall, ReleaseId = null, IdempotencyKey = "removal-fixture" } } };
            Check(domain.Validate(InstallationOperationKind.Uninstall, Options(removal), "removal-fixture") is ManagedRuntimeInstallationRequest,
                "Removal is a first-class ledger kind that carries no release");
            Reject(InstallationProblemCodes.InvalidRequest, () => domain.Validate(InstallationOperationKind.Uninstall,
                Options(removal with { Plan = removal.Plan with { Request = removal.Plan.Request with { ReleaseId = release.ReleaseId } } }), "removal-fixture"));

            foreach (var stage in Enum.GetValues<InstallationStage>())
            {
                var valid = stage is not (InstallationStage.Queued or InstallationStage.Completed or InstallationStage.Failed or InstallationStage.Cancelled or InstallationStage.Interrupted);
                Check(PrivilegedOperationFrame.Report(stage).IsValid() == valid, "Only actual nonterminal Helper stages are valid: " + stage);
            }
            Check(!PrivilegedOperationFrame.Report(InstallationStage.Verifying, 50).IsValid()
                && PrivilegedOperationFrame.Report(InstallationStage.Downloading, 50).IsValid(), "Percentages are accepted only for measured transfer/package stages");

            if (OperatingSystem.IsLinux())
            {
                var processRoot = root + "/process"; Directory.CreateDirectory(processRoot);
                var marker = processRoot + "/" + FixtureFile; await File.WriteAllTextAsync(marker, "fixture");
                var runner = new LocalPrivilegedOperationRunner(new PrivilegedHelperOptions { HelperPath = marker, SudoPath = System.Environment.ProcessPath! },
                    NullLogger<LocalPrivilegedOperationRunner>.Instance);
                var processRuntime = new SingBoxRuntimeService(runner, store, authorization, new TestMode(), new DirectDownloads());
                var processDomain = new SingBoxInstallationService(processRuntime);
                using var processLifetime = new Lifetime();
                var processOperations = new InstallationOperationStore(new Environment(processRoot));
                var processCoordinator = new InstallationCoordinator(processOperations, [processDomain], processLifetime);
                await processCoordinator.StartAsync(default);
                var processJob = processCoordinator.Start(processDomain.Id, InstallationOperationKind.Install, Options(input), "actor", "install-fixture");
                await UntilAsync(() => processCoordinator.Get(processJob.OperationId)!.Operation.Stage == InstallationStage.Downloading);
                processLifetime.StopApplication();
                await UntilAsync(() => !InstallationOperationStore.Active(processCoordinator.Get(processJob.OperationId)!.Operation));
                Check(processCoordinator.Get(processJob.OperationId)!.Operation.State == InstallationOperationState.Succeeded,
                    "After submission, host shutdown cannot discard the real Helper-process result through a progress callback");
                var audit = File.ReadAllText(processRoot + "/data/installation-operations.json");
                Check(new[] { "Downloading", "Extracting", "Verifying", "Activating", "HealthChecking" }.All(audit.Contains),
                    "Real NDJSON transport persists Helper stages in the existing installation audit");
                await processCoordinator.StopAsync(default);
            }
        }
        finally { Directory.Delete(root, true); }
        return count;
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(10, timeout.Token);
    }
    private static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
    private sealed class Transport(Func<PrivilegedOperationRequest, CancellationToken, Task<PrivilegedOperationResult>> run) : IPrivilegedOperationTransport
    { public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default) => run(request, ct); }
    private sealed class TestMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => true;
    }
    private sealed class NoCertificates : IProxyServerCertificateSource
    { public Task<RelaxKonOS.Protocol.ProxyServer.ProxyServerTlsMaterial> ReadAsync(RelaxKonOS.Protocol.ProxyServer.ProxyServerInbound inbound, CancellationToken ct) => throw new Exception("No certificate read expected"); }
    private sealed class Paths(string root) : IProxyPlatformPaths
    {
        public string GetEngineVersionsDirectory(string engineId) => root + "/versions";
        public string GetProtectedConfigurationDirectory() => root + "/config";
        public string GetEngineDataDirectory(string engineId) => root + "/engine";
        public string GetStateDirectory() => root + "/state";
        public string GetSanitizedLogDirectory() => root + "/logs";
    }
    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "RuntimeInstallationChecks";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => new(true);
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }
}
