using System.Security.Claims;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.ProxyServer;

namespace RelaxKonOS.Server.Endpoints;

public static class ProxyServerEndpoints
{
    public static IEndpointRouteBuilder MapProxyServerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProxyServerRoutes.Configuration, async (ProxyServerService service, CancellationToken ct) => Results.Ok(await service.ReadAsync(ct)))
            .RequireAuthorization("ProxyRead").WithTags("Proxy Server");
        app.MapGet(ProxyServerRoutes.Status, async (ProxyServerService service, CancellationToken ct) => Results.Ok(await service.StatusAsync(ct)))
            .RequireAuthorization("ProxyRead").WithTags("Proxy Server");
        app.MapPut(ProxyServerRoutes.Configuration, (SaveProxyServerConfiguration request, HttpContext context, ProxyServerService service,
            ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, service, operations, audit, "configuration.save", request, token => service.SaveAsync(request, token), ct))
            .RequireAuthorization("ProxyManage").WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Credentials, (ProxyServerCredentialRequest request, HttpContext context, ProxyServerService service,
            ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, service, operations, audit, "credential.save", request, token => service.CredentialAsync(request, token), ct))
            .RequireAuthorization("ProxyManage").WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Apply, (HttpContext context, ProxyServerService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, service, operations, audit, "configuration.apply", new { }, service.ApplyAsync, ct))
            .RequireAuthorization("ProxyManage").WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Runtime, (ProxyServerActionRequest request, HttpContext context, ProxyServerService service,
            ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
            QueueAsync(context, service, operations, audit, "runtime.action", request, token => service.ActionAsync(request.Action, token), ct))
            .RequireAuthorization("ProxyManage").WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Validate, async (ProxyServerService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ValidateAsync(ct)); }
            catch (ProxyServerException ex) { return Problem(ex.Message); }
        }).RequireAuthorization("ProxyManage").WithTags("Proxy Server");
        app.MapPost(ProxyServerRoutes.Export, async (ProxyServerExportRequest request, HttpContext context, ProxyServerService service, ProxyAuditStore audit, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
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
        }).RequireAuthorization("ProxyServerExport").WithTags("Proxy Server");
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
            return Results.Accepted(ProxyApiRoutes.Proxy + "/operations/" + item.OperationId, new ProxyOperationAcceptedDto(item.OperationId));
        }
        catch (ProxyServerException ex) { return Problem(ex.Message); }
    }
    private static string Actor(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? "unknown";
    private static IResult Problem(string code) => Results.Problem(statusCode: code.Contains("conflict", StringComparison.Ordinal) ? 409 : 400, title: code);
}
