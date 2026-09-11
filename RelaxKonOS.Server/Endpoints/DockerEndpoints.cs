using RelaxKonOS.Protocol.Docker;
using System.Security.Claims;

namespace RelaxKonOS.Server.Endpoints;

public static class DockerEndpoints
{
    public static IEndpointRouteBuilder MapDockerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/docker").RequireAuthorization().WithTags("Docker");
        group.MapGet("/status", (RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapPost("/installation/plan", (RelaxKonOS.Server.Docker.IDockerRuntimeInstaller installer, CancellationToken ct) => installer.CreatePlanAsync(ct));
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
        group.MapPost("/images/build", (DockerBuildRequest request, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.BuildImageAsync(request, ct));
        group.MapGet("/images/{id}/export", async (string id, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => await service.ExportImageAsync(id, ct) is { } archive ? Results.Ok(archive) : Results.NotFound());
        group.MapPost("/images/import", (DockerImageArchiveDto archive, RelaxKonOS.Server.Docker.IDockerEngineService service, CancellationToken ct) => service.ImportImageAsync(archive, ct));
        group.MapGet("/stacks", (RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => service.ListAsync(ct));
        group.MapPost("/stacks/validate", (DockerStackDefinitionDto definition, RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => service.ValidateAsync(definition, ct));
        group.MapPost("/stacks/deploy", (DockerStackDefinitionDto definition, RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => service.DeployAsync(definition, ct));
        group.MapGet("/stacks/{name}/definition", async (string name, RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => await service.GetDefinitionAsync(name, ct) is { } definition ? Results.Ok(definition) : Results.NotFound());
        group.MapGet("/stacks/{name}/services", (string name, RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => service.ListServicesAsync(name, ct));
        group.MapPost("/stacks/{name}/{action}", (string name, string action, DockerStackActionRequest request, RelaxKonOS.Server.Docker.IDockerComposeService service, CancellationToken ct) => service.ApplyActionAsync(name, action, request, ct));
        return app;
    }
}
