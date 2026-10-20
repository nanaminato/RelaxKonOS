using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;

/// <summary>Real Server authority/coordinator through the in-process Helper boundary to the fixed real
/// systemd service. This does not exercise installed Helper IPC, HTTP routes or application DI.</summary>
internal static class MihomoServerPublicationAcceptance
{
    internal static async Task<int> RunAsync(MihomoRuntimeLifecycle lifecycle, MihomoPublishedRuntime original,
        int originalPort, Func<int, Task<bool>> probe)
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-server-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new Paths(root, original.ControllerSecret);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        var store = new MihomoPublicationStore(paths, protection);
        var transport = new Transport(lifecycle);
        var publicationProfiles = await MihomoProfilePublicationChecks.CreateAsync(root);
        var hostFailures = 0;
        var systemProxy = MihomoPublicationTransactionChecks.Stub<IHostSystemProxyService>(method => method.Name switch
        {
            "ApplyAsync" => Task.FromResult<string?>(hostFailures-- > 0 ? ProxyProblemCodes.SystemProxyConflict : null),
            "GetCapabilitiesAsync" => Task.FromResult(new ProxySystemProxyCapabilities(true, true, false, false)),
            _ => throw new Exception("Unexpected host settings call")
        });

        MihomoRuntimeService Runtime(MihomoPublicationStore selected) => new(transport, selected,
            new(selected, paths, paths), paths);
        MihomoConfigurationPublicationService Service(MihomoPublicationStore selected) =>
            new(selected, new(selected, paths, paths), Runtime(selected), paths, new(paths, systemProxy), publicationProfiles);
        async Task<(Guid Operation, RuntimeConfigurationPlan Plan, long Revision)> PlanAsync(MihomoPublishedRuntime target)
        {
            var observed = await lifecycle.QueryAsync(default);
            var publication = await store.ReadAsync(default);
            return (Guid.NewGuid(), new(observed.Installation.InstallationId, observed.Installation.HostId, "mihomo",
                observed.Installation.StateRevision, observed.ActiveRevisionId!.Value, publication.Identity!,
                MihomoPublicationStore.Identity(target.Yaml), "server-real-" + Guid.NewGuid().ToString("N")), publication.Revision);
        }
        var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        try
        {
            var observed = await lifecycle.QueryAsync(default);
            if (observed.ConfigurationIdentity != MihomoPublicationStore.Identity(original.Yaml) || observed.ActiveRevisionId is null)
                throw new Exception("Server publication acceptance requires the already verified original installation");
            // Bootstrap is based on actual installed Helper evidence, not a fabricated pre-install confirm.
            var bootstrap = Guid.NewGuid();
            await store.BeginAsync(bootstrap, 0, original, default);
            Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
            await File.WriteAllTextAsync(active, original.Yaml);
            await store.ConfirmAsync(bootstrap, observed.ConfigurationIdentity, default);
            var candidate = MihomoConfigurationPolicy.Describe(original.Yaml.Replace("mixed-port: " + originalPort,
                "mixed-port: " + (originalPort + 2), StringComparison.Ordinal)).Material;
            var first = await PlanAsync(candidate);
            var result = await Service(store).PublishAsync(first.Operation, first.Revision, first.Plan, candidate, default);
            Check(result.Outcome == MihomoPublicationOutcome.Confirmed && await probe(originalPort + 2),
                "Real Server publication confirms the changed listener and HTTP traffic");
            Check((await new MihomoRuntimeAuthorizationProvider(store, paths, paths).ReadAsync(default)).Published!.Yaml == candidate.Yaml
                && await File.ReadAllTextAsync(active) == candidate.Yaml,
                "Server grants and its file mirror use the actual Helper-confirmed candidate");
            Check((await Service(store).PublishAsync(first.Operation, first.Revision, first.Plan, candidate, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && transport.ConfigureCalls == 1, "Real committed publication replays without another service restart");

            var second = await PlanAsync(original);
            transport.LoseNextConfigurationResponse = true;
            try
            {
                await Service(store).PublishAsync(second.Operation, second.Revision, second.Plan, original, default);
                throw new Exception("Lost real Helper response was not simulated");
            }
            catch (IOException) { count++; }
            Check((await store.ReadAsync(default)).Pending?.OperationId == second.Operation && await probe(originalPort),
                "Real Helper commits while the lost Server response leaves publication pending");
            store = new(paths, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
            using (var startup = new MihomoPublicationRecoveryService(store, Service(store),
                new(store, new(store, paths, paths), Runtime(store), paths), new(store, Runtime(store), paths, new(paths, systemProxy)), NullLogger<MihomoPublicationRecoveryService>.Instance))
            {
                await startup.StartAsync(default);
                await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            }
            Check((await store.ReadAsync(default)) is { Pending: null } recovered && recovered.Identity == second.Plan.CandidateIdentity
                && transport.ConfigureCalls == 2 && transport.RecoverCalls == 0
                && await File.ReadAllTextAsync(active) == original.Yaml,
                "Recreated Server startup recovery reconciles the real receipt without reapplying the configuration");

            var invalid = MihomoConfigurationPolicy.Describe(original.Yaml.Replace("MATCH,DIRECT", "UNSUPPORTED-RULE,DIRECT", StringComparison.Ordinal)).Material;
            var rejected = await PlanAsync(invalid);
            var failed = await Service(store).PublishAsync(rejected.Operation, rejected.Revision, rejected.Plan, invalid, default);
            Check(failed.Outcome == MihomoPublicationOutcome.Compensated && await probe(originalPort)
                && (await store.ReadAsync(default)).Pending is null && await File.ReadAllTextAsync(active) == original.Yaml,
                "Actual native configuration refusal is reconciled as compensation, never publication success");
            Check((await Service(store).PublishAsync(rejected.Operation, rejected.Revision, rejected.Plan, invalid, default)).Outcome == MihomoPublicationOutcome.Compensated
                && transport.ConfigureCalls == 3, "Real failed publication replays its measured failure without another transaction");

            var profiles = publicationProfiles;
            var profile = await profiles.UpsertAsync(null, "real application", "mihomo", null, default);
            var profileId = profile.Id;
            var engine = MihomoPublicationTransactionChecks.Stub<IProxyEngine>(method => method.Name == "get_EngineId"
                ? "mihomo" : throw new Exception("Application used fixed runtime engine"));
            var settings = MihomoPublicationTransactionChecks.Stub<IProxySettingsService>(method => method.Name == "GetAsync"
                ? Task.FromResult(new ProxySettingsDto(false, false, false, false, false, "warning", originalPort + 2, false, "127.0.0.1", ProxyTunSettingsDto.Default))
                : throw new Exception("Unexpected settings call"));
            var application = new ProxyConfigurationTransactionService(paths, new ProxyEngineRegistry([engine]), profiles, paths,
                new MihomoControllerOptions { Endpoint = new("http://127.0.0.1:" + original.ControllerPort + "/") }, store,
                settingsService: settings, managedPublication: Service(store), managedRuntime: Runtime(store));
            Check(await application.ApplyAsync(profileId, "rules:\n  - MATCH,DIRECT\n", default) is null
                && (await profiles.GetAsync(profileId, default)) is { IsActive: true, Revision: 2 } && await probe(originalPort + 2),
                "Application profile activation reaches real Helper transaction and serves HTTP on the selected listener");
            Check(await application.ApplyAsync(profileId, "rules:\n  - UNSUPPORTED-RULE,DIRECT\n", default) == ProxyProblemCodes.ConfigApplyFailed
                && (await profiles.GetAsync(profileId, default)) is { IsActive: true, Revision: 2 } && await probe(originalPort + 2) && (await store.ReadAsync(default)).Pending is null,
                "Application native rejection preserves real published configuration and profile selection");
            var nextProfile = await profiles.UpsertAsync(null, "recovered selection", "mihomo", null, default);
            await using (var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "profiles.db")))
            {
                await database.OpenAsync();
                await using var trigger = database.CreateCommand();
                trigger.CommandText = "CREATE TRIGGER reject_profile_publication BEFORE UPDATE ON proxy_profile_publication BEGIN SELECT RAISE(ABORT, 'injected marker failure'); END;";
                await trigger.ExecuteNonQueryAsync();
                var selectedYaml = "rules:\n  - DOMAIN,example.test,DIRECT\n  - MATCH,DIRECT\n";
                Check(await application.ApplyAsync(nextProfile.Id, selectedYaml, default) == ProxyProblemCodes.RecoveryRequired
                    && (await store.ReadAsync(default)).Pending?.Profile?.ProfileId == nextProfile.Id
                    && (await profiles.GetAsync(profileId, default)) is { IsActive: true, Revision: 2 }
                    && !(await profiles.GetAsync(nextProfile.Id, default))!.IsActive && await probe(originalPort + 2),
                    "Real runtime commits while SQLite marker failure rolls back profile selection and retains pending intent");
                trigger.CommandText = "DROP TRIGGER reject_profile_publication;";
                await trigger.ExecuteNonQueryAsync();
            }
            var beforeProfileRecovery = transport.ConfigureCalls;
            store = new(paths, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
            publicationProfiles = await MihomoProfilePublicationChecks.CreateAsync(root);
            using (var startup = new MihomoPublicationRecoveryService(store, Service(store),
                new(store, new(store, paths, paths), Runtime(store), paths), new(store, Runtime(store), paths, new(paths, systemProxy)), NullLogger<MihomoPublicationRecoveryService>.Instance))
            {
                await startup.StartAsync(default);
                await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            }
            Check((await store.ReadAsync(default)).Pending is null && transport.ConfigureCalls == beforeProfileRecovery
                && (await profiles.GetAsync(nextProfile.Id, default)) is { IsActive: true, Revision: 2 }
                && !(await profiles.GetAsync(profileId, default))!.IsActive && await probe(originalPort + 2),
                "Server restart reconciles real SQLite profile selection without repeating native configuration");
            var noOpApplication = new ProxyConfigurationTransactionService(paths, new ProxyEngineRegistry([engine]), publicationProfiles, paths,
                new MihomoControllerOptions { Endpoint = new("http://127.0.0.1:" + original.ControllerPort + "/") }, store,
                settingsService: settings, managedPublication: Service(store), managedRuntime: Runtime(store));
            var beforeSelectionOnly = await lifecycle.QueryAsync(default);
            Check(await noOpApplication.ActivateStoredAsync(nextProfile.Id, default) is null && transport.ConfigureCalls == beforeProfileRecovery
                && (await lifecycle.QueryAsync(default)).Installation.StateRevision == beforeSelectionOnly.Installation.StateRevision,
                "Selecting identical published profile reconciles metadata without a real native transaction");
            var restore = await PlanAsync(original);
            if ((await Service(store).PublishAsync(restore.Operation, restore.Revision, restore.Plan, original, default)).Outcome != MihomoPublicationOutcome.Confirmed)
                throw new Exception("Could not restore original configuration after application acceptance");

            MihomoSettingsService SettingsService() => new(paths,
                MihomoPublicationTransactionChecks.Stub<IMihomoControllerClient>(_ => throw new Exception("Settings used direct controller")),
                paths, new MihomoControllerOptions(), systemProxy, store,
                managedPublication: Service(store), managedRuntime: Runtime(store), settingsFiles: new(paths, systemProxy));
            var settingsRequest = new UpdateProxySettingsRequest(false, false, false, false, false, "warning", originalPort + 4);
            hostFailures = 1;
            var settingsProblem = await SettingsService().UpdateAsync(settingsRequest, default);
            var pendingSettings = (await store.ReadAsync(default)).Pending;
            Check(settingsProblem == ProxyProblemCodes.RecoveryRequired && pendingSettings?.Settings is not null
                && await probe(originalPort + 4) && !File.Exists(Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json")),
                "Real runtime commit plus failed host-settings effect leaves exact intent pending");
            var beforeRecovery = transport.ConfigureCalls;
            store = new(paths, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
            using (var startup = new MihomoPublicationRecoveryService(store, Service(store),
                new(store, new(store, paths, paths), Runtime(store), paths), new(store, Runtime(store), paths, new(paths, systemProxy)), NullLogger<MihomoPublicationRecoveryService>.Instance))
            {
                await startup.StartAsync(default);
                await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            }
            Check((await store.ReadAsync(default)).Pending is null && (await SettingsService().GetAsync(default)).MixedPort == originalPort + 4
                && transport.ConfigureCalls == beforeRecovery && await probe(originalPort + 4),
                "Startup settings recovery confirms real Helper receipt and resumes settings without native replay");
            var beforeMetadata = await lifecycle.QueryAsync(default);
            Check(await SettingsService().UpdateAsync(settingsRequest with { AllowInsecureSubscriptionSources = true }, default) is null
                && transport.ConfigureCalls == beforeRecovery && (await lifecycle.QueryAsync(default)).Installation.StateRevision == beforeMetadata.Installation.StateRevision
                && (await SettingsService().GetAsync(default)).AllowInsecureSubscriptionSources && await probe(originalPort + 4),
                "Host-only settings persist without a real runtime transaction or service restart");
            restore = await PlanAsync(original);
            if ((await Service(store).PublishAsync(restore.Operation, restore.Revision, restore.Plan, original, default)).Outcome != MihomoPublicationOutcome.Confirmed)
                throw new Exception("Could not restore original configuration after settings acceptance");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Server acceptance cleanup escaped its root");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
        return count;
    }

    private sealed class Transport(MihomoRuntimeLifecycle lifecycle) : IPrivilegedOperationTransport
    {
        internal bool LoseNextConfigurationResponse;
        internal int ConfigureCalls, RecoverCalls;
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            request = request with { OperationId = request.OperationId ?? Guid.NewGuid() };
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure) ConfigureCalls++;
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover) RecoverCalls++;
            var result = await MihomoRuntimeOperations.ExecuteAsync(request, () => lifecycle);
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure && LoseNextConfigurationResponse)
            {
                LoseNextConfigurationResponse = false;
                throw new IOException("Simulated loss after real Helper execution");
            }
            return result;
        }
    }
    private sealed class Paths(string root, string secret) : IProxyPlatformPaths, IProxyControllerSecretStore, IServerModeResolver
    {
        public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "versions");
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
        public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "data");
        public string GetStateDirectory() => Path.Combine(root, "state");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
        public Task<string?> ReadExistingAsync(CancellationToken ct) => Task.FromResult<string?>(secret);
        public Task<string> GetOrCreateAsync(CancellationToken ct) => throw new Exception("Server publication generated credentials");
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
