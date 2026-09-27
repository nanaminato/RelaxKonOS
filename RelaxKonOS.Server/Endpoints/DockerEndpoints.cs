using RelaxKonOS.Protocol.Docker;
using System.Security.Claims;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Endpoints;

public static class DockerEndpoints
{
    public static IEndpointRouteBuilder MapDockerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/docker").RequireAuthorization().WithTags("Docker").RequireHostFeature(ServerHostFeature.Docker);
        group.MapGet("/status", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.GetStatusAsync(ct));
        // Host-wide engine lifecycle. Stopping or restarting the engine terminates every running
        // container, so the request has to carry an explicit confirmation.
        group.MapPost("/engine/{action}", (string action, DockerEngineActionRequest request, RelaxKonOS.Server.Docker.IDockerEngineControlService service, CancellationToken ct) => service.ApplyAsync(action, request.Confirmed, ct));
        group.MapGet("/containers", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ListContainersAsync(ct));
        group.MapGet("/containers/{id}", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.GetContainerAsync(id, ct) is { } details ? Results.Ok(details) : Results.NotFound());
        group.MapPost("/containers", (DockerContainerCreateRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.CreateContainerAsync(request, ct));
        group.MapPut("/containers/{id}", (string id, DockerContainerUpdateRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.UpdateContainerAsync(id, request, ct));
        group.MapPost("/containers/{id}/{action}", (string id, string action, DockerContainerActionRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ApplyContainerActionAsync(id, action, request, ct));
        group.MapGet("/images", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ListImagesAsync(ct));
        group.MapPost("/images/pull", (DockerImageOperationRequest request, ClaimsPrincipal principal, RelaxKonOS.Server.ImageMirrors.IDockerImageMirrorResolver mirrors, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) =>
        {
            var subject = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return !Guid.TryParse(subject, out var userId)
                ? Task.FromResult(new DockerOperationResult(false, "docker.operation_failed"))
                : service.PullImageAsync(request, mirrors.Resolve(userId, request.ImageReference), ct);
        });
        // DELETE endpoints do not infer a complex parameter as a request body.  The client
        // sends image-operation options in the body, so make that binding explicit.
        group.MapDelete("/images/{id}", (string id, [Microsoft.AspNetCore.Mvc.FromBody] DockerImageOperationRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.DeleteImageAsync(id, request, ct));
        group.MapGet("/networks", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ListNetworksAsync(ct));
        group.MapGet("/volumes", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ListVolumesAsync(ct));
        group.MapPost("/networks", (DockerNetworkCreateRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.CreateNetworkAsync(request, ct));
        group.MapGet("/networks/{id}", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.GetNetworkAsync(id, ct) is { } details ? Results.Ok(details) : Results.NotFound());
        group.MapPost("/volumes", (DockerVolumeCreateRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.CreateVolumeAsync(request, ct));
        group.MapGet("/volumes/{name}", async (string name, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.GetVolumeAsync(name, ct) is { } details ? Results.Ok(details) : Results.NotFound());
        group.MapDelete("/networks/{id}", (string id, bool confirmed, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.DeleteNetworkAsync(id, confirmed, ct));
        group.MapDelete("/volumes/{name}", (string name, bool confirmed, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.DeleteVolumeAsync(name, confirmed, ct));
        group.MapGet("/containers/{id}/logs", async (string id, int? tail, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.GetContainerLogsAsync(id, tail ?? 200, ct) is { } logs ? Results.Ok(logs) : Results.NotFound());
        group.MapGet("/containers/{id}/stats", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.GetContainerStatsAsync(id, ct) is { } stats ? Results.Ok(stats) : Results.NotFound());
        group.MapPost("/images/build", (DockerBuildRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.BuildImageAsync(request, cancellationToken: ct));
        group.MapGet("/images/{id}/export", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.ExportImageAsync(id, ct) is { } archive ? Results.Ok(archive) : Results.NotFound());
        group.MapPost("/images/import", (DockerImageArchiveDto archive, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ImportImageAsync(archive, ct));
        group.MapGet("/stacks", (RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => service.ListAsync(ct));
        // A preview parses the definition and changes nothing, so it stays a plain synchronous read of
        // what the Compose parser thinks the document means.
        group.MapPost("/stacks/preview", (DockerStackDefinitionDto definition, RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) =>
            HandleAsync(async () => Results.Ok(await service.PreviewAsync(definition, ct))));
        // Every mutation is a durable operation. The 202 answers with the record that will still be
        // readable after the phone that asked for it is gone or the server has restarted.
        group.MapPost("/stacks/deploy", (DockerStackDeployRequest request, HttpContext http, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => Accepted(coordinator.Deploy(request, Actor(http.User), Key(http)))));
        group.MapGet("/stacks/{name}/operations", (string name, int? limit, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => Results.Ok(coordinator.History(name, limit ?? 20))));
        group.MapGet("/stacks/{name}/operations/active", (string name, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => coordinator.GetActive(name) is { } operation ? Results.Ok(operation) : Results.NotFound()));
        group.MapGet("/stack-operations/{operationId:guid}", (Guid operationId, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => coordinator.Get(operationId) is { } operation ? Results.Ok(operation) : Results.NotFound()));
        // Why a step failed is a read of the same operation, so it stays a separate call: listing
        // operations must not carry command output nobody asked to see.
        group.MapGet("/stack-operations/{operationId:guid}/diagnostics", (Guid operationId, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => Results.Ok(coordinator.Diagnostics(operationId))));
        group.MapPost("/stack-operations/{operationId:guid}/cancel", (Guid operationId, HttpContext http, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => Results.Ok(coordinator.Cancel(operationId, Key(http)))));
        group.MapGet("/stacks/{name}/definition", async (string name, RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => await service.GetDefinitionAsync(name, ct) is { } definition ? Results.Ok(definition) : Results.NotFound());
        group.MapGet("/stacks/{name}/services", (string name, RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => service.ListServicesAsync(name, ct));
        group.MapPost("/stacks/{name}/{action}", (string name, string action, DockerStackActionRequest request, HttpContext http, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => DockerStackActionRoutes.TryParseAction(action, out var kind)
                ? Accepted(coordinator.ApplyAction(name, kind, request.Confirmed, Actor(http.User), Key(http)))
                : Problem(RelaxKonOS.Server.Docker.DockerStackProblem.ValidationFailed, 400)));
        return app;
    }

    /// <summary>An idempotency key is mandatory for every action that can change the host.</summary>
    private static string Key(HttpContext http) => http.Request.Headers["Idempotency-Key"].ToString();

    /// <summary>The actor is a stable identifier, never a display name, and is only ever stored hashed.</summary>
    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException();

    private static IResult Accepted(DockerStackOperationDto operation) =>
        Results.Accepted(DockerApiRoutes.StackOperation(operation.OperationId), operation);

    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (RelaxKonOS.Server.Docker.DockerStackException error) { return Problem(error.ProblemCode, error.StatusCode); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RelaxKonOS.Server.Docker.DockerStackException error) { return Problem(error.ProblemCode, error.StatusCode); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }

    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code,
        type: "https://relaxkonos.app/problems/" + code, extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
