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
        foreach (var enabled in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "proxy-core-safety-" + Guid.NewGuid().ToString("N"));
            var paths = new TestProxyPaths(root);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.AddProxyRuntimeServices();
            if (enabled) new ProxyModule().ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite"));
            Check(builder.Services.Any(item => item.ServiceType == typeof(IProxyEngineRegistry)) == enabled,
                "Core safety registration does not register the optional engine management graph");
            if (enabled) Check(builder.Services.Single(item => item.ServiceType == typeof(IProxyEngineRegistry)).Lifetime == ServiceLifetime.Scoped
                && builder.Services.Single(item => item.ServiceType == typeof(IProxyLifecycleService)).Lifetime == ServiceLifetime.Scoped,
                "Engine registry and lifecycle facades belong to execution scopes");
            builder.Services.AddSingleton<IProxyPlatformPaths>(paths);
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton<IServerModeResolver>(new SystemMode());
            var helper = new Helper(); builder.Services.AddSingleton<IPrivilegedOperationTransport>(helper);
            builder.Services.AddSingleton<IRuntimeDownloadProxyProvider>(new Downloads());
            var host = new Host(); builder.Services.AddSingleton<IHostSystemProxyService>(host);
            var runtime = new TestProxyTunRuntimeController(); builder.Services.AddSingleton<IProxyTunRuntimeController>(runtime);
            var network = new TestProxyNetworkSafetyPlatform { SnapshotSafe = true }; builder.Services.AddSingleton<IProxyNetworkSafetyPlatform>(network);
            builder.Services.AddSingleton<IProxyEngineRegistry>(_ => throw new Exception("Safety constructed management engines"));
            builder.Services.AddSingleton<ProxyServerService>(_ => throw new Exception("Expiry constructed the inbound management facade"));
            builder.Services.AddSingleton<MihomoSettingsService>(_ => throw new Exception("Safety constructed the settings writer"));
            await using var services = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            try
            {
                foreach (var type in new[] { typeof(ProxyRecoveryHostedService), typeof(SystemProxyGuardHostedService), typeof(ProxyServerExpiryService) })
                    Check(builder.Services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == type) == 1,
                        "Exactly one Core owner regardless of management enablement: " + type.Name + "/" + enabled);
                var safety = services.GetRequiredService<IProxyTunSafetyService>();
                await safety.EnableAsync(Guid.NewGuid(), null, default);
                Check((await safety.GetStatusAsync(default)).HasRecoveryMarker, "A durable TUN session exists before simulated restart");
                var store = services.GetRequiredService<ProxyServerStore>();
                var id = Guid.NewGuid(); var inboundId = Guid.NewGuid(); var secret = Guid.NewGuid().ToString("D");
                var inbound = new ProxyServerInbound(inboundId, "expired", ProxyServerProtocol.VlessReality, "127.0.0.1", 24443,
                    "proxy.example.com", 24443, "proxy.example.com", HandshakeHost: "www.example.com");
                var published = new ProxyServerMaterial(new(1, [inbound]), [new(id, [inboundId], secret)],
                    [new(inboundId, new string('a', 43), new string('a', 43), "0123456789abcdef")]);
                await store.WriteAsync(ProxyServerState.Empty with { Published = published,
                    Credentials = [new(id, Guid.NewGuid(), "expired", [inboundId], secret, true, DateTimeOffset.UtcNow.AddMinutes(-1))] }, default);
                helper.Hash = ProxyServerPublicationService.Hash(SingBoxConfiguration.Render(published, false));
                var recovery = ActivatorUtilities.CreateInstance<ProxyRecoveryHostedService>(services);
                var guard = ActivatorUtilities.CreateInstance<SystemProxyGuardHostedService>(services);
                var expiry = ActivatorUtilities.CreateInstance<ProxyServerExpiryService>(services);
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
                    await recovery.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(40));
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
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine($"Core Proxy safety ownership checks passed: {count}");
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
        public TaskCompletionSource Applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (request.Operation == PrivilegedOperationKind.ProxyServerApply)
            {
                ApplyCalls++; Hash = ProxyServerPublicationService.Hash(SingBoxConfiguration.Render(request.ProxyServer!.Material!, false)); Applied.TrySetResult();
                if (LoseReply) { LoseReply = false; return Task.FromResult(new PrivilegedOperationResult(false, Error: "proxy_server.helper_unavailable")); }
            }
            else if (request.Operation != PrivilegedOperationKind.ProxyServerStatus) throw new Exception("Unexpected Core Helper action");
            var status = new ProxyServerStatus(true, true, true, false, "test", Hash, true, null);
            return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status))));
        }
    }
}
