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
/// <item><c>Docker.DockerProxyResolver</c> reads <c>IProxySettingsReader</c> to derive the managed
/// outbound proxy, and the mihomo installation bootstrap reads the same saved intent,
/// so the lightweight reader remains in Core while the settings writer can be migrated separately.</item>
/// </list>
/// This module owns management surfaces and scoped execution facades. Core owns TUN recovery,
/// system-proxy reconciliation, grant expiry and durable publication recovery across module disable.
/// The aggregate remains resident; scoped object release is not collectible code or module sleep.
/// </summary>
internal sealed class ProxyModule : IServerModule, IServerModuleDeactivationGuard, IServerModuleWorkResidency
{
    public string Id => ServerModules.ProxyId;
    public RelaxKonOS.Server.Residency.IServerModuleRuntimeFactory RuntimeFactory { get; } = new ProxyWorkRuntimeFactory();

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
        // Management engine graphs are owned by request or independent operation scopes.
        builder.Services.AddScoped<IProxyEngine, MihomoEngine>();
        builder.Services.AddScoped<IProxyEngineRegistry, ProxyEngineRegistry>();
        builder.Services.AddSingleton<IProxyPlatformService, ProxyPlatformService>();

        // Operation and audit ledgers are the durable record of what the operator asked for; they
        // outlive the HTTP request that started it.
        builder.Services.AddSingleton<IProxyWorkLeaseSource, ProxyWorkLeaseSource>();
        builder.Services.AddSingleton<ProxyOperationStore>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ProxyOperationStore>());
        builder.Services.AddSingleton<ProxySubscriptionOperations>();
        builder.Services.AddSingleton<ProxyLifecycleOperations>();
        builder.Services.AddSingleton<ProxyAuditStore>();
        builder.Services.AddScoped<IProxyLifecycleService, ProxyLifecycleService>();

        // Core provides the one shared TUN safety owner to both routes and engine recovery hooks.

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

        // Core owns system-proxy/TUN recovery and expiry independently of this management surface.

        // Proxy server operations (sing-box inbound). The runtime, its store and its authorization
        // provider stay in Core because task orchestration and installation drive them directly.
        // Operations retain their own instance until completion; the expiry timer resolves a
        // fresh executor per pass. The singleton store remains the sole serialization gate.
        builder.Services.AddTransient<ProxyServerService>();
        builder.Services.AddSingleton<ProxyServerOperationQueue>();

        // Squid gateway: configuration plus lifecycle through the privileged Helper.
        builder.Services.AddSingleton<SquidConfigurationStore>();
        builder.Services.AddTransient<SquidGatewayService>();

        // Profile/subscription/configuration services. Their repositories are registered with the
        // storage provider (SQLite only today), so a memory-storage host keeps exactly the state it
        // had before this module existed: no service, instead of a service whose repository is
        // missing. The repositories themselves must stay in Core — Core's mihomo publication reads
        // IProxyProfileRepository and fails closed when it is absent.
        if (string.Equals(context.StorageProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddTransient<IProxyProfileService, ProxyProfileService>();
            builder.Services.AddScoped<IProxySubscriptionService, ProxySubscriptionService>();
            builder.Services.AddScoped<IProxyConfigurationTransactionService, ProxyConfigurationTransactionService>();
            builder.Services.AddScoped<IProxyConfigurationService, ProxyConfigurationService>();
        }
    }

    /// <summary>
    /// A runtime publication that was begun but never confirmed is an unresolved authority: the
    /// Core recovery owner (see Startup/ProxyRuntimeRegistration.cs) can confirm or roll it back.
    /// Deactivation remains blocked while authority is unknown; management disablement never
    /// removes the Core safety, expiry or publication recovery owners.
    /// </summary>
    public async ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken)
    {
        if (services.GetService<RelaxKonOS.Server.Residency.ModuleResidencyCoordinator>()?.HasBackgroundWork(Id) == true)
            return [new(Id, "Proxy execution, cleanup or response remains owned in this process.")];
        var operations = services.GetService<ProxyOperationStore>();
        var facts = operations is null
            ? await ProxyOperationStore.InspectPersistedResponsibilitiesAsync(services.GetRequiredService<IProxyPlatformPaths>(), cancellationToken)
            : await operations.InspectResponsibilitiesAsync(cancellationToken);
        var publication = await InspectPublicationAsync(services, cancellationToken);
        var safety = await ProxySafetyResponsibilities.InspectAsync(services, services.GetRequiredService<IProxyPlatformPaths>(), cancellationToken);
        return [.. facts.Concat(safety).Select(fact => new ServerModuleOrphanedWork(Id, fact)), .. publication];
    }

    private async Task<IReadOnlyList<ServerModuleOrphanedWork>> InspectPublicationAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<MihomoPublicationStore>();
        try
        {
            var state = await store.ReadAsync(cancellationToken);
            return state.Pending is { } pending
                ?
                [
                    new(Id, $"the mihomo runtime publication for operation {pending.OperationId} is unresolved; "
                        + "Core retains recovery ownership; reconcile the durable publication before deactivation."),
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
