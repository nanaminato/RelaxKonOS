using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;

namespace RelaxKonOS.Server.Endpoints;

public static class GitBuildEndpoints
{
    public static IEndpointRouteBuilder MapGitBuildEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(GitApiRoutes.Builds).RequireAuthorization().WithTags("GitBuilds");
        group.MapGet("/credentials", (ClaimsPrincipal principal, GitBuildService service) =>
            Handle(() => Results.Ok(service.Credentials(Owner(principal)))));
        group.MapPost("/credentials", (GitBuildCredentialRequest request, ClaimsPrincipal principal, GitBuildService service) =>
            Handle(() => Results.Ok(service.SetCredential(Owner(principal), request))))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        group.MapPost("/resolve", (GitBuildResolveRequest request, ClaimsPrincipal principal, GitBuildService service, CancellationToken ct) =>
            HandleAsync(async () => Results.Ok(await service.ResolveAsync(Owner(principal), request, ct))));
        group.MapPost("/refs", (GitBuildResolveRequest request, ClaimsPrincipal principal, GitBuildService service, CancellationToken ct) =>
            HandleAsync(async () => Results.Ok(await service.RefsAsync(Owner(principal), request, ct))));
        group.MapGet("", (ClaimsPrincipal principal, GitBuildService service) =>
            Handle(() => Results.Ok(service.Builds(Owner(principal)))));
        group.MapPost("", (GitBuildRequest request, HttpContext http, GitBuildService service, CancellationToken ct) =>
            HandleAsync(async () =>
            {
                var operation = await service.StartAsync(Owner(http.User), request, http.Request.Headers["Idempotency-Key"].ToString(), ct);
                return Results.Accepted($"{GitApiRoutes.Builds}/{operation.Id:D}", operation);
            })).RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        group.MapGet("/{id:guid}", (Guid id, ClaimsPrincipal principal, GitBuildService service) =>
            Handle(() => service.Get(Owner(principal), id) is { } operation ? Results.Ok(operation) : Results.NotFound()));
        group.MapPost("/{id:guid}/cancel", (Guid id, ClaimsPrincipal principal, GitBuildService service) =>
            Handle(() => Results.Ok(service.Cancel(Owner(principal), id))))
            .RequireAuthorization(ApplicationDeploymentEndpoints.ManagePolicy);
        return app;
    }

    private static Guid Owner(ClaimsPrincipal user) => Guid.Parse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new UnauthorizedAccessException());

    private static IResult Handle(Func<IResult> action)
    {
        try { return action(); }
        catch (GitBuildProblem error) { return Problem(error); }
    }

    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (GitBuildProblem error) { return Problem(error); }
    }

    private static IResult Problem(GitBuildProblem error) => Results.Problem(statusCode: error.Status,
        title: error.Code, type: "https://relaxkonos.app/problems/" + error.Code,
        extensions: new Dictionary<string, object?> { ["problemCode"] = error.Code });
}
