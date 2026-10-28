using System.Security.Claims;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// Git build ledger and credential routes. Domain failures (<see cref="GitBuildProblem"/>) are not
/// translated here: they carry their own problem code, and the request pipeline maps every
/// <see cref="IProblemException"/> through <see cref="ProblemMapping"/>.
/// </summary>
public static class GitBuildEndpoints
{
    public static IEndpointRouteBuilder MapGitBuildEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(GitApiRoutes.Builds).RequireAuthorization().WithTags("GitBuilds");
        group.MapGet("/credentials", (ClaimsPrincipal principal, IGitBuildService service) =>
            Results.Ok(service.Credentials(principal.UserId())));
        group.MapPost("/credentials", (GitBuildCredentialRequest request, ClaimsPrincipal principal, IGitBuildService service) =>
            Results.Ok(service.SetCredential(principal.UserId(), request)))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        group.MapPost("/resolve", async (GitBuildResolveRequest request, ClaimsPrincipal principal,
            IGitBuildService service, CancellationToken ct) =>
            Results.Ok(await service.ResolveAsync(principal.UserId(), request, ct)));
        group.MapPost("/refs", async (GitBuildResolveRequest request, ClaimsPrincipal principal,
            IGitBuildService service, CancellationToken ct) =>
            Results.Ok(await service.RefsAsync(principal.UserId(), request, ct)));
        group.MapGet("", (ClaimsPrincipal principal, IGitBuildService service) =>
            Results.Ok(service.Builds(principal.UserId())));
        group.MapPost("", async (GitBuildRequest request, HttpContext http, IGitBuildService service, CancellationToken ct) =>
        {
            var operation = await service.StartAsync(http.User.UserId(), request,
                http.Request.Headers["Idempotency-Key"].ToString(), ct);
            return Results.Accepted($"{GitApiRoutes.Builds}/{operation.Id:D}", operation);
        }).RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        group.MapGet("/{id:guid}", (Guid id, ClaimsPrincipal principal, IGitBuildService service) =>
            service.Get(principal.UserId(), id) is { } operation ? Results.Ok(operation) : Results.NotFound());
        group.MapPost("/{id:guid}/cancel", (Guid id, ClaimsPrincipal principal, IGitBuildService service) =>
            Results.Ok(service.Cancel(principal.UserId(), id)))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        return app;
    }
}
