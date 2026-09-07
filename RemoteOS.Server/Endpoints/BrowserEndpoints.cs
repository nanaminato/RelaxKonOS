using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RemoteOS.Protocol.Browser;
using Server.ConfigurationRegistry;
using Server.Storage;

namespace Server.Endpoints;

/// <summary>浏览器书签/历史记录 REST 端点。路由常量见 <see cref="BrowserApiRoutes"/>。所有端点需 JWT（[Authorize]）。
/// 数据按 JWT sub claim 限定到当前用户。错误统一返回 RFC 7807 ProblemDetails（type URI 作错误码）。</summary>
public static class BrowserEndpoints
{
    private const string ProblemBase = "https://remoteos.app/problems/";

    public static IEndpointRouteBuilder MapBrowserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(BrowserApiRoutes.Settings, (ClaimsPrincipal principal, IWorkspaceRepository workspaces, IRegistryRepository registry) =>
        {
            var workspace = workspaces.FindByUserId(GetUserId(principal));
            return workspace is null ? Results.NotFound() : Results.Ok(ReadSettings(registry, workspace));
        })
        .RequireAuthorization()
        .WithTags("Browser");

        app.MapPut(BrowserApiRoutes.Settings, (BrowserSettingsDto request, ClaimsPrincipal principal, IWorkspaceRepository workspaces, IRegistryRepository registry) =>
        {
            var workspace = workspaces.FindByUserId(GetUserId(principal));
            if (workspace is null)
                return Results.NotFound();

            var settings = request.Normalize();
            WorkspaceConfigurationRegistry.Write(registry, workspace, WorkspaceConfigurationRegistry.BrowserPath, settings, workspace.UserId.ToString("D"));
            return Results.Ok(settings);
        })
        .RequireAuthorization()
        .WithTags("Browser");

        // ── bookmarks ──

        // GET bookmarks — 列举当前用户全部书签
        app.MapGet(BrowserApiRoutes.Bookmarks, (ClaimsPrincipal principal, IBrowserRepository repo) =>
            Results.Ok(repo.ListBookmarks(GetUserId(principal)).Select(b => b.ToDto())))
           .RequireAuthorization()
           .WithTags("Browser");

        // POST bookmarks — 新增（同 URL 则更新 Title）
        app.MapPost(BrowserApiRoutes.BookmarksCreate, (CreateBookmarkRequest req, ClaimsPrincipal principal, IBrowserRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url))
                return Problem(400, "invalid-input", "输入无效", "url 不能为空");
            var bm = repo.UpsertBookmark(GetUserId(principal), req.Title, req.Url);
            return Results.Created($"/{RemoteOS.Protocol.Common.RemoteOsEndpoints.ApiVersionPrefix}/browser/bookmarks/{bm.Id}", bm.ToDto());
        })
        .RequireAuthorization()
        .WithTags("Browser");

        // DELETE bookmarks/{id} — 删除单条
        app.MapDelete(BrowserApiRoutes.BookmarksDelete, (Guid id, ClaimsPrincipal principal, IBrowserRepository repo) =>
        {
            return repo.DeleteBookmark(GetUserId(principal), id)
                ? Results.NoContent()
                : Problem(404, "not-found", "书签不存在", $"找不到 id={id} 的书签（或它不属于当前用户）");
        })
        .RequireAuthorization()
        .WithTags("Browser");

        // DELETE bookmarks — 清空当前用户全部书签
        app.MapDelete(BrowserApiRoutes.BookmarksClear, (ClaimsPrincipal principal, IBrowserRepository repo) =>
        {
            var n = repo.ClearBookmarks(GetUserId(principal));
            return Results.Ok(new { removed = n });
        })
        .RequireAuthorization()
        .WithTags("Browser");

        // ── history ──

        // GET history?limit= — 列举历史（按 LastVisitedAt 倒序，默认 100 上限 1000）
        app.MapGet(BrowserApiRoutes.History, (int? limit, ClaimsPrincipal principal, IBrowserRepository repo) =>
        {
            var l = limit ?? 100;
            if (l < 0) l = 100;
            if (l > 1000) l = 1000;
            return Results.Ok(repo.ListHistory(GetUserId(principal), l).Select(h => h.ToDto()));
        })
        .RequireAuthorization()
        .WithTags("Browser");

        // POST history — 记录一次访问（同 URL 则 VisitCount++）
        app.MapPost(BrowserApiRoutes.HistoryCreate, (CreateHistoryEntryRequest req, ClaimsPrincipal principal, IBrowserRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url))
                return Problem(400, "invalid-input", "输入无效", "url 不能为空");
            var h = repo.UpsertHistory(GetUserId(principal), req.Title, req.Url);
            return Results.Created($"/{RemoteOS.Protocol.Common.RemoteOsEndpoints.ApiVersionPrefix}/browser/history/{h.Id}", h.ToDto());
        })
        .RequireAuthorization()
        .WithTags("Browser");

        // DELETE history/{id} — 删除单条
        app.MapDelete(BrowserApiRoutes.HistoryDelete, (Guid id, ClaimsPrincipal principal, IBrowserRepository repo) =>
        {
            return repo.DeleteHistory(GetUserId(principal), id)
                ? Results.NoContent()
                : Problem(404, "not-found", "历史记录不存在", $"找不到 id={id} 的历史记录（或它不属于当前用户）");
        })
        .RequireAuthorization()
        .WithTags("Browser");

        // DELETE history — 清空当前用户全部历史记录
        app.MapDelete(BrowserApiRoutes.HistoryClear, (ClaimsPrincipal principal, IBrowserRepository repo) =>
        {
            var n = repo.ClearHistory(GetUserId(principal));
            return Results.Ok(new { removed = n });
        })
        .RequireAuthorization()
        .WithTags("Browser");

        return app;
    }

    /// <summary>从 JWT sub claim 取当前用户 Id。已通过 RequireAuthorization 保证 principal 非空。</summary>
    private static Guid GetUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? throw new InvalidOperationException("JWT 缺少 sub claim。");
        return Guid.Parse(sub);
    }

    private static BrowserSettingsDto ReadSettings(IRegistryRepository registry, Server.Domain.Workspace workspace) =>
        WorkspaceConfigurationRegistry.Read(registry, workspace, WorkspaceConfigurationRegistry.BrowserPath, BrowserSettingsDto.Default).Normalize();

    private static IResult Problem(int status, string typeSuffix, string title, string detail)
        => Results.Problem(detail: detail, statusCode: status, title: title, type: ProblemBase + typeSuffix);
}
