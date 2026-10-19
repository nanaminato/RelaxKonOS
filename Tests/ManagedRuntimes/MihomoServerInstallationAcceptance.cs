using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Installations;
using System.Text.Json;
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
        RuntimeChangePlan plan, RuntimeRelease newer, MihomoPublishedRuntime material, Action<PrivilegedOperationFrame> report)
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-server-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new Paths(root, material.ControllerSecret);
        var keys = new DirectoryInfo(Path.Combine(root, "keys"));
        var store = new MihomoPublicationStore(paths, DataProtectionProvider.Create(keys));
        var transport = new Transport(lifecycle, report);
        using var lifetime = new ManagedRuntimeLedgerFixture.Lifetime();
        var ledger = new InstallationOperationStore(new ManagedRuntimeLedgerFixture.Environment(root));
        var request = new ManagedRuntimeInstallationRequest(true, plan);
        var entry = ledger.Create(InstallationServiceId.Mihomo, InstallationOperationKind.Install, "native-ledger", plan.Request.IdempotencyKey,
            InstallationOperationStore.Reference(JsonSerializer.Serialize(request)), ["service:Mihomo", "runtime:mihomo"], out _);
        var operationId = entry.Operation.OperationId;
        ledger.Update(operationId, operation => operation with { State = InstallationOperationState.Running,
            Stage = InstallationStage.Preflight, StartedAt = DateTimeOffset.UtcNow }, "started");
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
            var unusedSettings = MihomoPublicationTransactionChecks.Stub<IProxySettingsService>(_ => throw new Exception("Ledger recovery read settings"));
            var domain = new MihomoRuntimeInstallationService(store, restarted.Runtime,
                new(store, restarted.Install, unusedSettings, paths, new()), restarted.Install, new(store, restarted.Runtime, paths, MihomoRemovalHostFixture.Files(paths)));
            var coordinator = new InstallationCoordinator(ledger, [domain], lifetime);
            await coordinator.StartAsync(default);
            Check(coordinator.Get(operationId)!.Operation.State == InstallationOperationState.Succeeded,
                "Installation ledger startup confirms real pending installation publication before reporting success");
            var confirmed = await store.ReadAsync(default);
            Check(confirmed is { Pending: null, Published: not null } && confirmed.Identity == installed.ConfigurationIdentity
                && await File.ReadAllTextAsync(active) == material.Yaml && transport.InstallCalls == 1 && transport.RecoveryCalls == 0,
                "Recreated startup confirms the actual install without downloading, re-installing or compensating again");
            Check((await new MihomoRuntimeAuthorizationProvider(store, paths, paths).ReadAsync(default)).Published!.Yaml == material.Yaml,
                "Ordinary runtime authority becomes available only after the actual installation is confirmed");
            Check((await restarted.Install.InstallAsync(operationId, 0, plan, material, default)).Outcome == MihomoPublicationOutcome.Confirmed
                && transport.InstallCalls == 1, "Real first-install replay returns its verified result without another installation");
            var replay = coordinator.Start(InstallationServiceId.Mihomo, InstallationOperationKind.Install,
                JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "native-ledger", plan.Request.IdempotencyKey);
            Check(replay.OperationId == operationId && transport.InstallCalls == 1, "Ledger replay retains operation identity and never repeats real install");
            foreach (var change in new[] { RuntimeChange.Repair, RuntimeChange.Update, RuntimeChange.Rollback, RuntimeChange.Uninstall, RuntimeChange.Install })
            {
                var current = await lifecycle.QueryAsync(default);
                var selected = change == RuntimeChange.Update ? newer : plan.Release!;
                var key = "native-ledger-" + change;
                var nextPlan = new RuntimeChangePlan(new(current.Installation.InstallationId, current.Installation.HostId, change,
                    current.Installation.StateRevision, plan.Request.ExpectedCatalogRevision, plan.Request.ExpectedTrustRevision,
                    change == RuntimeChange.Uninstall ? null : selected.ReleaseId, key), "mihomo",
                    change == RuntimeChange.Uninstall ? null : selected, current.Installation.ActiveReleaseId);
                var kind = change switch
                {
                    RuntimeChange.Repair => InstallationOperationKind.Repair, RuntimeChange.Update => InstallationOperationKind.Upgrade,
                    RuntimeChange.Rollback => InstallationOperationKind.Rollback, RuntimeChange.Uninstall => InstallationOperationKind.Uninstall,
                    _ => InstallationOperationKind.Install
                };
                var operation = coordinator.Start(InstallationServiceId.Mihomo, kind,
                    JsonSerializer.SerializeToElement(new ManagedRuntimeInstallationRequest(true, nextPlan), new JsonSerializerOptions(JsonSerializerDefaults.Web)), "native-ledger", key);
                var terminal = await ManagedRuntimeLedgerFixture.TerminalAsync(coordinator, operation.OperationId);
                var after = await lifecycle.QueryAsync(default, operation.OperationId);
                Check(terminal.State == InstallationOperationState.Succeeded && after.Receipt is { Outcome: RuntimeSwitchOutcome.Succeeded }
                    && (change == RuntimeChange.Uninstall ? after.Installation.Ownership == RuntimeOwnership.Absent
                        : after.Installation.ActiveReleaseId == selected.ReleaseId && after.Installation.Ownership == RuntimeOwnership.Managed)
                    && (await store.ReadAsync(default)).Pending is null,
                    "Real ledger action reconciles runtime and publication after response loss: " + change + ": " + terminal.ProblemCode);
            }
            var ledgerText = await File.ReadAllTextAsync(Path.Combine(root, "data", "installation-operations.json"));
            using (var document = JsonDocument.Parse(ledgerText))
            {
                var measured = document.RootElement.GetProperty("audit").EnumerateArray().Select(item => item.GetProperty("stage").GetString()).ToArray();
                Check(measured.Contains("Downloading") && measured.Contains("Verifying") && measured.Contains("HealthChecking"),
                    "Real lifecycle ledger records measured Host preparation and health stages");
            }
            Check(!ledgerText.Contains(material.ControllerSecret, StringComparison.Ordinal) && !ledgerText.Contains("MATCH,DIRECT", StringComparison.Ordinal),
                "Real lifecycle ledger stores neither credentials nor configuration text");
            await coordinator.StopAsync(default);
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
            var result = await MihomoRuntimeOperations.ExecuteAsync(request, () => lifecycle, async frame => { report(frame); await PrivilegedFrameReader.ReportAsync(frame); });
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
