using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.ProxyServer;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// The operator-facing proxy surfaces: the client engine (Mihomo) manager, subscription/profile
/// management, the TUN safety net, the sing-box proxy server operations and the Squid gateway.
///
/// The audit for this domain found no clean "management vs transport" seam inside a single
/// namespace — it runs the other way. The mihomo/sing-box *runtime and publication* chain stays in
/// Core (<c>Startup/ProxyRuntimeRegistration.cs</c>) because unrelated Core domains drive it:
/// <list type="bullet">
/// <item><c>Installations.MihomoRuntimeInstallationService</c> installs and publishes the mihomo
/// runtime, so the publication store, the bootstrap and the installation/removal publication
/// services are part of the installation ledger, not of this UI.</item>
/// <item><c>TaskOrchestration</c> runs sing-box recovery/reconciliation, and
/// <c>Components.SingBoxComponentAdapter</c> / <c>RuntimeCatalogEndpoints</c> report it, so
/// <c>SingBoxRuntimeService</c> and the proxy-server store it reads stay in Core.</item>
/// <item><c>Docker.DockerProxyResolver</c> reads <c>IProxySettingsService</c> to derive the managed
/// outbound proxy, and the mihomo installation bootstrap writes through the same settings service,
/// so the settings service itself cannot move without silently changing what a Docker build uses.</item>
/// </list>
/// This module therefore owns the parts that only answer HTTP clients: engine adapters, lifecycle,
/// operations/audit ledgers, subscription downloading, TUN safety, the proxy server operations and
/// the Squid gateway. Recovery workers keep running from Core when the module is disabled, which is
/// the ordering this design requires: recovery first, then optional surfaces.
/// </summary>
internal sealed class ProxyModule : IServerModule, IServerModuleDeactivationGuard
{
    public string Id => ServerModules.ProxyId;

    // One module, three applications: the client engine, the Squid gateway and the sing-box proxy
    // server share the same engine/platform plumbing, and the host gates all three on the same
    // deployment feature (privileged operations).
    public IReadOnlyList<string> Capabilities =>
    [
        ServerCapabilities.Proxy,
        ServerCapabilities.GatewayProxy,
        ServerCapabilities.ProxyServer,
    ];

    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        // Engine adapters. The registry is what lets the proxy server and the lifecycle service ask
        // "which engine speaks this protocol" without naming Mihomo.
        builder.Services.AddSingleton<IProxyEngine, MihomoEngine>();
        builder.Services.AddSingleton<IProxyEngineRegistry, ProxyEngineRegistry>();
        builder.Services.AddSingleton<IProxyPlatformService, ProxyPlatformService>();

        // Operation and audit ledgers are the durable record of what the operator asked for; they
        // outlive the HTTP request that started it.
        builder.Services.AddSingleton<ProxyOperationStore>();
        builder.Services.AddSingleton<ProxyAuditStore>();
        builder.Services.AddSingleton<IProxyLifecycleService, ProxyLifecycleService>();
        builder.Services.AddHostedService<ProxyRecoveryHostedService>();

        // TUN safety: one service backs both the routes and the engine's recovery hook, so enabling
        // TUN cannot be confirmed by a code path that the emergency exit does not know about.
        builder.Services.AddSingleton<IProxyNetworkSafetyPlatform, HostProxyNetworkSafetyPlatform>();
        builder.Services.AddSingleton<IProxyTunSafetyService, ProxyTunSafetyService>();
        builder.Services.AddSingleton<IProxyRecoveryService>(services => services.GetRequiredService<IProxyTunSafetyService>());

        // Subscriptions: four clients, because "direct" vs "through the saved system proxy" and
        // "verify TLS" vs "accept the mirror certificate" are independent operator choices.
        builder.Services.AddHttpClient("ProxySubscriptionDirect", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = ProxySubscriptionNetworkPolicy.ConnectAsync,
            });
        builder.Services.AddHttpClient("ProxySubscriptionDirectInsecureTls", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = ProxySubscriptionNetworkPolicy.ConnectAsync,
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
                        ProxySubscriptionNetworkPolicy.ConnectUsingSystemProxyAsync(systemProxy, context, cancellationToken),
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
                        ProxySubscriptionNetworkPolicy.ConnectUsingSystemProxyAsync(systemProxy, context, cancellationToken),
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    },
                };
            });
        builder.Services.AddSingleton<IProxySubscriptionDownloader, ProxySubscriptionDownloader>();

        // The guard re-asserts the system proxy the operator asked for; it is a UI-owned promise,
        // not a recovery duty, so it stops with the module.
        builder.Services.AddHostedService<SystemProxyGuardHostedService>();

        // Proxy server operations (sing-box inbound). The runtime, its store and its authorization
        // provider stay in Core because task orchestration and installation drive them directly.
        builder.Services.AddSingleton<ProxyServerService>();
        builder.Services.AddHostedService<ProxyServerExpiryService>();

        // Squid gateway: configuration plus lifecycle through the privileged Helper.
        builder.Services.AddSingleton<SquidConfigurationStore>();
        builder.Services.AddSingleton<SquidGatewayService>();

        // Profile/subscription/configuration services. Their repositories are registered with the
        // storage provider (SQLite only today), so a memory-storage host keeps exactly the state it
        // had before this module existed: no service, instead of a service whose repository is
        // missing. The repositories themselves must stay in Core — Core's mihomo publication reads
        // IProxyProfileRepository and fails closed when it is absent.
        if (string.Equals(context.StorageProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<IProxyProfileService, ProxyProfileService>();
            builder.Services.AddScoped<IProxySubscriptionService, ProxySubscriptionService>();
            builder.Services.AddScoped<IProxyConfigurationTransactionService, ProxyConfigurationTransactionService>();
            builder.Services.AddScoped<IProxyConfigurationService, ProxyConfigurationService>();
        }
    }

    /// <summary>
    /// A runtime publication that was begun but never confirmed is an unresolved authority: the
    /// recovery worker (Core-owned, see Startup/ProxyRuntimeRegistration.cs) is the only thing that can
    /// confirm or roll it back. Refusing to start with the module disabled is the difference between
    /// "the proxy UI is off" and "we silently lost track of which runtime is authorized".
    /// </summary>
    public async ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<MihomoPublicationStore>();
        try
        {
            var state = await store.ReadAsync(cancellationToken);
            return state.Pending is { } pending
                ?
                [
                    new(Id, $"the mihomo runtime publication for operation {pending.OperationId} is unresolved; "
                        + "re-enable the module so the publication recovery worker can confirm or roll it back."),
                ]
                : [];
        }
        catch (Exception exception) when (exception is RelaxKonOS.ManagedRuntimes.RuntimePolicyException
                                              or IOException or UnauthorizedAccessException)
        {
            // An unreadable authority is unknown, and unknown blocks deactivation: assuming "nothing
            // pending" here would be the same silent loss this gate exists to prevent.
            return
            [
                new(Id, $"the mihomo publication authority cannot be read ({exception.Message.TrimEnd('.')}); "
                    + "an unknown authority is not the same as no work to do."),
            ];
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapProxyEndpoints();
        endpoints.MapGatewayProxyEndpoints();
        endpoints.MapProxyServerEndpoints();
    }
}
