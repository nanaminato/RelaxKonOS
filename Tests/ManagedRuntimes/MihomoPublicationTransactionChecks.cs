using Microsoft.AspNetCore.Builder;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Startup.Modules;
using Microsoft.Extensions.DependencyInjection;
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
            var recovery = f.RecreateService();
            var builder = WebApplication.CreateBuilder();
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            builder.Services.AddSingleton(f.Store);
            builder.Services.AddSingleton<IProxyPlatformPaths>(f);
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddTransient(_ => recovery);
            await using var app = builder.Build();
            var guard = new ProxyModule();
            Check((await guard.InspectOrphanedWorkAsync(app.Services, context, default)).Count == 1 && f.RecoverCalls == 0,
                "Read-only deactivation inspection keeps the pending publication and does not contact Helper");
            f.TransientRecoveryFailures = 1;
            Check((await guard.InspectDisabledStartupAsync(app.Services, context, default)).Single().Description.Contains(f.Operation.ToString())
                && (await f.Store.ReadAsync(default)).Pending?.OperationId == f.Operation,
                "A busy Helper blocks disabled startup and preserves the original operation");
            Check((await guard.InspectDisabledStartupAsync(app.Services, context, default)).Count == 0
                && f.RecoverCalls == 2 && f.ConfigureCalls == 1 && (await f.Store.ReadAsync(default)).Pending is null
                && f.Snapshot.Receipt is { Outcome: RuntimeSwitchOutcome.Interrupted } receipt && receipt.OperationId == f.Operation,
                "Disabled startup compensates the original publication without replaying the candidate");
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
            using var recoveryServices1 = new PublicationRecoveryServices(f.Store, () => f.Service, () => f.Installation, () => f.Removal);
            var startup = new MihomoPublicationRecoveryService(recoveryServices1.Scopes, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.ReconcileAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            Check(f.ConfigureCalls == 0 && f.RecoverCalls == 0 && f.QueryCalls == 0,
                "Startup with no pending publication never probes or mutates Helper");
            Check(await f.Store.UncoordinatedWriteProblemAsync(default) == RelaxKonOS.Protocol.Proxy.ProxyProblemCodes.NotSupported,
                "A healthy confirmed authority blocks unsupported direct mutation without falsely reporting pending recovery");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery"; await f.PublishAsync();
            using var recoveryServices2 = new PublicationRecoveryServices(f.Store, () => f.Service, () => f.Installation, () => f.Removal);
            var startup = new MihomoPublicationRecoveryService(recoveryServices2.Scopes, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.ReconcileAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            Check(f.RecoverCalls == 1 && f.ConfigureCalls == 1 && (await f.Store.ReadAsync(default)).Pending is null,
                "Startup invokes named compensation without rerunning preparation or configuration");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery"; await f.PublishAsync(); f.TransientRecoveryFailures = 1;
            using var recoveryServices3 = new PublicationRecoveryServices(f.Store, () => f.Service, () => f.Installation, () => f.Removal);
            var startup = new MihomoPublicationRecoveryService(recoveryServices3.Scopes, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.ReconcileAsync(default).WaitAsync(TimeSpan.FromSeconds(15));
            Check(f.RecoverCalls == 2 && f.ConfigureCalls == 1 && (await f.Store.ReadAsync(default)).Pending is null,
                "Busy startup recovery retries the same transaction without preparing or configuring again");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery"; await f.PublishAsync(); f.RemainRecoveryRequired = true;
            using var recoveryServices4 = new PublicationRecoveryServices(f.Store, () => f.Service, () => f.Installation, () => f.Removal);
            var startup = new MihomoPublicationRecoveryService(recoveryServices4.Scopes, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.ReconcileAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
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
            var settings = Stub<IProxySettingsReader>(method => method.Name == "GetAsync"
                ? Task.FromResult(new ProxySettingsDto(false, false, false, false, false, "warning", 18889, false, "127.0.0.1", ProxyTunSettingsDto.Default))
                : throw new Exception("Unexpected settings mutation"));
            f.Profiles = profiles; f.RecreateService();
            var runtime = new MihomoRuntimeService(f, f.Store, f.Provider, f, new DirectDownloads());
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
                managedPublication: f.Service, managedRuntime: new(f, f.Store, f.Provider, f, new DirectDownloads()), settingsFiles: settingsFiles);
            var metadataOnly = mode.StartsWith("metadata-", StringComparison.Ordinal);
            var request = new UpdateProxySettingsRequest(false, false, true, true, false, "warning", metadataOnly ? 18888 : 18889,
                AllowInsecureSubscriptionSources: metadataOnly);
            var problem = await service.UpdateAsync(request, default);
            var state = await f.Store.ReadAsync(default);
            if (mode is "success" or "metadata-only")
            {
                Check(problem is null && state.Pending is null && (await new ProxySettingsReader(f).GetAsync(default)).MixedPort == request.MixedPort,
                    "Managed settings commit runtime, host intent and settings together: " + mode);
                Check(f.ConfigureCalls == (metadataOnly ? 0 : 1) && f.HostProxy.Calls == 1,
                    "Unchanged YAML settings avoid native restarts and fabricated receipts: " + mode);
                Check(await new MihomoSystemProxyReconciler(f, new ProxySettingsReader(f), f, f.HostProxy, f.Store,
                    new(f, f.Store, f.Provider, f, new DirectDownloads()), settingsFiles).ReconcileSystemProxyAsync(true, default) is null && f.HostProxy.Calls == 2,
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
                        && (await new ProxySettingsReader(f).GetAsync(default)).MixedPort == request.MixedPort,
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
        foreach (var failure in new[] { "revoked", "yaml-drift", "settings-corrupt", "pending", "helper-recovery", "ownership" })
        {
            using var f = await Fixture.CreateAsync();
            var reconciler = new MihomoSystemProxyReconciler(f, new ProxySettingsReader(f), f, f.HostProxy, f.Store,
                new(f, f.Store, f.Provider, f, new DirectDownloads()), new(f, f.HostProxy));
            if (failure == "revoked") f.Secret = null;
            if (failure == "yaml-drift") await File.WriteAllTextAsync(f.Active, "# unrelated YAML");
            if (failure == "settings-corrupt") await File.WriteAllTextAsync(f.SettingsPath, "corrupt");
            if (failure == "pending") await f.StageAsync();
            if (failure == "helper-recovery") f.Snapshot = f.Snapshot with
                { Installation = f.Snapshot.Installation with { RecoveryRequired = true } };
            if (failure == "ownership") f.Snapshot = f.Snapshot with
                { Installation = f.Snapshot.Installation with { Ownership = RuntimeOwnership.External } };
            Check(await reconciler.ReconcileSystemProxyAsync(true, default) == ProxyProblemCodes.RecoveryRequired
                && f.HostProxy.Calls == 0 && f.ConfigureCalls == 0,
                "Narrow guard refuses unsafe publication without a host write or native apply: " + failure);
            Check(await f.Store.Gate.WaitAsync(TimeSpan.FromSeconds(1)), "Guard refusal releases the publication gate: " + failure);
            f.Store.Gate.Release();
        }
        await CheckRecoveryBoundaryAsync(Check);
        await CheckContinuousRecoveryAsync(Check);
        return count;
    }

    private sealed class PublicationProfiles(IProxyProfileRepository inner, string mode) : IProxyProfileRepository
    {
        private bool injected;
        public Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<ProxyProfileDto?> GetActiveAsync(CancellationToken ct) => inner.GetActiveAsync(ct);
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

    private static async Task CheckRecoveryBoundaryAsync(Action<bool, string> check)
    {
        using (var f = await Fixture.CreateAsync())
        {
            using var services = new PublicationRecoveryServices(f.Store,
                () => throw new Exception("No pending work must not construct configuration recovery"),
                () => throw new Exception("No pending work must not construct installation recovery"),
                () => throw new Exception("No pending work must not construct removal recovery"));
            var worker = new MihomoPublicationRecoveryService(services.Scopes, NullLogger<MihomoPublicationRecoveryService>.Instance);
            await worker.ReconcileAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            check(f.QueryCalls == 0, "Idle recovery constructs no publication executor or Helper graph");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery"; await f.PublishAsync(); f.TransientRecoveryFailures = 1;
            var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var created = 0; var cleaned = 0;
            var instances = new List<WeakReference>();
            var services = new ServiceCollection(); services.AddSingleton(f.Store);
            services.AddScoped(_ => new RecoveryScopeProbe(async () =>
            {
                if (Interlocked.Increment(ref cleaned) == 1)
                { disposing.TrySetResult(); await release.Task; disposed.TrySetResult(); }
            }));
            services.AddTransient<MihomoConfigurationPublicationService>(scope =>
            {
                scope.GetRequiredService<RecoveryScopeProbe>(); Interlocked.Increment(ref created);
                var executor = new MihomoConfigurationPublicationService(f.Store, f.Provider,
                    new(f, f.Store, f.Provider, f, new DirectDownloads()), f, new(f, f.HostProxy));
                instances.Add(new WeakReference(executor)); return executor;
            });
            services.AddTransient<MihomoInstallationPublicationService>(_ => throw new Exception("Wrong recovery kind: installation"));
            services.AddTransient<MihomoRemovalPublicationService>(_ => throw new Exception("Wrong recovery kind: removal"));
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var worker = new MihomoPublicationRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MihomoPublicationRecoveryService>.Instance);
            check(created == 0, "Creating a recovery scheduler does not construct a coordinator");
            var run = worker.ReconcileAsync(default);
            try
            {
                await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(created == 1 && !run.IsCompleted && (await f.Store.ReadAsync(default)).Pending is not null,
                    "Busy recovery waits for asynchronous scope disposal and keeps the durable operation pending");
            }
            finally { release.TrySetResult(); }
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var attempt = 0; attempt < 10 && instances[0].IsAlive; attempt++)
            { await Task.Delay(20); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            check(!instances[0].IsAlive, "The five-second retry timer does not retain the completed coordinator");
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            check(created == 2 && cleaned == 2 && f.ConfigureCalls == 1 && f.RecoverCalls == 2
                && (await f.Store.ReadAsync(default)).Pending is null,
                "Retry selects only a fresh configuration executor and reconciles the original operation without reapplying");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Mode = "recovery"; await f.PublishAsync();
            var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var services = new ServiceCollection(); services.AddSingleton(f.Store);
            services.AddScoped(_ => new RecoveryScopeProbe(() => { disposed.TrySetResult(); return Task.CompletedTask; }));
            services.AddTransient<MihomoConfigurationPublicationService>(scope =>
            { scope.GetRequiredService<RecoveryScopeProbe>(); resolved.TrySetResult(); return f.Service; });
            await using var provider = services.BuildServiceProvider();
            var worker = new MihomoPublicationRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MihomoPublicationRecoveryService>.Instance);
            using var stopping = new CancellationTokenSource();
            await f.Store.Gate.WaitAsync();
            try
            {
                var run = worker.ReconcileAsync(stopping.Token); await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                stopping.Cancel();
                try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
                check(disposed.Task.IsCompletedSuccessfully && f.Store.Gate.CurrentCount == 0
                    && (await f.Store.ReadAsync(default)).Pending is not null,
                    "Stopping a recovery pass waiting for publication ownership disposes its scope without stealing the gate or clearing pending");
            }
            finally { f.Store.Gate.Release(); }
        }
    }
    private static async Task CheckContinuousRecoveryAsync(Action<bool, string> check)
    {
        using var f = await Fixture.CreateAsync();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(f.Store);
        services.AddTransient<MihomoPublicationRecoveryService>();
        services.AddTransient<MihomoConfigurationPublicationService>(_ => f.Service);
        services.AddTransient<MihomoInstallationPublicationService>(_ => throw new Exception("Scheduler selected wrong installation recovery"));
        services.AddTransient<MihomoRemovalPublicationService>(_ => throw new Exception("Scheduler selected wrong removal recovery"));
        await using var provider = services.BuildServiceProvider();
        using var scheduler = new MihomoPublicationRecoveryScheduler(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MihomoPublicationRecoveryScheduler>.Instance);
        check(await scheduler.ScanAsync(null, default) is null && f.QueryCalls == 0,
            "An idle Core recovery scan reads only durable responsibility, with no native or management work");
        await scheduler.StartAsync(default);
        await Task.Delay(100); // Let the initial empty scan finish before accepting a later transaction.
        f.Mode = "recovery"; await f.PublishAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while ((await f.Store.ReadAsync(default)).Pending is not null && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        await scheduler.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        check((await f.Store.ReadAsync(default)).Pending is null && f.ConfigureCalls == 1 && f.RecoverCalls == 1,
            "Core recovery discovers a transaction accepted after startup and reconciles without reconfiguration");

        using var blocked = await Fixture.CreateAsync(); blocked.Mode = "recovery"; await blocked.PublishAsync(); blocked.RemainRecoveryRequired = true;
        using var blockedServices = new PublicationRecoveryServices(blocked.Store, () => blocked.Service, () => blocked.Installation, () => blocked.Removal);
        var collection = new ServiceCollection(); collection.AddSingleton(blocked.Store);
        collection.AddTransient(_ => new MihomoPublicationRecoveryService(blockedServices.Scopes, NullLogger<MihomoPublicationRecoveryService>.Instance));
        await using var blockedProvider = collection.BuildServiceProvider();
        using var blockedScheduler = new MihomoPublicationRecoveryScheduler(blockedProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MihomoPublicationRecoveryScheduler>.Instance);
        var indexPath = Path.Combine(blocked.GetStateDirectory(), "mihomo-publication.protected.schedule");
        var pendingId = (await blocked.Store.ReadAsync(default)).Pending!.OperationId;
        await File.WriteAllTextAsync(indexPath, "corrupt");
        check(await blocked.Store.ReadPendingIdentityAsync(default) == pendingId
            && await File.ReadAllTextAsync(indexPath) != "corrupt",
            "A damaged schedule index is rebuilt from the original pending identity");
        File.Delete(indexPath);
        Directory.CreateDirectory(indexPath);
        try
        {
            check(await blocked.Store.ReadPendingIdentityAsync(default) == pendingId,
                "An unwritable schedule index cannot suppress the original pending identity");
        }
        finally { Directory.Delete(indexPath); }
        var statePath = Path.Combine(blocked.GetStateDirectory(), "mihomo-publication.protected");
        var savedState = await File.ReadAllBytesAsync(statePath);
        File.Delete(statePath);
        Directory.CreateDirectory(statePath);
        try
        {
            try
            {
                await blockedScheduler.ScanAsync(null, default);
                throw new InvalidOperationException("A directory at the authority path was treated as an empty ledger.");
            }
            catch (RuntimePolicyException error) when (error.ProblemCode == "runtime.publication_state_invalid")
            {
                check(blocked.RecoverCalls == 0, "An invalid authority blocks scheduling without replaying publication");
            }
        }
        finally
        {
            Directory.Delete(statePath);
            await File.WriteAllBytesAsync(statePath, savedState);
        }
        var identity = await blockedScheduler.ScanAsync(null, default);
        check(identity == (await blocked.Store.ReadAsync(default)).Pending!.OperationId && blocked.RecoverCalls == 1,
            "An operator-required transaction remains a durable blocked identity");
        check(await blockedScheduler.ScanAsync(identity, default) == identity && blocked.RecoverCalls == 1,
            "Periodic scans do not automatically replay an operator-required transaction");

        using var raced = await Fixture.CreateAsync(); raced.Mode = "recovery"; await raced.PublishAsync();
        var next = Guid.NewGuid(); var raceServices = new ServiceCollection(); raceServices.AddSingleton(raced.Store);
        raceServices.AddScoped(_ => new RecoveryScopeProbe(async () =>
        {
            await raced.Store.Gate.WaitAsync();
            try
            {
                var state = await raced.Store.ReadAsync(default);
                await raced.Store.BeginConfigurationAsync(next, state.Revision, raced.Candidate,
                    raced.Plan with { ExpectedStateRevision = raced.Snapshot.Installation.StateRevision, IdempotencyKey = "newer-publication" }, default);
            }
            finally { raced.Store.Gate.Release(); }
        }));
        raceServices.AddTransient<MihomoConfigurationPublicationService>(scope =>
        { scope.GetRequiredService<RecoveryScopeProbe>(); return raced.Service; });
        await using var raceProvider = raceServices.BuildServiceProvider();
        var runner = new MihomoPublicationRecoveryService(raceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MihomoPublicationRecoveryService>.Instance);
        check(await runner.ReconcileAsync(default) is null && (await raced.Store.ReadAsync(default)).Pending?.OperationId == next,
            "A publication accepted during old-pass disposal is not misclassified as an already-attempted blocked operation");
    }

    private sealed class RecoveryScopeProbe(Func<Task> dispose) : IAsyncDisposable
    { public async ValueTask DisposeAsync() => await dispose(); }

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
        internal MihomoRemovalPublicationService Removal => new(Store, new(this, Store, new(Store, this, this), this, new DirectDownloads()), this, MihomoRemovalHostFixture.Files(this));
        internal MihomoPublicationStore Store { get; private set; } = null!;
        internal MihomoRuntimeAuthorizationProvider Provider { get; private set; } = null!;
        internal MihomoConfigurationPublicationService Service { get; private set; } = null!;
        internal MihomoInstallationPublicationService Installation { get; private set; } = null!;
        internal MihomoPublishedRuntime Previous { get; private set; } = null!;
        internal MihomoPublishedRuntime Candidate { get; private set; } = null!;
        internal RuntimeConfigurationPlan Plan { get; private set; } = null!;
        internal RuntimeInstallationSnapshot Snapshot { get; set; } = null!;
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
            var runtime = new MihomoRuntimeService(this, Store, Provider, this, new DirectDownloads());
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
