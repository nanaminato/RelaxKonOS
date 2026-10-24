using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;

namespace RelaxKonOS.Server.Endpoints;

public static class GitWorkspaceEndpoints
{
    public static void MapGitWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(GitApiRoutes.Graph, async (string id, string? reference, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.GetGraphAsync(repoId, UserId(principal), reference, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git reference", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git read failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapGet(GitApiRoutes.Console, async (string id, long? after, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.GetConsoleAsync(repoId, UserId(principal), after ?? 0, ct)); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git read failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapGet(GitApiRoutes.ListTags, async (string id, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.ListTagsAsync(repoId, UserId(principal), ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.CreateTag, async (string id, GitTagCreateRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.CreateTagAsync(repoId, UserId(principal), request, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.Rebase, async (string id, GitReferenceRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.RebaseAsync(repoId, UserId(principal), request, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.CherryPick, async (string id, GitReferenceRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.CherryPickAsync(repoId, UserId(principal), request, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapGet(GitApiRoutes.ListStashes, async (string id, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.ListStashesAsync(repoId, UserId(principal), ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.Stash, async (string id, GitStashRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.StashAsync(repoId, UserId(principal), request, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapGet(GitApiRoutes.ListWorktrees, async (string id, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.ListWorktreesAsync(repoId, UserId(principal), ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.CreateWorktree, async (string id, GitWorktreeCreateRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.CreateWorktreeAsync(repoId, UserId(principal), request, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.RemoveWorktree, async (string id, GitWorktreeRemoveRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.RemoveWorktreeAsync(repoId, UserId(principal), request, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.ExportPatch, async (string id, GitPatchRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.ExportPatchAsync(repoId, UserId(principal), request, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.ApplyPatch, async (string id, GitPatchApplyRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.ApplyPatchAsync(repoId, UserId(principal), request, ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
        app.MapPost(GitApiRoutes.Clone, async (GitCloneRequest request, ClaimsPrincipal principal, IGitRepositoryService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.CloneAsync(request, UserId(principal), ct)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: "Invalid Git request", type: "https://relaxkonos.app/problems/git-workspace-invalid", detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: "Git operation failed", type: "https://relaxkonos.app/problems/git-workspace-failed", detail: ex.Message); }
        }).RequireAuthorization().WithTags("Git");
    }
    private static Guid UserId(ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
