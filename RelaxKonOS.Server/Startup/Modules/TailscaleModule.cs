using RelaxKonOS.Server.Residency;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Tailscale;

namespace RelaxKonOS.Server.Startup.Modules;

internal sealed class TailscaleModule : IServerModule, IServerModuleResidency
{
    public string Id => "relaxkonos.tailscale";
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Tailscale];
    public IReadOnlyList<string> Requires => [];
    public IServerModuleRuntimeFactory RuntimeFactory { get; } = new TailscaleModuleRuntimeFactory();
    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        builder.Services.AddSingleton<ITailscaleManager>(services =>
            ModuleServiceProxy<ITailscaleManager>.Create(services.GetRequiredService<ModuleResidencyCoordinator>(), Id));
        builder.Services.AddSingleton<ITailscaleControl>(services =>
            ModuleServiceProxy<ITailscaleControl>.Create(services.GetRequiredService<ModuleResidencyCoordinator>(), Id));
        builder.Services.AddSingleton<ITailscaleLogin>(services =>
            ModuleServiceProxy<ITailscaleLogin>.Create(services.GetRequiredService<ModuleResidencyCoordinator>(), Id));
    }
    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        endpoints.MapGet(TailscaleEndpoints.Status, async (ITailscaleManager manager, HttpContext http,
                RelaxKonOS.Server.Privileged.IHostElevationSessionStore grants, CancellationToken ct) =>
            {
                var status = await manager.GetStatusAsync(ct);
                return grants.IsGranted(http.User, RelaxKonOS.Protocol.Privileged.HostElevationCapability.HostNetworkChange,
                    TailscaleAccess.Resource) ? status : status with
                    {
                        // Peer, account and tailnet identities stay behind the same host-network grant.
                        Peers = null, PeersRestricted = true, Account = null, TailnetName = null,
                        MagicDnsSuffix = null, ExitNode = null,
                        Self = status.Self is { } self ? self with { Owner = null, Tags = null } : null,
                    };
            }).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapGet(TailscaleEndpoints.Control, (ITailscaleControl control, HttpContext http, CancellationToken ct) =>
            Respond(() => control.ReadAsync(http.User, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapPost(TailscaleEndpoints.Operations, (TailscalePrepareRequest request, ITailscaleControl control, HttpContext http, CancellationToken ct) =>
            Respond(() => control.PrepareAsync(http.User, request, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapPost(TailscaleEndpoints.Execute, (Guid operationId, TailscaleExecuteRequest request, ITailscaleControl control, HttpContext http, CancellationToken ct) =>
            Respond(() => control.ExecuteAsync(http.User, operationId, request.Confirmed, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapGet(TailscaleEndpoints.Recovery, (Guid operationId, ITailscaleControl control, HttpContext http, CancellationToken ct) =>
            Respond(() => control.ReadRecoveryAsync(http.User, operationId, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapPost(TailscaleEndpoints.Close, (Guid operationId, TailscaleCloseRequest request, ITailscaleControl control, HttpContext http, CancellationToken ct) =>
            operationId != request.OperationId ? Task.FromResult<IResult>(Results.Problem(statusCode: 400, title: "tailscale.invalid_request", type: "https://relaxkonos.app/problems/tailscale.invalid_request")) : Respond(() => control.CloseAsync(http.User, request, ct)))
            .RequireAuthorization().WithTags("Tailscale");
        endpoints.MapGet(TailscaleEndpoints.Operation, (Guid operationId, ITailscaleControl control, HttpContext http, CancellationToken ct) =>
            Respond(() => control.GetAsync(http.User, operationId, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapPost(TailscaleEndpoints.LoginOperations, (TailscaleLoginPrepareRequest request, ITailscaleLogin login, HttpContext http, CancellationToken ct) =>
            Respond(() => login.PrepareAsync(http.User, request, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapPost(TailscaleEndpoints.LoginExecute, (Guid operationId, TailscaleLoginExecuteRequest request, ITailscaleLogin login, HttpContext http, CancellationToken ct) =>
            Respond(() => login.ExecuteAsync(http.User, operationId, request.Confirmed, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapGet(TailscaleEndpoints.LoginOperation, (Guid operationId, ITailscaleLogin login, HttpContext http, CancellationToken ct) =>
            Respond(() => login.GetAsync(http.User, operationId, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapGet(TailscaleEndpoints.LoginChallenge, async (Guid operationId, ITailscaleLogin login, HttpContext http, CancellationToken ct) =>
            {
                // The challenge is delivered once and must not enter shared caches or history snapshots.
                http.Response.Headers.CacheControl = "no-store";
                return await Respond(() => login.ReadChallengeAsync(http.User, operationId, ct));
            }).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapGet(TailscaleEndpoints.LoginRecovery, (Guid operationId, ITailscaleLogin login, HttpContext http, CancellationToken ct) =>
            Respond(() => login.ReadRecoveryAsync(http.User, operationId, ct))).RequireAuthorization().WithTags("Tailscale");
        endpoints.MapPost(TailscaleEndpoints.LoginClose, (Guid operationId, TailscaleLoginCloseRequest request, ITailscaleLogin login, HttpContext http, CancellationToken ct) =>
            operationId != request.OperationId ? Task.FromResult<IResult>(Results.Problem(statusCode: 400, title: "tailscale.invalid_request", type: "https://relaxkonos.app/problems/tailscale.invalid_request"))
                : Respond(() => login.CloseAsync(http.User, request, ct))).RequireAuthorization().WithTags("Tailscale");
    }

    private static async Task<IResult> Respond<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (TailscaleControlException ex)
        {
            var grantRequired = ex.ProblemCode == "tailscale.elevation_required";
            return Results.Problem(statusCode: ex.StatusCode, title: ex.ProblemCode,
                type: "https://relaxkonos.app/problems/" + (grantRequired ? "elevation-required" : ex.ProblemCode),
                extensions: new Dictionary<string, object?> { ["code"] = ex.ProblemCode });
        }
    }
}


