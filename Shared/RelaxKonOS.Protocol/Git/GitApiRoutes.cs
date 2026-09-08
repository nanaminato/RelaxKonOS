using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Git;

/// <summary>Routes for the server-side Git repository integration. Shared by server endpoint registration and client URL composition.</summary>
public static class GitApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
    public const string EngineStatus = $"/{V1}/git/engine/status";
    public const string EngineInstall = $"/{V1}/git/engine/install";
    public const string Repositories = $"/{V1}/git/repositories";
    public const string RepositoryById = $"/{V1}/git/repositories/{{id}}";
    public const string Status = $"/{V1}/git/repositories/{{id}}/status";
    public const string Branches = $"/{V1}/git/repositories/{{id}}/branches";
    public const string BranchByName = $"/{V1}/git/repositories/{{id}}/branches/{{name}}";
    public const string RenameBranch = $"/{V1}/git/repositories/{{id}}/branches/{{name}}/rename";
    public const string BranchTracking = $"/{V1}/git/repositories/{{id}}/branches/{{name}}/tracking";
    public const string BranchComparison = $"/{V1}/git/repositories/{{id}}/branches/{{name}}/comparison";
    public const string Checkout = $"/{V1}/git/repositories/{{id}}/checkout";
    public const string Commit = $"/{V1}/git/repositories/{{id}}/commit";
    public const string Merge = $"/{V1}/git/repositories/{{id}}/merge";
    public const string Pull = $"/{V1}/git/repositories/{{id}}/pull";
    public const string Push = $"/{V1}/git/repositories/{{id}}/push";
    public const string Log = $"/{V1}/git/repositories/{{id}}/log";
    public const string CommitDetail = $"/{V1}/git/repositories/{{id}}/commits/{{sha}}";
    public const string Diff = $"/{V1}/git/repositories/{{id}}/diff";
    public const string Revert = $"/{V1}/git/repositories/{{id}}/revert";
    public const string Resolve = $"/{V1}/git/repositories/{{id}}/resolve";
    public const string Fetch = $"/{V1}/git/repositories/{{id}}/fetch";
    public const string Reset = $"/{V1}/git/repositories/{{id}}/reset";
    public const string Restore = $"/{V1}/git/repositories/{{id}}/restore";
    public const string Stage = $"/{V1}/git/repositories/{{id}}/stage";
    public const string Unstage = $"/{V1}/git/repositories/{{id}}/unstage";

    // ── 路径探测与初始化（不依赖已注册仓库）──
    public const string Probe = $"/{V1}/git/probe";
    public const string Init = $"/{V1}/git/init";

    // ── 远程仓库（remote）管理 ──
    public const string Remotes = $"/{V1}/git/repositories/{{id}}/remotes";
    public const string RemoteByName = $"/{V1}/git/repositories/{{id}}/remotes/{{name}}";
}
