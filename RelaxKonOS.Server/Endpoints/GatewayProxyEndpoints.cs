using RelaxKonOS.Server.Startup;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Endpoints;

public static class GatewayProxyEndpoints
{
    public static IEndpointRouteBuilder MapGatewayProxyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(GatewayProxyRoutes.Configuration, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ExecuteAsync(context, coordinator, async (service, ct) =>
                await service.GetConfigurationAsync(ct) is { } snapshot ? Results.Ok(snapshot) : Results.Problem(statusCode: 503, title: "squid.helper.unavailable")))
            .RequireAuthorization("ProxyRead").WithTags("Gateway Proxy");
        app.MapGet(GatewayProxyRoutes.Status, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ExecuteAsync(context, coordinator, async (service, ct) =>
                await service.GetStatusAsync(ct) is { } status ? Results.Ok(status) : Results.Problem(statusCode: 503, title: "squid.helper.unavailable")))
            .RequireAuthorization("ProxyRead").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Preview, (SquidConfiguration configuration, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, (_, _) =>
                Task.FromResult<IResult>(Results.Ok(SquidConfigurationRenderer.Preview(configuration)))))
            .RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Validate, (SquidConfiguration configuration, HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ExecuteAsync(context, coordinator, async (service, ct) => Results.Ok(await service.ValidateAsync(configuration, ct))))
            .RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Apply, (ApplySquidConfigurationRequest request, HttpContext context,
            ProxyOperationStore operations, CancellationToken ct) =>
        {
            var preview = SquidConfigurationRenderer.Preview(request.Configuration);
            return preview.Valid ? QueueAsync(context, operations, "configuration.apply",
                (execution, id, stage, _, token) => execution.ApplyAsync(id, request, stage, token), ct, request)
                : Task.FromResult<IResult>(Results.BadRequest(preview));
        }).RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Lifecycle, (SquidLifecycleRequest request, HttpContext context,
            ProxyOperationStore operations, CancellationToken ct) =>
            Enum.IsDefined(request.Action)
                ? QueueAsync(context, operations, "lifecycle." + request.Action.ToString().ToLowerInvariant(), (execution, id, stage, _, token) => execution.ExecuteAsync(id, request.Action, stage, token), ct, request)
                : Task.FromResult<IResult>(Results.BadRequest(new { problemCode = "squid.action.invalid" })))
            .RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        app.MapGet(GatewayProxyRoutes.Accounts, (HttpContext context, ModuleResidencyCoordinator coordinator) =>
            ExecuteAsync(context, coordinator, async (service, ct) =>
                await service.GetAccountsAsync(ct) is { } accounts ? Results.Ok(accounts) : Results.Problem(statusCode: 503, title: "squid.helper.unavailable")))
            .RequireAuthorization("ProxyRead").WithTags("Gateway Proxy");
        app.MapPost(GatewayProxyRoutes.Accounts, (SquidAccountMutation request, HttpContext context,
            ProxyOperationStore operations, CancellationToken ct) =>
            SquidAccountValidation.Valid(request)
                ? QueueAsync(context, operations, "accounts." + request.Action.ToString().ToLowerInvariant(), (execution, id, stage, identity, token) => execution.MutateAccountAsync(id, identity!, request, stage, token), ct, request)
                : Task.FromResult<IResult>(Results.BadRequest(new { problemCode = "squid.accounts.invalid" })))
            .RequireAuthorization("ProxyManage").WithTags("Gateway Proxy");
        return app;
    }

    private static Task ExecuteAsync(HttpContext context, ModuleResidencyCoordinator coordinator,
        Func<SquidGatewayService, CancellationToken, Task<IResult>> execute) =>
        ProxyRequestScopeOwner.ExecuteAsync(context, coordinator, (services, ct) =>
            execute(services.GetRequiredService<SquidGatewayService>(), ct));

    private static async Task<IResult> QueueAsync(HttpContext context, ProxyOperationStore store,
        string kind, Func<SquidGatewayService, Guid, ProxyOperationStageReporter, string?, CancellationToken, Task<string?>> action,
        CancellationToken ct, object? identifiedRequest = null)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160)
            return Results.BadRequest(new { problemCode = "squid.idempotency_key.required_or_too_long" });
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? "unknown";
        var scopes = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var requestIdentity = identifiedRequest switch
        {
            null => null,
            SquidAccountMutation account => AccountIdentity(context, actor, account),
            _ => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { actor, publication = identifiedRequest })))
        };
        try
        {
            var item = await store.EnqueueIdentifiedAsync("squid:" + key, "squid." + kind, (id, stage, _, token) =>
                ProxyOperationScope.RunAsync(scopes, actor, "squid." + kind,
                    (services, execution) => action(services.GetRequiredService<SquidGatewayService>(), id, stage, requestIdentity, execution), token),
                ct, requestIdentity);
            return new ResidentOperationResult(ServerModules.ProxyId, Results.Accepted(ProxyApiRoutes.Proxy + "/operations/" + item.OperationId, new ProxyOperationAcceptedDto(item.OperationId)));
        }
        catch (ProxyOperationValidationException error) { return Results.BadRequest(new { problemCode = error.ProblemCode }); }
    }

    private static string AccountIdentity(HttpContext context, string actor, SquidAccountMutation account)
    {
        var secret = Encoding.UTF8.GetBytes(context.RequestServices.GetRequiredService<IOptions<JwtOptions>>().Value.Secret);
        var key = HMACSHA256.HashData(secret, "RelaxKonOS.Squid.AccountRequest"u8);
        var data = JsonSerializer.SerializeToUtf8Bytes(new { actor, account });
        try { return Convert.ToHexString(HMACSHA256.HashData(key, data)); }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(data);
        }
    }
}
