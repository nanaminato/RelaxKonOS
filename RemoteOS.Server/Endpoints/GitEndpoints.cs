using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RemoteOS.Protocol.Git;
using RemoteOS.Protocol.Privileged;
using Server.Privileged;

namespace Server.Endpoints;

public static class GitEndpoints
{
    private const string ProblemBase = "https://remoteos.app/problems/git-";

    public static IEndpointRouteBuilder MapGitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup($"/{RemoteOS.Protocol.Common.RemoteOsEndpoints.ApiVersionPrefix}/git").RequireAuthorization().WithTags("Git");

        // ── Host Git engine probe & install (host-level, user-agnostic) ──
        group.MapGet("/engine/status", (Server.Git.IGitRepositoryService service, CancellationToken ct) =>
            service.GetEngineStatusAsync(ct));

        group.MapPost("/engine/install", async (HttpContext http, IHostElevationSessionStore elevations, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(http.User, HostElevationCapability.GitPackageInstall, "git"))
                return Results.Problem(statusCode: 403, title: "需要管理员权限", detail: "Git 安装需要当前会话的管理员授权。", type: "https://remoteos.app/problems/elevation-required");
            try { return Results.Ok(await service.InstallEngineAsync(ct)); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git install", type: ProblemBase + "install-failed"); }
        });

        // ── Repository registration ──
        group.MapGet("/repositories", (ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
            service.ListRepositoriesAsync(GetUserId(principal), ct));

        group.MapPost("/repositories", async (GitRepositoryRegistration registration, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            try
            {
                var dto = await service.RegisterRepositoryAsync(registration, GetUserId(principal), ct);
                return Results.Ok(dto);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 400, title: "Invalid repository", type: ProblemBase + "invalid-repository");
            }
        });

        group.MapGet("/repositories/{id}", async (string id, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            var dto = await service.GetRepositoryAsync(repoId, GetUserId(principal), ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        group.MapDelete("/repositories/{id}", async (string id, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            var deleted = await service.UnregisterRepositoryAsync(repoId, GetUserId(principal), ct);
            return deleted ? Results.Ok() : Results.NotFound();
        });

        // ── Real-time git operations ──
        group.MapGet("/repositories/{id}/status", async (string id, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.GetStatusAsync(repoId, GetUserId(principal), ct)); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git error", type: ProblemBase + "status-failed"); }
        });

        group.MapGet("/repositories/{id}/branches", async (string id, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.ListBranchesAsync(repoId, GetUserId(principal), ct)); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git error", type: ProblemBase + "branches-failed"); }
        });

        group.MapGet("/repositories/{id}/branches/{name}/comparison", async (string id, string name, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.CompareBranchAsync(repoId, GetUserId(principal), name, ct)); }
            catch (ArgumentException ex) { return Results.Problem(detail: ex.Message, statusCode: 400, title: "Invalid branch", type: ProblemBase + "invalid-branch"); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git error", type: ProblemBase + "branch-comparison-failed"); }
        });

        group.MapPost("/repositories/{id}/branches", async (string id, GitBranchCreateRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.CreateBranchAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapDelete("/repositories/{id}/branches/{name}", async (string id, string name, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.DeleteBranchAsync(repoId, GetUserId(principal), name, ct));
        });

        group.MapPost("/repositories/{id}/branches/{name}/rename", async (string id, string name, GitBranchRenameRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.RenameBranchAsync(repoId, GetUserId(principal), name, request, ct));
        });

        group.MapPut("/repositories/{id}/branches/{name}/tracking", async (string id, string name, GitBranchTrackingRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.SetBranchTrackingAsync(repoId, GetUserId(principal), name, request, ct));
        });

        group.MapPost("/repositories/{id}/checkout", async (string id, GitCheckoutRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.CheckoutAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/commit", async (string id, GitCommitRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.CommitAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/merge", async (string id, GitMergeRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.MergeBranchAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/fetch", async (string id, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.FetchAsync(repoId, GetUserId(principal), ct));
        });

        group.MapPost("/repositories/{id}/pull", async (string id, GitPullRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.PullAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/push", async (string id, GitPushRequest? request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.PushAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapGet("/repositories/{id}/log", async (string id, int? limit, int? skip, string? reference, string? search,
            string? author, string? path, string? dateRange, bool? caseSensitive, bool? useRegex,
            ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            var query = new GitLogQuery(reference, search, author, path, dateRange, caseSensitive ?? false, useRegex ?? false);
            try { return Results.Ok(await service.GetLogAsync(repoId, GetUserId(principal), limit ?? 200, skip ?? 0, query, ct)); }
            catch (ArgumentException ex) { return Results.Problem(detail: ex.Message, statusCode: 400, title: "Invalid log filter", type: ProblemBase + "invalid-log-filter"); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git error", type: ProblemBase + "log-failed"); }
        });

        group.MapGet("/repositories/{id}/commits/{sha}", async (string id, string sha, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.GetCommitDetailAsync(repoId, GetUserId(principal), sha, ct)); }
            catch (ArgumentException ex) { return Results.Problem(detail: ex.Message, statusCode: 400, title: "Invalid commit", type: ProblemBase + "invalid-commit"); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 404, title: "Git error", type: ProblemBase + "commit-not-found"); }
        });

        group.MapGet("/repositories/{id}/diff", async (string id, string path, bool? staged, string? @ref, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.GetDiffAsync(repoId, GetUserId(principal), path, staged ?? false, @ref, ct)); }
            catch (ArgumentException ex) { return Results.Problem(detail: ex.Message, statusCode: 400, title: "Invalid path", type: ProblemBase + "invalid-path"); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git error", type: ProblemBase + "diff-failed"); }
        });

        group.MapPost("/repositories/{id}/revert", async (string id, GitRevertRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.RevertAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/resolve", async (string id, GitResolveRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.ResolveConflictsAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/reset", async (string id, GitResetRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.ResetAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/restore", async (string id, GitRestoreRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.RestoreAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/stage", async (string id, GitStageRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.StageAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPost("/repositories/{id}/unstage", async (string id, GitUnstageRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.UnstageAsync(repoId, GetUserId(principal), request, ct));
        });

        // ── 路径探测与初始化（不依赖已注册仓库）──
        group.MapGet("/probe", async (string path, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ProbeRepositoryAsync(path, GetUserId(principal), ct)); }
            catch (ArgumentException ex) { return Results.Problem(detail: ex.Message, statusCode: 400, title: "Invalid path", type: ProblemBase + "invalid-path"); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git error", type: ProblemBase + "probe-failed"); }
        });

        group.MapPost("/init", async (string path, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.InitRepositoryAsync(path, GetUserId(principal), ct)); }
            catch (ArgumentException ex) { return Results.Problem(detail: ex.Message, statusCode: 400, title: "Invalid path", type: ProblemBase + "invalid-path"); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git error", type: ProblemBase + "init-failed"); }
        });

        // ── 远程（remote）管理 ──
        group.MapGet("/repositories/{id}/remotes", async (string id, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            try { return Results.Ok(await service.ListRemotesAsync(repoId, GetUserId(principal), ct)); }
            catch (InvalidOperationException ex) { return Results.Problem(detail: ex.Message, statusCode: 500, title: "Git error", type: ProblemBase + "remotes-failed"); }
        });

        group.MapPost("/repositories/{id}/remotes", async (string id, GitRemoteRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.AddRemoteAsync(repoId, GetUserId(principal), request, ct));
        });

        group.MapPut("/repositories/{id}/remotes/{name}", async (string id, string name, GitRemoteRequest request, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.UpdateRemoteAsync(repoId, GetUserId(principal), name, request, ct));
        });

        group.MapDelete("/repositories/{id}/remotes/{name}", async (string id, string name, ClaimsPrincipal principal, Server.Git.IGitRepositoryService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var repoId)) return Results.BadRequest();
            return Results.Ok(await service.RemoveRemoteAsync(repoId, GetUserId(principal), name, ct));
        });

        return app;
    }

    private static Guid GetUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? throw new InvalidOperationException("JWT missing sub claim.");
        return Guid.Parse(sub);
    }
}
