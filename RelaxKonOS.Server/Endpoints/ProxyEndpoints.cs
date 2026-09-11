using System.Security.Claims;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Proxy API: all controller interaction remains Server-side and dangerous work is queued in the host ledger.</summary>
public static class ProxyEndpoints
{
    public static IEndpointRouteBuilder MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProxyApiRoutes.Overview, (IProxyLifecycleService service, CancellationToken ct) => service.GetOverviewAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Runtime, (IProxyRuntimeManager runtime, CancellationToken ct) => runtime.GetAsync("mihomo", ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.RuntimeDownload, (string? version, RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManifest manifest) =>
            manifest.Find(version) is { } release
                ? Results.Ok(new ProxyRuntimeDownloadDto(release.Version, release.DownloadUri.AbsoluteUri))
                : Results.NotFound()).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Settings, (IProxySettingsService settings, CancellationToken ct) => settings.GetAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapPut(ProxyApiRoutes.Settings, async (UpdateProxySettingsRequest request, IProxySettingsService settings, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var problem = await settings.UpdateAsync(request, ct);
            await audit.RecordAsync(Actor(context.User), "settings.update", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.GeoData, (IProxyGeoDataService geoData, CancellationToken ct) => geoData.GetAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapPut(ProxyApiRoutes.GeoData, async (ConfigureProxyGeoDataRequest request, IProxyGeoDataService geoData, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var problem = await geoData.ConfigureFromServerFileAsync(request.FilePath, ct);
            await audit.RecordAsync(Actor(context.User), "geodata.configure", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.RuntimeExternalDetection, async (ProxyRuntimeRequest request, IProxyRuntimeManager runtime, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(request.ExternalPath) ? Problem(ProxyProblemCodes.ExternalRuntimeInvalid, StatusCodes.Status400BadRequest) : Results.Ok(await runtime.DetectExternalAsync(request.EngineId, request.ExternalPath, ct)))
            .RequireAuthorization("ProxyManage").WithTags("Proxy");

        app.MapPost(ProxyApiRoutes.Lifecycle, (string action, HttpContext context, ProxyOperationStore operations, IProxyLifecycleService lifecycle, ProxyAuditStore audit, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ProxyLifecycleAction>(action, true, out var parsed)) return Task.FromResult<IResult>(Problem(ProxyProblemCodes.NotSupported, StatusCodes.Status400BadRequest));
            return QueueAsync(context, operations, "lifecycle." + action.ToLowerInvariant(), async (actor, token) =>
            {
                var problem = await lifecycle.ExecuteLifecycleAsync(parsed, token);
                await audit.RecordAsync(actor, "lifecycle." + action.ToLowerInvariant(), string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, token);
                return problem;
            }, ct);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");

        app.MapGet(ProxyApiRoutes.Tun, (IProxyTunSafetyService tun, CancellationToken ct) => tun.GetStatusAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.TunEnable, (ProxyTunRequest request, HttpContext context, ProxyOperationStore operations, IProxyTunSafetyService tun, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, operations, "tun.enable", async (actor, token) =>
            {
                var problem = await tun.EnableAsync(request.ProfileId, token);
                await audit.RecordAsync(actor, "tun.enable", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, token);
                return problem;
            }, ct)).RequireAuthorization("ProxyDangerous").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.TunDisable, (HttpContext context, ProxyOperationStore operations, IProxyTunSafetyService tun, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, operations, "tun.disable", async (actor, token) =>
            {
                var problem = await tun.DisableAsync(token);
                await audit.RecordAsync(actor, "tun.disable", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, token);
                return problem;
            }, ct)).RequireAuthorization("ProxyDangerous").WithTags("Proxy");
        app.MapPost(ProxyApiRoutes.TunEmergencyDisable, (HttpContext context, ProxyOperationStore operations, IProxyTunSafetyService tun, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, operations, "tun.emergency-disable", async (actor, token) =>
            {
                var problem = await tun.EmergencyDisableAsync(token);
                await audit.RecordAsync(actor, "tun.emergency-disable", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, token);
                return problem;
            }, ct)).RequireAuthorization("ProxyDangerous").WithTags("Proxy");
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
        app.MapPost(ProxyApiRoutes.SubscriptionsRefresh, (HttpContext context, ProxyOperationStore operations, IProxySubscriptionService service, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, operations, "subscription.refresh_all", async (actor, token) =>
            {
                var problem = await service.RefreshAllAsync(token);
                await audit.RecordAsync(actor, "subscription.refresh_all", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, token);
                return problem;
            }, ct)).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.SubscriptionRefreshPattern), (Guid subscriptionId, HttpContext context, ProxyOperationStore operations, IProxySubscriptionService service, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, operations, "subscription.refresh", async (actor, token) =>
            {
                var problem = await service.RefreshAsync(subscriptionId, token);
                await audit.RecordAsync(actor, "subscription.refresh", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, token);
                return problem;
            }, ct)).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.SubscriptionActivatePattern), (Guid subscriptionId, HttpContext context, ProxyOperationStore operations, IProxySubscriptionService service, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, operations, "subscription.activate", async (actor, token) =>
            {
                var problem = await service.ActivateAsync(subscriptionId, token);
                await audit.RecordAsync(actor, "subscription.activate", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, token);
                return problem;
            }, ct)).RequireAuthorization("ProxyManage").WithTags("Proxy");
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
        app.MapPost(Route(ProxyApiRoutes.ProfileActivatePattern), async (Guid profileId, IProxyProfileRepository profiles, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var profile = await profiles.SetActiveAsync(profileId, ct); await audit.RecordAsync(Actor(context.User), "profile.activate", profile is null ? "failed" : "succeeded", profile is null ? ProxyProblemCodes.ConfigInvalid : null, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapPost(Route(ProxyApiRoutes.ProfileConfigurationApplyPattern), async (Guid profileId, ApplyProxyConfigurationRequest request, IProxyConfigurationTransactionService configuration, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var problem = await configuration.ApplyAsync(profileId, request.Yaml, ct); await audit.RecordAsync(Actor(context.User), "profile.configuration.apply", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");

        app.MapGet(ProxyApiRoutes.Groups, async (IProxyEngineRegistry engines, CancellationToken ct) => Results.Ok(await engines.Find("mihomo")!.GetGroupsAsync(ct))).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapPut(Route(ProxyApiRoutes.GroupSelectionPattern), async (string groupName, SelectProxyGroupRequest request, IProxyEngineRegistry engines, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var problem = await engines.Find("mihomo")!.SelectGroupAsync(groupName, request.Proxy, ct); await audit.RecordAsync(Actor(context.User), "group.select", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Routing, async (IProxyEngineRegistry engines, CancellationToken ct) =>
        {
            var mode = await engines.Find("mihomo")!.GetRoutingModeAsync(ct);
            return string.IsNullOrEmpty(mode.ProblemCode) ? Results.Ok(mode) : Problem(mode.ProblemCode, StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization("ProxyRead").WithTags("Proxy");
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
        app.MapGet(ProxyApiRoutes.Connections, async (IProxyEngineRegistry engines, CancellationToken ct) => Results.Ok(await engines.Find("mihomo")!.GetConnectionsAsync(ct))).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Traffic, (IProxyEngineRegistry engines, CancellationToken ct) => engines.Find("mihomo")!.GetTrafficAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapDelete(Route(ProxyApiRoutes.ConnectionPattern), async (string connectionId, IProxyEngineRegistry engines, ProxyAuditStore audit, HttpContext context, CancellationToken ct) =>
        {
            var problem = await engines.Find("mihomo")!.CloseConnectionAsync(connectionId, ct); await audit.RecordAsync(Actor(context.User), "connection.close", string.IsNullOrEmpty(problem) ? "succeeded" : "failed", problem, ct);
            return string.IsNullOrEmpty(problem) ? Results.NoContent() : Problem(problem, StatusCodes.Status400BadRequest);
        }).RequireAuthorization("ProxyManage").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Logs, async (int? limit, IProxyEngineRegistry engines, CancellationToken ct) => Results.Ok(await engines.Find("mihomo")!.GetLogsAsync(Math.Clamp(limit ?? 100, 1, 500), ct))).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(ProxyApiRoutes.Dns, (IProxyEngineRegistry engines, CancellationToken ct) => engines.Find("mihomo")!.GetDnsStatusAsync(ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        app.MapGet(Route(ProxyApiRoutes.OperationsPattern), (Guid operationId, ProxyOperationStore operations, CancellationToken ct) => operations.GetAsync(operationId, ct)).RequireAuthorization("ProxyRead").WithTags("Proxy");
        return app;
    }

    private static async Task<IResult> QueueAsync(HttpContext context, ProxyOperationStore store, string kind, Func<string, CancellationToken, Task<string?>> action, CancellationToken ct)
        => await QueueAsync(context, store, kind, (actor, _, token) => action(actor, token), ct);

    private static async Task<IResult> QueueAsync(HttpContext context, ProxyOperationStore store, string kind, Func<string, ProxyOperationStageReporter, CancellationToken, Task<string?>> action, CancellationToken ct)
    {
        var actor = Actor(context.User);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try { var item = await store.EnqueueAsync(idempotencyKey, kind, (reportStage, token) => action(actor, reportStage, token), ct); return Results.Accepted(ProxyApiRoutes.Proxy + "/operations/" + item.OperationId, new ProxyOperationAcceptedDto(item.OperationId)); }
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
