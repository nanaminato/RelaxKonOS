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
        app.MapPost(ProxyApiRoutes.RuntimeExternalDetection, async (ProxyRuntimeRequest request, IProxyRuntimeManager runtime, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(request.ExternalPath) ? Problem(ProxyProblemCodes.ExternalRuntimeInvalid, StatusCodes.Status400BadRequest) : Results.Ok(await runtime.DetectExternalAsync(request.EngineId, request.ExternalPath, ct)))
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
        app.MapGet(ProxyApiRoutes.Recovery, (IProxyRecoveryService recovery, CancellationToken ct) => recovery.GetStatusAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");

        app.MapGet(ProxyApiRoutes.Profiles, (IProxyProfileService service, CancellationToken ct) => service.ListAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Subscriptions, (IProxySubscriptionService service, CancellationToken ct) => service.ListAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.SubscriptionDownloadOptions, (IProxySubscriptionService service, CancellationToken ct) => service.GetDownloadOptionsAsync(ct)).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.Subscriptions, async (ImportProxySubscriptionRequest request, IProxySubscriptionService service, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var subscription = await service.ImportAsync(request, ct);
                await audit.RecordAsync(Actor(context.User), "subscription.import", "succeeded", null, ct);
                return Results.Created(ProxyApiRoutes.Subscriptions + "/" + subscription.Id, subscription);
            }
            catch (ProxySubscriptionException error)
            {
                await audit.RecordAsync(Actor(context.User), "subscription.import", "failed", error.ProblemCode, ct);
                return Problem(error.ProblemCode, StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapGet(Route(ProxyApiRoutes.SubscriptionContentPattern), async (Guid subscriptionId, IProxySubscriptionService service, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var content = await service.GetContentAsync(subscriptionId, ct);
                await audit.RecordAsync(Actor(context.User), "subscription.content.read", content is null ? "failed" : "succeeded", content is null ? ProxyProblemCodes.SubscriptionInvalid : null, ct);
                return content is null ? Problem(ProxyProblemCodes.SubscriptionInvalid, StatusCodes.Status404NotFound) : Results.Ok(content);
            }
            catch (ProxySubscriptionException error)
            {
                await audit.RecordAsync(Actor(context.User), "subscription.content.read", "failed", error.ProblemCode, ct);
                return Problem(error.ProblemCode, StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapSubscriptionOperationEndpoints();
        app.MapGet(Route(ProxyApiRoutes.ProfilePattern), async (Guid profileId, IProxyProfileService service, CancellationToken ct) =>
            await service.GetAsync(profileId, ct) is { } profile ? Results.Ok(profile) : Results.NotFound()).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.Profiles, async (UpsertProxyProfileRequest request, IProxyProfileRepository profiles, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
            await ProfileAsync(() => profiles.UpsertAsync(null, request.Name, request.EngineId, null, ct), audit, context, "profile.create", ct, true)).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPut(Route(ProxyApiRoutes.ProfilePattern), async (Guid profileId, UpsertProxyProfileRequest request, IProxyProfileRepository profiles, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
            await ProfileAsync(() => profiles.UpsertAsync(profileId, request.Name, request.EngineId, request.ExpectedRevision, ct), audit, context, "profile.update", ct)).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapDelete(Route(ProxyApiRoutes.ProfilePattern), async (Guid profileId, IProxyProfileRepository profiles, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var deleted = await profiles.DeleteAsync(profileId, ct); await audit.RecordAsync(Actor(context.User), "profile.delete", deleted ? "succeeded" : "failed", deleted ? null : ProxyProblemCodes.ConfigApplyFailed, ct);
            return deleted ? Results.NoContent() : Results.Conflict();
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapProxyProfilePublicationEndpoints();

        app.MapPut(Route(ProxyApiRoutes.GroupSelectionPattern), async (string groupName, SelectProxyGroupRequest request, IProxyEngineRegistry engines, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var problem = await engines.Find("mihomo")!.SelectGroupAsync(groupName, request.Proxy, ct); await audit.RecordAsync(Actor(context.User), "group.select", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPut(ProxyApiRoutes.Routing, async (ProxyRoutingModeDto request, IProxyEngineRegistry engines, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var problem = await engines.Find("mihomo")!.SetRoutingModeAsync(request.Mode, ct);
            await audit.RecordAsync(Actor(context.User), "routing.mode.set", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.GroupProxyDelayPattern), async (string groupName, string proxyName, TestProxyDelayRequest request, IProxyEngineRegistry engines, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var delay = await engines.Find("mihomo")!.TestProxyDelayAsync(proxyName, request.Url, request.TimeoutMilliseconds, ct);
            await audit.RecordAsync(Actor(context.User), "group.proxy.delay", string.IsNullOrEmpty(delay.ProblemCode) ? "succeeded" : "failed", delay.ProblemCode, ct);
            return string.IsNullOrEmpty(delay.ProblemCode) ? Results.Ok(delay) : Problem(delay.ProblemCode, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapDelete(Route(ProxyApiRoutes.ConnectionPattern), async (string connectionId, IProxyEngineRegistry engines, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var problem = await engines.Find("mihomo")!.CloseConnectionAsync(connectionId, ct); await audit.RecordAsync(Actor(context.User), "connection.close", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        return app;
    }

    internal static void MapTunOperationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProxyApiRoutes.Tun, (IProxyTunSafetyService tun, CancellationToken ct) => tun.GetStatusAsync(ct))
            .RequireAuthorization("ProxyRead").WithTags("Proxy");
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
            ProxyWriteScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
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
            ProxyWriteScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var problem = await services.GetRequiredService<IProxyConfigurationTransactionService>().ActivateStoredAsync(profileId, ct);
                await services.GetRequiredService<ProxyAuditStore>().RecordAsync(Actor(context.User), "profile.activate",
                    string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, CancellationToken.None);
                return string.IsNullOrEmpty(problem) ? Results.Ok(await services.GetRequiredService<IProxyProfileRepository>().GetAsync(profileId, ct))
                    : Problem(problem, StatusCodes.Status400BadRequest);
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.ProfileConfigurationApplyPattern), (Guid profileId, ApplyProxyConfigurationRequest request,
            HttpContext context, ModuleResidencyCoordinator coordinator) => ProxyWriteScopeOwner.ExecuteAsync(context, coordinator, async (services, ct) =>
            {
                var problem = await services.GetRequiredService<IProxyConfigurationTransactionService>().ApplyAsync(profileId, request.Yaml, ct);
                await services.GetRequiredService<ProxyAuditStore>().RecordAsync(Actor(context.User), "profile.configuration.apply",
                    string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, CancellationToken.None);
                return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
            })).RequireAuthorization("ProxyManage").WithTags("Proxy");
    }

    internal static void MapProxyObservationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProxyApiRoutes.Runtime, (HttpContext context, IResidentModuleWorkLeaseSource leases) => ExecuteScopedAsync(context, leases,
            static async (services, ct) => Results.Ok((await ProxyReadSampler<MihomoRuntimeObservation>.ReadAsync(services, ct)).Runtime)))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyRuntimeDto>();
        app.MapGet(ProxyApiRoutes.Groups, (HttpContext context, IResidentModuleWorkLeaseSource leases) => ExecuteScopedAsync(context, leases,
            static async (services, ct) => Results.Ok(await MihomoProxyGroupOrder.ReadGroupsAsync(
                services.GetRequiredService<IMihomoControllerClient>(), services.GetRequiredService<IProxyPlatformPaths>(), ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyGroupDto[]>();
        app.MapGet(ProxyApiRoutes.ControllerConfiguration, (HttpContext context, IResidentModuleWorkLeaseSource leases) => ExecuteScopedAsync(context, leases,
            static async (services, ct) => Results.Ok(await services.GetRequiredService<IMihomoControllerClient>().GetControllerConfigurationAsync(ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyControllerConfigurationDto>();
        app.MapGet(ProxyApiRoutes.Overview, (HttpContext context, IResidentModuleWorkLeaseSource leases) => ExecuteScopedAsync(context, leases,
            static async (services, ct) => Results.Ok(await ProxyReadSampler<ProxyOverviewDto>.ReadAsync(services, ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyOverviewDto>();
        app.MapGet(ProxyApiRoutes.Connections, (HttpContext context, IResidentModuleWorkLeaseSource leases) => ExecuteScopedAsync(context, leases, static async (services, ct) =>
        {
            var result = await ProxyReadSampler<ControllerResult<IReadOnlyList<ProxyConnectionDto>>>.ReadAsync(services, ct);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(result.ProblemCode, StatusCodes.Status503ServiceUnavailable);
        })).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Traffic, (HttpContext context, IResidentModuleWorkLeaseSource leases) =>
            ExecuteScopedAsync(context, leases, static async (services, ct) =>
                Results.Ok(await ProxyReadSampler<ProxyTrafficDto>.ReadAsync(services, ct))))
            .RequireAuthorization("ProxyRead").WithTags("Proxy").Produces<ProxyTrafficDto>();
        app.MapGet(ProxyApiRoutes.Logs, (int? limit, HttpContext context, IResidentModuleWorkLeaseSource leases) =>
            ExecuteScopedAsync(context, leases, async (services, ct) =>
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
