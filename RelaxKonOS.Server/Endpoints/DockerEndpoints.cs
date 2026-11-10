using RelaxKonOS.Server.Startup;
using RelaxKonOS.Protocol.Docker;
using System.Security.Claims;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Residency;

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
        group.MapGet("/containers", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => ReadAsync(() => service.ListContainersAsync(ct)));
        group.MapGet("/containers/{id}", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await HandleAsync(async () => await service.GetContainerAsync(id, ct) is { } details ? Results.Ok(details) : Results.NotFound()));
        group.MapPost("/containers", (DockerContainerCreateRequest request, IDockerEngineService service, CancellationToken ct) =>
            ReadAsync(() => DockerApplicationResourcePolicy.CreateContainerAsync(service, request, ct)));
        group.MapPut("/containers/{id}", (string id, DockerContainerUpdateRequest request, IDockerEngineService service, CancellationToken ct) =>
            ReadAsync(() => DockerApplicationResourcePolicy.ChangeContainerAsync(service, id, request.Name,
                resolved => service.UpdateContainerAsync(resolved, request, ct), ct)));
        group.MapPost("/containers/{id}/{action}", (string id, string action, DockerContainerActionRequest request, IDockerEngineService service, CancellationToken ct) =>
            ReadAsync(() => DockerApplicationResourcePolicy.ChangeContainerAsync(service, id, null,
                resolved => service.ApplyContainerActionAsync(resolved, action, request, ct), ct)));
        group.MapGet("/images", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => ReadAsync(() => service.ListImagesAsync(ct)));
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
        group.MapGet("/networks", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => ReadAsync(() => service.ListNetworksAsync(ct)));
        group.MapGet("/volumes", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => ReadAsync(() => service.ListVolumesAsync(ct)));
        group.MapPost("/networks", (DockerNetworkCreateRequest request, IDockerEngineService service, CancellationToken ct) =>
            DockerApplicationResourcePolicy.CreateNetworkAsync(service, request, ct));
        group.MapGet("/networks/{id}", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await HandleAsync(async () => await service.GetNetworkAsync(id, ct) is { } details ? Results.Ok(details) : Results.NotFound()));
        group.MapPost("/volumes", (DockerVolumeCreateRequest request, IDockerEngineService service, CancellationToken ct) =>
            DockerApplicationResourcePolicy.CreateVolumeAsync(service, request, ct));
        group.MapGet("/volumes/{name}", async (string name, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await HandleAsync(async () => await service.GetVolumeAsync(name, ct) is { } details ? Results.Ok(details) : Results.NotFound()));
        group.MapDelete("/networks/{id}", (string id, bool confirmed, IDockerEngineService service, CancellationToken ct) =>
            ReadAsync(() => DockerApplicationResourcePolicy.DeleteNetworkAsync(service, id, confirmed, ct)));
        group.MapDelete("/volumes/{name}", (string name, bool confirmed, IDockerEngineService service, CancellationToken ct) =>
            ReadAsync(() => DockerApplicationResourcePolicy.DeleteVolumeAsync(service, name, confirmed, ct)));
        group.MapGet("/containers/{id}/logs", async (string id, int? tail, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.GetContainerLogsAsync(id, tail ?? 200, ct) is { } logs ? Results.Ok(logs) : Results.NotFound());
        group.MapGet("/containers/{id}/stats", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await HandleAsync(async () => await service.GetContainerStatsAsync(id, ct) is { } stats ? Results.Ok(stats) : Results.NotFound()));
        group.MapPost("/images/build", (DockerBuildRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.BuildImageAsync(request, cancellationToken: ct));
        group.MapGet("/images/{id}/export", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.ExportImageAsync(id, ct) is { } archive ? Results.Ok(archive) : Results.NotFound());
        group.MapPost("/images/import", (DockerImageArchiveDto archive, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ImportImageAsync(archive, ct));
        group.MapGet("/stacks", (HttpContext http) => ComposeReadAsync(http,
            static async (compose, ct) => Results.Ok(await compose.ListAsync(ct))));
        // Preview is a read of the actual Compose parser, with its own process and temporary file.
        group.MapPost("/stacks/preview", (DockerStackDefinitionDto definition, HttpContext http) => ComposeReadAsync(http,
            async (compose, ct) => Results.Ok(await compose.PreviewAsync(definition, ct))));
        // Every mutation is a durable operation. The 202 answers with the record that will still be
        // readable after the phone that asked for it is gone or the server has restarted.
        group.MapPost("/stacks/deploy", (DockerStackDeployRequest request, HttpContext http, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator, CancellationToken ct) =>
            HandleAsync(async () => Accepted(await coordinator.DeployAsync(request, Actor(http.User), Key(http), ct), coordinator)));
        group.MapGet("/stacks/{name}/operations", (string name, int? limit, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => Results.Ok(coordinator.History(name, limit ?? 20))));
        group.MapGet("/stacks/{name}/operations/active", (string name, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => coordinator.GetActive(name) is { } operation ? Results.Ok(operation) : Results.NotFound()));
        group.MapGet("/stack-operations/{operationId:guid}", (Guid operationId, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => coordinator.Get(operationId) is { } operation ? Results.Ok(operation) : Results.NotFound()));
        group.MapGet("/stack-operations/by-request", (HttpContext http, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => coordinator.GetByRequest(Actor(http.User), Key(http)) is { } operation ? Results.Ok(operation) : Results.NotFound()));
        // Why a step failed is a read of the same operation, so it stays a separate call: listing
        // operations must not carry command output nobody asked to see.
        group.MapGet("/stack-operations/{operationId:guid}/diagnostics", (Guid operationId, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => Results.Ok(coordinator.Diagnostics(operationId))));
        group.MapPost("/stack-operations/{operationId:guid}/cancel", (Guid operationId, HttpContext http, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator) =>
            Handle(() => Results.Ok(coordinator.Cancel(operationId, Key(http)))));
        group.MapGet("/stacks/{name}/definition", (string name, HttpContext http) => ComposeReadAsync(http,
            async (compose, ct) => await compose.GetDefinitionAsync(name, ct) is { } definition ? Results.Ok(definition) : Results.NotFound()));
        group.MapGet("/stacks/{name}/services", (string name, HttpContext http) => ComposeReadAsync(http,
            async (compose, ct) => Results.Ok(await compose.ListServicesAsync(name, ct))));
        group.MapPost("/stacks/{name}/{action}", (string name, string action, DockerStackActionRequest request, HttpContext http, RelaxKonOS.Server.Docker.DockerStackOperationCoordinator coordinator, CancellationToken ct) =>
            HandleAsync(async () => DockerStackActionRoutes.TryParseAction(action, out var kind)
                ? Accepted(await coordinator.ApplyActionAsync(name, kind, request.Confirmed, Actor(http.User), Key(http), ct), coordinator)
                : Problem(RelaxKonOS.Server.Docker.DockerStackProblem.ValidationFailed, 400)));
        return app;
    }

    private static Task ComposeReadAsync(HttpContext context, Func<IDockerComposeService, CancellationToken, Task<IResult>> read) =>
        ResidentScopedResponse.ExecuteAsync(context, context.RequestServices.GetRequiredService<IResidentModuleWorkLeaseSource>(), ServerModules.DockerId,
            (services, ct) => HandleAsync(() => read(services.GetRequiredService<IDockerComposeService>(), ct)),
            static error => error is DockerComposeCleanupException);

    /// <summary>An idempotency key is mandatory for every action that can change the host.</summary>
    private static string Key(HttpContext http) => http.Request.Headers["Idempotency-Key"].ToString();

    /// <summary>The actor is a stable identifier, never a display name, and is only ever stored hashed.</summary>
    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException();

    private static IResult Accepted(DockerStackOperationDto operation, DockerStackOperationCoordinator coordinator)
    {
        var receipt = Results.Accepted(DockerApiRoutes.StackOperation(operation.OperationId), operation);
        // The original work owns the cleanup gate. Reading its durable receipt requires no new work.
        return coordinator.HoldsCleanupResponsibility(operation.OperationId) ? receipt : new ResidentOperationResult(ServerModules.DockerId, receipt);
    }

    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (RelaxKonOS.Server.Docker.DockerStackException error) { return Problem(error.ProblemCode, error.StatusCode); }
        catch (RelaxKonOS.Server.Docker.DockerReadException error) { return Problem(error.ProblemCode, StatusCodes.Status503ServiceUnavailable); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RelaxKonOS.Server.Docker.DockerStackException error) { return Problem(error.ProblemCode, error.StatusCode); }
        catch (RelaxKonOS.Server.Docker.DockerReadException error) { return Problem(error.ProblemCode, StatusCodes.Status503ServiceUnavailable); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    }

    private static async Task<IResult> ReadAsync<T>(Func<Task<T>> read) => await HandleAsync(async () => Results.Ok(await read()));

    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code,
        type: "https://relaxkonos.app/problems/" + code, extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
