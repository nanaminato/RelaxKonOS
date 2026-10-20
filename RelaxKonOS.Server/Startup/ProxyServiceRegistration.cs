using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Startup;

internal static class ProxyServiceRegistration
{
    public static void AddProxyServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<RelaxKonOS.Server.ProxyServer.ProxyServerStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ProxyServer.ProxyServerRuntimeAuthorizationProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.SingBoxRuntimeService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ProxyServer.IProxyServerCertificateSource, RelaxKonOS.Server.ProxyServer.ProxyServerCertificateSource>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ProxyServer.ProxyServerService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.ProxyServer.ProxyServerExpiryService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.GatewayProxy.SquidConfigurationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.GatewayProxy.SquidGatewayService>();
        // Proxy Goal 2: a Server-only, loopback-only controller adapter. There is deliberately no
        // endpoint mapping or client registration until Goal 6, and no service/process management until Goal 3.
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
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyEngine, RelaxKonOS.Server.Proxy.Mihomo.MihomoEngine>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyEngineRegistry, RelaxKonOS.Server.Proxy.ProxyEngineRegistry>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Platform.IProxyPrivilegedOperations, RelaxKonOS.Server.Proxy.Platform.NativeMihomoPrivilegedOperations>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyPlatformPaths, RelaxKonOS.Server.Proxy.Platform.ProxyPlatformPaths>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeAuthorizationProvider>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeRecoveryService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoSettingsPublicationFiles>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoConfigurationPublicationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoInstallationPublicationService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoInstallationBootstrapService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoRemovalPublicationService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.Proxy.Mihomo.MihomoPublicationRecoveryService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyPlatformService, RelaxKonOS.Server.Proxy.Platform.ProxyPlatformService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Platform.IHostSystemProxyService, RelaxKonOS.Server.Proxy.Platform.HostSystemProxyService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyDiagnosticLogStore, RelaxKonOS.Server.Proxy.ProxyDiagnosticLogStore>();
        builder.Services.AddSingleton<RelaxKonOS.Protocol.Proxy.MihomoRuntimeManifest>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.IMihomoRuntimeProbe, RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeProbe>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyRuntimeManager>(sp => sp.GetRequiredService<RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager>());
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Mihomo.IMihomoConfigurationValidator>(sp => sp.GetRequiredService<RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager>());
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxySettingsService, RelaxKonOS.Server.Proxy.Mihomo.MihomoSettingsService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyTunRuntimeController>(sp => (RelaxKonOS.Server.Proxy.Mihomo.MihomoSettingsService)sp.GetRequiredService<RelaxKonOS.Server.Proxy.IProxySettingsService>());
        builder.Services.AddHostedService<RelaxKonOS.Server.Proxy.Mihomo.SystemProxyGuardHostedService>();
        builder.Services.AddHttpClient("ProxySubscriptionDirect", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = RelaxKonOS.Server.Proxy.ProxySubscriptionNetworkPolicy.ConnectAsync,
            });
        builder.Services.AddHttpClient("ProxySubscriptionDirectInsecureTls", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = RelaxKonOS.Server.Proxy.ProxySubscriptionNetworkPolicy.ConnectAsync,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
            });
        builder.Services.AddHttpClient("ProxySubscriptionSystemProxy", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                // Keep the handler and the connection policy on the exact same IWebProxy instance.
                // HttpClient's proxy source is deliberately used here; WebRequest.DefaultWebProxy is
                // a different (and obsolete) proxy pipeline which can choose a different endpoint.
                var systemProxy = HttpClient.DefaultProxy;
                return new SocketsHttpHandler
                {
                    UseProxy = true,
                    Proxy = systemProxy,
                    AllowAutoRedirect = false,
                    ConnectCallback = (context, cancellationToken) =>
                        RelaxKonOS.Server.Proxy.ProxySubscriptionNetworkPolicy.ConnectUsingSystemProxyAsync(systemProxy, context, cancellationToken),
                };
            });
        builder.Services.AddHttpClient("ProxySubscriptionSystemProxyInsecureTls", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var systemProxy = HttpClient.DefaultProxy;
                return new SocketsHttpHandler
                {
                    UseProxy = true,
                    Proxy = systemProxy,
                    AllowAutoRedirect = false,
                    ConnectCallback = (context, cancellationToken) =>
                        RelaxKonOS.Server.Proxy.ProxySubscriptionNetworkPolicy.ConnectUsingSystemProxyAsync(systemProxy, context, cancellationToken),
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    },
                };
            });
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxySubscriptionDownloader, RelaxKonOS.Server.Proxy.ProxySubscriptionDownloader>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.Platform.IProxyNetworkSafetyPlatform, RelaxKonOS.Server.Proxy.Platform.HostProxyNetworkSafetyPlatform>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyTunSafetyService, RelaxKonOS.Server.Proxy.ProxyTunSafetyService>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyRecoveryService>(sp => sp.GetRequiredService<RelaxKonOS.Server.Proxy.IProxyTunSafetyService>());
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.ProxyOperationStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.ProxyAuditStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyLifecycleService, RelaxKonOS.Server.Proxy.ProxyLifecycleService>();
        builder.Services.AddHostedService<RelaxKonOS.Server.Proxy.ProxyRecoveryHostedService>();
    }
}
