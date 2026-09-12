using System.Security.Claims;
using System.Text.Json;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Endpoints;

public static class InstallationEndpoints
{
    public static IEndpointRouteBuilder MapInstallationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(InstallationApiRoutes.Root).RequireAuthorization().WithTags("Installations");
        group.MapPost(InstallationApiRoutes.StartPattern, (string service, string kind, JsonElement request,
            HttpContext http, InstallationCoordinator coordinator, IHostElevationSessionStore elevations) => Handle(() =>
        {
            if (!TryEnum(service, out InstallationServiceId id) || !TryEnum(kind, out InstallationOperationKind operationKind))
                return Problem(InstallationProblemCodes.InvalidRequest, 400);
            if (!CanInstall(http.User, id)) return Problem("installation.permission_denied", 403);
            if (!elevations.IsGranted(http.User, Capability(id), Target(id))) return Problem("elevation-required", 403);
            var operation = coordinator.Start(id, operationKind, request, Actor(http.User), http.Request.Headers["Idempotency-Key"].ToString());
            return Results.Accepted(InstallationApiRoutes.Operation(operation.OperationId), operation);
        }));
        group.MapPost(InstallationApiRoutes.FileReferencePattern, (string service, CreateInstallationFileReferenceRequest request,
            HttpContext http, InstallationFileReferenceStore references) => Handle(() =>
        {
            if (!TryEnum(service, out InstallationServiceId id) || !CanInstall(http.User, id))
                return Problem("installation.permission_denied", 403);
            return Results.Ok(references.Create(id, Actor(http.User), request.Path));
        }));
        group.MapGet(InstallationApiRoutes.ActivePattern, (string service, HttpContext http, InstallationCoordinator coordinator) => Handle(() =>
        {
            if (!TryEnum(service, out InstallationServiceId id)) return Problem(InstallationProblemCodes.InvalidRequest, 400);
            var entry = coordinator.GetActive(id, Actor(http.User), CanInstall(http.User, id));
            return entry is null ? Results.NotFound() : Results.Ok(entry.Operation);
        }));
        group.MapGet(InstallationApiRoutes.OperationPattern, (Guid operationId, HttpContext http, InstallationCoordinator coordinator) => Handle(() =>
        {
            var entry = coordinator.Get(operationId);
            return entry is null || !CanObserve(http.User, entry) ? Results.NotFound() : Results.Ok(entry.Operation);
        }));
        group.MapPost(InstallationApiRoutes.CancelPattern, (Guid operationId, HttpContext http, InstallationCoordinator coordinator) => Handle(() =>
        {
            var key = http.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 128) return Problem(InstallationProblemCodes.IdempotencyRequired, 400);
            var entry = coordinator.Get(operationId);
            return entry is null || !CanObserve(http.User, entry) ? Results.NotFound() : Results.Ok(coordinator.Cancel(operationId));
        }));
        return app;
    }

    // A service-specific install claim can restrict future administrator roles. Ordinary app grants are never consulted.
    public static bool CanInstall(ClaimsPrincipal user, InstallationServiceId service) =>
        (user.IsInRole("controller") || user.HasClaim("role", "controller"))
        && (!user.HasClaim(x => x.Type == "installation:install") || user.HasClaim("installation:install", Target(service)));
    private static bool CanObserve(ClaimsPrincipal user, InstallationEntry entry) =>
        entry.ActorReference == InstallationOperationStore.Reference(Actor(user)) || CanInstall(user, entry.Operation.Service);
    public static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException();
    private static bool TryEnum<T>(string value, out T result) where T : struct, Enum => Enum.TryParse(value, true, out result)
        && Enum.IsDefined(result) && !int.TryParse(value, out _);
    public static string Target(InstallationServiceId service) => service == InstallationServiceId.Frp ? "frp" : service.ToString().ToLowerInvariant();
    public static HostElevationCapability Capability(InstallationServiceId service) => service switch
    {
        InstallationServiceId.Smb => HostElevationCapability.SmbInstall,
        InstallationServiceId.Nginx => HostElevationCapability.NginxInstall,
        InstallationServiceId.Frp => HostElevationCapability.FrpInstall,
        InstallationServiceId.Mihomo => HostElevationCapability.MihomoInstall,
        InstallationServiceId.Docker => HostElevationCapability.DockerInstall,
        InstallationServiceId.Git => HostElevationCapability.GitPackageInstall,
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };
    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (InstallationException error) { return Problem(error.ProblemCode, error.StatusCode); }
    }
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code,
        type: "https://relaxkonos.app/problems/" + code, extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
