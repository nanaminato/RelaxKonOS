using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
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
        internal Guid Operation { get; } = Guid.NewGuid();
        internal string Mode = "success";
        internal string Secret = "installation-publication-secret";
        internal int Installs, Recoveries;
        internal bool AllowInitialMaterial;
        internal int SettingsReads, SecretCreations;
        internal ProxySettingsDto Settings { get; } = new(false, false, true, false, false, "info", 18888);
        internal bool GateHeld, CandidateOnly;
        internal string Active => Path.Combine(GetProtectedConfigurationDirectory(), "active.yaml");
        internal MihomoPublishedRuntime Material { get; set; }
        internal RuntimeChangePlan Plan { get; }
        internal RuntimeInstallationSnapshot Snapshot { get; private set; }
        internal MihomoRemovalPublicationService Removal => new(Store, new(this, Store, new(Store, this, this), this), this);
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
            var runtime = new MihomoRuntimeService(this, Store, provider, this);
            Service = new(Store, provider, runtime, this);
            Configuration = new(Store, provider, runtime, this);
        }
        internal Task<MihomoPublicationResult> InstallAsync() => Service.InstallAsync(Operation, 0, Plan, Material, default);
        internal void Commit(RuntimeSwitchOutcome outcome)
        {
            var success = outcome == RuntimeSwitchOutcome.Succeeded;
            Snapshot = Snapshot with { Installation = Snapshot.Installation with { StateRevision = 5, RecoveryRequired = false,
                Ownership = success ? RuntimeOwnership.Managed : RuntimeOwnership.Absent, ActiveReleaseId = success ? Plan.Release!.ReleaseId : null },
                ActiveRevisionId = success ? Operation : null, MeasuredVersion = success ? "1.19.30" : null,
                ConfigurationIdentity = success ? MihomoPublicationStore.Identity(Material.Yaml) : null, PendingOperationId = null,
                Receipt = new(Operation, outcome, success ? null : "runtime.native_configuration_invalid") };
        }
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeQuery) return Json(Snapshot);
            if (Store.Gate.Wait(0)) { Store.Gate.Release(); throw new Exception("Install publication gate was not held"); }
            GateHeld = true;
            var channel = request.RuntimeLifecycle!.Authorization!;
            var grant = await RuntimeAuthorizationClient.ReadAsync<MihomoInstallationAuthorization>(channel, request.OperationId!.Value, ct);
            CandidateOnly = grant.OperationId == Operation && grant.PlanIdentity == MihomoRuntimeMaterial.InstallationPlanIdentity(Plan)
                && (await Store.ReadAsync(default)).Published is null;
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
            Commit(Mode == "failed" || grant.Candidate is null ? RuntimeSwitchOutcome.Failed : RuntimeSwitchOutcome.Succeeded);
            if (Mode == "wrong-version") Snapshot = Snapshot with { MeasuredVersion = "9.0.0" };
            if (Mode == "wrong-revision") Snapshot = Snapshot with { ActiveRevisionId = Guid.NewGuid() };
            if (Mode == "mirror-drift") { Directory.CreateDirectory(Path.GetDirectoryName(Active)!); await File.WriteAllTextAsync(Active, "# unrelated file"); }
            if (Mode == "lost-response") throw new IOException("Simulated loss after installed Helper receipt");
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
