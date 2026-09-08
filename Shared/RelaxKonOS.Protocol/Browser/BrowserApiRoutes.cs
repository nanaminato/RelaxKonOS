using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Browser;

/// <summary>浏览器书签/历史记录 REST 端点路由常量。路径已含 /api/v1.0 前缀。Server 注册路由与 Client 拼接 URL 共用。
/// 所有端点需 JWT（[Authorize]），按 JWT sub claim 限定到当前用户。错误统一返回 RFC 7807 ProblemDetails。</summary>
public static class BrowserApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;

    /// <summary>Read or persist browser preferences for the authenticated user's workspace.</summary>
    public const string Settings = $"/{V1}/browser/settings";

    // ── bookmarks ──

    /// <summary>列举当前用户书签（GET，需 JWT）。</summary>
    public const string Bookmarks = $"/{V1}/browser/bookmarks";

    /// <summary>新增书签（POST，需 JWT）。body: CreateBookmarkRequest。同 URL 重复则更新 Title。</summary>
    public const string BookmarksCreate = $"/{V1}/browser/bookmarks";

    /// <summary>删除单个书签（DELETE，需 JWT）。route: id。仅当书签属于当前用户才删除。</summary>
    public const string BookmarksDelete = $"/{V1}/browser/bookmarks/{{id}}";

    /// <summary>清空当前用户全部书签（DELETE，需 JWT）。</summary>
    public const string BookmarksClear = $"/{V1}/browser/bookmarks";

    // ── history ──

    /// <summary>列举当前用户历史记录（GET，需 JWT）。query: limit（可选，默认 100，上限 1000）。</summary>
    public const string History = $"/{V1}/browser/history";

    /// <summary>记录一次访问（POST，需 JWT）。body: CreateHistoryEntryRequest。同 URL 累加 VisitCount。</summary>
    public const string HistoryCreate = $"/{V1}/browser/history";

    /// <summary>删除单条历史记录（DELETE，需 JWT）。route: id。仅当属于当前用户才删除。</summary>
    public const string HistoryDelete = $"/{V1}/browser/history/{{id}}";

    /// <summary>清空当前用户全部历史记录（DELETE，需 JWT）。</summary>
    public const string HistoryClear = $"/{V1}/browser/history";
}
