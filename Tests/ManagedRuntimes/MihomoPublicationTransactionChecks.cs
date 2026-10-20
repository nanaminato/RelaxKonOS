using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Reflection;
using RelaxKonOS.Protocol.Proxy;
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
using RelaxKonOS.Server.Proxy.Platform;

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
        foreach (var mode in new[] { "success", "compensated", "lost-response", "revoked", "resource", "tun", "selection-failure", "selection-lost-response", "selection-conflict" })
        {
            using var f = await Fixture.CreateAsync();
            f.ApplicationMode = true;
            f.Mode = mode;
            var databaseProfiles = await MihomoProfilePublicationChecks.CreateAsync(f.GetStateDirectory());
            var profile = await databaseProfiles.UpsertAsync(null, "managed", "mihomo", null, default);
            var profileId = profile.Id;
            var profiles = new PublicationProfiles(databaseProfiles, mode);
            var engine = Stub<IProxyEngine>(method => method.Name == "get_EngineId" ? "mihomo"
                : throw new Exception("Managed application called the fixed runtime engine"));
            var settings = Stub<IProxySettingsService>(method => method.Name == "GetAsync"
                ? Task.FromResult(new ProxySettingsDto(false, false, false, false, false, "warning", 18889, false, "127.0.0.1", ProxyTunSettingsDto.Default))
                : throw new Exception("Unexpected settings mutation"));
            f.Profiles = profiles; f.RecreateService();
            var runtime = new MihomoRuntimeService(f, f.Store, f.Provider, f);
            var service = new ProxyConfigurationTransactionService(f, new ProxyEngineRegistry([engine]), profiles, f,
                new MihomoControllerOptions { Endpoint = new("http://127.0.0.1:19090/") }, f.Store,
                settingsService: settings, managedPublication: f.Service, managedRuntime: runtime);
            var yaml = "rules:\n  - MATCH,DIRECT\n";
            if (mode == "resource") yaml += "proxy-providers: {local: {type: file, path: proxy_providers/local.yaml}}\n";
            if (mode == "tun") yaml += "tun: {enable: true}\n";
            var stored = await service.StoreAsync(profileId, yaml, default);
            if (mode is "resource" or "tun")
            {
                Check(stored == ProxyProblemCodes.NotSupported && f.ConfigureCalls == 0 && !(await profiles.GetAsync(profileId, default))!.IsActive,
                    "Managed drafts reject unprepared resources/network transitions: " + mode);
                continue;
            }
            Check(stored is null && f.ConfigureCalls == 0, "Managed draft storage does not touch runtime: " + mode);
            if (mode == "revoked") f.Secret = null;
            var problem = await service.ActivateStoredAsync(profileId, default);
            var current = (await profiles.GetAsync(profileId, default))!;
            Check(mode == "success" ? problem is null && current.IsActive && f.ConfigureCalls == 1
                : problem is not null && (mode == "selection-lost-response" || !current.IsActive) && f.ConfigureCalls == (mode == "revoked" ? 0 : 1),
                "Application activation follows measured publication outcome: " + mode);
            if (mode == "success")
            {
                Check(await service.ActivateStoredAsync(profileId, default) is null && f.ConfigureCalls == 1,
                    "Selecting an identical already-published profile does not restart the runtime");
            }
            if (mode is "selection-failure" or "selection-lost-response")
            {
                var pending = (await f.Store.ReadAsync(default)).Pending!;
                Check(pending.Profile?.ProfileId == profileId && pending.Profile.ExpectedRevision == profile.Revision,
                    "Metadata failure preserves exact profile intent before authority confirmation: " + mode);
                Check((await f.RecreateService().RecoverAsync(pending.OperationId, default)).Outcome == MihomoPublicationOutcome.Confirmed
                    && (await profiles.GetAsync(profileId, default)) is { IsActive: true, Revision: 2 } && f.ConfigureCalls == 1
                    && (await f.Store.ReadAsync(default)).Pending is null,
                    "Restart reconciles SQLite marker without repeating native switch or profile revision: " + mode);
            }
            if (mode == "selection-conflict")
            {
                var pending = (await f.Store.ReadAsync(default)).Pending!;
                await Denied("profile_selection_conflict", () => f.RecreateService().RecoverAsync(pending.OperationId, default));
                Check((await f.Store.ReadAsync(default)).Pending is not null && !(await profiles.GetAsync(profileId, default))!.IsActive,
                    "Recovery preserves a conflicting profile edit instead of claiming a successful selection");
            }
            if (mode == "lost-response")
                Check((await f.Store.ReadAsync(default)).Pending is not null,
                    "Application transport loss preserves durable recovery rather than direct rollback");
        }
        foreach (var mode in new[] { "success", "compensated", "lost-response", "host-failure", "settings-drift", "revoked", "corrupt-settings", "metadata-only", "metadata-host-failure" })
        {
            using var f = await Fixture.CreateAsync(canonicalSettings: mode.StartsWith("metadata-", StringComparison.Ordinal));
            f.ApplicationMode = true;
            f.Mode = mode;
            if (mode is "host-failure" or "metadata-host-failure") f.HostProxy.Failures = 1;
            if (mode == "revoked") f.Secret = null;
            if (mode == "corrupt-settings")
            { Directory.CreateDirectory(f.GetStateDirectory()); await File.WriteAllTextAsync(f.SettingsPath, "corrupt"); }
            var controller = Stub<IMihomoControllerClient>(_ => throw new Exception("Managed settings used the direct controller"));
            var settingsFiles = new MihomoSettingsPublicationFiles(f, f.HostProxy);
            var service = new MihomoSettingsService(f, controller, f,
                new MihomoControllerOptions { Endpoint = new("http://127.0.0.1:19090/") }, f.HostProxy, f.Store,
                managedPublication: f.Service, managedRuntime: new(f, f.Store, f.Provider, f), settingsFiles: settingsFiles);
            var metadataOnly = mode.StartsWith("metadata-", StringComparison.Ordinal);
            var request = new UpdateProxySettingsRequest(false, false, true, true, false, "warning", metadataOnly ? 18888 : 18889,
                AllowInsecureSubscriptionSources: metadataOnly);
            var problem = await service.UpdateAsync(request, default);
            var state = await f.Store.ReadAsync(default);
            if (mode is "success" or "metadata-only")
            {
                Check(problem is null && state.Pending is null && (await service.GetAsync(default)).MixedPort == request.MixedPort,
                    "Managed settings commit runtime, host intent and settings together: " + mode);
                Check(f.ConfigureCalls == (metadataOnly ? 0 : 1) && f.HostProxy.Calls == 1,
                    "Unchanged YAML settings avoid native restarts and fabricated receipts: " + mode);
                Check(await service.ReconcileSystemProxyAsync(true, default) is null && f.HostProxy.Calls == 2,
                    "Confirmed managed settings can reconcile the system proxy at startup: " + mode);
            }
            else if (mode is "revoked" or "corrupt-settings")
                Check(problem == ProxyProblemCodes.RecoveryRequired && f.ConfigureCalls == 0 && f.HostProxy.Calls == 0 && state.Pending is null,
                    "Invalid current settings/credentials cannot start a transaction: " + mode);
            else if (mode == "compensated")
                Check(problem == ProxyProblemCodes.ConfigApplyFailed && state.Pending is null && !File.Exists(f.SettingsPath) && f.HostProxy.Calls == 0,
                    "Native compensation never applies candidate application settings");
            else
            {
                Check(problem == ProxyProblemCodes.RecoveryRequired && state.Pending?.Settings is not null,
                    "Incomplete settings publication retains encrypted exact intent: " + mode);
                var calls = f.ConfigureCalls;
                Check(await service.UpdateAsync(request, default) == ProxyProblemCodes.RecoveryRequired && f.ConfigureCalls == calls,
                    "Pending settings reject a second runtime transaction: " + mode);
                var operation = state.Pending!.OperationId;
                if (mode == "settings-drift")
                {
                    await Denied("settings_drift", () => f.RecreateService().RecoverAsync(operation, default));
                    Check(await File.ReadAllTextAsync(f.SettingsPath) == "unrelated settings", "Settings recovery does not overwrite unrelated bytes");
                }
                else
                {
                    var recovered = await f.RecreateService().RecoverAsync(operation, default);
                    Check(recovered.Outcome == MihomoPublicationOutcome.Confirmed && f.ConfigureCalls == calls
                        && (await f.Store.ReadAsync(default)).Pending is null
                        && (await service.GetAsync(default)).MixedPort == request.MixedPort,
                        "Restart resumes exact settings intent without another native apply: " + mode);
                }
            }
        }
        using (var f = await Fixture.CreateAsync())
        {
            var desired = new ProxySettingsDto(false, false, true, true, false, "warning", 18889, false,
                "127.0.0.1", ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default);
            var previous = desired with { MixedPort = 7890 };
            var files = new MihomoSettingsPublicationFiles(f, f.HostProxy);
            var intent = await files.CaptureAsync(previous, desired, default);
            f.SetCandidateMaterial(MihomoSettingsPublicationFiles.Candidate(f.Previous, desired));
            await f.Store.BeginConfigurationAsync(f.Operation, f.InitialRevision, f.Candidate, f.Plan, default, intent);
            f.Commit(RuntimeSwitchOutcome.Succeeded);
            await File.WriteAllTextAsync(f.Active, f.Candidate.Yaml);
            await File.WriteAllBytesAsync(f.SettingsPath, JsonSerializer.SerializeToUtf8Bytes(desired));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f.SettingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Check((await f.RecreateService().RecoverAsync(f.Operation, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && f.ConfigureCalls == 0 && f.HostProxy.Calls == 1 && (await f.Store.ReadAsync(default)).Pending is null,
                "Crash after settings file replacement resumes exact candidate without a native replay");
            if (!OperatingSystem.IsWindows())
                Check((File.GetUnixFileMode(f.SettingsPath) & (UnixFileMode.OtherWrite | UnixFileMode.GroupWrite)) == 0,
                    "Settings publication does not make settings writable by other users");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var desired = new ProxySettingsDto(false, false, true, true, false, "warning", 18889, false,
                "127.0.0.1", ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default);
            var files = new MihomoSettingsPublicationFiles(f, f.HostProxy);
            var intent = await files.CaptureAsync(desired with { MixedPort = 7890 }, desired, default);
            await Denied("publication_state_invalid", () => f.Store.BeginConfigurationAsync(f.Operation, f.InitialRevision, f.Candidate, f.Plan, default, intent));
            f.SetCandidateMaterial(MihomoSettingsPublicationFiles.Candidate(f.Previous, desired));
            var state = await f.Store.BeginConfigurationAsync(f.Operation, f.InitialRevision, f.Candidate, f.Plan, default, intent);
            var node = JsonNode.Parse(RuntimeJson.Serialize(state))!;
            node["pending"]!.AsObject().Remove("settings");
            var statePath = Path.Combine(f.GetStateDirectory(), "mihomo-publication.protected");
            await File.WriteAllBytesAsync(statePath, f.Protect(Encoding.UTF8.GetBytes(node.ToJsonString())));
            await Denied("metadata_invalid", () => f.Store.ReadAsync(default));
            node = JsonNode.Parse(RuntimeJson.Serialize(state))!;
            node["pending"]!.AsObject().Remove("profile");
            await File.WriteAllBytesAsync(statePath, f.Protect(Encoding.UTF8.GetBytes(node.ToJsonString())));
            await Denied("metadata_invalid", () => f.Store.ReadAsync(default));
        }
        return count;
    }

    private sealed class PublicationProfiles(IProxyProfileRepository inner, string mode) : IProxyProfileRepository
    {
        private bool injected;
        public Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<ProxyProfileDto?> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<ProxyProfileDto> UpsertAsync(Guid? id, string name, string engine, long? revision, CancellationToken ct) => inner.UpsertAsync(id, name, engine, revision, ct);
        public Task<ProxyProfileDto?> SetActiveAsync(Guid id, CancellationToken ct) => throw new Exception("Managed publication used unjournaled activation");
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public Task ValidatePublicationAsync(MihomoProfilePublication selection, CancellationToken ct) => inner.ValidatePublicationAsync(selection, ct);
        public async Task CommitPublicationAsync(Guid operation, MihomoProfilePublication selection, string identity, CancellationToken ct)
        {
            if (!injected && mode is "selection-failure" or "selection-lost-response" or "selection-conflict")
            {
                injected = true;
                if (mode == "selection-failure") throw new IOException("Injected unavailable selection storage");
                if (mode == "selection-conflict") await inner.UpsertAsync(selection.ProfileId, "edited profile", "mihomo", selection.ExpectedRevision, ct);
                if (mode == "selection-lost-response")
                {
                    await inner.CommitPublicationAsync(operation, selection, identity, ct);
                    throw new IOException("Injected committed selection response loss");
                }
            }
            await inner.CommitPublicationAsync(operation, selection, identity, ct);
        }
    }

    private sealed class HostProxy : IHostSystemProxyService
    {
        internal int Calls, Failures;
        public Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new ProxySystemProxyCapabilities(true, true, false, false));
        public Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken ct)
        { Calls++; return Task.FromResult<string?>(Failures-- > 0 ? ProxyProblemCodes.SystemProxyConflict : null); }
    }

    public class MethodStub : DispatchProxy
    {
        public Func<MethodInfo, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => Handler(method!);
    }
    internal static T Stub<T>(Func<MethodInfo, object?> handler) where T : class
    {
        var value = DispatchProxy.Create<T, MethodStub>();
        ((MethodStub)(object)value).Handler = handler;
        return value;
    }

    private sealed class Fixture : IDisposable, IPrivilegedOperationTransport, IProxyControllerSecretStore, IProxyPlatformPaths, IServerModeResolver
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mihomo-server-transaction-" + Guid.NewGuid().ToString("N"));
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        internal bool ApplicationMode;
        internal Guid Operation { get; private set; } = Guid.NewGuid();
        internal string? Secret = "transaction-controller-secret";
        internal string Mode = "success";
        internal int ConfigureCalls, RecoverCalls, QueryCalls;
        internal int TransientRecoveryFailures;
        internal bool RemainRecoveryRequired;
        internal bool GateHeldDuringHelper, ConfigurationGrantRead;
        internal MihomoRemovalPublicationService Removal => new(Store, new(this, Store, new(Store, this, this), this), this, MihomoRemovalHostFixture.Files(this));
        internal MihomoPublicationStore Store { get; private set; } = null!;
        internal MihomoRuntimeAuthorizationProvider Provider { get; private set; } = null!;
        internal MihomoConfigurationPublicationService Service { get; private set; } = null!;
        internal MihomoInstallationPublicationService Installation { get; private set; } = null!;
        internal MihomoPublishedRuntime Previous { get; private set; } = null!;
        internal MihomoPublishedRuntime Candidate { get; private set; } = null!;
        internal RuntimeConfigurationPlan Plan { get; private set; } = null!;
        internal RuntimeInstallationSnapshot Snapshot { get; private set; } = null!;
        internal long InitialRevision;
        internal IProxyProfileRepository? Profiles;
        internal HostProxy HostProxy { get; } = new();
        internal string SettingsPath => Path.Combine(GetStateDirectory(), "mihomo-settings.json");
        internal string Active => Path.Combine(GetProtectedConfigurationDirectory(), "active.yaml");
        internal static async Task<Fixture> CreateAsync(bool canonicalSettings = false)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.GetProtectedConfigurationDirectory());
            f.Previous = MihomoConfigurationPolicy.Describe("mixed-port: 18888\nexternal-controller: 127.0.0.1:19090\nsecret: "
                + f.Secret + "\nrules:\n  - MATCH,DIRECT\n").Material;
            if (canonicalSettings)
                f.Previous = MihomoSettingsPublicationFiles.Candidate(f.Previous,
                    new(false, false, true, true, false, "warning", 18888, false, "127.0.0.1", ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default));
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
            Service = new(Store, Provider, runtime, this, new(this, HostProxy), Profiles);
            Installation = new(Store, Provider, runtime, this);
            return Service;
        }
        internal Task<MihomoPublicationResult> PublishAsync() => Service.PublishAsync(Operation, InitialRevision, Plan, Candidate, default);
        internal Task<MihomoPublicationState> StageAsync() => Store.BeginConfigurationAsync(Operation, InitialRevision, Candidate, Plan, default);
        internal byte[] Protect(byte[] bytes) => protection.CreateProtector("RelaxKonOS.Mihomo.Publication.v1").Protect(bytes);
        internal void SetCandidateMaterial(MihomoPublishedRuntime material)
        { Candidate = material; Plan = Plan with { CandidateIdentity = MihomoPublicationStore.Identity(material.Yaml) }; }
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
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeQuery) { QueryCalls++; return Json(Snapshot with { Receipt = request.OperationId == Snapshot.Receipt?.OperationId ? Snapshot.Receipt : null }); }
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
            if (ApplicationMode && request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure)
            {
                Operation = request.OperationId!.Value;
                Plan = request.RuntimeLifecycle.ConfigurationPlan!;
                Candidate = (await Store.ReadAsync(ct)).Pending!.Candidate;
            }
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
            if (Mode == "settings-drift")
            { Directory.CreateDirectory(GetStateDirectory()); await File.WriteAllTextAsync(SettingsPath, "unrelated settings"); }
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
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
