using System.Security.Claims;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Runtimes;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.Tunnels;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Tunnel API. All mutations are server-authorized and all error payloads carry only stable problem codes.</summary>
public static class TunnelEndpoints
{
    public static IEndpointRouteBuilder MapTunnelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(TunnelApiRoutes.Tunnels).RequireAuthorization().WithTags("Tunnels");
        group.MapGet(TunnelApiRoutes.ProfilesPattern, (ClaimsPrincipal user, ITunnelService service, CancellationToken ct) => service.ListProfilesAsync(UserId(user), ct)).RequireAuthorization("TunnelsRead");
        group.MapGet(TunnelApiRoutes.ProfilePattern, async (Guid profileId, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await service.GetProfileAsync(profileId, UserId(user), ct) is { } value ? Results.Ok(value) : Results.NotFound()).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.ProfilesPattern, async (UpsertTunnelServerProfileRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleAsync(() => service.UpsertProfileAsync(null, request, UserId(user), ct), created: true)).RequireAuthorization("TunnelsManage");
        group.MapPut(TunnelApiRoutes.ProfilePattern, async (Guid profileId, UpsertTunnelServerProfileRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleAsync(() => service.UpsertProfileAsync(profileId, request, UserId(user), ct))).RequireAuthorization("TunnelsManage");
        group.MapDelete(TunnelApiRoutes.ProfilePattern, async (Guid profileId, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleDeleteAsync(() => service.DeleteProfileAsync(profileId, UserId(user), ct))).RequireAuthorization("TunnelsManage");
        // This dedicated write-only endpoint is the sole credential ingress. Its response never reflects the request body.
        group.MapPut(TunnelApiRoutes.ProfileSecretPattern, async (Guid profileId, SetTunnelProfileTokenRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
        {
            try { await service.SetProfileTokenAsync(profileId, request.Token, UserId(user), ct); return Results.NoContent(); }
            catch (TunnelNotFoundException) { return Results.NotFound(); }
            catch (SecretValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status400BadRequest); }
            catch (TunnelValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status400BadRequest); }
        }).RequireAuthorization("TunnelsManage");
        group.MapGet(TunnelApiRoutes.ProfileLogsPattern, async (Guid profileId, ClaimsPrincipal user, ITunnelProvider provider, CancellationToken ct) =>
            await provider.GetLogsAsync(profileId, UserId(user), ct) is { } logs ? Results.Ok(logs) : Results.NotFound()).RequireAuthorization("TunnelsRead");

        // Provider list augments Desired State with the host-local frpc snapshot. Reading via
        // ITunnelService here would incorrectly label every running tunnel SavedNotApplied.
        group.MapGet(TunnelApiRoutes.CollectionPattern, (ClaimsPrincipal user, ITunnelProvider provider, CancellationToken ct) => provider.ListAsync(UserId(user), ct)).RequireAuthorization("TunnelsRead");
        group.MapGet(TunnelApiRoutes.TunnelPattern, async (Guid tunnelId, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await service.GetTunnelAsync(tunnelId, UserId(user), ct) is { } value ? Results.Ok(value) : Results.NotFound()).RequireAuthorization("TunnelsRead");
        group.MapPost(TunnelApiRoutes.CollectionPattern, async (UpsertTunnelDefinitionRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleAsync(() => service.UpsertTunnelAsync(null, request, UserId(user), ct), created: true)).RequireAuthorization("TunnelsManage");
        group.MapPut(TunnelApiRoutes.TunnelPattern, async (Guid tunnelId, UpsertTunnelDefinitionRequest request, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleAsync(() => service.UpsertTunnelAsync(tunnelId, request, UserId(user), ct))).RequireAuthorization("TunnelsManage");
        group.MapDelete(TunnelApiRoutes.TunnelPattern, async (Guid tunnelId, ClaimsPrincipal user, ITunnelService service, CancellationToken ct) =>
            await HandleDeleteAsync(() => service.DeleteTunnelAsync(tunnelId, UserId(user), ct))).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.ApplyProfilePattern, (Guid profileId, ClaimsPrincipal user, ITunnelProvider provider, CancellationToken ct) => provider.ApplyAsync(profileId, UserId(user), ct)).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.StopProfilePattern, (Guid profileId, ClaimsPrincipal user, ITunnelProvider provider, CancellationToken ct) => provider.StopAsync(profileId, UserId(user), ct)).RequireAuthorization("TunnelsManage");
        group.MapGet(TunnelApiRoutes.ManagedFrpsPattern, (IManagedFrpsService frps, CancellationToken ct) => frps.GetAsync(ct)).RequireAuthorization("TunnelsRead");
        group.MapGet(TunnelApiRoutes.ManagedFrpsEditorPattern, (ClaimsPrincipal user, IManagedFrpsService frps, CancellationToken ct) => frps.GetForEditingAsync(UserId(user), ct)).RequireAuthorization("TunnelsManage");
        group.MapPut(TunnelApiRoutes.ManagedFrpsPattern, async (UpdateManagedFrpsConfigurationRequest request, ClaimsPrincipal user, IManagedFrpsService frps, CancellationToken ct) =>
        {
            try { return Results.Ok(await frps.UpdateAsync(request, UserId(user), ct)); }
            catch (ManagedFrpsValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status400BadRequest); }
        }).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.ManagedFrpsStartPattern, (ClaimsPrincipal user, IManagedFrpsService frps, CancellationToken ct) => frps.StartAsync(UserId(user), ct)).RequireAuthorization("TunnelsManage");
        group.MapPost(TunnelApiRoutes.ManagedFrpsStopPattern, (ClaimsPrincipal user, IManagedFrpsService frps, CancellationToken ct) => frps.StopAsync(UserId(user), ct)).RequireAuthorization("TunnelsManage");
        group.MapGet(TunnelApiRoutes.ManagedFrpsLogsPattern, (IManagedFrpsService frps, CancellationToken ct) => frps.GetLogsAsync(ct)).RequireAuthorization("TunnelsRead");
        group.MapGet(TunnelApiRoutes.ManagedFrpsAuditPattern, (ITunnelAudit audit, CancellationToken ct) => audit.ListFrpsAsync(ct)).RequireAuthorization("TunnelsRead");
        // Patterns registered on a route group must be relative to that group.  Using the
        // public, absolute client route here would duplicate the API prefix inside this group.
        // and make the advertised runtime API return 404.
        group.MapGet(TunnelApiRoutes.RuntimePattern, (IRuntimeManager runtime, CancellationToken ct) => runtime.GetManagedFrpcStatusAsync(ct)).RequireAuthorization("TunnelsRead");
        group.MapPost(TunnelApiRoutes.RuntimeDetectExternalPattern, (DetectExternalTunnelRuntimeRequest request, IRuntimeManager runtime, CancellationToken ct) => runtime.DetectExternalFrpcAsync(request.ExecutablePath, ct)).RequireAuthorization("TunnelsManage");
        return app;
    }

    private static async Task<IResult> HandleAsync<T>(Func<Task<T>> operation, bool created = false)
    {
        try { return created ? Results.Created("", await operation()) : Results.Ok(await operation()); }
        catch (TunnelNotFoundException) { return Results.NotFound(); }
        catch (TunnelRevisionConflictException) { return Problem("tunnel.revision_conflict", StatusCodes.Status409Conflict); }
        catch (TunnelValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status400BadRequest); }
    }
    private static async Task<IResult> HandleDeleteAsync(Func<Task<bool>> operation)
    {
        try { return await operation() ? Results.NoContent() : Results.NotFound(); }
        catch (TunnelValidationException ex) { return Problem(ex.ProblemCode, StatusCodes.Status409Conflict); }
    }
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code, type: $"https://relaxkonos.app/problems/{code}", extensions: new Dictionary<string, object?> { ["problemCode"] = code });
    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException();
}
