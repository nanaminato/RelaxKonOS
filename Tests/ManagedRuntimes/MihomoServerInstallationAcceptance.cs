using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Actual first install with no pre-confirmed Server publication. Only the response is dropped;
/// the reviewed binary, systemd registration and Helper journal are real. Helper IPC remains in-process.</summary>
internal static class MihomoServerInstallationAcceptance
{
    internal static async Task<(RuntimeSwitchResult Receipt, int Checks)> RunAsync(MihomoRuntimeLifecycle lifecycle,
        RuntimeChangePlan plan, MihomoPublishedRuntime material, Action<PrivilegedOperationFrame> report)
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-server-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new Paths(root, material.ControllerSecret);
        var keys = new DirectoryInfo(Path.Combine(root, "keys"));
        var store = new MihomoPublicationStore(paths, DataProtectionProvider.Create(keys));
        var transport = new Transport(lifecycle, report);
        var operationId = Guid.NewGuid();
        (MihomoRuntimeService Runtime, MihomoInstallationPublicationService Install, MihomoConfigurationPublicationService Configuration) Compose(MihomoPublicationStore authority)
        {
            var provider = new MihomoRuntimeAuthorizationProvider(authority, paths, paths);
            var runtime = new MihomoRuntimeService(transport, authority, provider, paths);
            return (runtime, new(authority, provider, runtime, paths), new(authority, provider, runtime, paths));
        }
        var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        try
        {
            Check((await store.ReadAsync(default)).Published is null && !File.Exists(active),
                "Real first install starts without a fabricated confirmed publication or Server mirror");
            var first = Compose(store);
            try
            {
                await first.Install.InstallAsync(operationId, 0, plan, material, default);
                throw new Exception("Expected simulated loss after real install");
            }
            catch (IOException) { count++; }
            var installed = await lifecycle.QueryAsync(default, operationId);
            Check(installed.Receipt is { Outcome: RuntimeSwitchOutcome.Succeeded }
                && installed.ActiveRevisionId == operationId && (await store.ReadAsync(default)) is { Published: null, Pending: not null }
                && !File.Exists(active), "Real installed receipt precedes Server confirmation even when its response is lost");
            store = new(paths, DataProtectionProvider.Create(keys));
            var restarted = Compose(store);
            using (var startup = new MihomoPublicationRecoveryService(store, restarted.Configuration, restarted.Install, new(store, restarted.Runtime, paths),
                NullLogger<MihomoPublicationRecoveryService>.Instance))
            {
                await startup.StartAsync(default);
                await startup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            }
            var confirmed = await store.ReadAsync(default);
            Check(confirmed is { Pending: null, Published: not null } && confirmed.Identity == installed.ConfigurationIdentity
                && await File.ReadAllTextAsync(active) == material.Yaml && transport.InstallCalls == 1 && transport.RecoveryCalls == 0,
                "Recreated startup confirms the actual install without downloading, re-installing or compensating again");
            Check((await new MihomoRuntimeAuthorizationProvider(store, paths, paths).ReadAsync(default)).Published!.Yaml == material.Yaml,
                "Ordinary runtime authority becomes available only after the actual installation is confirmed");
            Check((await restarted.Install.InstallAsync(operationId, 0, plan, material, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && transport.InstallCalls == 1, "Real first-install replay returns its verified result without another installation");
            return (installed.Receipt!, count);
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Server first-install cleanup escaped its root");
            Directory.Delete(root, true);
        }
    }
    private sealed class Transport(MihomoRuntimeLifecycle lifecycle, Action<PrivilegedOperationFrame> report) : IPrivilegedOperationTransport
    {
        internal int InstallCalls, RecoveryCalls;
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            request = request with { OperationId = request.OperationId ?? Guid.NewGuid() };
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeExecute) InstallCalls++;
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover) RecoveryCalls++;
            var result = await MihomoRuntimeOperations.ExecuteAsync(request, () => lifecycle, frame => { report(frame); return Task.CompletedTask; });
            if (request.Operation == PrivilegedOperationKind.MihomoRuntimeExecute)
                throw new IOException("Simulated response loss after real Helper first install");
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
        public Task<string> GetOrCreateAsync(CancellationToken ct) => throw new Exception("First install recovery generated a credential");
        public ServerMode Mode => ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
}
