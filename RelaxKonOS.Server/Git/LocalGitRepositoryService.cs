using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Git;

/// <summary>Singleton service that invokes the host git CLI and manages repository registrations.
/// Write operations are serialized per-repository via SemaphoreSlim to avoid index.lock conflicts.
/// Runtime state (status/branches/log/diff) is never persisted—only GitRepository registration records.</summary>
public sealed partial class LocalGitRepositoryService(
    IDbContextFactory<RelaxKonOSDbContext> dbFactory,
    IHostGitCli gitCli,
    IDataProtectionProvider dataProtection,
    ILogger<LocalGitRepositoryService> logger,
    IServiceScopeFactory executionScopes,
    IUserExecutionTransport executionTransport,
    IServerModeResolver serverMode,
    RelaxKonOS.Server.UserExecution.UserExecutionBackendSelection userExecution,
    IHttpContextAccessor http) : IGitRepositoryService
{
    private const int MaxDiffPatchSize = 200 * 1024; // 200KB
    private static readonly TimeSpan SemaphoreTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _writeLocks = new();
    private readonly IDataProtector _credentialProtector = dataProtection.CreateProtector("RelaxKonOS.GitCredentials.v1");
    private const string CredentialAppId = "relaxkonos.git.internal";

    // ── Host Git engine probe & install ──

    public async Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken = default)
    {
        var path = gitCli.ResolveGitPath();
        if (string.IsNullOrEmpty(path))
            return new GitEngineStatusDto(false, ProblemCode: "not_installed", CanAutoInstall: CanAutoInstallGit());
        var version = await GetGitVersionAsync(path, cancellationToken);
        return new GitEngineStatusDto(version is not null, ProblemCode: version is null ? "git.probe_failed" : "", Version: version, ExecutablePath: path);
    }

    private async Task<string?> GetGitVersionAsync(string gitPath, CancellationToken cancellationToken)
    {
        var r = await RunGitAsync(gitPath, Environment.CurrentDirectory, ["--version"], cancellationToken);
        if (!r.Success) return null;
        var line = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line)) return null;
        const string prefix = "git version";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line.Substring(prefix.Length).Trim()
            : line;
    }

    private static bool CanAutoInstallGit()
    {
        return OperatingSystem.IsLinux() && File.Exists("/usr/bin/apt-get");
    }

    // ── Repository registration ──

    public async Task<IReadOnlyList<GitRepositoryDto>> ListRepositoriesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repos = await db.Set<GitRepository>()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
        return repos.Count > 0 ? repos.Select(r => r.ToDto()).ToArray() : Array.Empty<GitRepositoryDto>();
    }

    public async Task<GitRepositoryDto?> GetRepositoryAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await db.Set<GitRepository>().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);
        if (repo is null) return null;
        var (branch, upstream, ahead, behind, isDetached, uncommitted) = await GetBranchSummaryAsync(repo.Path, cancellationToken);
        return repo.ToDto(branch, null, ahead, behind, !string.IsNullOrEmpty(upstream), uncommitted);
    }

    public async Task<GitRepositoryDto> RegisterRepositoryAsync(GitRepositoryRegistration registration, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(registration.Path))
            throw new ArgumentException("Repository path must be absolute.", nameof(registration.Path));
        var gitPath = ResolveGitPathOrThrow();

        // Opening the working directory and checking the worktree must use the same
        // execution identity. The service account need not be able to traverse a user's home.
        var revParse = await RunGitAsync(gitPath, registration.Path, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (!revParse.Success || !revParse.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The path is not a Git repository.", nameof(registration.Path));

        var repo = new GitRepository
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = registration.Name,
            Path = registration.Path,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Set<GitRepository>().Add(repo);
        await db.SaveChangesAsync(cancellationToken);
        return repo.ToDto();
    }

    public async Task<bool> UnregisterRepositoryAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await db.Set<GitRepository>().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);
        if (repo is null) return false;
        db.Set<GitRepository>().Remove(repo);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Real-time git operations ──

    public async Task<GitStatusDto> GetStatusAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        var result = await RunGitAsync(gitPath, repo.Path, ["status", "--porcelain=v2", "--branch", "-uall"], cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git status failed: {result.Error}");
        return ParseStatus(result.Output, await ReadConfigVersionAsync(gitPath, repo.Path, cancellationToken));
    }

    public async Task<GitOperationResult> CommitAsync(Guid id, Guid userId, GitCommitRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            if (request.Paths.Count > 0)
            {
                foreach (var path in request.Paths)
                    if (!IsPathSafe(repo.Path, path))
                        return new GitOperationResult(false, "commit", Message: $"Path outside repository: {path}");
                var add = await RunGitAsync(gitPath, repo.Path, ["add", "--", .. request.Paths], cancellationToken);
                if (!add.Success) return new GitOperationResult(false, "commit", Message: add.Error);
            }
            var args = new List<string> { "commit", "-m", request.Message };
            if (request.Amend) args.Add("--amend");
            // --only commits the requested paths without sweeping unrelated staged work into a
            // mobile one-file commit. Git itself rejects unresolved conflicts on these paths.
            if (request.Paths.Count > 0)
            {
                args.Add("--only");
                args.Add("--");
                args.AddRange(request.Paths);
            }
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            return new GitOperationResult(result.Success, "commit", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> MergeBranchAsync(Guid id, Guid userId, GitMergeRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
            return new GitOperationResult(false, "merge", Message: "Source branch is required.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "merge" };
            switch ((request.Strategy ?? "merge").Trim().ToLowerInvariant())
            {
                case "no-ff":
                    args.Add("--no-ff");
                    break;
                case "ff-only":
                    args.Add("--ff-only");
                    break;
                case "squash":
                    args.Add("--squash");
                    break;
                // "merge" (default) 不附加策略参数，让 git 尊重仓库 merge.ff 配置
            }
            if (request.NoCommit) args.Add("--no-commit");
            if (!string.IsNullOrWhiteSpace(request.Message))
            {
                args.Add("-m");
                args.Add(request.Message);
            }
            args.Add(request.Source);

            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            // 与 pull/revert 同语义：即使 git exit != 0，只要检测到冲突文件就仍然返回 Success=false 但带 Conflicts 负载
            return new GitOperationResult(result.Success, "merge",
                Conflicts: conflicts,
                Message: result.Success ? null : result.Error);
        });
    }

    // ── 路径探测与初始化 ──

    public async Task<GitRepositoryProbeDto> ProbeRepositoryAsync(string path, Guid userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Repository path must not be empty.", nameof(path));
        if (!Path.IsPathRooted(path))
            throw new ArgumentException("Repository path must be absolute.", nameof(path));
        var gitPath = gitCli.ResolveGitPath()
            ?? throw new InvalidOperationException("Git executable not found on the host.");

        // Let the effective Git identity open the directory, as in registration.
        var revParse = await RunGitAsync(gitPath, path, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (!revParse.Success || !revParse.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return new GitRepositoryProbeDto(false);

        // 是否有提交
        var headSha = await RunGitAsync(gitPath, path, ["rev-parse", "HEAD"], cancellationToken);
        var hasCommits = headSha.Success && !string.IsNullOrWhiteSpace(headSha.Output.Trim());

        string? currentBranch = null;
        string? defaultBranch = null;
        if (hasCommits)
        {
            var branchResult = await RunGitAsync(gitPath, path, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            if (branchResult.Success)
            {
                var name = branchResult.Output.Trim();
                currentBranch = string.IsNullOrEmpty(name) || name == "HEAD" ? null : name;
            }
        }

        var defaultBranchResult = await RunGitAsync(gitPath, path,
            ["config", "--get", "init.defaultBranch"], cancellationToken);
        if (defaultBranchResult.Success)
        {
            var db = defaultBranchResult.Output.Trim();
            defaultBranch = string.IsNullOrEmpty(db) ? null : db;
        }
        defaultBranch ??= "main";

        return new GitRepositoryProbeDto(true, hasCommits, currentBranch, defaultBranch,
            await GetRemotesAsync(gitPath, path, cancellationToken));
    }

    public async Task<GitOperationResult> InitRepositoryAsync(string path, Guid userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Repository path must not be empty.", nameof(path));
        if (!Path.IsPathRooted(path))
            throw new ArgumentException("Repository path must be absolute.", nameof(path));
        if (!Directory.Exists(path))
            throw new ArgumentException("Repository path does not exist.", nameof(path));

        var gitPath = gitCli.ResolveGitPath()
            ?? throw new InvalidOperationException("Git executable not found on the host.");

        var initResult = await RunGitAsync(gitPath, path, ["init"], cancellationToken);
        if (!initResult.Success)
            return new GitOperationResult(false, "init", Message: initResult.Error);
        // git init 输出形如 "Initialized empty Git repository in /path/.git/"；不解析也行。
        return new GitOperationResult(true, "init");
    }

    // ── Helpers ──

    private string ResolveGitPath() => gitCli.ResolveGitPath() ?? "";
    private string ResolveGitPathOrThrow() => gitCli.ResolveGitPath()
        ?? throw new InvalidOperationException("Git executable not found on the host.");

    private static async Task<GitRepository> GetRepoOrThrowAsync(RelaxKonOSDbContext db, Guid id, Guid userId, CancellationToken cancellationToken)
    {
        return await db.Set<GitRepository>().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException($"Repository {id} not found.");
    }

    private async Task<(string branch, string? upstream, int ahead, int behind, bool isDetached, int uncommitted)> GetBranchSummaryAsync(string repoPath, CancellationToken cancellationToken)
    {
        var gitPath = ResolveGitPath();
        if (string.IsNullOrEmpty(gitPath)) return ("unknown", null, 0, 0, false, 0);
        var result = await RunGitAsync(gitPath, repoPath, ["status", "--porcelain=v2", "--branch", "-uall"], cancellationToken);
        if (!result.Success) return ("unknown", null, 0, 0, false, 0);
        var status = ParseStatus(result.Output, await ReadConfigVersionAsync(gitPath, repoPath, cancellationToken));
        var uncommitted = status.Staged.Count + status.Unstaged.Count + status.Untracked.Count + status.Conflicts.Count;
        return (status.Branch, status.Upstream, status.Ahead, status.Behind, status.IsDetached, uncommitted);
    }

    private async Task<string> ReadConfigVersionAsync(string gitPath, string root, CancellationToken ct)
    {
        var config = await RunGitAsync(gitPath, root, ["config", "--list", "--null"], ct);
        if (!config.Success || config.Output.Length > 1024 * 1024) throw new InvalidOperationException("Git configuration could not be read.");
        // Includes all fetch/push URLs, branch mappings and refspecs. Raw config can contain credentials;
        // only an opaque digest leaves the ordinary execution boundary through the HTTP status DTO.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.Output))).ToLowerInvariant();
    }

}
