using RelaxKonOS.Server.Startup;
using System.Security.Claims;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Proxy API: all controller interaction remains Server-side and dangerous work is queued in the host ledger.</summary>
public static class ProxyEndpoints
{
    public static IEndpointRouteBuilder MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapProxyObservationEndpoints();
        app.MapGet(ProxyApiRoutes.Settings, (IProxySettingsReader settings, CancellationToken ct) => settings.GetAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapProxySettingsUpdateEndpoint();
        app.MapPost(ProxyApiRoutes.RuntimeExternalDetection, (ProxyRuntimeRequest request, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.ExternalPath))
                    return Problem(ProxyProblemCodes.ExternalRuntimeInvalid, StatusCodes.Status400BadRequest);
                var result = await services.GetRequiredService<MihomoExternalVersionProbe>().DetectAsync(request.EngineId, request.ExternalPath, ct);
                return result.ProblemCode == ProxyProblemCodes.RecoveryRequired
                    ? Problem(result.ProblemCode, StatusCodes.Status503ServiceUnavailable) : Results.Ok(result);
            }))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.RuntimeReleases, (RelaxKonOS.Protocol.Proxy.MihomoRuntimeManifest manifest) => manifest.Available())
            .RequireAuthorization("ProxyRead").WithTags("Proxy");

        app.MapPost(ProxyApiRoutes.Lifecycle, (string action, HttpContext context, ProxyLifecycleOperations operations, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ProxyLifecycleAction>(action, true, out var parsed) || !Enum.IsDefined(parsed))
                return Task.FromResult<IResult>(Problem(ProxyProblemCodes.NotSupported, StatusCodes.Status400BadRequest));
            return QueueValueAsync(context, (key, actor, token) => operations.EnqueueLifecycleAsync(key, parsed, actor, token), ct);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");

        app.MapTunOperationEndpoints();
        app.MapGet(ProxyApiRoutes.Recovery, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, static async (services, ct) =>
                Results.Ok(await services.GetRequiredService<IProxyRecoveryService>().GetStatusAsync(ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyRecoveryStatusDto>();

        app.MapGet(ProxyApiRoutes.Profiles, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, static async (services, ct) =>
                Results.Ok(await services.GetRequiredService<IProxyProfileService>().ListAsync(ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Subscriptions, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, static async (services, ct) =>
                Results.Ok(await services.GetRequiredService<IProxySubscriptionService>().ListAsync(ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.SubscriptionDownloadOptions, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, static async (services, ct) =>
                Results.Ok(await services.GetRequiredService<IProxySubscriptionService>().GetDownloadOptionsAsync(ct))))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.Subscriptions, (ImportProxySubscriptionRequest request, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var service = services.GetRequiredService<IProxySubscriptionService>();
                var audit = services.GetRequiredService<ProxyAuditStore>();
                var actor = Actor(context.User);
                try
                {
                    var subscription = await service.ImportAsync(request, ct);
                    await audit.RecordAsync(actor, "subscription.import", "succeeded", null, ct);
                    return Results.Created(ProxyApiRoutes.Subscriptions + "/" + subscription.Id, subscription);
                }
                catch (ProxySubscriptionException error)
                {
                    await audit.RecordAsync(actor, "subscription.import", "failed", error.ProblemCode, ct);
                    return Problem(error.ProblemCode, StatusCodes.Status400BadRequest);
                }
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapGet(Route(ProxyApiRoutes.SubscriptionContentPattern), (Guid subscriptionId, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var service = services.GetRequiredService<IProxySubscriptionService>();
                var audit = services.GetRequiredService<ProxyAuditStore>();
                var actor = Actor(context.User);
                try
                {
                    var content = await service.GetContentAsync(subscriptionId, ct);
                    await audit.RecordAsync(actor, "subscription.content.read", content is null ? "failed" : "succeeded",
                        content is null ? ProxyProblemCodes.SubscriptionInvalid : null, ct);
                    return content is null ? Problem(ProxyProblemCodes.SubscriptionInvalid, StatusCodes.Status404NotFound) : Results.Ok(content);
                }
                catch (ProxySubscriptionException error)
                {
                    await audit.RecordAsync(actor, "subscription.content.read", "failed", error.ProblemCode, ct);
                    return Problem(error.ProblemCode, StatusCodes.Status400BadRequest);
                }
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapSubscriptionOperationEndpoints();
        app.MapGet(Route(ProxyApiRoutes.ProfilePattern), (Guid profileId, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
                await services.GetRequiredService<IProxyProfileService>().GetAsync(profileId, ct) is { } profile
                    ? Results.Ok(profile) : Results.NotFound()))
            .RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.Profiles, (UpsertProxyProfileRequest request, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, (services, ct) =>
                ProfileAsync(() => services.GetRequiredService<IProxyProfileRepository>().UpsertAsync(null, request.Name, request.EngineId, null, ct),
                    services.GetRequiredService<ProxyAuditStore>(), context, "profile.create", ct, true)))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPut(Route(ProxyApiRoutes.ProfilePattern), (Guid profileId, UpsertProxyProfileRequest request, HttpContext context,
            ModuleResidencyCoordinator coordinator) => ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, (services, ct) =>
                ProfileAsync(() => services.GetRequiredService<IProxyProfileRepository>().UpsertAsync(profileId, request.Name, request.EngineId,
                    request.ExpectedRevision, ct), services.GetRequiredService<ProxyAuditStore>(), context, "profile.update", ct)))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapDelete(Route(ProxyApiRoutes.ProfilePattern), (Guid profileId, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var deleted = await services.GetRequiredService<IProxyProfileRepository>().DeleteAsync(profileId, ct);
                await services.GetRequiredService<ProxyAuditStore>().RecordAsync(Actor(context.User), "profile.delete",
                    deleted ? "succeeded" : "failed", deleted ? null : ProxyProblemCodes.ConfigApplyFailed, ct);
                return deleted ? Results.NoContent() : Results.Conflict();
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapProxyProfilePublicationEndpoints();

        app.MapPut(Route(ProxyApiRoutes.GroupSelectionPattern), (string groupName, SelectProxyGroupRequest request, HttpContext context,
            ModuleResidencyCoordinator coordinator) => ExecuteControllerMutationAsync(context, coordinator, "group.select",
                (engine, ct) => engine.SelectGroupAsync(groupName, request.Proxy, ct)))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPut(ProxyApiRoutes.Routing, (ProxyRoutingModeDto request, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ExecuteControllerMutationAsync(context, coordinator, "routing.mode.set", (engine, ct) => engine.SetRoutingModeAsync(request.Mode, ct)))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.GroupProxyDelayPattern), (string proxyName, TestProxyDelayRequest request, HttpContext context,
            ModuleResidencyCoordinator coordinator) => ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var engine = services.GetRequiredService<IProxyEngineRegistry>().Find("mihomo")!;
                var delay = await engine.TestProxyDelayAsync(proxyName, request.Url, request.TimeoutMilliseconds, ct);
                await services.GetRequiredService<ProxyAuditStore>().RecordAsync(Actor(context.User), "group.proxy.delay",
                    string.IsNullOrEmpty(delay.ProblemCode) ? "succeeded" : "failed", delay.ProblemCode, ct);
                return string.IsNullOrEmpty(delay.ProblemCode) ? Results.Ok(delay) : Problem(delay.ProblemCode, StatusCodes.Status400BadRequest);
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapDelete(Route(ProxyApiRoutes.ConnectionPattern), (string connectionId, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ExecuteControllerMutationAsync(context, coordinator, "connection.close", (engine, ct) => engine.CloseConnectionAsync(connectionId, ct)))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        return app;
    }

    private static Task ExecuteControllerMutationAsync(HttpContext context, ModuleResidencyCoordinator coordinator, string action,
        Func<IProxyEngine, CancellationToken, Task<string?>> mutate) =>
        ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
        {
            var problem = await mutate(services.GetRequiredService<IProxyEngineRegistry>().Find("mihomo")!, ct);
            await services.GetRequiredService<ProxyAuditStore>().RecordAsync(Actor(context.User), action,
                string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        });

    internal static void MapTunOperationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProxyApiRoutes.Tun, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, static async (services, ct) =>
                Results.Ok(await services.GetRequiredService<IProxyTunSafetyService>().GetStatusAsync(ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyRecoveryStatusDto>();
        app.MapPost(ProxyApiRoutes.TunEnable, (ProxyTunRequest request, HttpContext context, ProxyLifecycleOperations operations, CancellationToken ct) =>
        {
            var address = context.Connection.RemoteIpAddress;
            return QueueValueAsync(context, (key, actor, token) => operations.EnqueueTunAsync(key, ProxyTunOperation.Enable, request.ProfileId, address, actor, token), ct);
        }).RequireAuthorization("ProxyDangerous").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.TunDisable, (HttpContext context, ProxyLifecycleOperations operations, CancellationToken ct) =>
            QueueValueAsync(context, (key, actor, token) => operations.EnqueueTunAsync(key, ProxyTunOperation.Disable, Guid.Empty, null, actor, token), ct))
            .RequireAuthorization("ProxyDangerous").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.TunEmergencyDisable, (HttpContext context, ProxyLifecycleOperations operations, CancellationToken ct) =>
            QueueValueAsync(context, (key, actor, token) => operations.EnqueueTunAsync(key, ProxyTunOperation.EmergencyDisable, Guid.Empty, null, actor, token), ct))
            .RequireAuthorization("ProxyDangerous").WithTags("Proxy");
        app.MapGet(Route(ProxyApiRoutes.OperationsPattern), (Guid operationId, HttpContext context, IResidentModuleWorkLeaseSource leases) =>
            ExecuteScopedAsync(context, leases, async (services, ct) =>
                Results.Json(await services.GetRequiredService<ProxyOperationStore>().GetAsync(operationId, ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyOperationDto>();
    }

    internal static void MapProxySettingsUpdateEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut(ProxyApiRoutes.Settings, (UpdateProxySettingsRequest request, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var problem = await services.GetRequiredService<IProxySettingsWriter>().UpdateAsync(request, ct);
                await services.GetRequiredService<ProxyAuditStore>().RecordAsync(Actor(context.User), "settings.update",
                    string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, CancellationToken.None);
                return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
    }

    internal static void MapProxyProfilePublicationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route(ProxyApiRoutes.ProfileActivatePattern), (Guid profileId, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var problem = await services.GetRequiredService<IProxyConfigurationTransactionService>().ActivateStoredAsync(profileId, ct);
                await services.GetRequiredService<ProxyAuditStore>().RecordAsync(Actor(context.User), "profile.activate",
                    string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, CancellationToken.None);
                return string.IsNullOrEmpty(problem) ? Results.Ok(await services.GetRequiredService<IProxyProfileRepository>().GetAsync(profileId, ct))
                    : Problem(problem, StatusCodes.Status400BadRequest);
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.ProfileConfigurationApplyPattern), (Guid profileId, ApplyProxyConfigurationRequest request,
            HttpContext context, ModuleResidencyCoordinator coordinator) => ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var problem = await services.GetRequiredService<IProxyConfigurationTransactionService>().ApplyAsync(profileId, request.Yaml, ct);
                await services.GetRequiredService<ProxyAuditStore>().RecordAsync(Actor(context.User), "profile.configuration.apply",
                    string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, CancellationToken.None);
                return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
    }

    internal static void MapProxyObservationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProxyApiRoutes.Runtime, (HttpContext context, ModuleResidencyCoordinator coordinator) => ProxyRequestScopeOwner.ExecuteAsync(context, coordinator,
            static async (services, ct) => Results.Ok((await ProxyReadSampler<MihomoRuntimeObservation>.ReadAsync(services, ct)).Runtime)))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyRuntimeDto>();
        app.MapGet(ProxyApiRoutes.Groups, (HttpContext context, ModuleResidencyCoordinator coordinator) => ProxyRequestScopeOwner.ExecuteAsync(context, coordinator,
            static async (services, ct) => Results.Ok(await MihomoProxyGroupOrder.ReadGroupsAsync(
                services.GetRequiredService<IMihomoControllerClient>(), services.GetRequiredService<IProxyPlatformPaths>(), ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyGroupDto[]>();
        app.MapGet(ProxyApiRoutes.ControllerConfiguration, (HttpContext context, ModuleResidencyCoordinator coordinator) => ProxyRequestScopeOwner.ExecuteAsync(context, coordinator,
            static async (services, ct) => Results.Ok(await services.GetRequiredService<IMihomoControllerClient>().GetControllerConfigurationAsync(ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyControllerConfigurationDto>();
        app.MapGet(ProxyApiRoutes.Overview, (HttpContext context, ModuleResidencyCoordinator coordinator) => ProxyRequestScopeOwner.ExecuteAsync(context, coordinator,
            static async (services, ct) => Results.Ok(await ProxyReadSampler<ProxyOverviewDto>.ReadAsync(services, ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyOverviewDto>();
        app.MapGet(ProxyApiRoutes.Connections, (HttpContext context, ModuleResidencyCoordinator coordinator) => ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, static async (services, ct) =>
        {
            var result = await ProxyReadSampler<ControllerResult<IReadOnlyList<ProxyConnectionDto>>>.ReadAsync(services, ct);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(result.ProblemCode, StatusCodes.Status503ServiceUnavailable);
        })).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Traffic, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, static async (services, ct) =>
                Results.Ok(await ProxyReadSampler<ProxyTrafficDto>.ReadAsync(services, ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyTrafficDto>();
        app.MapGet(ProxyApiRoutes.Logs, (int? limit, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
                Results.Ok(await MihomoLogReader.ReadAsync(services.GetRequiredService<IMihomoControllerClient>(),
                    services.GetRequiredService<IProxyDiagnosticLogStore>(), limit ?? 100, ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyLogEntryDto[]>();
    }

    private static Task ExecuteScopedAsync(HttpContext context, IResidentModuleWorkLeaseSource leases,
        Func<IServiceProvider, CancellationToken, Task<IResult>> observe) =>
        ResidentScopedResponse.ExecuteAsync(context, leases, ServerModules.ProxyId, observe);

    private static async Task<IResult> QueueValueAsync(HttpContext context,
        Func<string, string, CancellationToken, Task<ProxyOperationDto>> enqueue, CancellationToken ct)
    {
        var actor = Actor(context.User);
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var item = await enqueue(key, actor, ct);
            return new ResidentOperationResult(ServerModules.ProxyId, Results.Accepted(ProxyApiRoutes.Proxy + "/operations/" + item.OperationId, new ProxyOperationAcceptedDto(item.OperationId)));
        }
        catch (ProxyOperationValidationException error)
        {
            return Problem(error.ProblemCode, error.ProblemCode == ProxyProblemCodes.IdempotencyConflict
                ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
        }
    }

    internal static void MapSubscriptionOperationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ProxyApiRoutes.SubscriptionsRefresh, (HttpContext context, ProxySubscriptionOperations operations, CancellationToken ct) =>
            QueueSubscriptionAsync(context, operations, ProxySubscriptionOperation.RefreshAll, Guid.Empty, ct))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.SubscriptionRefreshPattern), (Guid subscriptionId, HttpContext context, ProxySubscriptionOperations operations, CancellationToken ct) =>
            QueueSubscriptionAsync(context, operations, ProxySubscriptionOperation.Refresh, subscriptionId, ct))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.SubscriptionActivatePattern), (Guid subscriptionId, HttpContext context, ProxySubscriptionOperations operations, CancellationToken ct) =>
            QueueSubscriptionAsync(context, operations, ProxySubscriptionOperation.Activate, subscriptionId, ct))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");
    }

    private static async Task<IResult> QueueSubscriptionAsync(HttpContext context, ProxySubscriptionOperations operations,
        ProxySubscriptionOperation operation, Guid id, CancellationToken ct)
    {
        var actor = Actor(context.User);
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var item = await operations.EnqueueAsync(key, operation, id, actor, ct);
            return new ResidentOperationResult(ServerModules.ProxyId, Results.Accepted(ProxyApiRoutes.Proxy + "/operations/" + item.OperationId, new ProxyOperationAcceptedDto(item.OperationId)));
        }
        catch (ProxyOperationValidationException error) { return Problem(error.ProblemCode, StatusCodes.Status400BadRequest); }
    }
    private static string Route(string pattern) => ProxyApiRoutes.Proxy + pattern;
    private static async Task<IResult> ProfileAsync(Func<Task<ProxyProfileDto>> action, ProxyAuditStore audit, HttpContext context, string name, CancellationToken ct, bool created = false)
    {
        try { var profile = await action(); await audit.RecordAsync(Actor(context.User), name, "succeeded", null, ct); return created ? Results.Created(ProxyApiRoutes.Profiles + "/" + profile.Id, profile) : Results.Ok(profile); }
        catch (ProxyProfileValidationException error) { await audit.RecordAsync(Actor(context.User), name, "failed", error.ProblemCode, ct); return Problem(error.ProblemCode, StatusCodes.Status400BadRequest); }
    }
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code, detail: code,
        type: "https://relaxkonos.app/problems/" + code, extensions: new Dictionary<string, object?> { ["problemCode"] = code });
    private static string Actor(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub") ?? "unknown";
}
