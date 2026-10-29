using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoInstallationPublicationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        async Task Denied(string code, Func<Task> action)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected installation publication refusal: " + code);
        }
        foreach (var mode in new[] { "success", "failed" })
        {
            using var f = new Fixture { Mode = mode };
            var result = await f.InstallAsync();
            var state = await f.Store.ReadAsync(default);
            Check(result.Outcome == (mode == "success" ? MihomoPublicationOutcome.Confirmed : MihomoPublicationOutcome.Compensated)
                && state.Pending is null, "Install authority resolves the measured Helper result: " + mode);
            Check((state.Published is not null) == (mode == "success") && File.Exists(f.Active) == (mode == "success"),
                "Publication and mirror exist only after a confirmed installation: " + mode);
            Check(f.GateHeld && f.CandidateOnly && f.Installs == 1, "The frozen candidate is supplied under the publication gate: " + mode);
            Check((await f.InstallAsync()).Outcome == result.Outcome && f.Installs == 1, "Install replay never reexecutes an accepted target: " + mode);
            await Denied("idempotency_conflict", () => f.Service.InstallAsync(f.Operation, 0, f.Plan with
                { Request = f.Plan.Request with { IdempotencyKey = "changed-install" } }, f.Material, default));
        }
        using (var f = new Fixture { Mode = "lost-response" })
        {
            try { await f.InstallAsync(); throw new Exception("Lost install response was not simulated"); }
            catch (IOException) { count++; }
            Check((await f.Store.ReadAsync(default)) is { Published: null, Pending: not null } && !File.Exists(f.Active),
                "Lost installed receipt leaves candidate authority pending without a Server mirror");
            f.Recreate();
            Check((await f.Service.RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && f.Installs == 1 && f.Recoveries == 0 && await File.ReadAllTextAsync(f.Active) == f.Material.Yaml,
                "Restart confirms the exact installed receipt without preparing or installing again");
        }
        using (var f = new Fixture { Mode = "recovery" })
        {
            Check((await f.InstallAsync()).Outcome == MihomoPublicationOutcome.RecoveryRequired
                && (await f.Store.ReadAsync(default)).Published is null, "Incomplete install compensation never creates a confirmed publication");
            Check((await f.Service.RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Compensated
                && f.Recoveries == 1 && f.Installs == 1 && !File.Exists(f.Active), "Fresh install recovery compensates to measured absence");
        }
        using (var f = new Fixture())
        {
            await f.Store.BeginInstallationAsync(f.Operation, 0, f.Material, f.Plan, default);
            Check((await f.Service.RecoverAsync(f.Operation, default)) is { Outcome: MihomoPublicationOutcome.NotStarted, Receipt: null }
                && f.Recoveries == 1, "Server-only acceptance requires Helper lock-backed absence before closing as NotStarted");
            Check((await f.InstallAsync()).Outcome == MihomoPublicationOutcome.NotStarted && f.Installs == 0,
                "NotStarted install replay does not invent a receipt or submit a new install");
        }
        using (var f = new Fixture())
        {
            await f.Store.BeginInstallationAsync(f.Operation, 0, f.Material, f.Plan, default);
            f.Commit(RuntimeSwitchOutcome.Succeeded);
            Directory.CreateDirectory(Path.GetDirectoryName(f.Active)!);
            await File.WriteAllTextAsync(f.Active, f.Material.Yaml);
            f.Recreate();
            using var startup = new MihomoPublicationRecoveryService(f.Store, f.Configuration, f.Service, f.Removal, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.StartAsync(default); await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Check((await f.Store.ReadAsync(default)) is { Pending: null, Published: not null } && f.Installs == 0,
                "Startup dispatches installation Pending and confirms the exact mirror-write crash window");
        }
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f.Active)!);
            await File.WriteAllTextAsync(f.Active, "# unknown existing configuration");
            await Denied("publication_unconfirmed", () => f.InstallAsync());
            Check(f.Installs == 0 && (await f.Store.ReadAsync(default)).Pending is null,
                "Unknown pre-existing Server files are never adopted by first install");
        }
        foreach (var mode in new[] { "wrong-version", "wrong-revision", "mirror-drift" })
        {
            using var f = new Fixture { Mode = mode };
            await Denied("publication_unconfirmed", () => f.InstallAsync());
            Check((await f.Store.ReadAsync(default)) is { Published: null, Pending: not null },
                "Conflicting installed evidence keeps publication pending: " + mode);
        }
        using (var f = new Fixture())
        {
            f.Secret = "revoked-installation-secret";
            Check((await f.InstallAsync()).Outcome == MihomoPublicationOutcome.Compensated
                && (await f.Store.ReadAsync(default)).Published is null && !File.Exists(f.Active),
                "Current secret revocation cannot manufacture a confirmed first publication");
        }
        var bootstrap = MihomoInstallationBootstrapService.CreateMaterial(
            new ProxySettingsDto(true, true, true, true, true, "warning", 18888),
            new MihomoControllerOptions { Endpoint = new Uri("http://127.0.0.1:19090/") }, "bootstrap-secret");
        var description = MihomoConfigurationPolicy.Validate(bootstrap);
        Check(description.ResourcePaths.Count == 0 && !bootstrap.NetworkTransitionRequired
            && bootstrap.ControllerPort == 19090 && bootstrap.ControllerSecret == "bootstrap-secret",
            "Bootstrap uses bounded server-owned material without resource or host-network transitions");
        Check(bootstrap.Yaml.Contains("MATCH,DIRECT") && bootstrap.Yaml.Contains("18888"),
            "Bootstrap preserves selected listener settings and provides a DIRECT baseline");
        using (var f = new Fixture())
        {
            await f.Store.BeginInstallationAsync(f.Operation, 0, f.Material, f.Plan, default);
            f.Commit(RuntimeSwitchOutcome.Succeeded);
            var selection = new MihomoInstallationBootstrapService(f.Store, f.Service, new UnreadableSettings(), f, new());
            Check((await selection.InstallAsync(f.Operation, f.Plan, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && f.Installs == 0, "Bootstrap replay uses frozen pending material without reading settings or generating credentials");
        }
        using (var f = new Fixture())
        {
            f.AllowInitialMaterial = true;
            f.Material = MihomoInstallationBootstrapService.CreateMaterial(f.Settings,
                new() { Endpoint = new("http://127.0.0.1:19090/") }, f.Secret);
            var selection = new MihomoInstallationBootstrapService(f.Store, f.Service, f, f,
                new() { Endpoint = new("http://127.0.0.1:19090/") });
            Check((await selection.InstallAsync(f.Operation, f.Plan, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && f.SettingsReads == 1 && f.SecretCreations == 1 && f.GateHeld,
                "First explicit installation selects settings and credentials once under the publication gate");
            f.AllowInitialMaterial = false;
            Check((await selection.InstallAsync(f.Operation, f.Plan, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && f.SettingsReads == 1 && f.SecretCreations == 1 && f.Installs == 1,
                "Confirmed installation replay retains exact published material without regenerating settings or credentials");
        }
        using (var f = new Fixture())
        {
            var domain = f.LedgerAdapter();
            JsonElement Options(RuntimeChangePlan plan, bool confirmed = true) => JsonSerializer.SerializeToElement(
                new MihomoInstallationRequest(confirmed, plan), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            foreach (var (kind, change) in new[] { (InstallationOperationKind.Install, RuntimeChange.Install),
                (InstallationOperationKind.Upgrade, RuntimeChange.Update), (InstallationOperationKind.Repair, RuntimeChange.Repair),
                (InstallationOperationKind.Rollback, RuntimeChange.Rollback), (InstallationOperationKind.Uninstall, RuntimeChange.Uninstall) })
            {
                var plan = f.Plan with { Request = f.Plan.Request with { Change = change, ReleaseId = change == RuntimeChange.Uninstall ? null : f.Plan.Release!.ReleaseId },
                    Release = change == RuntimeChange.Uninstall ? null : f.Plan.Release };
                Check(domain.Validate(kind, Options(plan), f.Plan.Request.IdempotencyKey) is MihomoInstallationRequest,
                    "Managed Mihomo ledger accepts the exact current plan contract: " + change);
            }
            void Refuse(string code, Func<object> call)
            {
                try { call(); }
                catch (InstallationException ex) when (ex.ProblemCode == code) { count++; return; }
                throw new Exception("Mihomo ledger accepted an invalid plan");
            }
            Refuse(InstallationProblemCodes.IdempotencyConflict, () => domain.Validate(InstallationOperationKind.Install, Options(f.Plan), "other-key"));
            Refuse(InstallationProblemCodes.InvalidRequest, () => domain.Validate(InstallationOperationKind.Install, Options(f.Plan with { RuntimeId = "sing-box" }), f.Plan.Request.IdempotencyKey));
            Refuse(InstallationProblemCodes.InvalidRequest, () => domain.Validate(InstallationOperationKind.Rollback, Options(f.Plan), f.Plan.Request.IdempotencyKey));
            Refuse(InstallationProblemCodes.ConfirmationRequired, () => domain.Validate(InstallationOperationKind.Install, Options(f.Plan, false), f.Plan.Request.IdempotencyKey));
        }
        foreach (var mode in new[] { "success", "failed", "lost-response", "cancelled", "interrupted", "recovery", "lost-response-stop" })
        {
            using var f = new Fixture { Mode = mode, LedgerMode = true, AllowInitialMaterial = true };
            using var lifetime = new ManagedRuntimeLedgerFixture.Lifetime();
            if (mode == "lost-response-stop") f.ResponseLost = lifetime.StopApplication;
            var ledger = new InstallationOperationStore(new ManagedRuntimeLedgerFixture.Environment(f.LedgerRoot));
            var domain = f.LedgerAdapter();
            var coordinator = new InstallationCoordinator(ledger, [domain], lifetime);
            await coordinator.StartAsync(default);
            var request = new MihomoInstallationRequest(true, f.Plan);
            var input = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var operation = coordinator.Start(InstallationServiceId.Mihomo, InstallationOperationKind.Install, input, "ledger-actor", f.Plan.Request.IdempotencyKey);
            var completed = await ManagedRuntimeLedgerFixture.TerminalAsync(coordinator, operation.OperationId);
            var expected = mode switch
            {
                "success" or "lost-response" or "lost-response-stop" => InstallationOperationState.Succeeded,
                "cancelled" => InstallationOperationState.Cancelled,
                "interrupted" => InstallationOperationState.Interrupted,
                _ => InstallationOperationState.Failed
            };
            Check(completed.State == expected && f.Installs == 1, "Mihomo ledger reflects durable native/publication outcome: " + mode);
            if (mode == "lost-response-stop")
                Check(ledger.Read().Single().Operation.State == InstallationOperationState.Succeeded && f.Installs == 1,
                    "Shutdown during response loss never replaces a confirmed native result with an inferred interruption");
            else Check(coordinator.Start(InstallationServiceId.Mihomo, InstallationOperationKind.Install, input, "ledger-actor", f.Plan.Request.IdempotencyKey).OperationId == operation.OperationId
                && f.Installs == 1, "Mihomo ledger request replay never installs again: " + mode);
            Check(mode == "recovery" ? (await f.Store.ReadAsync(default)).Pending is not null
                : (await f.Store.ReadAsync(default)).Pending is null, "Mihomo ledger does not erase a pending publication: " + mode);
            if (mode == "lost-response")
                Check(f.Recoveries == 0 && f.SettingsReads == 1 && f.SecretCreations == 1,
                    "Transport-loss reconciliation confirms exact install without native replay or new material");
            var ledgerText = await File.ReadAllTextAsync(Path.Combine(f.LedgerRoot, "data", "installation-operations.json"));
            Check(!ledgerText.Contains(f.Secret, StringComparison.Ordinal) && !ledgerText.Contains("MATCH,DIRECT", StringComparison.Ordinal),
                "Mihomo ledger excludes callback secrets and published YAML: " + mode);
            await coordinator.StopAsync(default);
        }
        foreach (var mode in new[] { "success", "failed", "cancelled", "interrupted", "recovery", "lost-response" })
        {
            using var f = new Fixture { Mode = mode, LedgerMode = true };
            await f.SeedConfirmedAsync();
            using var lifetime = new ManagedRuntimeLedgerFixture.Lifetime();
            var ledger = new InstallationOperationStore(new ManagedRuntimeLedgerFixture.Environment(f.LedgerRoot));
            var coordinator = new InstallationCoordinator(ledger, [f.LedgerAdapter()], lifetime);
            await coordinator.StartAsync(default);
            var repair = f.Plan with { Request = f.Plan.Request with { Change = RuntimeChange.Repair }, PreviousReleaseId = f.Plan.Release!.ReleaseId };
            var operation = coordinator.Start(InstallationServiceId.Mihomo, InstallationOperationKind.Repair,
                JsonSerializer.SerializeToElement(new MihomoInstallationRequest(true, repair), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                "repair-actor", repair.Request.IdempotencyKey);
            var terminal = await ManagedRuntimeLedgerFixture.TerminalAsync(coordinator, operation.OperationId);
            var expected = mode switch
            {
                "success" or "lost-response" => InstallationOperationState.Succeeded,
                "cancelled" => InstallationOperationState.Cancelled, "interrupted" => InstallationOperationState.Interrupted,
                _ => InstallationOperationState.Failed
            };
            Check(terminal.State == expected && f.Installs == 1 && f.SettingsReads == 0 && f.SecretCreations == 0,
                "Program-only ledger outcome preserves the original native result without bootstrap: " + mode);
            Check((await f.Store.ReadAsync(default)) is { Pending: null, Published: not null }
                && (mode != "failed" || terminal.ProblemCode == "runtime.native_configuration_invalid"),
                "Program-only ledger preserves confirmed configuration and precise failure code: " + mode);
            await coordinator.StopAsync(default);
        }
        using (var f = new Fixture { Mode = "wait-cancel", LedgerMode = true, AllowInitialMaterial = true })
        {
            using var lifetime = new ManagedRuntimeLedgerFixture.Lifetime();
            var ledger = new InstallationOperationStore(new ManagedRuntimeLedgerFixture.Environment(f.LedgerRoot));
            var coordinator = new InstallationCoordinator(ledger, [f.LedgerAdapter()], lifetime);
            await coordinator.StartAsync(default);
            var input = JsonSerializer.SerializeToElement(new MihomoInstallationRequest(true, f.Plan), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var operation = coordinator.Start(InstallationServiceId.Mihomo, InstallationOperationKind.Install, input, "cancel-actor", f.Plan.Request.IdempotencyKey);
            await f.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(coordinator.Get(operation.OperationId)!.Operation is { Stage: InstallationStage.Downloading, Cancellable: true },
                "Mihomo ledger exposes cancellation only from the Host's preparation stage");
            await coordinator.CancelAsync(operation.OperationId, default);
            var terminal = await ManagedRuntimeLedgerFixture.TerminalAsync(coordinator, operation.OperationId);
            Check(terminal.State == InstallationOperationState.Cancelled && f.CancelDeliveries == 1
                && (await f.Store.ReadAsync(default)).Pending is null && (await f.Store.ReadAsync(default)).Published is null,
                "Mihomo ledger delivers cancellation without releasing its worker before root compensation");
            await coordinator.StopAsync(default);
        }
        using (var f = new Fixture { LedgerMode = true })
        {
            var result = await f.LedgerAdapter().RecoverAsync(new(f.Operation, InstallationServiceId.Mihomo, InstallationOperationKind.Install,
                InstallationOperationState.Running, InstallationStage.Preflight, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false), default);
            Check(result.State == InstallationOperationState.Interrupted && result.ProblemCode == "runtime.operation_not_started"
                && f.Recoveries == 1 && f.SecretCreations == 0 && f.SettingsReads == 0,
                "Ledger recovery before publication acceptance proves Helper lock-backed absence without creating material");
        }
        using (var f = new Fixture { LedgerMode = true })
        {
            await f.Store.BeginInstallationAsync(f.Operation, 0, f.Material, f.Plan, default);
            f.TransientRecoveryFailures = 1;
            var result = await f.LedgerAdapter().RecoverAsync(new(f.Operation, InstallationServiceId.Mihomo, InstallationOperationKind.Install,
                InstallationOperationState.Running, InstallationStage.Preflight, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false), default);
            Check(result.State == InstallationOperationState.Interrupted && f.Recoveries == 2
                && (await f.Store.ReadAsync(default)).Pending is null && f.SecretCreations == 0,
                "Busy install recovery retries the same named publication and never restarts installation");
        }
        return count;
    }

    private sealed class UnreadableSettings : IProxySettingsService
    {
        public Task<ProxySettingsDto> GetAsync(CancellationToken ct) => throw new Exception("Replay read settings");
        public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken ct) => throw new Exception("Bootstrap changed settings");
        public Task<string?> ReconcileSystemProxyAsync(bool startup, CancellationToken ct) => throw new Exception("Bootstrap activated host proxy");
    }

    private sealed class Fixture : IDisposable, IProxyPlatformPaths, IProxyControllerSecretStore, IPrivilegedOperationTransport, IServerModeResolver, IProxySettingsService
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mihomo-install-publication-" + Guid.NewGuid().ToString("N"));
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        internal Guid Operation { get; private set; } = Guid.NewGuid();
        internal string Mode = "success";
        internal string Secret = "installation-publication-secret";
        internal int Installs, Recoveries;
        internal bool AllowInitialMaterial;
        internal bool LedgerMode;
        internal Action? ResponseLost;
        internal int TransientRecoveryFailures;
        internal int CancelDeliveries;
        internal TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal string LedgerRoot => root;
        internal MihomoRuntimeInstallationService LedgerAdapter()
        {
            var provider = new MihomoRuntimeAuthorizationProvider(Store, this, this);
            var runtime = new MihomoRuntimeService(this, Store, provider, this, new DirectDownloads());
            return new(Store, runtime, new(Store, Service, this, this, new() { Endpoint = new("http://127.0.0.1:19090/") }), Service,
                new(Store, runtime, this, MihomoRemovalHostFixture.Files(this)), null!, null!);
        }

        internal int SettingsReads, SecretCreations;
        internal ProxySettingsDto Settings { get; } = new(false, false, true, false, false, "info", 18888);
        internal bool GateHeld, CandidateOnly;
        internal string Active => Path.Combine(GetProtectedConfigurationDirectory(), "active.yaml");
        internal MihomoPublishedRuntime Material { get; set; }
        internal RuntimeChangePlan Plan { get; private set; }
        internal RuntimeInstallationSnapshot Snapshot { get; private set; }
        internal MihomoRemovalPublicationService Removal => new(Store, new(this, Store, new(Store, this, this), this, new DirectDownloads()), this, MihomoRemovalHostFixture.Files(this));
        internal MihomoPublicationStore Store { get; private set; } = null!;
        internal MihomoInstallationPublicationService Service { get; private set; } = null!;
        internal MihomoConfigurationPublicationService Configuration { get; private set; } = null!;
        internal Fixture()
        {
            Directory.CreateDirectory(root);
            Material = MihomoConfigurationPolicy.Describe("mixed-port: 18888\nexternal-controller: 127.0.0.1:19090\nsecret: installation-publication-secret\nrules:\n  - MATCH,DIRECT\n").Material;
            var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
            var artifact = new RuntimeArtifact("fixture", new string('a', 64), 4096, "gz", "mihomo", "signed-manifest:key",
                new(4096, 1, [new("mihomo", 16, new string('b', 64), true)]));
            var release = new RuntimeRelease("release-1", "mihomo", "v1.19.30", "1.19.30", RuntimeChannel.Stable, "fixture-source", target,
                artifact, "mihomo-v1", true, RuntimeTrust.Confirmed, RuntimeEvidence.NativeConfiguration, DateTimeOffset.UtcNow);
            var installation = new RuntimeInstallation(Guid.NewGuid(), "install-publication-host", "mihomo", 0, RuntimeOwnership.Absent, target, null, false);
            Snapshot = new(installation, null, null, null, null, null, null);
            Plan = new(new(installation.InstallationId, installation.HostId, RuntimeChange.Install, 0, 1, 1, release.ReleaseId, "server-install"),
                "mihomo", release, null);
            Recreate();
        }
        internal void Recreate()
        {
            Store = new(this, protection);
            var provider = new MihomoRuntimeAuthorizationProvider(Store, this, this);
            var runtime = new MihomoRuntimeService(this, Store, provider, this, new DirectDownloads());
            Service = new(Store, provider, runtime, this);
            Configuration = new(Store, provider, runtime, this);
        }
        internal Task<MihomoPublicationResult> InstallAsync() => Service.InstallAsync(Operation, 0, Plan, Material, default);
        internal async Task SeedConfirmedAsync()
        {
            var operation = Guid.NewGuid();
            await Store.BeginAsync(operation, 0, Material, default);
            Directory.CreateDirectory(Path.GetDirectoryName(Active)!);
            await File.WriteAllTextAsync(Active, Material.Yaml);
            await Store.ConfirmAsync(operation, MihomoPublicationStore.Identity(Material.Yaml), default);
            Snapshot = Snapshot with { Installation = Snapshot.Installation with { Ownership = RuntimeOwnership.Managed, ActiveReleaseId = Plan.Release!.ReleaseId },
                ActiveRevisionId = Guid.NewGuid(), MeasuredVersion = "1.19.30", ConfigurationIdentity = MihomoPublicationStore.Identity(Material.Yaml) };
        }
        internal void Commit(RuntimeSwitchOutcome outcome)
        {
            var success = outcome == RuntimeSwitchOutcome.Succeeded;
            if (Plan.Request.Change != RuntimeChange.Install)
            {
                Snapshot = Snapshot with { Installation = Snapshot.Installation with { StateRevision = 5, RecoveryRequired = false },
                    ActiveRevisionId = success ? Operation : Snapshot.ActiveRevisionId, PendingOperationId = null,
                    Receipt = new(Operation, outcome, success ? null : "runtime.native_configuration_invalid") };
                return;
            }
            Snapshot = Snapshot with { Installation = Snapshot.Installation with { StateRevision = 5, RecoveryRequired = false,
                Ownership = success ? RuntimeOwnership.Managed : RuntimeOwnership.Absent, ActiveReleaseId = success ? Plan.Release!.ReleaseId : null },
                ActiveRevisionId = success ? Operation : null, MeasuredVersion = success ? "1.19.30" : null,
                ConfigurationIdentity = success ? MihomoPublicationStore.Identity(Material.Yaml) : null, PendingOperationId = null,
                Receipt = new(Operation, outcome, success ? null : "runtime.native_configuration_invalid") };
        }
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeCancel)
            {
                if (request.RuntimeLifecycle?.CancelOperationId != Operation) throw new Exception("Wrong cancellation target");
                CancelDeliveries++; CancelSignal.TrySetResult();
                return Json(new RuntimeCancelRequestResult(Operation, true));
            }
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeQuery)
                return Json(Snapshot with { Receipt = Snapshot.Receipt?.OperationId == request.OperationId ? Snapshot.Receipt : null });
            if (LedgerMode && request.Operation == PrivilegedOperationKind.MihomoRuntimeExecute)
            {
                Operation = request.OperationId!.Value;
                Plan = request.RuntimeLifecycle!.FrozenPlan!;
                var state = await Store.ReadAsync(ct);
                Material = Plan.Request.Change == RuntimeChange.Install ? state.Pending!.Candidate : state.Published!;
            }
            if (LedgerMode && request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover)
            {
                if (TransientRecoveryFailures-- > 0) { Recoveries++; throw new RuntimeLifecycleException("runtime.instance_busy"); }
                if ((await Store.ReadAsync(ct)).Pending is null && Snapshot.PendingOperationId is null)
                {
                    if (Store.Gate.CurrentCount != 0) throw new Exception("Recovery lacks publication gate");
                    Recoveries++; return Json<RuntimeSwitchResult?>(null);
                }
            }
            if (Store.Gate.Wait(0)) { Store.Gate.Release(); throw new Exception("Install publication gate was not held"); }
            GateHeld = true;
            var channel = request.RuntimeLifecycle!.Authorization!;
            var candidateAllowed = true;
            if (Plan.Request.Change == RuntimeChange.Install)
            {
                var grant = await RuntimeAuthorizationClient.ReadAsync<MihomoInstallationAuthorization>(channel, request.OperationId!.Value, ct);
                CandidateOnly = grant.OperationId == Operation && grant.PlanIdentity == MihomoRuntimeMaterial.InstallationPlanIdentity(Plan)
                    && (await Store.ReadAsync(default)).Published is null;
                candidateAllowed = grant.Candidate is not null;
            }
            else
            {
                var grant = await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(channel, request.OperationId!.Value, ct);
                if (grant.Published?.Yaml != Material.Yaml) throw new Exception("Wrong ordinary program authorization");
            }
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover)
            {
                Recoveries++;
                if (Snapshot.PendingOperationId is null) return Json<RuntimeSwitchResult?>(null);
                Commit(RuntimeSwitchOutcome.Interrupted); return Json(Snapshot.Receipt);
            }
            if (request.Operation != PrivilegedOperationKind.MihomoRuntimeExecute) throw new Exception("Unexpected install Helper operation");
            Installs++;
            if (Mode == "recovery")
            {
                Snapshot = Snapshot with { PendingOperationId = Operation,
                    Installation = Snapshot.Installation with { StateRevision = 2, RecoveryRequired = true } };
                return Json(new RuntimeSwitchResult(Operation, RuntimeSwitchOutcome.RecoveryRequired, "runtime.recovery_required"));
            }
            if (Mode == "wait-cancel")
            {
                await PrivilegedFrameReader.ReportAsync(PrivilegedOperationFrame.Report(InstallationStage.Downloading, cancellable: true));
                DownloadStarted.TrySetResult();
                await CancelSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (LedgerMode)
                foreach (var stage in new[] { InstallationStage.Downloading, InstallationStage.Verifying, InstallationStage.Activating, InstallationStage.HealthChecking })
                    await PrivilegedFrameReader.ReportAsync(PrivilegedOperationFrame.Report(stage));
            Commit(Mode == "failed" || !candidateAllowed ? RuntimeSwitchOutcome.Failed
                : Mode is "cancelled" or "wait-cancel" ? RuntimeSwitchOutcome.Cancelled : Mode == "interrupted" ? RuntimeSwitchOutcome.Interrupted : RuntimeSwitchOutcome.Succeeded);
            if (Mode == "wrong-version") Snapshot = Snapshot with { MeasuredVersion = "9.0.0" };
            if (Mode == "wrong-revision") Snapshot = Snapshot with { ActiveRevisionId = Guid.NewGuid() };
            if (Mode == "mirror-drift") { Directory.CreateDirectory(Path.GetDirectoryName(Active)!); await File.WriteAllTextAsync(Active, "# unrelated file"); }
            if (Mode is "lost-response" or "lost-response-stop")
            { ResponseLost?.Invoke(); throw new IOException("Simulated loss after installed Helper receipt"); }
            return Json(Snapshot.Receipt);
        }
        public Task<string?> ReadExistingAsync(CancellationToken ct) => Task.FromResult<string?>(Secret);
        public Task<string> GetOrCreateAsync(CancellationToken ct)
        {
            if (!AllowInitialMaterial || Store.Gate.CurrentCount != 0) throw new Exception("Install reconciliation generated credentials");
            SecretCreations++; return Task.FromResult(Secret);
        }
        public Task<ProxySettingsDto> GetAsync(CancellationToken ct)
        {
            if (!AllowInitialMaterial || Store.Gate.CurrentCount != 0) throw new Exception("Install reconciliation read settings");
            SettingsReads++; return Task.FromResult(Settings);
        }
        public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken ct) => throw new Exception("Bootstrap changed settings");
        public Task<string?> ReconcileSystemProxyAsync(bool startup, CancellationToken ct) => throw new Exception("Bootstrap activated host proxy");
        ServerMode IServerModeResolver.Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "versions");
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
        public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "data");
        public string GetStateDirectory() => Path.Combine(root, "state");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
        public void Dispose() => Directory.Delete(root, true);
    }
}
