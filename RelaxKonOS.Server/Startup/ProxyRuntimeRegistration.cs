using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Startup;

/// <summary>
/// The proxy runtime and publication backbone. It is registered by the Host (not by
/// <c>relaxkonos.proxy</c>) because Core domains outside the proxy module drive it directly:
/// the installation ledger installs and publishes the mihomo runtime, task orchestration recovers
/// and reconciles sing-box, the component center and runtime catalog report both runtimes, and the
/// Docker proxy resolver reads the saved proxy settings to derive the managed outbound proxy.
///
/// Management facades/routes belong to <see cref="Modules.ProxyModule"/>. Core retains the one
/// durable safety owner for system-proxy reconciliation, TUN startup restoration, grant expiry
/// and publication recovery. No management engine registry or subscription parser is registered here.
/// </summary>
internal static class ProxyRuntimeRegistration
{
    public static void AddProxyRuntimeServices(this WebApplicationBuilder builder)
    {
        // Host paths for engine state. The installation domain's managed-component cleanup resolves
        // this while the proxy module may be disabled.
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyPlatformPaths, RelaxKonOS.Server.Proxy.Platform.ProxyPlatformPaths>();

        // ---------------------------------------------------------------- mihomo runtime transport
        // A Server-only, loopback-only controller adapter. The runtime manager below is the process
        // and release owner; it is what the installation ledger and the component center call.
        var mihomoController = builder.Configuration.GetSection("Proxy:Mihomo:Controller").Get<RelaxKonOS.Server.Proxy.Mihomo.MihomoControllerOptions>()
            ?? new RelaxKonOS.Server.Proxy.Mihomo.MihomoControllerOptions();
        mihomoController.Validate();
        builder.Services.AddSingleton(mihomoController);
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.IProxyControllerSecretStore, RelaxKonOS.Server.Proxy.Mihomo.DataProtectionProxyControllerSecretStore>();
        // The controller is an optional local process. Its expected "not started" condition is
        // reported by MihomoControllerClient as one actionable warning, instead of HttpClient's
        // full connection exception stack on every status poll.
        builder.Services.AddHttpClient<RelaxKonOS.Server.Proxy.Mihomo.IMihomoControllerClient, RelaxKonOS.Server.Proxy.Mihomo.MihomoControllerClient>()
            .RemoveAllLoggers();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Platform.IProxyPrivilegedOperations, RelaxKonOS.Server.Proxy.Platform.NativeMihomoPrivilegedOperations>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyDiagnosticLogStore, RelaxKonOS.Server.Proxy.ProxyDiagnosticLogStore>();
        builder.Services.AddSingleton<RelaxKonOS.Protocol.Proxy.MihomoRuntimeManifest>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.IMihomoRuntimeProbe, RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeProbe>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyRuntimeManager>(services => services.GetRequiredService<RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager>());
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.IMihomoConfigurationValidator>(services => services.GetRequiredService<RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager>());

        // ------------------------------------------------------- mihomo publication (durable ledger)
        // The installation ledger publishes configuration through this chain, so it is not a UI
        // concern. Its recovery worker must survive a disabled proxy module: an interrupted
        // publication is an unresolved authority, not an optional feature.
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeAuthorizationProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoSettingsPublicationFiles>();
        builder.Services.AddTransient<RelaxKonOS.Server.Proxy.Mihomo.MihomoConfigurationPublicationService>();
        builder.Services.AddTransient<RelaxKonOS.Server.Proxy.Mihomo.IMihomoConfigurationPublicationRecovery>(services =>
            services.GetRequiredService<RelaxKonOS.Server.Proxy.Mihomo.MihomoConfigurationPublicationService>());
        builder.Services.AddTransient<RelaxKonOS.Server.Proxy.Mihomo.MihomoInstallationPublicationService>();
        builder.Services.AddTransient<RelaxKonOS.Server.Proxy.Mihomo.MihomoInstallationBootstrapService>();
        builder.Services.AddTransient<RelaxKonOS.Server.Proxy.Mihomo.MihomoRemovalPublicationService>();
        builder.Services.AddTransient<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationRecoveryService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationRecoveryScheduler>();

        // ------------------------------------------------------------------ settings and system proxy
        // Shared by two Core consumers: the installation bootstrap selects material from these
        // settings, and DockerProxyResolver derives the managed outbound proxy endpoint from them.
        // Only a pending listener switch needs the guard's scoped controller observation.
        // Mutation, TUN and reconciliation still serialize through the same publication store gate.
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Platform.IHostSystemProxyService, RelaxKonOS.Server.Proxy.Platform.HostSystemProxyService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxySettingsReader, RelaxKonOS.Server.Proxy.ProxySettingsReader>();
        builder.Services.AddTransient<Func<RelaxKonOS.Server.Proxy.Mihomo.IMihomoControllerClient>>(services =>
            () => services.GetRequiredService<RelaxKonOS.Server.Proxy.Mihomo.IMihomoControllerClient>());
        builder.Services.AddTransient<RelaxKonOS.Server.Proxy.IProxySystemProxyReconciler, RelaxKonOS.Server.Proxy.Mihomo.MihomoSystemProxyReconciler>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Platform.IProxyTunDeviceObserver, RelaxKonOS.Server.Proxy.Platform.HostProxyTunDeviceObserver>();
        builder.Services.AddScoped<RelaxKonOS.Server.Proxy.Mihomo.MihomoTunRuntimeController>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyTunRuntimeController, RelaxKonOS.Server.Proxy.ScopedProxyTunRuntimeController>();

        // Durable safety promises survive disabling the management surface. Exactly one owner.
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Platform.IProxyNetworkSafetyPlatform, RelaxKonOS.Server.Proxy.Platform.HostProxyNetworkSafetyPlatform>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyTunSafetyService, RelaxKonOS.Server.Proxy.ProxyTunSafetyService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyRecoveryService>(services => services.GetRequiredService<RelaxKonOS.Server.Proxy.IProxyTunSafetyService>());
        builder.Services.AddHostedService<RelaxKonOS.Server.Proxy.ProxyRecoveryHostedService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.Proxy.Mihomo.SystemProxyGuardHostedService>();

        // Managed runtime lifecycle: the component center and the installation domain install and
        // switch mihomo/sing-box releases through these services.
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.IMihomoRuntimeReceiptReader>(services =>
            services.GetRequiredService<RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeService>());
        builder.Services.AddTransient<RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeRecoveryService>();
        builder.Services.AddTransient<RelaxKonOS.Server.Proxy.Mihomo.MihomoDirectSettingsCompensationService>();

        // --------------------------------------------------------------------- sing-box (proxy server)
        // Task orchestration's sing-box observer, reconciliation and recovery domains, plus the
        // component adapter and runtime catalog, resolve these. The store holds the inbound
        // configuration the runtime authorization is derived from.
        builder.Services.AddSingleton<RelaxKonOS.Server.ProxyServer.ProxyServerStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ProxyServer.ProxyServerRuntimeAuthorizationProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ProxyServer.IProxyServerCertificateSource, RelaxKonOS.Server.ProxyServer.ProxyServerCertificateSource>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.SingBoxRuntimeService>();
        builder.Services.AddTransient<RelaxKonOS.Server.ProxyServer.IProxyServerExpiryExecutor, RelaxKonOS.Server.ProxyServer.ProxyServerPublicationService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.ProxyServer.ProxyServerExpiryService>();
    }
}
