using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Real Helper entry, systemd and files; installed Helper IPC and default app ledger remain separate gates.</summary>
internal static class MihomoServerRemovalAcceptance
{
    internal static async Task<int> RunAsync(MihomoRuntimeLifecycle lifecycle, RuntimeChangePlan plan, MihomoPublishedRuntime material,
        Func<RuntimeChangePlan>? reinstall = null)
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-server-removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new Paths(root, material.ControllerSecret);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        var store = new MihomoPublicationStore(paths, protection);
        var transport = new Transport(lifecycle);
        var hostProxy = new MihomoRemovalHostFixture(Path.Combine(root, "host-proxy"));
        var settingsFiles = new MihomoSettingsPublicationFiles(paths, hostProxy);
        var settingsPath = Path.Combine(paths.GetStateDirectory(), "mihomo-settings.json");
        async Task<ProxySettingsDto> SettingsAsync() => System.Text.Json.JsonSerializer.Deserialize<ProxySettingsDto>(await File.ReadAllBytesAsync(settingsPath))!;
        MihomoRuntimeService Runtime() => new(transport, store, new(store, paths, paths), paths, new DirectDownloads());
        MihomoRemovalPublicationService Service() => new(store, Runtime(), paths, settingsFiles);
        var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        var operation = Guid.NewGuid();
        try
        {
            var measured = await lifecycle.QueryAsync(default);
            Check(measured.Installation.Ownership == RuntimeOwnership.Managed
                && measured.ConfigurationIdentity == MihomoPublicationStore.Identity(material.Yaml),
                "Removal starts from a real installed and matching published configuration");
            var preferences = new ProxySettingsDto(true, false, true, false, true, "info", material.Listeners.First().Port,
                true, "127.0.0.1", ProxyTunSettingsDto.Default, ProxySystemProxyOptionsDto.Default);
            await hostProxy.SeedAsync(paths, preferences);
            hostProxy.HttpsProxy = "http://external-edit.invalid:9443";
            var seed = Guid.NewGuid(); await store.BeginAsync(seed, 0, material, default);
            Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory()); await File.WriteAllTextAsync(active, material.Yaml);
            await store.ConfirmAsync(seed, measured.ConfigurationIdentity!, default);
            try { await Service().RemoveAsync(operation, plan, default); throw new Exception("Expected lost real removal response"); }
            catch (IOException) { count++; }
            var removed = await lifecycle.QueryAsync(default, operation);
            Check(removed.Installation.Ownership == RuntimeOwnership.Absent && removed.Receipt is { Outcome: RuntimeSwitchOutcome.Succeeded }
                && removed.ConfigurationIdentity == (plan.Request.KeepData ? measured.ConfigurationIdentity : null)
                && (await store.ReadAsync(default)).Pending?.RemovalPlan is not null && File.Exists(active),
                "Real Helper removal commits before Server cleanup and a lost response preserves durable acceptance");
            Check((await store.ReadAsync(default)).Pending?.Settings is not null && hostProxy.Calls == 0
                && (await SettingsAsync()).SystemProxyEnabled,
                "Real native receipt does not silently confirm removal before host proxy cleanup");
            if (!plan.Request.KeepData) File.Delete(active); // Measured receipt already proves removal; simulate mirror-cleanup crash.
            store = new(paths, protection);
            var runtime = Runtime(); var authorization = new MihomoRuntimeAuthorizationProvider(store, paths, paths);
            hostProxy.Failures = 1;
            using var startup = new MihomoPublicationRecoveryService(store, new(store, authorization, runtime, paths),
                new(store, authorization, runtime, paths), Service(), NullLogger<MihomoPublicationRecoveryService>.Instance);
            await startup.StartAsync(default); await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            Check((await store.ReadAsync(default)).Pending?.Settings is not null && hostProxy.Calls == 1
                && (await SettingsAsync()).SystemProxyEnabled && transport.Removals == 1,
                "Real runtime absence plus rejected host cleanup keeps Server removal pending");
            var recoveryEntry = new MihomoRuntimeRecoveryService(store, runtime, new(store, authorization, runtime, paths),
                new(store, authorization, runtime, paths), Service());
            var recoveryResult = await recoveryEntry.RetryAsync(operation, default);
            Check(recoveryResult.OperationId == operation && recoveryResult.Outcome == RuntimeSwitchOutcome.Succeeded,
                "Named recovery entry confirms the complete native and Server removal commit");
            var state = await store.ReadAsync(default);
            Check(state is { Pending: null, LastRemoval.Confirmed: true } && (state.Published is not null) == plan.Request.KeepData
                && File.Exists(active) == plan.Request.KeepData && transport.Removals == 1 && transport.Recoveries == 0,
                "Startup reconciles the real receipt and exact Server cleanup window without repeating removal");
            Check(await SettingsAsync() == (preferences with { SystemProxyEnabled = false })
                && hostProxy.HttpProxy == MihomoRemovalHostFixture.Original && hostProxy.HttpsProxy == "http://external-edit.invalid:9443"
                && hostProxy.Calls == 2,
                "Real removal recovery restores owned host proxy, preserves external edits and persists only disabled activation");
            Check((await Service().RemoveAsync(operation, plan, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && transport.Removals == 1, "Real completed uninstall replays once without another Helper mutation");
            if (reinstall is not null)
            {
                var installation = new MihomoInstallationPublicationService(store, authorization, runtime, paths);
                var bootstrap = new MihomoInstallationBootstrapService(store, installation, new UnreadableSettings(), paths, new());
                var installed = await bootstrap.InstallAsync(Guid.NewGuid(), reinstall(), default);
                Check(installed.Outcome == MihomoPublicationOutcome.Confirmed && transport.Installs == 1
                    && (await store.ReadAsync(default)).Published!.Yaml == material.Yaml,
                    "Real retained reinstall uses the same confirmed Server material without reading drafts or generating credentials");
                Check(!(await SettingsAsync()).SystemProxyEnabled && hostProxy.HttpProxy == MihomoRemovalHostFixture.Original && hostProxy.Calls == 2,
                    "Retained reinstall never silently re-enables the removed runtime's host proxy");
            }
            return count;
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Transport(MihomoRuntimeLifecycle lifecycle) : IPrivilegedOperationTransport
    {
        internal int Removals, Recoveries, Installs;
        private bool loseRemovalResponse = true;
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            request = request with { OperationId = request.OperationId ?? Guid.NewGuid() };
            var removal = request.Operation == PrivilegedOperationKind.MihomoRuntimeExecute
                && request.RuntimeLifecycle?.FrozenPlan?.Request.Change == RuntimeChange.Uninstall;
            if (removal) Removals++;
            else if (request.Operation == PrivilegedOperationKind.MihomoRuntimeExecute) Installs++;
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover) Recoveries++;
            var result = await MihomoRuntimeOperations.ExecuteAsync(request, () => lifecycle);
            if (removal && loseRemovalResponse) { loseRemovalResponse = false; throw new IOException("Lost real removal response"); }
            return result;
        }
    }
    private sealed class UnreadableSettings : IProxySettingsService
    {
        public Task<ProxySettingsDto> GetAsync(CancellationToken ct) => throw new Exception("Retained reinstall read current settings");
        public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken ct) => throw new Exception("Retained reinstall changed settings");
        public Task<string?> ReconcileSystemProxyAsync(bool startup, CancellationToken ct) => throw new Exception("Retained reinstall changed host proxy");
    }
    private sealed class Paths(string root, string secret) : IProxyPlatformPaths, IProxyControllerSecretStore, IServerModeResolver
    {
        public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "versions");
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
        public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "data");
        public string GetStateDirectory() => Path.Combine(root, "state");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
        public Task<string?> ReadExistingAsync(CancellationToken ct) => Task.FromResult<string?>(secret);
        public Task<string> GetOrCreateAsync(CancellationToken ct) => throw new Exception("Removal generated credentials");
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
