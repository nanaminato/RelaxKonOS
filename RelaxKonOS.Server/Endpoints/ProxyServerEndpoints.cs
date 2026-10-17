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
        app.MapGet(ProxyServerRoutes.OperationPattern, async (Guid id, ProxyServerService service,
                ProxyOperationStore operations, CancellationToken ct) =>
            {
                var operation = await service.OperationAsync(id, operations, ct);
                return operation is null || !operation.Kind.StartsWith("proxy_server.", StringComparison.Ordinal)
                    ? Results.NotFound()
                    : Results.Ok(operation);
            })
            .RequireAuthorization(ReadPolicy).WithTags("Proxy Server");

        app.MapPut(ProxyServerRoutes.Configuration, (SaveProxyServerConfiguration? request, HttpContext context,
                ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, service, operations, audit, "configuration.save", request,
                        token => service.SaveAsync(request, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Validate, async (ProxyServerService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ValidateAsync(ct)); }
            catch (ProxyServerException ex) { return Problem(ex.Message); }
        }).RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Apply, (HttpContext context, ProxyServerService service,
                ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                QueueAsync(context, service, operations, audit, "configuration.apply", new { },
                    service.ApplyAsync, ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Rollback, (HttpContext context, ProxyServerService service,
                ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                QueueAsync(context, service, operations, audit, "configuration.rollback", new { },
                    service.RollbackAsync, ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Recovery, (HttpContext context, ProxyServerService service,
                ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                QueueAsync(context, service, operations, audit, "recovery", new { },
                    service.RecoverAsync, ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.RuntimeInstall, (HttpContext context, ProxyServerService service,
                ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                QueueAsync(context, service, operations, audit, "runtime.install", new { },
                    service.InstallAsync, ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.RuntimeUpgrade, (ProxyServerUpgradeRequest? request, HttpContext context,
                ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, service, operations, audit, "runtime.upgrade", request,
                        token => service.UpgradeAsync(request.Version, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.RuntimeUninstall, (ProxyServerUninstallRequest? request, HttpContext context,
                ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
            {
                var keepData = request?.KeepData ?? true;
                return QueueAsync(context, service, operations, audit, "runtime.uninstall", new { keepData },
                    token => service.UninstallAsync(keepData, token), ct);
            })
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(Route(ProxyServerRoutes.RuntimeActionPattern), (string? action, HttpContext context,
                ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ProxyServerAction>(action, true, out var parsed) || !Enum.IsDefined(parsed))
                    return Task.FromResult(BadRequest("proxy_server.action_invalid"));
                return QueueAsync(context, service, operations, audit, "runtime.action." + parsed.ToString().ToLowerInvariant(),
                    new { action = parsed }, token => service.ActionAsync(parsed, token), ct);
            })
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Principals, (SaveProxyServerPrincipal? request, HttpContext context,
                ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, service, operations, audit, "principal.save", request,
                        token => service.SavePrincipalAsync(request, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPut(Route(ProxyServerRoutes.PrincipalPattern), (Guid id, SaveProxyServerPrincipal? request, HttpContext context,
                ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, service, operations, audit, "principal.save", request with { Id = id },
                        token => service.SavePrincipalAsync(request with { Id = id }, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapDelete(Route(ProxyServerRoutes.PrincipalPattern), (Guid id, HttpContext context, ProxyServerService service,
                ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                QueueAsync(context, service, operations, audit, "principal.delete", new { id },
                    token => service.DeletePrincipalAsync(id, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");

        app.MapPost(ProxyServerRoutes.Credentials, (ProxyServerCredentialRequest? request, HttpContext context,
                ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, service, operations, audit, "credential.save", request,
                        token => service.SaveCredentialAsync(request, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPut(Route(ProxyServerRoutes.CredentialPattern), (Guid id, ProxyServerCredentialRequest? request,
                HttpContext context, ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit,
                CancellationToken ct) =>
                request is null ? Task.FromResult(BadRequest("proxy_server.request_invalid"))
                    : QueueAsync(context, service, operations, audit, "credential.save", request with { Id = id },
                        token => service.SaveCredentialAsync(request with { Id = id }, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapDelete(Route(ProxyServerRoutes.CredentialPattern), (Guid id, HttpContext context, ProxyServerService service,
                ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
                QueueAsync(context, service, operations, audit, "credential.delete", new { id },
                    token => service.DeleteCredentialAsync(id, token), ct))
            .RequireAuthorization(ManagePolicy).WithTags("Proxy Server");
        app.MapPost(Route(ProxyServerRoutes.CredentialRotatePattern), (Guid id, ProxyServerCredentialRotation? request,
                HttpContext context, ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit,
                CancellationToken ct) =>
                QueueAsync(context, service, operations, audit, "credential.rotate", new { id, request },
                    token => service.RotateCredentialAsync(id, request, token), ct))
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
                    var issued = await service.IssueSubscriptionAsync(request, ct);
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
        app.MapDelete(Route(ProxyServerRoutes.SubscriptionPattern), async (Guid id, HttpContext context, ProxyServerService service,
                ProxyAuditStore audit, CancellationToken ct) =>
            {
                var problem = await service.RevokeSubscriptionAsync(id, ct);
                await audit.RecordAsync(Actor(context), "proxy_server.subscription.revoke",
                    problem is null ? "succeeded" : "failed", problem, ct);
                return problem is null ? Results.NoContent() : Problem(problem);
            })
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

    private static async Task<IResult> QueueAsync(HttpContext context, ProxyServerService service, ProxyOperationStore operations,
        ProxyAuditStore audit, string kind, object request, Func<CancellationToken, Task<string?>> action, CancellationToken ct)
    {
        var actor = Actor(context);
        try
        {
            var item = await service.EnqueueAsync(actor, context.Request.Headers["Idempotency-Key"].ToString(), kind, request, operations, async token =>
            {
                string? problem;
                try { problem = await action(token); }
                catch (ProxyServerException ex) { problem = ex.Message; }
                await audit.RecordAsync(actor, "proxy_server." + kind, problem is null ? "succeeded" : "failed", problem, token);
                return problem;
            }, ct);
            return Results.Accepted(ProxyServerRoutes.Root + "/operations/" + item.OperationId, new ProxyOperationAcceptedDto(item.OperationId));
        }
        catch (ProxyServerException ex) { return Problem(ex.Message); }
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
