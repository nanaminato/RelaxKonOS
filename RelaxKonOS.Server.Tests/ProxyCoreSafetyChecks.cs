using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Builder;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.ProxyServer;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Startup.Modules;
using RelaxKonOS.Server.ManagedRuntimes;

internal static class ProxyCoreSafetyChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine($"PASS CORE PROXY {++count}: {label}"); }
        var responseHost = WebApplication.CreateBuilder();
        responseHost.Logging.ClearProviders();
        var context = ServerModuleContext.Create(responseHost.Configuration, responseHost.Environment, "memory");
        ServerModules.CreateManaged(new HashSet<string> { ServerModules.ProxyId }).ConfigureServices(responseHost, context);
        await using (var app = responseHost.Build())
        {
            var coordinator = app.Services.GetRequiredService<RelaxKonOS.Server.Residency.ModuleResidencyCoordinator>();
            var leases = app.Services.GetRequiredService<RelaxKonOS.Server.Residency.IResidentModuleWorkLeaseSource>();
            using (await leases.AcquireAsync(ServerModules.ProxyId, default))
            {
                var reasons = await new ProxyModule().InspectOrphanedWorkAsync(app.Services, context, default);
                Check(reasons.Count == 1 && reasons[0].ToString()!.Contains("response", StringComparison.Ordinal),
                    "Proxy deactivation sees a real resident response lease without consulting idle business ledgers");
            }
            Check(!coordinator.HasBackgroundWork(ServerModules.ProxyId),
                "The original Proxy coordinator observes response lease release");
        }
        foreach (var enabled in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "proxy-core-safety-" + Guid.NewGuid().ToString("N"));
            var paths = new TestProxyPaths(root);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.AddProxyRuntimeServices();
            if (enabled) new ProxyModule().ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite"));
            Check(builder.Services.Any(item => item.ServiceType == typeof(IProxyEngineRegistry)) == enabled,
                "Core safety registration does not register the optional engine management graph");
            Check(builder.Services.Any(item => item.ServiceType == typeof(IProxyPlatformService)) == enabled
                && builder.Services.Any(item => item.ServiceType == typeof(IProxySubscriptionDownloader)) == enabled,
                "Core safety does not register the management capability and download facades");
            Check(builder.Services.Any(item => item.ServiceType == typeof(ProxyServerOperationQueue)) == enabled,
                "Core publication does not register the inbound HTTP acceptance facade");
            if (enabled) Check(builder.Services.Single(item => item.ServiceType == typeof(IProxyEngineRegistry)).Lifetime == ServiceLifetime.Scoped
                && builder.Services.Single(item => item.ServiceType == typeof(IProxyLifecycleService)).Lifetime == ServiceLifetime.Scoped,
                "Engine registry and lifecycle facades belong to execution scopes");
            builder.Services.AddSingleton<IProxyPlatformPaths>(paths);
            var protection = new CountingProtection();
            builder.Services.AddSingleton<IDataProtectionProvider>(protection);
            builder.Services.AddSingleton<IServerModeResolver>(new SystemMode());
            var helper = new Helper(); builder.Services.AddSingleton<IPrivilegedOperationTransport>(helper);
            builder.Services.AddSingleton<IRuntimeDownloadProxyProvider>(new Downloads());
            var host = new Host(); builder.Services.AddSingleton<IHostSystemProxyService>(host);
            var runtime = new TestProxyTunRuntimeController(); builder.Services.AddSingleton<IProxyTunRuntimeController>(runtime);
            var network = new TestProxyNetworkSafetyPlatform { SnapshotSafe = true }; builder.Services.AddSingleton<IProxyNetworkSafetyPlatform>(network);
            builder.Services.AddSingleton<IProxyEngineRegistry>(_ => throw new Exception("Safety constructed management engines"));
            builder.Services.AddSingleton<ProxyServerService>(_ => throw new Exception("Expiry constructed the inbound management facade"));
            builder.Services.AddSingleton<MihomoSettingsService>(_ => throw new Exception("Safety constructed the settings writer"));
            var expiryActivations = 0;
            builder.Services.RemoveAll<IProxyServerExpiryExecutor>();
            builder.Services.AddTransient<IProxyServerExpiryExecutor>(services =>
            {
                Interlocked.Increment(ref expiryActivations);
                return ActivatorUtilities.CreateInstance<ProxyServerPublicationService>(services);
            });
            var guardActivations = 0;
            builder.Services.RemoveAll<IProxySystemProxyReconciler>();
            builder.Services.AddTransient<IProxySystemProxyReconciler>(services =>
            {
                Interlocked.Increment(ref guardActivations);
                return ActivatorUtilities.CreateInstance<MihomoSystemProxyReconciler>(services);
            });
            await using var services = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            try
            {
                if (enabled)
                {
                    await using var first = services.CreateAsyncScope();
                    await using var next = services.CreateAsyncScope();
                    var platform = first.ServiceProvider.GetRequiredService<IProxyPlatformService>();
                    var downloader = first.ServiceProvider.GetRequiredService<IProxySubscriptionDownloader>();
                    Check(ReferenceEquals(platform, first.ServiceProvider.GetRequiredService<IProxyPlatformService>())
                        && ReferenceEquals(downloader, first.ServiceProvider.GetRequiredService<IProxySubscriptionDownloader>())
                        && !ReferenceEquals(platform, next.ServiceProvider.GetRequiredService<IProxyPlatformService>())
                        && !ReferenceEquals(downloader, next.ServiceProvider.GetRequiredService<IProxySubscriptionDownloader>()),
                        "Capability and download facades are reused only within their execution scope");
                    var inboundQueue = first.ServiceProvider.GetRequiredService<ProxyServerOperationQueue>();
                    Check(ReferenceEquals(inboundQueue, first.ServiceProvider.GetRequiredService<ProxyServerOperationQueue>())
                        && !ReferenceEquals(inboundQueue, next.ServiceProvider.GetRequiredService<ProxyServerOperationQueue>())
                        && ReferenceEquals(first.ServiceProvider.GetRequiredService<ProxyServerStore>(),
                            next.ServiceProvider.GetRequiredService<ProxyServerStore>()),
                        "Inbound request scopes share one Core publication authority and have independent acceptance facades");
                }
                foreach (var type in new[] { typeof(ProxyRecoveryHostedService), typeof(SystemProxyGuardHostedService), typeof(ProxyServerExpiryService) })
                    Check(builder.Services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == type) == 1,
                        "Exactly one Core owner regardless of management enablement: " + type.Name + "/" + enabled);
                var previousSafety = new ProxyTunSafetyService(paths, network, runtime);
                await previousSafety.EnableAsync(Guid.NewGuid(), null, default);
                var safety = services.GetRequiredService<IProxyTunSafetyService>();
                Check(await safety.CaptureRecoveryAsync(includeCurrentSession: false, default) is not null,
                    "The new safety owner can recover a completed session from the previous owner");
                var store = services.GetRequiredService<ProxyServerStore>();
                var id = Guid.NewGuid(); var inboundId = Guid.NewGuid(); var secret = Guid.NewGuid().ToString("D");
                var inbound = new ProxyServerInbound(inboundId, "expired", ProxyServerProtocol.VlessReality, "127.0.0.1", 24443,
                    "proxy.example.com", 24443, "proxy.example.com", HandshakeHost: "www.example.com");
                var published = new ProxyServerMaterial(new(1, [inbound]), [new(id, [inboundId], secret)],
                    [new(inboundId, new string('a', 43), new string('a', 43), "0123456789abcdef")]);
                var future = DateTimeOffset.UtcNow.AddHours(1);
                var saved = ProxyServerState.Empty with { Published = published,
                    Credentials = [new(id, Guid.NewGuid(), "expiring", [inboundId], secret, true, future)] };
                await store.WriteAsync(saved, default);
                var expiryIndex = Path.Combine(paths.GetStateDirectory(), "proxy-server.protected.expiry");
                var staleIndex = await File.ReadAllBytesAsync(expiryIndex);
                var reopened = new ProxyServerStore(paths, services.GetRequiredService<IDataProtectionProvider>());
                var reads = protection.StateReads;
                var schedule = await reopened.ReadExpiryScheduleAsync(default);
                Check(protection.StateReads == reads, "A matching protected index avoids decrypting the original state");
                Check(!schedule.RequiresPass(future.AddTicks(-1)) && schedule.RequiresPass(future),
                    "Expiry schedule is derived from the persisted grant and uses its exact expiry boundary");
                using (var idle = ActivatorUtilities.CreateInstance<ProxyServerExpiryService>(services))
                    await idle.ReconcileAsync(default);
                Check(expiryActivations == 0, "An idle expiry scan does not construct the publication executor");
                await store.WriteAsync(saved with { PendingPublication = published }, default);
                await File.WriteAllBytesAsync(expiryIndex, staleIndex);
                reads = protection.StateReads;
                Check((await reopened.ReadExpiryScheduleAsync(default)).PendingPublication && protection.StateReads == reads + 1,
                    "An authenticated but stale index cannot hide the original pending publication");
                Check((await reopened.ReadExpiryScheduleAsync(default)).PendingPublication && protection.StateReads == reads + 1,
                    "A recovered index preserves pending work without another ledger decryption");
                await File.WriteAllTextAsync(expiryIndex, "damaged");
                Check((await reopened.ReadExpiryScheduleAsync(default)).PendingPublication,
                    "A damaged index falls back to the authoritative pending publication");
                File.Delete(expiryIndex);
                Check((await reopened.ReadExpiryScheduleAsync(default)).PendingPublication,
                    "A missing index preserves expiry recovery for existing ledgers");
                File.Delete(expiryIndex);
                if (OperatingSystem.IsLinux())
                {
                    var target = expiryIndex + ".target";
                    await File.WriteAllTextAsync(target, "untouched");
                    File.CreateSymbolicLink(expiryIndex, target);
                    Check((await reopened.ReadExpiryScheduleAsync(default)).PendingPublication
                        && await File.ReadAllTextAsync(target) == "untouched"
                        && new FileInfo(expiryIndex).LinkTarget is not null,
                        "A linked index cannot hide pending work or redirect index repair");
                    File.Delete(expiryIndex);
                    File.Delete(target);
                }
                Directory.CreateDirectory(expiryIndex);
                try
                {
                    await store.WriteAsync(saved with { Credentials = [saved.Credentials.Single() with { Enabled = false }] }, default);
                    Check((await store.ReadExpiryScheduleAsync(default)).RequiresPass(DateTimeOffset.UtcNow),
                        "An unavailable index cannot prevent committing or detecting a disabled published grant");
                }
                finally { Directory.Delete(expiryIndex); }
                await store.WriteAsync(saved with { Credentials = [saved.Credentials.Single() with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }] }, default);
                helper.Hash = ProxyServerPublicationService.Hash(SingBoxConfiguration.Render(published, false));
                var recovery = ActivatorUtilities.CreateInstance<ProxyRecoveryHostedService>(services);
                var guard = ActivatorUtilities.CreateInstance<SystemProxyGuardHostedService>(services);
                var expiry = ActivatorUtilities.CreateInstance<ProxyServerExpiryService>(services);
                var idleSettings = (await services.GetRequiredService<IProxySettingsReader>().GetAsync(default)) with { SystemProxyEnabled = false };
                Check(await guard.ReconcileAsync(false, idleSettings, default) is null && guardActivations == 0,
                    "An idle system proxy guard does not construct the reconciliation graph after startup");
                using (recovery) using (guard) using (expiry)
                {
                    if (!enabled) { network.RestoreSucceeds = false; helper.LoseReply = true; host.Failures = 1; }
                    await recovery.StartAsync(default); await guard.StartAsync(default); await expiry.StartAsync(default);
                    if (!enabled)
                    {
                        await Task.Delay(100);
                        Check((await safety.GetStatusAsync(default)).HasRecoveryMarker && !recovery.ExecuteTask!.IsCompleted,
                            "A failed Core restore retains its durable marker and scheduled recovery without management");
                        network.RestoreSucceeds = true;
                    }
                    using (var recoveryBudget = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                    {
                        while ((await safety.GetStatusAsync(recoveryBudget.Token)).HasRecoveryMarker)
                            await Task.Delay(20, recoveryBudget.Token);
                    }
                    await recovery.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
                    await host.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await helper.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                    while ((await store.ReadAsync(default)).PendingPublication is not null && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
                    await expiry.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); await guard.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!(await safety.GetStatusAsync(default)).HasRecoveryMarker && runtime.DisableCount == (enabled ? 1 : 2) && network.RestoreCount == (enabled ? 1 : 2),
                        "Core startup restores TUN without loading management services: " + enabled);
                    Check((await store.ReadAsync(default)) is { Published.Credentials.Count: 0, PendingPublication: null } && helper.ApplyCalls == 1,
                        "Core expiry revokes only the persisted published grant while management is disabled or enabled: " + enabled);
                    if (!enabled) Check(host.Calls >= 2 && helper.ApplyCalls == 1,
                        "Disabled management retains guard retry and reconciles a lost expiry response without a second publication");
                }
                var mihomo = services.GetRequiredService<MihomoPublicationStore>();
                var direct = await mihomo.BeginDirectReloadAsync("mode: rule\n", "mode: direct\n", default);
                if (!enabled)
                {
                    var module = new ProxyModule();
                    var moduleContext = ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite");
                    Check((await module.InspectOrphanedWorkAsync(services, moduleContext, default))
                        .Any(item => item.ToString()!.Contains(direct.OperationId.ToString(), StringComparison.Ordinal))
                        && (await module.InspectDisabledStartupAsync(services, moduleContext, default)).Count == 0,
                        "Direct uncertainty is visible to deactivation inspection while disabled startup can run Core safety");
                }
                var hostCalls = host.Calls;
                await using var guardScope = services.CreateAsyncScope();
                Check(await guardScope.ServiceProvider.GetRequiredService<IProxySystemProxyReconciler>().ReconcileSystemProxyAsync(true, default) is null
                    && host.Calls == hostCalls + 1 && (await mihomo.ReadAsync(default)).DirectReload == direct,
                    "Core guard can still reconcile the prior settings without settling the direct checkpoint");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine($"Core Proxy safety ownership checks passed: {count}");
    }
    private sealed class CountingProtection : IDataProtectionProvider
    {
        private readonly EphemeralDataProtectionProvider _inner = new();
        internal int StateReads;

        public IDataProtector CreateProtector(string purpose)
        {
            var protector = _inner.CreateProtector(purpose);
            return purpose == "RelaxKonOS.ProxyServer.State.v1" ? new StateProtector(protector, this) : protector;
        }

        private sealed class StateProtector(IDataProtector inner, CountingProtection owner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => inner.CreateProtector(purpose);
            public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);
            public byte[] Unprotect(byte[] protectedData)
            {
                Interlocked.Increment(ref owner.StateReads);
                return inner.Unprotect(protectedData);
            }
        }
    }

    private sealed class Downloads : IRuntimeDownloadProxyProvider
    { public Task<RelaxKonOS.Protocol.ManagedRuntimes.RuntimeDownloadProxy?> ResolveAsync(CancellationToken ct) => throw new Exception("Safety downloaded software"); }
    private sealed class SystemMode : IServerModeResolver
    {
        public RelaxKonOS.Protocol.Common.ServerMode Mode => RelaxKonOS.Protocol.Common.ServerMode.System;
        public bool Supports(ServerHostFeature feature) => true;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
    private sealed class Host : IHostSystemProxyService
    {
        public int Failures, Calls;
        public TaskCompletionSource Applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProxySystemProxyCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(new ProxySystemProxyCapabilities(true, false, true, true));
        public Task<string?> ApplyAsync(ProxySettingsDto settings, ProxySettingsDto previous, bool enforce, CancellationToken ct)
        { Calls++; Applied.TrySetResult(); return Task.FromResult<string?>(Failures-- > 0 ? ProxyProblemCodes.SystemProxyConflict : null); }
    }
    private sealed class Helper : IPrivilegedOperationTransport
    {
        public string? Hash;
        public int ApplyCalls;
        public bool LoseReply;
        private ProxyServerPublicationReceipt? receipt;
        public TaskCompletionSource Applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (request.Operation == PrivilegedOperationKind.ProxyServerApply)
            {
                var previous = Hash!;
                ApplyCalls++; Hash = ProxyServerPublicationService.Hash(SingBoxConfiguration.Render(request.ProxyServer!.Material!, false));
                receipt = new(request.OperationId!.Value, Hash, previous, ProxyServerPublicationOutcome.Committed);
                Applied.TrySetResult();
                if (LoseReply) { LoseReply = false; return Task.FromResult(new PrivilegedOperationResult(false, Error: "proxy_server.helper_unavailable")); }
            }
            else if (request.Operation == PrivilegedOperationKind.ProxyServerPublicationReceipt && receipt?.OperationId == request.OperationId)
                return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(receipt))));
            else if (request.Operation != PrivilegedOperationKind.ProxyServerStatus) throw new Exception("Unexpected Core Helper action");
            var status = new ProxyServerStatus(true, true, true, false, "test", Hash, true, null);
            return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status))));
        }
    }
}
