using RelaxKonOS.Server.Startup;
using System.Security.Claims;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.ProxyServer;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// Proxy Server management API. Reads are role-gated, every mutation is queued in the durable host
/// ledger with an idempotency key, and only the export endpoints can return connection secrets.
/// </summary>
public static class ProxyServerEndpoints
{
    private const string ReadPolicy = "ProxyRead";
    private const string ManagePolicy = "ProxyManage";
    private const string ExportPolicy = "ProxyServerExport";
    public const string SubscriptionRateLimitPolicy = "proxy-subscription";

    public static IEndpointRouteBuilder MapProxyServerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProxyServerRoutes.Capabilities, (ProxyServerService service) => Results.Ok(service.Capabilities()))
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");
        app.MapGet(ProxyServerRoutes.Status, async (ProxyServerService service, CancellationToken ct) =>
                Results.Ok(await service.StatusAsync(ct)))
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");
        app.MapGet(ProxyServerRoutes.Configuration, async (ProxyServerService service, CancellationToken ct) =>
                Results.Ok(await service.ReadAsync(ct)))
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");
        app.MapGet(ProxyServerRoutes.Principals, async (ProxyServerService service, CancellationToken ct) =>
                Results.Ok((await service.ReadAsync(ct)).Principals))
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");
        app.MapGet(ProxyServerRoutes.Credentials, async (ProxyServerService service, CancellationToken ct) =>
                Results.Ok((await service.ReadAsync(ct)).Credentials))
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");
        app.MapGet(ProxyServerRoutes.Subscriptions, async (ProxyServerService service, CancellationToken ct) =>
                Results.Ok(await service.SubscriptionsAsync(ct)))
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");
        app.MapGet(ProxyServerRoutes.RuntimeReleases, async (ProxyServerService service, CancellationToken ct) =>
                Results.Ok(await service.ReleasesAsync(ct)))
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");
        app.MapGet(ProxyServerRoutes.OperationPattern, async (Guid id, ProxyOperationStore operations, CancellationToken ct) =>
            {
                var operation = await operations.GetAsync(id, ct);
                return operation is null || !operation.Kind.StartsWith("proxy_server.", StringComparison.Ordinal)
                    ? Results.NotFound()
                    : Results.Ok(operation);
            })
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");

        app.MapPut(ProxyServerRoutes.Configuration, (SaveProxyServerConfiguration? request, HttpContext context,
                ProxyServerOperationQueue queue, ProxyOperationStore operations, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, queue, operations, "configuration.save", request,
                        (execution, token) => execution.SaveAsync(request, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Validate, async (ProxyServerService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ValidateAsync(ct)); }
            catch (ProxyServerException ex) { return Problem(ex.Message); }
        }).RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Apply, (HttpContext context, ProxyServerOperationQueue queue,
                ProxyOperationStore operations, CancellationToken ct) =>
                QueueAsync(context, queue, operations, "configuration.apply", new { },
                    (execution, token) => execution.ApplyAsync(token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Rollback, (HttpContext context, ProxyServerOperationQueue queue,
                ProxyOperationStore operations, CancellationToken ct) =>
                QueueAsync(context, queue, operations, "configuration.rollback", new { },
                    (execution, token) => execution.RollbackAsync(token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Recovery, (HttpContext context, ProxyServerOperationQueue queue,
                ProxyOperationStore operations, CancellationToken ct) =>
                QueueAsync(context, queue, operations, "recovery", new { },
                    (execution, token) => execution.RecoverAsync(token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.RuntimeInstall, (HttpContext context, ProxyServerOperationQueue queue,
                ProxyOperationStore operations, CancellationToken ct) =>
                QueueAsync(context, queue, operations, "runtime.install", new { },
                    (execution, token) => execution.InstallAsync(token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.RuntimeUpgrade, (ProxyServerUpgradeRequest? request, HttpContext context,
                ProxyServerOperationQueue queue, ProxyOperationStore operations, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, queue, operations, "runtime.upgrade", request,
                        (execution, token) => execution.UpgradeAsync(request.Version, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.RuntimeUninstall, (ProxyServerUninstallRequest? request, HttpContext context,
                ProxyServerOperationQueue queue, ProxyOperationStore operations, CancellationToken ct) =>
            {
                var keepData = request?.KeepData ?? true;
                return QueueAsync(context, queue, operations, "runtime.uninstall", new { keepData },
                    (execution, token) => execution.UninstallAsync(keepData, token), ct);
            })
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(Route(ProxyServerRoutes.RuntimeActionPattern), (string? action, HttpContext context,
                ProxyServerOperationQueue queue, ProxyOperationStore operations, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ProxyServerAction>(action, true, out var parsed) || !Enum.IsDefined(parsed))
                    return Task.FromResult(BadRequest("proxy_server.action_invalid"));
                return QueueAsync(context, queue, operations, "runtime.action." + parsed.ToString().ToLowerInvariant(),
                    new { action = parsed }, (execution, token) => execution.ActionAsync(parsed, token), ct);
            })
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Principals, (SaveProxyServerPrincipal? request, HttpContext context,
                ProxyServerOperationQueue queue, ProxyOperationStore operations, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, queue, operations, "principal.save", request,
                        (execution, token) => execution.SavePrincipalAsync(request, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPut(Route(ProxyServerRoutes.PrincipalPattern), (Guid id, SaveProxyServerPrincipal? request, HttpContext context,
                ProxyServerOperationQueue queue, ProxyOperationStore operations, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, queue, operations, "principal.save", request with { Id = id },
                        (execution, token) => execution.SavePrincipalAsync(request with { Id = id }, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapDelete(Route(ProxyServerRoutes.PrincipalPattern), (Guid id, HttpContext context, ProxyServerOperationQueue queue,
                ProxyOperationStore operations, CancellationToken ct) =>
                QueueAsync(context, queue, operations, "principal.delete", new { id },
                    (execution, token) => execution.DeletePrincipalAsync(id, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Credentials, (ProxyServerCredentialRequest? request, HttpContext context,
                ProxyServerOperationQueue queue, ProxyOperationStore operations, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, queue, operations, "credential.save", request,
                        (execution, token) => execution.SaveCredentialAsync(request, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPut(Route(ProxyServerRoutes.CredentialPattern), (Guid id, ProxyServerCredentialRequest? request,
                HttpContext context, ProxyServerOperationQueue queue, ProxyOperationStore operations,
                CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, queue, operations, "credential.save", request with { Id = id },
                        (execution, token) => execution.SaveCredentialAsync(request with { Id = id }, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapDelete(Route(ProxyServerRoutes.CredentialPattern), (Guid id, HttpContext context, ProxyServerOperationQueue queue,
                ProxyOperationStore operations, CancellationToken ct) =>
                QueueAsync(context, queue, operations, "credential.delete", new { id },
                    (execution, token) => execution.DeleteCredentialAsync(id, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(Route(ProxyServerRoutes.CredentialRotatePattern), (Guid id, ProxyServerCredentialRotation? request,
                HttpContext context, ProxyServerOperationQueue queue, ProxyOperationStore operations,
                CancellationToken ct) =>
                QueueAsync(context, queue, operations, "credential.rotate", new { id, request },
                    (execution, token) => execution.RotateCredentialAsync(id, request, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapGet(Route(ProxyServerRoutes.CredentialExportFormatsPattern), async (Guid id, ProxyServerService service,
                CancellationToken ct) =>
            {
                try { return Results.Ok(await service.ExportFormatsAsync(id, ct)); }
                catch (ProxyServerException ex) { return Problem(ex.Message); }
            })
            .RequireAuthorization(ExportPolicy).WithTags("Proxy Server");
        app.MapPost(Route(ProxyServerRoutes.CredentialExportPattern), async (Guid id, ProxyServerExportRequest? request,
                HttpContext context, ProxyServerService service, ProxyAuditStore audit, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (request is null || request.CredentialId != id) return Problem("proxy_server.request_invalid");
                try
                {
                    var export = await service.ExportAsync(request, ct);
                    await audit.RecordAsync(Actor(context), "proxy_server.credential.export", "succeeded", null, ct);
                    return Results.Ok(export);
                }
                catch (ProxyServerException ex)
                {
                    await audit.RecordAsync(Actor(context), "proxy_server.credential.export", "failed", ex.Message, ct);
                    return Problem(ex.Message);
                }
            })
            .RequireAuthorization(ExportPolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Subscriptions, async (IssueProxyServerSubscription? request, HttpContext context,
                ProxyServerService service, ProxyAuditStore audit, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (request is null) return Problem("proxy_server.request_invalid");
                try
                {
                    var issued = await service.IssueSubscriptionAsync(request, Actor(context), context.Request.Headers["Idempotency-Key"].ToString(), ct);
                    // Only the subscription id reaches the audit trail; the token does not.
                    await audit.RecordAsync(Actor(context), "proxy_server.subscription.issue", "succeeded", null, ct);
                    return Results.Created(ProxyServerRoutes.Subscriptions + "/" + issued.Subscription.Id, issued);
                }
                catch (ProxyServerException ex)
                {
                    await audit.RecordAsync(Actor(context), "proxy_server.subscription.issue", "failed", ex.Message, ct);
                    return Problem(ex.Message);
                }
            })
            .RequireAuthorization(ExportPolicy).WithTags("Proxy Server");
        app.MapDelete(Route(ProxyServerRoutes.SubscriptionPattern), (Guid id, HttpContext context, ProxyServerOperationQueue queue,
                ProxyOperationStore operations, CancellationToken ct) =>
                QueueAsync(context, queue, operations, "subscription.revoke", new { id },
                    (execution, token) => execution.RevokeSubscriptionAsync(id, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        // Anonymous download. The token is the only credential; the format is an explicit route value
        // so no User-Agent sniffing or content negotiation can change what a client receives.
        app.MapGet(ProxyServerRoutes.PublicSubscriptionPattern, async (string token, string format,
                HttpContext context, ProxyServerService service, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                try
                {
                    var export = await service.SubscriptionContentAsync(token, format, ct);
                    return Results.Text(export.Content, ContentType(format));
                }
                catch (ProxyServerException)
                {
                    // One uniform answer: a token cannot be distinguished from a wrong format or an expired grant.
                    return Results.NotFound();
                }
            })
            .AllowAnonymous()
            .RequireRateLimiting(SubscriptionRateLimitPolicy)
            .WithTags("Proxy Server");
        return app;
    }

    private static async Task<IResult> QueueAsync(HttpContext context, ProxyServerOperationQueue queue, ProxyOperationStore operations, string kind, object request, Func<ProxyServerService, CancellationToken, Task<string?>> action, CancellationToken ct)
    {
        var actor = Actor(context);
        var scopes = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        try
        {
            var item = await queue.EnqueueAsync(actor, context.Request.Headers["Idempotency-Key"].ToString(), kind, request, operations,
                token => ProxyOperationScope.RunAsync(scopes, actor, "proxy_server." + kind, async (services, executionToken) =>
                {
                    try { return await action(services.GetRequiredService<ProxyServerService>(), executionToken); }
                    catch (ProxyServerException ex) { return ex.Message; }
                }, token), ct);
            return new ResidentOperationResult(ServerModules.ProxyId, Results.Accepted(ProxyServerRoutes.Root + "/operations/" + item.OperationId, new ProxyOperationAcceptedDto(item.OperationId)));
        }
        catch (ProxyServerException ex) { return Problem(ex.Message); }
        catch (ProxyOperationValidationException ex) { return Problem(ex.ProblemCode); }
    }

    private static string ContentType(string format) => format switch
    {
        ProxyServerExportFormats.SingBox => "application/json; charset=utf-8",
        ProxyServerExportFormats.Mihomo => "text/yaml; charset=utf-8",
        _ => "text/plain; charset=utf-8"
    };

    private static string Route(string pattern) => pattern;

    private static string Actor(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? "unknown";

    private static IResult BadRequest(string code) => Problem(code);

    private static IResult Problem(string code) => Results.Problem(statusCode: StatusFor(code), title: code, detail: code,
        type: "https://relaxkonos.app/problems/" + code,
        extensions: new Dictionary<string, object?> { ["problemCode"] = code });

    private static int StatusFor(string code) => code switch
    {
        "proxy_server.revision_conflict" or "proxy_server.idempotency_conflict" or "proxy_server.recovery_required"
            or "proxy_server.configuration_drift" or "proxy_server.subscription_expiry_invalid" => StatusCodes.Status409Conflict,
        "proxy_server.platform_unsupported" or "proxy_server.feature_unsupported" => StatusCodes.Status501NotImplemented,
        "proxy_server.helper_unavailable" or "proxy_server.runtime_unavailable" or "proxy_server.statistics_unavailable"
            or "proxy_server.publication_unconfirmed" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };
}
