using System.Security.Claims;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Endpoints;

public static class GatewayProxyEndpoints
{
    public static IEndpointRouteBuilder MapGatewayProxyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(GatewayProxyRoutes.Configuration, async (SquidGatewayService service, CancellationToken ct) =>
            await service.GetConfigurationAsync(ct) is { } snapshot ? Results.Ok(snapshot) : Results.Problem(statusCode: 503, title: "squid.helper.unavailable"))
            .RequireAuthorization("ProxyRead").WithTags("Gateway Proxy");
        app.MapGet(GatewayProxyRoutes.Status, async (SquidGatewayService service, CancellationToken ct) =>
            await service.GetStatusAsync(ct) is { } status ? Results.Ok(status) : Results.Problem(statusCode: 503, title: "squid.helper.unavailable"))
            .RequireAuthorization("ProxyRead").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Preview, (SquidConfiguration configuration) =>
        {
            var preview = SquidConfigurationRenderer.Preview(configuration);
            return Results.Ok(preview);
        }).RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Validate, async (SquidConfiguration configuration, SquidGatewayService service, CancellationToken ct) =>
        {
            var result = await service.ValidateAsync(configuration, ct);
            return Results.Ok(result);
        }).RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Apply, (ApplySquidConfigurationRequest request, HttpContext context,
            SquidGatewayService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
        {
            var preview = SquidConfigurationRenderer.Preview(request.Configuration);
            return preview.Valid ? QueueAsync(context, operations, audit, "configuration.apply", token => service.ApplyAsync(request, token), ct)
                : Task.FromResult<IResult>(Results.BadRequest(preview));
        }).RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Lifecycle, (SquidLifecycleRequest request, HttpContext context,
            SquidGatewayService service, ProxyOperationStore operations, ProxyAuditStore audit, CancellationToken ct) =>
            Enum.IsDefined(request.Action)
                ? QueueAsync(context, operations, audit, "lifecycle." + request.Action.ToString().ToLowerInvariant(), token => service.ExecuteAsync(request.Action, token), ct)
                : Task.FromResult<IResult>(Results.BadRequest(new { problemCode = "squid.action.invalid" })))
            .RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        return app;
    }

    private static async Task<IResult> QueueAsync(HttpContext context, ProxyOperationStore store, ProxyAuditStore audit,
        string kind, Func<CancellationToken, Task<string?>> action, CancellationToken ct)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160)
            return Results.BadRequest(new { problemCode = "squid.idempotency_key.required_or_too_long" });
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? "unknown";
        var item = await store.EnqueueAsync("squid:" + key, "squid." + kind, async (_, token) =>
        {
            var problem = await action(token);
            await audit.RecordAsync(actor, "squid." + kind, problem is null ? "succeeded" : "failed", problem, token);
            return problem;
        }, ct);
        return Results.Accepted(ProxyApiRoutes.Proxy + "/operations/" + item.OperationId, new ProxyOperationAcceptedDto(item.OperationId));
    }
}
