using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.HostMode;
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
        builder.Services.AddScoped<IMihomoConfigurationValidator, MihomoConfigurationValidator>();
        builder.Services.AddScoped<IProxyEngineRegistry, ProxyEngineRegistry>();
        builder.Services.AddScoped<IProxyPlatformService, ProxyPlatformService>();
        builder.Services.AddScoped<MihomoExternalVersionProbe>();
        builder.Services.AddScoped<IProxySettingsWriter, MihomoSettingsService>();
        builder.Services.AddScoped<IMihomoApplicationConfigurationPublisher>(services =>
            services.GetRequiredService<MihomoConfigurationPublicationService>());

        // Operation and audit ledgers are the durable record of what the operator asked for; they
        // outlive the HTTP request that started it.
        builder.Services.AddSingleton<IProxyWorkLeaseSource, ProxyWorkLeaseSource>();
        builder.Services.AddSingleton<ProxyOperationStore>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ProxyOperationStore>());
        builder.Services.AddScoped<ProxySubscriptionOperations>();
        builder.Services.AddScoped<ProxyLifecycleOperations>();
        builder.Services.AddScoped<MihomoManagedTunOperations>();
        builder.Services.AddScoped<MihomoNamedTunStartRecovery>();
        builder.Services.AddSingleton<ProxyAuditStore>();
        builder.Services.AddScoped<IProxyLifecycleService, ProxyLifecycleService>();
        ProxyReadSampler<MihomoRuntimeObservation>.Register(builder.Services, static (services, _, ct) =>
            services.GetRequiredService<MihomoRuntimeStatusReader>().GetObservedAsync(
                token => services.GetRequiredService<IMihomoControllerClient>().IsReachableAsync(token), ct));
        ProxyOverviewService.Register(builder.Services);
        ProxyReadSampler<ProxyTrafficDto>.Register(builder.Services,
            static (services, _, ct) => services.GetRequiredService<IMihomoControllerClient>().GetTrafficAsync(ct));
        ProxyReadSampler<ControllerResult<IReadOnlyList<ProxyConnectionDto>>>.Register(builder.Services,
            static (services, _, ct) => services.GetRequiredService<IMihomoControllerClient>().GetConnectionsAsync(ct));

        // Core provides the one shared TUN safety owner to both routes and engine recovery hooks.

        // The operation scope owns its HTTP connection pools as well as the downloader.
        builder.Services.AddScoped<ProxySubscriptionHttpClientFactory>();
        builder.Services.AddScoped<IProxySubscriptionDownloader>(services => new ProxySubscriptionDownloader(
            services.GetRequiredService<ProxySubscriptionHttpClientFactory>(),
            services.GetRequiredService<IProxySettingsReader>(), services.GetService<ILogger<ProxySubscriptionDownloader>>()));

        // Core owns system-proxy/TUN recovery and expiry independently of this management surface.

        // Proxy server operations (sing-box inbound). The runtime, its store and its authorization
        // provider stay in Core because task orchestration and installation drive them directly.
        // Operations retain their own instance until completion; the expiry timer resolves a
        // fresh executor per pass. The singleton store remains the sole serialization gate.
        builder.Services.AddTransient<ProxyServerService>();
        builder.Services.AddScoped<ProxyServerOperationQueue>();

        // Squid gateway: configuration plus lifecycle through the privileged Helper.
        builder.Services.AddSingleton<SquidConfigurationStore>();
        builder.Services.AddScoped<SquidGatewayService>();

        // Profile/subscription/configuration services. Their repositories are registered with the
        // storage provider (SQLite only today), so a memory-storage host keeps exactly the state it
        // had before this module existed: no service, instead of a service whose repository is
        // missing. The repositories themselves must stay in Core — Core's mihomo publication reads
        // IProxyProfileRepository and fails closed when it is absent.
        if (string.Equals(context.StorageProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddTransient<IProxyProfileService, ProxyProfileService>();
            builder.Services.AddScoped<IProxySubscriptionService, ProxySubscriptionService>();
            builder.Services.AddScoped<IProxySubscriptionActions, ProxySubscriptionActions>();
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
    public ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken) =>
        InspectResponsibilitiesAsync(services, confirmPublication: false, cancellationToken);

    public ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectDisabledStartupAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken) =>
        InspectResponsibilitiesAsync(services, confirmPublication: true, cancellationToken);

    private async ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectResponsibilitiesAsync(
        IServiceProvider services, bool confirmPublication, CancellationToken cancellationToken)
    {
        if (services.GetService<RelaxKonOS.Server.Residency.ModuleResidencyCoordinator>()?.HasBackgroundWork(Id) == true)
            return [new(Id, "Proxy execution, cleanup or response remains owned in this process.")];
        var operations = services.GetService<ProxyOperationStore>();
        var facts = operations is null
            ? await ProxyOperationStore.InspectPersistedResponsibilitiesAsync(services.GetRequiredService<IProxyPlatformPaths>(), cancellationToken)
            : await operations.InspectResponsibilitiesAsync(cancellationToken);
        var publication = await InspectPublicationAsync(services, confirmPublication, cancellationToken);
        var paths = services.GetRequiredService<IProxyPlatformPaths>();
        var safety = await ProxySafetyResponsibilities.InspectAsync(services, paths, confirmPublication, cancellationToken);
        var probe = MihomoExternalVersionProbe.InspectResponsibility(paths);
        if (probe is not null) return [.. facts.Concat(safety).Select(fact => new ServerModuleOrphanedWork(Id, fact)), .. publication, new(Id, probe)];
        return [.. facts.Concat(safety).Select(fact => new ServerModuleOrphanedWork(Id, fact)), .. publication];
    }

    private async Task<IReadOnlyList<ServerModuleOrphanedWork>> InspectPublicationAsync(IServiceProvider services,
        bool recoverConfiguration, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<MihomoPublicationStore>();
        try
        {
            var state = await store.ReadAsync(cancellationToken);
            if (!recoverConfiguration && state.DirectReload is { } direct)
                // Core must still start its disk reconciliation, guard and TUN safety workers.
                return [new(Id, $"Mihomo direct reload {direct.OperationId} requires verification before deactivation.")];
            // A disabled-startup admission check cannot start a pending managed network transaction.
            if (recoverConfiguration && state.Pending is { ConfigurationPlan: not null, TunIntent: null } accepted)
            {
                try
                {
                    await using var scope = services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IMihomoConfigurationPublicationRecovery>()
                        .RecoverAsync(accepted.OperationId, cancellationToken);
                }
                catch (Exception error) when (error is RelaxKonOS.ManagedRuntimes.RuntimePolicyException
                    or RelaxKonOS.Server.ManagedRuntimes.RuntimeLifecycleException or IOException or UnauthorizedAccessException)
                {
                    return [new(Id, $"the mihomo runtime publication for operation {accepted.OperationId} could not be reconciled ({error.Message.TrimEnd('.')}).")];
                }
                state = await store.ReadAsync(cancellationToken);
            }
            if (state.Pending is { } pending)
                return [
                    new(Id, $"the mihomo runtime publication for operation {pending.OperationId} is unresolved; "
                        + "Core retains recovery ownership; reconcile the durable publication before deactivation."),
                ];
            if (state.Published?.NetworkTransitionRequired == true)
                return await InspectManagedTunAsync(services, store, state, cancellationToken);
            if (OperatingSystem.IsLinux() && services.GetService<IServerModeResolver>() is { Mode: ServerMode.System } mode
                && mode.Supports(ServerHostFeature.PrivilegedOperations))
                return await InspectNativeResponsibilityAsync(services, store, state.Revision, cancellationToken);
            return [];
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

    private async Task<IReadOnlyList<ServerModuleOrphanedWork>> InspectNativeResponsibilityAsync(IServiceProvider services,
        MihomoPublicationStore store, long revision, CancellationToken ct)
    {
        var unverified = new ServerModuleOrphanedWork(Id,
            "Mihomo Helper responsibility cannot be verified; retain the original owner.", RecoveryRequired: true);
        if (!await store.Gate.WaitAsync(0, ct)) return [unverified];
        try
        {
            if ((await store.ReadAsync(ct)).Revision != revision
                || services.GetService<IMihomoRuntimeReceiptReader>() is not { } reader) return [unverified];
            var native = await reader.QueryResponsibilityAsync(ct);
            if (native.PendingOperationId is { } pending)
                return [new(Id, $"Managed Mihomo operation {pending:D} is unresolved; original Helper ownership is retained.",
                    RecoveryRequired: true)];
            if (native.TunPublicationId is { } publication)
                return [new(Id, $"Managed Mihomo TUN publication {publication:D} has no matching Server publication; original Helper ownership is retained.",
                    RecoveryRequired: true)];
            return [];
        }
        catch (Exception error) when (error is RuntimeLifecycleException or RelaxKonOS.ManagedRuntimes.RuntimePolicyException
            or IOException or UnauthorizedAccessException)
        { return [unverified]; }
        finally { store.Gate.Release(); }
    }

    private async Task<IReadOnlyList<ServerModuleOrphanedWork>> InspectManagedTunAsync(IServiceProvider services,
        MihomoPublicationStore store, MihomoPublicationState state, CancellationToken ct)
    {
        var publication = state.LastOperationId!.Value;
        var unverified = new ServerModuleOrphanedWork(Id,
            $"Managed Mihomo TUN publication {publication:D} cannot be verified; original Helper ownership is retained.",
            RecoveryRequired: true);
        if (!await store.Gate.WaitAsync(0, ct)) return [unverified];
        try
        {
            if ((await store.ReadAsync(ct)).Revision != state.Revision) return [unverified];
            if (services.GetService<IMihomoRuntimeReceiptReader>() is not { } runtime) return [unverified];
            var observed = await runtime.QueryHelperAsync(ct, publication);
            if (observed.PendingOperationId is { } pending)
                return [new(Id, $"Managed Mihomo TUN operation {pending:D} is unresolved; original Helper ownership is retained.",
                    RecoveryRequired: true)];
            if (observed.Installation.RuntimeId != "mihomo" || observed.Installation.RecoveryRequired || observed.ProblemCode is not null
                || observed.ConfigurationIdentity != state.Identity
                || await new MihomoPublicationFiles(services.GetRequiredService<IProxyPlatformPaths>()).IdentityAsync(ct) != state.Identity)
                return [unverified];
            if (observed.Installation.Ownership == RuntimeOwnership.Managed
                && observed.MihomoTunPublicationId == publication && observed.MihomoTunRunning.HasValue
                && observed.ActiveRevisionId is not null && observed.CommitWitness is { } proof
                && proof.OperationId == publication && proof.ActiveRevisionId == observed.ActiveRevisionId
                && proof.ConfigurationIdentity == state.Identity)
                return [];

            // A keep-data removal retains the descriptor YAML and the original publication ID.
            if (observed.Installation.Ownership == RuntimeOwnership.Absent && state.LastRemoval is { Confirmed: true } removal)
            {
                var removed = await runtime.QueryHelperAsync(ct, removal.OperationId);
                if (MihomoManagedPublicationVerification.MatchesRetainedRemoval(state, removal.OperationId, removed))
                    return [];
            }
            return [unverified];
        }
        catch (Exception error) when (error is RuntimeLifecycleException or RelaxKonOS.ManagedRuntimes.RuntimePolicyException
            or IOException or UnauthorizedAccessException)
        { return [unverified]; }
        finally { store.Gate.Release(); }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapProxyEndpoints();
        endpoints.MapGatewayProxyEndpoints();
        endpoints.MapProxyServerEndpoints();
    }
}
