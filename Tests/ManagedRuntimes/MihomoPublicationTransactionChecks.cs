using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoPublicationTransactionChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        async Task Denied(string code, Func<Task> action)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            catch (RuntimeLifecycleException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected publication transaction refusal: " + code);
        }

        foreach (var mode in new[] { "success", "compensated" })
        {
            using var f = await Fixture.CreateAsync(); f.Mode = mode;
            var result = await f.PublishAsync();
            var state = await f.Store.ReadAsync(default);
            Check(result.Outcome == (mode == "success" ? MihomoPublicationOutcome.Confirmed : MihomoPublicationOutcome.Compensated)
                && state.Pending is null, "Server resolves the measured Helper outcome: " + mode);
            Check(state.Identity == (mode == "success" ? f.Plan.CandidateIdentity : f.Plan.PreviousIdentity)
                && await File.ReadAllTextAsync(f.Active) == (mode == "success" ? f.Candidate.Yaml : f.Previous.Yaml),
                "Authority and Server mirror agree after resolution: " + mode);
            Check(f.GateHeldDuringHelper && f.ConfigurationGrantRead && f.ConfigureCalls == 1,
                "Publication gate spans the typed callback and Helper operation: " + mode);
            Check((await f.PublishAsync()).Outcome == result.Outcome && f.ConfigureCalls == 1,
                "Success and compensated replays do not reexecute Helper: " + mode);
            await Denied("idempotency_conflict", () => f.Service.PublishAsync(f.Operation, f.InitialRevision,
                f.Plan with { IdempotencyKey = "different" }, f.Candidate, default));
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "lost-response";
            try { await f.PublishAsync(); throw new Exception("Expected lost response"); } catch (IOException) { count++; }
            Check((await f.Store.ReadAsync(default)).Pending?.OperationId == f.Operation
                && await File.ReadAllTextAsync(f.Active) == f.Previous.Yaml,
                "Lost committed Helper response leaves the Server publication pending");
            var recovered = await f.RecreateService().RecoverAsync(f.Operation, default);
            Check(recovered.Outcome == MihomoPublicationOutcome.Confirmed && f.ConfigureCalls == 1 && f.RecoverCalls == 0,
                "Server restart confirms the exact terminal receipt without re-executing or compensating it");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery";
            Check((await f.PublishAsync()).Outcome == MihomoPublicationOutcome.RecoveryRequired
                && (await f.Store.ReadAsync(default)).Pending is not null, "Failed Helper compensation remains pending on both sides");
            Check((await f.RecreateService().RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Compensated
                && f.RecoverCalls == 1 && (await f.Store.ReadAsync(default)).Pending is null,
                "Named recovery uses a fresh typed callback and verifies the restored identity");
        }
        using (var f = await Fixture.CreateAsync())
        {
            await f.StageAsync();
            var result = await f.RecreateService().RecoverAsync(f.Operation, default);
            Check(result is { Outcome: MihomoPublicationOutcome.NotStarted, Receipt: null } && f.RecoverCalls == 1,
                "Server-only acceptance is closed only after Helper lock-backed recovery proves no transaction");
            Check((await f.PublishAsync()).Outcome == MihomoPublicationOutcome.NotStarted && f.ConfigureCalls == 0,
                "Not-started replay never invents a Helper receipt or sends the candidate");
        }
        using (var f = await Fixture.CreateAsync())
        {
            await f.StageAsync();
            f.Commit(RuntimeSwitchOutcome.Succeeded);
            await File.WriteAllTextAsync(f.Active, f.Candidate.Yaml);
            Check((await f.Provider.ReadConfigurationAsync(f.Operation, default)).Previous is not null,
                "Named pending authorization recognizes the exact candidate mirror after the file-write crash window");
            Check((await f.RecreateService().RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Confirmed,
                "Restart between Server mirror replacement and authority commit confirms the measured publication");
        }
        using (var f = await Fixture.CreateAsync())
        {
            await f.StageAsync();
            f.Secret = "unrelated-current-controller-secret";
            var grant = await f.Provider.ReadConfigurationAsync(f.Operation, default);
            Check(grant.Previous is null && grant.Candidate is null, "Current credential revocation removes both grants without regenerating secrets");
            await Denied("publication_unconfirmed", () => f.Provider.ReadConfigurationAsync(Guid.NewGuid(), default));
            await Denied("publication_unconfirmed", () => f.Provider.ReadAsync(default));
            f.Secret = f.Previous.ControllerSecret;
            await File.WriteAllTextAsync(f.Active, "# unrelated draft");
            await Denied("publication_unconfirmed", () => f.Provider.ReadConfigurationAsync(f.Operation, default));
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.SetCandidateCredential("rotated-candidate-controller-secret");
            await f.StageAsync();
            var previous = await f.Provider.ReadConfigurationAsync(f.Operation, default);
            Check(previous is { Previous: not null, Candidate: null }, "Old current credential does not authorize a candidate with a different secret");
            f.Secret = f.Candidate.ControllerSecret;
            var candidate = await f.Provider.ReadConfigurationAsync(f.Operation, default);
            Check(candidate is { Previous: null, Candidate: not null }, "Rotated current credential authorizes only the candidate, never revoked old material");
        }
        foreach (var mode in new[] { "conflicting-receipt", "mirror-drift" })
        {
            using var f = await Fixture.CreateAsync(); f.Mode = mode;
            await Denied(mode == "conflicting-receipt" ? "operation_id_conflict" : "publication_unconfirmed", () => f.PublishAsync());
            Check((await f.Store.ReadAsync(default)).Pending is not null, "Conflicting proof never closes a pending publication: " + mode);
        }
        using (var f = await Fixture.CreateAsync())
        {
            using var startup = new MihomoPublicationRecoveryService(f.Store, f.Service, f.Installation, f.Removal, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.StartAsync(default);
            await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Check(f.ConfigureCalls == 0 && f.RecoverCalls == 0 && f.QueryCalls == 0,
                "Startup with no pending publication never probes or mutates Helper");
            Check(await f.Store.UncoordinatedWriteProblemAsync(default) == RelaxKonOS.Protocol.Proxy.ProxyProblemCodes.NotSupported,
                "A healthy confirmed authority blocks unsupported direct mutation without falsely reporting pending recovery");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery"; await f.PublishAsync();
            using var startup = new MihomoPublicationRecoveryService(f.Store, f.Service, f.Installation, f.Removal, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.StartAsync(default);
            await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Check(f.RecoverCalls == 1 && f.ConfigureCalls == 1 && (await f.Store.ReadAsync(default)).Pending is null,
                "Startup invokes named compensation without rerunning preparation or configuration");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery"; await f.PublishAsync(); f.TransientRecoveryFailures = 1;
            using var startup = new MihomoPublicationRecoveryService(f.Store, f.Service, f.Installation, f.Removal, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.StartAsync(default);
            await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            Check(f.RecoverCalls == 2 && f.ConfigureCalls == 1 && (await f.Store.ReadAsync(default)).Pending is null,
                "Busy startup recovery retries the same transaction without preparing or configuring again");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery"; await f.PublishAsync(); f.RemainRecoveryRequired = true;
            using var startup = new MihomoPublicationRecoveryService(f.Store, f.Service, f.Installation, f.Removal, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.StartAsync(default);
            await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Check(f.RecoverCalls == 1 && (await f.Store.ReadAsync(default)).Pending is not null,
                "Operator-required recovery remains blocked without an automatic retry loop");
        }
        return count;
    }

    private sealed class Fixture : IDisposable, IPrivilegedOperationTransport, IProxyControllerSecretStore, IProxyPlatformPaths, IServerModeResolver
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mihomo-server-transaction-" + Guid.NewGuid().ToString("N"));
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        internal Guid Operation { get; } = Guid.NewGuid();
        internal string? Secret = "transaction-controller-secret";
        internal string Mode = "success";
        internal int ConfigureCalls, RecoverCalls, QueryCalls;
        internal int TransientRecoveryFailures;
        internal bool RemainRecoveryRequired;
        internal bool GateHeldDuringHelper, ConfigurationGrantRead;
        internal MihomoRemovalPublicationService Removal => new(Store, new(this, Store, new(Store, this, this), this), this);
        internal MihomoPublicationStore Store { get; private set; } = null!;
        internal MihomoRuntimeAuthorizationProvider Provider { get; private set; } = null!;
        internal MihomoConfigurationPublicationService Service { get; private set; } = null!;
        internal MihomoInstallationPublicationService Installation { get; private set; } = null!;
        internal MihomoPublishedRuntime Previous { get; private set; } = null!;
        internal MihomoPublishedRuntime Candidate { get; private set; } = null!;
        internal RuntimeConfigurationPlan Plan { get; private set; } = null!;
        internal RuntimeInstallationSnapshot Snapshot { get; private set; } = null!;
        internal long InitialRevision;
        internal string Active => Path.Combine(GetProtectedConfigurationDirectory(), "active.yaml");
        internal static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.GetProtectedConfigurationDirectory());
            f.Previous = MihomoConfigurationPolicy.Describe("mixed-port: 18888\nexternal-controller: 127.0.0.1:19090\nsecret: "
                + f.Secret + "\nrules:\n  - MATCH,DIRECT\n").Material;
            f.Candidate = MihomoConfigurationPolicy.Describe(f.Previous.Yaml.Replace("18888", "18889", StringComparison.Ordinal)).Material;
            f.Store = new(f, f.protection);
            var bootstrap = Guid.NewGuid();
            await f.Store.BeginAsync(bootstrap, 0, f.Previous, default);
            await File.WriteAllTextAsync(f.Active, f.Previous.Yaml);
            f.InitialRevision = (await f.Store.ConfirmAsync(bootstrap, MihomoPublicationStore.Identity(f.Previous.Yaml), default)).Revision;
            var installation = new RuntimeInstallation(Guid.NewGuid(), "publication-host", "mihomo", 5, RuntimeOwnership.Managed,
                new("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd"), "release-1", false);
            f.Snapshot = new(installation, "1.19.30", Guid.NewGuid(), MihomoPublicationStore.Identity(f.Previous.Yaml), null, null, null);
            f.Plan = new(installation.InstallationId, installation.HostId, "mihomo", 5, f.Snapshot.ActiveRevisionId!.Value,
                f.Snapshot.ConfigurationIdentity!, MihomoPublicationStore.Identity(f.Candidate.Yaml), "server-publication");
            f.RecreateService();
            return f;
        }
        internal MihomoConfigurationPublicationService RecreateService()
        {
            Store = new(this, protection);
            Provider = new(Store, this, this);
            var runtime = new MihomoRuntimeService(this, Store, Provider, this);
            Service = new(Store, Provider, runtime, this);
            Installation = new(Store, Provider, runtime, this);
            return Service;
        }
        internal Task<MihomoPublicationResult> PublishAsync() => Service.PublishAsync(Operation, InitialRevision, Plan, Candidate, default);
        internal Task<MihomoPublicationState> StageAsync() => Store.BeginConfigurationAsync(Operation, InitialRevision, Candidate, Plan, default);
        internal void SetCandidateCredential(string secret)
        {
            Candidate = MihomoConfigurationPolicy.Describe(Candidate.Yaml.Replace(Previous.ControllerSecret, secret, StringComparison.Ordinal)).Material;
            Plan = Plan with { CandidateIdentity = MihomoPublicationStore.Identity(Candidate.Yaml) };
        }
        internal void Commit(RuntimeSwitchOutcome outcome)
        {
            Snapshot = Snapshot with { ConfigurationIdentity = outcome == RuntimeSwitchOutcome.Succeeded ? Plan.CandidateIdentity : Plan.PreviousIdentity,
                PendingOperationId = null, Installation = Snapshot.Installation with { StateRevision = 10, RecoveryRequired = false },
                Receipt = new(Operation, outcome, outcome == RuntimeSwitchOutcome.Succeeded ? null : "runtime.health_check_failed") };
        }
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeQuery) { QueryCalls++; return Json(Snapshot); }
            if (request.Operation is not (PrivilegedOperationKind.MihomoRuntimeConfigure or PrivilegedOperationKind.MihomoRuntimeRecover))
                throw new Exception("Unexpected Server publication Helper action");
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover)
            {
                RecoverCalls++;
                if (TransientRecoveryFailures-- > 0) throw new RuntimeLifecycleException("runtime.instance_busy");
            }
            if (Store.Gate.Wait(0)) { Store.Gate.Release(); throw new Exception("Publication gate not held"); }
            GateHeldDuringHelper = true;
            var channel = request.RuntimeLifecycle!.Authorization!;
            var grant = await RuntimeAuthorizationClient.ReadAsync<MihomoConfigurationAuthorization>(channel, request.OperationId!.Value, ct);
            ConfigurationGrantRead = grant.OperationId == Operation && grant.Previous?.Yaml == Previous.Yaml && grant.Candidate?.Yaml == Candidate.Yaml;
            if (!ConfigurationGrantRead) throw new Exception("Wrong Server publication grant");
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover)
            {
                if (RemainRecoveryRequired) return Json(new RuntimeSwitchResult(Operation, RuntimeSwitchOutcome.RecoveryRequired, "runtime.recovery_required"));
                if (Snapshot.PendingOperationId is null) return Json<RuntimeSwitchResult?>(null);
                Commit(RuntimeSwitchOutcome.Interrupted);
                return Json(Snapshot.Receipt);
            }
            ConfigureCalls++;
            if (Mode == "recovery")
            {
                Snapshot = Snapshot with { PendingOperationId = Operation, ConfigurationIdentity = Plan.CandidateIdentity,
                    Installation = Snapshot.Installation with { StateRevision = 7, RecoveryRequired = true } };
                return Json(new RuntimeSwitchResult(Operation, RuntimeSwitchOutcome.RecoveryRequired, "runtime.recovery_required"));
            }
            Commit(Mode is "compensated" or "conflicting-receipt" ? RuntimeSwitchOutcome.Failed : RuntimeSwitchOutcome.Succeeded);
            if (Mode == "lost-response") throw new IOException("Simulated lost committed Helper response");
            if (Mode == "mirror-drift") await File.WriteAllTextAsync(Active, "# unrelated draft");
            return Json(Mode == "conflicting-receipt" ? new RuntimeSwitchResult(Operation, RuntimeSwitchOutcome.Succeeded, null) : Snapshot.Receipt);
        }
        public Task<string?> ReadExistingAsync(CancellationToken ct) => Task.FromResult(Secret);
        public Task<string> GetOrCreateAsync(CancellationToken ct) => throw new Exception("Publication generated credentials");
        public ServerMode ModeValue => ServerMode.System;
        ServerMode IServerModeResolver.Mode => ModeValue;
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
