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
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Git;

/// <summary>Singleton service that invokes the host git CLI and manages repository registrations.
/// Write operations are serialized per-repository via SemaphoreSlim to avoid index.lock conflicts.
/// Runtime state (status/branches/log/diff) is never persisted—only GitRepository registration records.</summary>
public sealed class LocalGitRepositoryService(
    IDbContextFactory<RemoteOsDbContext> dbFactory,
    IHostGitCli gitCli,
    IDataProtectionProvider dataProtection,
    RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport transport,
    ILogger<LocalGitRepositoryService> logger) : IGitRepositoryService
{
    private const int MaxDiffPatchSize = 200 * 1024; // 200KB
    private static readonly TimeSpan SemaphoreTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _writeLocks = new();
    private readonly IDataProtector _credentialProtector = dataProtection.CreateProtector("RelaxKonOS.GitCredentials.v1");
    private const string CredentialAppId = "remoteos.git.internal";

    // ── Host Git engine probe & install ──

    public async Task<GitEngineStatusDto> GetEngineStatusAsync(CancellationToken cancellationToken = default)
    {
        var path = gitCli.ResolveGitPath();
        if (string.IsNullOrEmpty(path))
            return new GitEngineStatusDto(false, ProblemCode: "not_installed", CanAutoInstall: CanAutoInstallGit());
        var version = await GetGitVersionAsync(path, cancellationToken);
        return new GitEngineStatusDto(true, ProblemCode: "", Version: version, ExecutablePath: path);
    }

    public async Task<GitEngineInstallResult> InstallEngineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/apt-get"))
                return new GitEngineInstallResult(false, "此主机需要手动安装 Git；RelaxKonOS 仅支持受限的 Linux APT Helper 安装。", "git.install_not_supported");
            var result = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.GitPackageInstall), cancellationToken);
            if (!result.Success && gitCli.ResolveGitPath() is null)
                return new GitEngineInstallResult(false, null,
                    result.ProblemCode == PrivilegedProblemCode.HelperUnavailable ? "git.privileged_helper_unavailable" : "git.install_failed");

            return gitCli.ResolveGitPath() is not null
                ? new GitEngineInstallResult(true, "Git 安装成功。")
                : new GitEngineInstallResult(false, "安装命令执行成功，但仍未检测到 git 可执行文件，请检查 PATH 配置或重启服务。", "git.install_verification_failed");
        }
        catch (Exception ex)
        {
            return new GitEngineInstallResult(false, $"安装过程中出错：{ex.Message}", "git.install_failed");
        }
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
        if (!Directory.Exists(registration.Path))
            throw new ArgumentException("Repository path does not exist.", nameof(registration.Path));

        var gitPath = ResolveGitPathOrThrow();

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
        return ParseStatus(result.Output);
    }

    public async Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        // Keep the full refname in addition to its display name.  A branch name is
        // allowed to contain '/', so it cannot tell us whether the ref is local or
        // remote (for example, a perfectly valid local branch is feature/login).
        var fmt = "%(refname)%09%(refname:short)%09%(upstream:short)%09%(upstream:track)%09%(HEAD)";
        var result = await RunGitAsync(gitPath, repo.Path,
            ["for-each-ref", $"--format={fmt}", "refs/heads", "refs/remotes"], cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git for-each-ref failed: {result.Error}");

        var branches = new List<GitBranchDto>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 5) continue;
            var fullRefName = parts[0];
            var name = parts[1];
            var upstream = parts[2].Length > 0 ? parts[2] : null;
            var track = parts[3];
            var isHead = parts[4] == "*";
            var isRemote = fullRefName.StartsWith("refs/remotes/", StringComparison.Ordinal);

            // refs/remotes/<remote>/HEAD is a symbolic pointer to the remote's
            // default branch, not a branch a user can check out or manage.
            if (isRemote && fullRefName.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
                continue;
            var (ahead, behind) = ParseTrack(track);
            branches.Add(new GitBranchDto(name, isRemote, isHead, false, upstream, ahead, behind));
        }
        return branches;
    }

    public async Task<GitBranchComparisonDto> CompareBranchAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException("A valid branch name is required.", nameof(name));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();

        // Resolve first so the branch name is never interpreted as a command option
        // and so a stale remote ref produces a useful validation error.
        var verify = await RunGitAsync(gitPath, repo.Path, ["rev-parse", "--verify", "--quiet", $"{name}^{{commit}}"], cancellationToken);
        if (!verify.Success)
            throw new ArgumentException($"Branch or revision '{name}' does not exist.", nameof(name));

        // A one-revision diff compares that revision with the index and working tree,
        // which is the same source/target direction exposed by IDEA's "Show Diff with
        // Working Tree" action.  -M preserves rename information for the file list.
        var result = await RunGitAsync(gitPath, repo.Path, ["diff", "--name-status", "-M", name], cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git diff failed: {result.Error}");
        return new GitBranchComparisonDto(name, ParseChangedFiles(result.Output));
    }

    public async Task<GitOperationResult> CreateBranchAsync(Guid id, Guid userId, GitBranchCreateRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.StartsWith("-", StringComparison.Ordinal))
                return new GitOperationResult(false, "create-branch", Message: "A valid branch name is required.");

            var args = new List<string> { "branch" };
            if (request.ResetExisting) args.Add("--force");
            if (request.Track) args.Add("--track");
            args.Add(request.Name);
            if (!string.IsNullOrEmpty(request.StartPoint)) args.Add(request.StartPoint);
            var createResult = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            if (!createResult.Success)
                return new GitOperationResult(false, "create-branch", Message: createResult.Error);

            if (!request.Checkout)
                return new GitOperationResult(true, "create-branch");

            var checkoutResult = await RunGitAsync(gitPath, repo.Path, ["checkout", request.Name], cancellationToken);
            var conflicts = checkoutResult.Success ? null : await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(checkoutResult.Success, "create-branch", Conflicts: conflicts,
                Message: checkoutResult.Success ? null : checkoutResult.Error);
        });
    }

    public async Task<GitOperationResult> DeleteBranchAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var branchResult = await RunGitAsync(gitPath, repo.Path, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            if (branchResult.Success && branchResult.Output.Trim() == name)
                return new GitOperationResult(false, "delete-branch", Message: "Cannot delete the current branch.");
            var result = await RunGitAsync(gitPath, repo.Path, ["branch", "-d", name], cancellationToken);
            return new GitOperationResult(result.Success, "delete-branch", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> RenameBranchAsync(Guid id, Guid userId, string name, GitBranchRenameRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new GitOperationResult(false, "rename-branch", Message: "Source branch name is required.");
        if (string.IsNullOrWhiteSpace(request.NewName))
            return new GitOperationResult(false, "rename-branch", Message: "New branch name is required.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["branch", "-m", name, request.NewName], cancellationToken);
            return new GitOperationResult(result.Success, "rename-branch", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> SetBranchTrackingAsync(Guid id, Guid userId, string name, GitBranchTrackingRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new GitOperationResult(false, "set-upstream", Message: "Branch name is required.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            // 解绑：upstream = null 或 空字符串
            if (string.IsNullOrWhiteSpace(request.Upstream) && string.IsNullOrWhiteSpace(request.Remote) && string.IsNullOrWhiteSpace(request.Branch))
            {
                var unsetResult = await RunGitAsync(gitPath, repo.Path, ["branch", "--unset-upstream", name], cancellationToken);
                return new GitOperationResult(unsetResult.Success, "set-upstream", Message: unsetResult.Success ? null : unsetResult.Error);
            }

            // 解析最终 upstream：优先用 Upstream；否则用 Remote/Branch 合成
            var upstream = request.Upstream;
            if (string.IsNullOrWhiteSpace(upstream))
            {
                if (string.IsNullOrWhiteSpace(request.Remote) || string.IsNullOrWhiteSpace(request.Branch))
                    return new GitOperationResult(false, "set-upstream", Message: "Either Upstream or both Remote+Branch must be provided.");
                upstream = $"{request.Remote}/{request.Branch}";
            }

            var setResult = await RunGitAsync(gitPath, repo.Path, ["branch", "-u", upstream, name], cancellationToken);
            return new GitOperationResult(setResult.Success, "set-upstream", Message: setResult.Success ? null : setResult.Error);
        });
    }

    public async Task<GitOperationResult> CheckoutAsync(Guid id, Guid userId, GitCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            if (string.IsNullOrWhiteSpace(request.Branch) || request.Branch.StartsWith("-", StringComparison.Ordinal))
                return new GitOperationResult(false, "checkout", Message: "A valid branch name is required.");

            var remoteRef = await RunGitAsync(gitPath, repo.Path,
                ["show-ref", "--verify", "--quiet", $"refs/remotes/{request.Branch}"], cancellationToken);
            var args = new List<string> { "checkout" };
            if (remoteRef.Success)
            {
                // Checking out a remote ref directly detaches HEAD.  Match IDE
                // behavior instead: create/check out a local branch that tracks it.
                var slash = request.Branch.IndexOf('/');
                var localName = slash >= 0 ? request.Branch[(slash + 1)..] : request.Branch;
                var localRef = await RunGitAsync(gitPath, repo.Path,
                    ["show-ref", "--verify", "--quiet", $"refs/heads/{localName}"], cancellationToken);
                if (localRef.Success)
                {
                    // Do not reset an existing local branch implicitly.  It may
                    // contain work that has not been pushed yet.
                    args.Add(localName);
                }
                else
                {
                    args.Add("--track");
                    args.Add(request.Branch);
                }
            }
            else
            {
                if (request.CreateIfMissing) args.Add("-b");
                args.Add(request.Branch);
            }
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            var conflicts = result.Success ? null : await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(result.Success, "checkout", Conflicts: conflicts, Message: result.Success ? null : result.Error);
        });
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
                var addArgs = new List<string> { "add", "--" };
                addArgs.AddRange(request.Paths);
                var addResult = await RunGitAsync(gitPath, repo.Path, [.. addArgs], cancellationToken);
                if (!addResult.Success)
                    return new GitOperationResult(false, "commit", Message: addResult.Error);
            }
            var args = new List<string> { "commit", "-m", request.Message };
            if (request.Amend) args.Add("--amend");
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
            return new GitOperationResult(result.Success || conflicts is not null, "merge",
                Conflicts: conflicts,
                Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> FetchAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["fetch"], cancellationToken);
            return new GitOperationResult(result.Success, "fetch",
                RequiresCredentials: !result.Success && IsCredentialError(result.Error),
                Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> PullAsync(Guid id, Guid userId, GitPullRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var currentBranch = await GetCurrentBranchAsync(gitPath, repo.Path, cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.Branch) &&
                !string.Equals(request.Branch, currentBranch, StringComparison.Ordinal))
                return await UpdateNonCurrentBranchAsync(gitPath, repo.Path, request, cancellationToken);

            var args = new List<string> { "pull" };
            if (string.Equals(request.Strategy, "rebase", StringComparison.OrdinalIgnoreCase))
                args.Add("--rebase");
            if (!string.IsNullOrEmpty(request.Remote)) args.Add(request.Remote);
            if (!string.IsNullOrEmpty(request.Refspec)) args.Add(request.Refspec);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(result.Success || conflicts is not null, "pull",
                Conflicts: conflicts,
                RequiresCredentials: !result.Success && IsCredentialError(result.Error),
                Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> PushAsync(Guid id, Guid userId, GitPushRequest? request = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var localBranch = request?.LocalBranch;
            if (string.IsNullOrWhiteSpace(localBranch))
                localBranch = await GetCurrentBranchAsync(gitPath, repo.Path, cancellationToken);
            if (string.IsNullOrWhiteSpace(localBranch) || !IsSafeRefComponent(localBranch))
                return new GitOperationResult(false, "push", Message: "A valid local branch is required.");

            var localRef = await RunGitAsync(gitPath, repo.Path,
                ["show-ref", "--verify", "--quiet", $"refs/heads/{localBranch}"], cancellationToken);
            if (!localRef.Success)
                return new GitOperationResult(false, "push", Message: $"Local branch '{localBranch}' does not exist.");

            var configuredRemote = await GetBranchConfigAsync(gitPath, repo.Path, localBranch, "remote", cancellationToken);
            var remote = string.IsNullOrWhiteSpace(request?.Remote) ? configuredRemote : request!.Remote!.Trim();
            if (string.IsNullOrWhiteSpace(remote)) remote = "origin";
            if (!IsSafeRefComponent(remote))
                return new GitOperationResult(false, "push", Message: "A valid remote name is required.");

            var configuredMerge = await GetBranchConfigAsync(gitPath, repo.Path, localBranch, "merge", cancellationToken);
            var remoteBranch = string.IsNullOrWhiteSpace(request?.RemoteBranch)
                ? StripHeadsPrefix(configuredMerge) ?? localBranch
                : request!.RemoteBranch!.Trim();
            if (!IsSafeRefComponent(remoteBranch))
                return new GitOperationResult(false, "push", Message: "A valid remote branch is required.");

            var remoteUri = await TryGetPushRemoteUriAsync(gitPath, repo.Path, remote, cancellationToken);
            var suppliedCredentials = request?.Credentials;
            if (suppliedCredentials is not null &&
                (string.IsNullOrWhiteSpace(suppliedCredentials.Username) || string.IsNullOrWhiteSpace(suppliedCredentials.Password)))
            {
                return new GitOperationResult(false, "push", Message: "Username and access token are required.", RequiresCredentials: true);
            }

            var credentials = suppliedCredentials;
            if (credentials is null && remoteUri is not null)
                credentials = await GetStoredCredentialsAsync(userId, remoteUri, cancellationToken);

            // Use an explicit refspec so a selected local branch can be pushed while another
            // branch remains checked out. This is equivalent to IDEA's Push on a non-HEAD branch.
            var result = await RunGitAsync(gitPath, repo.Path,
                ["push", remote, $"refs/heads/{localBranch}:refs/heads/{remoteBranch}"], cancellationToken, credentials);
            if (result.Success && suppliedCredentials is not null && request?.SaveCredentials != false && remoteUri is not null)
                await SaveCredentialsAsync(userId, remoteUri, suppliedCredentials, cancellationToken);

            return new GitOperationResult(result.Success, "push",
                RequiresCredentials: !result.Success && IsCredentialError(result.Error),
                Message: result.Success ? null : result.Error);
        });
    }

    /// <summary>Updates a local branch which is not checked out. Git cannot merge or rebase an
    /// inactive branch without changing the worktree, so this deliberately performs only a safe
    /// fast-forward after fetching its configured upstream.</summary>
    private async Task<GitOperationResult> UpdateNonCurrentBranchAsync(
        string gitPath, string repoPath, GitPullRequest request, CancellationToken cancellationToken)
    {
        var branch = request.Branch!.Trim();
        if (!IsSafeRefComponent(branch))
            return new GitOperationResult(false, "update-branch", Message: "A valid local branch is required.");

        var localRef = $"refs/heads/{branch}";
        var exists = await RunGitAsync(gitPath, repoPath, ["show-ref", "--verify", "--quiet", localRef], cancellationToken);
        if (!exists.Success)
            return new GitOperationResult(false, "update-branch", Message: $"Local branch '{branch}' does not exist.");

        var configuredRemote = await GetBranchConfigAsync(gitPath, repoPath, branch, "remote", cancellationToken);
        var remote = string.IsNullOrWhiteSpace(request.Remote) ? configuredRemote : request.Remote.Trim();
        if (string.IsNullOrWhiteSpace(remote))
            return new GitOperationResult(false, "update-branch", Message: $"Branch '{branch}' has no upstream remote.");
        if (!IsSafeRefComponent(remote))
            return new GitOperationResult(false, "update-branch", Message: "A valid remote name is required.");

        var configuredMerge = await GetBranchConfigAsync(gitPath, repoPath, branch, "merge", cancellationToken);
        var remoteBranch = string.IsNullOrWhiteSpace(request.Refspec) ? StripHeadsPrefix(configuredMerge) : request.Refspec.Trim();
        if (string.IsNullOrWhiteSpace(remoteBranch))
            return new GitOperationResult(false, "update-branch", Message: $"Branch '{branch}' has no upstream branch.");
        if (!IsSafeRefComponent(remoteBranch))
            return new GitOperationResult(false, "update-branch", Message: "A valid upstream branch is required.");

        var fetch = await RunGitAsync(gitPath, repoPath, ["fetch", remote], cancellationToken);
        if (!fetch.Success)
            return new GitOperationResult(false, "update-branch",
                RequiresCredentials: IsCredentialError(fetch.Error), Message: fetch.Error);

        var upstreamRef = $"refs/remotes/{remote}/{remoteBranch}";
        var upstream = await RunGitAsync(gitPath, repoPath, ["rev-parse", "--verify", "--quiet", $"{upstreamRef}^{{commit}}"], cancellationToken);
        if (!upstream.Success)
            return new GitOperationResult(false, "update-branch", Message: $"Upstream branch '{remote}/{remoteBranch}' was not found.");

        var localCommit = await RunGitAsync(gitPath, repoPath, ["rev-parse", localRef], cancellationToken);
        if (!localCommit.Success)
            return new GitOperationResult(false, "update-branch", Message: localCommit.Error);
        if (string.Equals(localCommit.Output.Trim(), upstream.Output.Trim(), StringComparison.Ordinal))
            return new GitOperationResult(true, "update-branch");

        var fastForward = await RunGitAsync(gitPath, repoPath,
            ["merge-base", "--is-ancestor", localRef, upstreamRef], cancellationToken);
        if (!fastForward.Success)
            return new GitOperationResult(false, "update-branch",
                Message: $"Branch '{branch}' has diverged from '{remote}/{remoteBranch}'. Check it out to merge or rebase it.");

        var update = await RunGitAsync(gitPath, repoPath,
            ["update-ref", localRef, upstream.Output.Trim(), localCommit.Output.Trim()], cancellationToken);
        return new GitOperationResult(update.Success, "update-branch", Message: update.Success ? null : update.Error);
    }

    private static bool IsSafeRefComponent(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("-", StringComparison.Ordinal) &&
        !value.Any(char.IsWhiteSpace) &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !value.Contains("@{", StringComparison.Ordinal) &&
        !value.EndsWith(".", StringComparison.Ordinal) &&
        !value.Any(c => c is '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    private static string? StripHeadsPrefix(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().StartsWith("refs/heads/", StringComparison.Ordinal)
            ? value.Trim()["refs/heads/".Length..]
            : value.Trim();

    private async Task<string?> GetCurrentBranchAsync(string gitPath, string repoPath, CancellationToken cancellationToken)
    {
        var current = await RunGitAsync(gitPath, repoPath, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        return current.Success ? current.Output.Trim() : null;
    }

    private async Task<string?> GetBranchConfigAsync(string gitPath, string repoPath, string branch, string key, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(gitPath, repoPath, ["config", "--get", $"branch.{branch}.{key}"], cancellationToken);
        return result.Success && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : null;
    }

    /// <summary>Loads the push URL without exposing it over the API. Credentials are meaningful only for HTTPS URLs;
    /// SSH continues to use the host service account's SSH agent/configuration.</summary>
    private async Task<Uri?> TryGetPushRemoteUriAsync(string gitPath, string repoPath, string remoteName, CancellationToken cancellationToken)
    {
        var urlResult = await RunGitAsync(gitPath, repoPath, ["remote", "get-url", "--push", remoteName], cancellationToken);
        if (!urlResult.Success)
        {
            var remotes = await RunGitAsync(gitPath, repoPath, ["remote"], cancellationToken);
            var onlyRemote = remotes.Success
                ? remotes.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2).ToArray()
                : [];
            if (onlyRemote.Length != 1) return null;
            urlResult = await RunGitAsync(gitPath, repoPath, ["remote", "get-url", "--push", onlyRemote[0]], cancellationToken);
        }

        return urlResult.Success && Uri.TryCreate(urlResult.Output.Trim(), UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    private async Task<GitCredentialRequest?> GetStoredCredentialsAsync(Guid userId, Uri remoteUri, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = CredentialKey(remoteUri);
        var setting = await db.Set<AppSetting>().SingleOrDefaultAsync(item =>
            item.UserId == userId && item.Scope == AppSettingsScope.User && item.ScopeId == userId &&
            item.AppId == CredentialAppId && item.Key == key, cancellationToken);
        if (setting is null) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredGitCredential>(setting.ValueJson);
            if (stored is null || string.IsNullOrWhiteSpace(stored.Username) || string.IsNullOrWhiteSpace(stored.ProtectedPassword))
                return null;
            return new GitCredentialRequest(stored.Username, _credentialProtector.Unprotect(stored.ProtectedPassword));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load a stored Git credential for the current user.");
            return null;
        }
    }

    private async Task SaveCredentialsAsync(Guid userId, Uri remoteUri, GitCredentialRequest credentials, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = CredentialKey(remoteUri);
        var setting = await db.Set<AppSetting>().SingleOrDefaultAsync(item =>
            item.UserId == userId && item.Scope == AppSettingsScope.User && item.ScopeId == userId &&
            item.AppId == CredentialAppId && item.Key == key, cancellationToken);

        var value = JsonSerializer.Serialize(new StoredGitCredential(credentials.Username, _credentialProtector.Protect(credentials.Password)));
        if (setting is null)
        {
            db.Add(new AppSetting
            {
                UserId = userId,
                Scope = AppSettingsScope.User,
                ScopeId = userId,
                AppId = CredentialAppId,
                Key = key,
                ValueJson = value,
                SchemaVersion = 1,
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            setting.ValueJson = value;
            setting.SchemaVersion = 1;
            setting.Revision++;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string CredentialKey(Uri remoteUri)
    {
        var identity = remoteUri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return "https-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private sealed record StoredGitCredential(string Username, string ProtectedPassword);

    public async Task<IReadOnlyList<GitCommitDto>> GetLogAsync(Guid id, Guid userId, int limit = 200, int skip = 0,
        GitLogQuery? query = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        // tformat 会在每条 commit 后自动追加换行（不含最后一条尾部多余空行）；字段间用 \x01(SOH) 分隔，避免与 subject/body 中的制表符/空格冲突
        var format = "%H%x01%h%x01%an%x01%ae%x01%aI%x01%s%x01%b";
        limit = Math.Clamp(limit, 1, 500);
        skip = Math.Max(skip, 0);
        var args = new List<string> { "log", $"--pretty=tformat:{format}", "--date=iso-strict", $"-n {limit}", $"--skip={skip}" };
        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            args.Add($"--grep={query.Search}");
            if (!query.CaseSensitive) args.Add("--regexp-ignore-case");
            if (!query.UseRegex) args.Add("--fixed-strings");
        }
        if (!string.IsNullOrWhiteSpace(query?.Author))
        {
            args.Add($"--author={query.Author}");
            if (!query.CaseSensitive) args.Add("--regexp-ignore-case");
        }
        switch (query?.DateRange)
        {
            case "today": args.Add("--since=midnight"); break;
            case "week": args.Add("--since=7 days ago"); break;
            case "month": args.Add("--since=30 days ago"); break;
            case null or "all": break;
            default: throw new ArgumentException("Unsupported log date range.", nameof(query));
        }
        if (!string.IsNullOrWhiteSpace(query?.Reference))
        {
            if (query.Reference.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("Invalid Git reference.", nameof(query));
            var verify = await RunGitAsync(gitPath, repo.Path, ["rev-parse", "--verify", $"{query.Reference}^{{commit}}"], cancellationToken);
            if (!verify.Success) throw new ArgumentException("The selected branch no longer exists.", nameof(query));
            args.Add(query.Reference);
        }
        if (!string.IsNullOrWhiteSpace(query?.Path))
        {
            if (!IsPathSafe(repo.Path, query.Path))
                throw new ArgumentException("Path outside repository.", nameof(query));
            args.Add("--");
            args.Add(query.Path);
        }
        var result = await RunGitAsync(gitPath, repo.Path, args, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git log failed: {result.Error}");

        var commits = new List<GitCommitDto>();
        // 先按行拆分每条 commit，再对单条 commit 按 SOH(\x01) 拆 7 个字段 — body 为空也不会丢失字段占位
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            // 限制最多拆 7 段，第 7 段(body)可包含后续的分隔符字符
            var parts = line.Split('\x01', 7, StringSplitOptions.None);
            if (parts.Length < 6) continue;
            var sha = parts[0].Trim();
            var shortSha = parts[1].Trim();
            var author = parts[2];
            var email = parts[3];
            var date = parts[4];
            var subject = parts[5];
            var body = parts.Length > 6 ? parts[6] : null;
            if (string.IsNullOrEmpty(body)) body = null;
            if (string.IsNullOrEmpty(sha)) continue;
            commits.Add(new GitCommitDto(sha, shortSha, author, email, date, subject, body));
        }
        return commits;
    }

    public async Task<GitCommitDetailDto> GetCommitDetailAsync(Guid id, Guid userId, string sha, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sha) || sha.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException("A commit SHA is required.", nameof(sha));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();

        var format = "%H%x00%an%x00%aI%x00%s%x00%b%x00%P";
        var commitResult = await RunGitAsync(gitPath, repo.Path, ["show", "-s", $"--format={format}", sha], cancellationToken);
        if (!commitResult.Success)
            throw new InvalidOperationException($"git show failed: {commitResult.Error}");

        var fields = commitResult.Output.Split('\0');
        if (fields.Length < 6 || string.IsNullOrWhiteSpace(fields[0]))
            throw new InvalidOperationException("git show returned an invalid commit record.");

        var filesResult = await RunGitAsync(gitPath, repo.Path,
            ["diff-tree", "--root", "--no-commit-id", "--name-status", "-r", "-M", sha], cancellationToken);
        if (!filesResult.Success)
            throw new InvalidOperationException($"git diff-tree failed: {filesResult.Error}");

        var changedFiles = ParseChangedFiles(filesResult.Output);
        var body = fields[4].TrimEnd('\r', '\n');
        return new GitCommitDetailDto(
            fields[0].Trim(), fields[1], fields[2], fields[3],
            fields[5].Split(' ', StringSplitOptions.RemoveEmptyEntries), changedFiles,
            string.IsNullOrEmpty(body) ? null : body);
    }

    public async Task<GitDiffDto> GetDiffAsync(Guid id, Guid userId, string path, bool staged = false, string? @ref = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        if (!IsPathSafe(repo.Path, path))
            throw new ArgumentException("Path outside repository.", nameof(path));

        var args = new List<string> { "diff", "--no-color" };
        if (staged) args.Add("--cached");
        if (!string.IsNullOrEmpty(@ref)) args.Add(@ref);
        args.Add("--");
        args.Add(path);

        var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
        var patch = result.Output;
        var truncated = false;
        if (patch.Length > MaxDiffPatchSize)
        {
            var statArgs = new List<string> { "diff", "--stat" };
            if (staged) statArgs.Add("--cached");
            if (!string.IsNullOrEmpty(@ref)) statArgs.Add(@ref);
            statArgs.Add("--");
            statArgs.Add(path);
            var statResult = await RunGitAsync(gitPath, repo.Path, [.. statArgs], cancellationToken);
            patch = statResult.Output;
            truncated = true;
        }

        var numstatArgs = new List<string> { "diff", "--numstat" };
        if (staged) numstatArgs.Add("--cached");
        if (!string.IsNullOrEmpty(@ref)) numstatArgs.Add(@ref);
        numstatArgs.Add("--");
        numstatArgs.Add(path);
        var numstat = await RunGitAsync(gitPath, repo.Path, [.. numstatArgs], cancellationToken);
        var (additions, deletions) = ParseNumstat(numstat.Output);

        var isBinary = patch.Contains("Binary files", StringComparison.OrdinalIgnoreCase) ||
                       patch.Contains("GIT binary patch", StringComparison.OrdinalIgnoreCase);

        return new GitDiffDto(path, null, isBinary ? "" : patch, additions, deletions, isBinary, truncated);
    }

    public async Task<GitOperationResult> RevertAsync(Guid id, Guid userId, GitRevertRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "revert" };
            if (request.NoCommit) args.Add("--no-commit");
            args.Add(request.Sha);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(result.Success || conflicts is not null, "revert",
                Conflicts: conflicts, Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> ResolveConflictsAsync(Guid id, Guid userId, GitResolveRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            foreach (var path in request.Paths)
                if (!IsPathSafe(repo.Path, path))
                    return new GitOperationResult(false, "resolve", Message: $"Path outside repository: {path}");

            if (request.Paths.Count > 0)
            {
                var addArgs = new List<string> { "add", "--" };
                addArgs.AddRange(request.Paths);
                var addResult = await RunGitAsync(gitPath, repo.Path, [.. addArgs], cancellationToken);
                if (!addResult.Success)
                    return new GitOperationResult(false, "resolve", Message: addResult.Error);
            }

            if (request.ContinueMerge)
            {
                var mergeHeadExists = File.Exists(Path.Combine(repo.Path, ".git", "MERGE_HEAD"));
                if (mergeHeadExists)
                {
                    var contResult = await RunGitAsync(gitPath, repo.Path, ["merge", "--continue"], cancellationToken);
                    var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
                    return new GitOperationResult(contResult.Success, "resolve",
                        Conflicts: conflicts, Message: contResult.Success ? null : contResult.Error);
                }
                var rebaseResult = await RunGitAsync(gitPath, repo.Path, ["rebase", "--continue"], cancellationToken);
                var remainingConflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
                return new GitOperationResult(rebaseResult.Success, "resolve",
                    Conflicts: remainingConflicts, Message: rebaseResult.Success ? null : rebaseResult.Error);
            }
            return new GitOperationResult(true, "resolve");
        });
    }

    public async Task<GitOperationResult> ResetAsync(Guid id, Guid userId, GitResetRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sha) || request.Sha.StartsWith("-", StringComparison.Ordinal))
            return new GitOperationResult(false, "reset", Message: "A commit SHA is required.");

        var mode = (request.Mode ?? "mixed").Trim().ToLowerInvariant();
        if (mode is not ("soft" or "mixed"))
            return new GitOperationResult(false, "reset", Message: "Only soft and mixed reset modes are supported.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["reset", $"--{mode}", request.Sha], cancellationToken);
            return new GitOperationResult(result.Success, "reset", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> RestoreAsync(Guid id, Guid userId, GitRestoreRequest request, CancellationToken cancellationToken = default)
    {
        if (!ArePathsSafe(request.Paths, out var pathError))
            return new GitOperationResult(false, "restore", Message: pathError);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        if (request.Paths.Any(path => !IsPathSafe(repo.Path, path)))
            return new GitOperationResult(false, "restore", Message: "Path outside repository.");

        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "restore", "--worktree" };
            if (!string.IsNullOrWhiteSpace(request.Source)) args.Add($"--source={request.Source}");
            args.Add("--");
            args.AddRange(request.Paths);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            return new GitOperationResult(result.Success, "restore", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> StageAsync(Guid id, Guid userId, GitStageRequest request, CancellationToken cancellationToken = default)
    {
        if (!ArePathsSafe(request.Paths, out var pathError))
            return new GitOperationResult(false, "stage", Message: pathError);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        if (request.Paths.Any(path => !IsPathSafe(repo.Path, path)))
            return new GitOperationResult(false, "stage", Message: "Path outside repository.");

        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "add", "--" };
            args.AddRange(request.Paths);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            return new GitOperationResult(result.Success, "stage", Message: result.Success ? null : result.Error);
        });
    }

    public async Task<GitOperationResult> UnstageAsync(Guid id, Guid userId, GitUnstageRequest request, CancellationToken cancellationToken = default)
    {
        if (!ArePathsSafe(request.Paths, out var pathError))
            return new GitOperationResult(false, "unstage", Message: pathError);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        if (request.Paths.Any(path => !IsPathSafe(repo.Path, path)))
            return new GitOperationResult(false, "unstage", Message: "Path outside repository.");

        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var args = new List<string> { "restore", "--staged", "--" };
            args.AddRange(request.Paths);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);

            // An initial repository has no HEAD, so `restore --staged` cannot obtain an index source.
            // Removing just the index entries is the equivalent safe unstage operation in that state.
            if (!result.Success && result.Error.Contains("could not resolve HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackArgs = new List<string> { "rm", "--cached", "--ignore-unmatch", "--" };
                fallbackArgs.AddRange(request.Paths);
                result = await RunGitAsync(gitPath, repo.Path, [.. fallbackArgs], cancellationToken);
            }
            return new GitOperationResult(result.Success, "unstage", Message: result.Success ? null : result.Error);
        });
    }

    // ── 路径探测与初始化 ──

    public async Task<GitRepositoryProbeDto> ProbeRepositoryAsync(string path, Guid userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Repository path must not be empty.", nameof(path));
        if (!Path.IsPathRooted(path))
            throw new ArgumentException("Repository path must be absolute.", nameof(path));
        if (!Directory.Exists(path))
            throw new ArgumentException("Repository path does not exist.", nameof(path));

        var gitPath = gitCli.ResolveGitPath()
            ?? throw new InvalidOperationException("Git executable not found on the host.");

        // 是否是 git 仓库
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

    // ── 远程仓库（remote）管理 ──

    public async Task<IReadOnlyList<GitRemoteDto>> ListRemotesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await GetRemotesAsync(gitPath, repo.Path, cancellationToken);
    }

    public async Task<GitOperationResult> AddRemoteAsync(Guid id, Guid userId, GitRemoteRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Url))
            return new GitOperationResult(false, "add-remote", Message: "Remote name and URL are required.");
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["remote", "add", request.Name, request.Url], cancellationToken);
            if (!result.Success)
                return new GitOperationResult(false, "add-remote", Message: result.Error);
            if (!string.IsNullOrEmpty(request.PushUrl))
            {
                var pushResult = await RunGitAsync(gitPath, repo.Path,
                    ["remote", "set-url", "--push", request.Name, request.PushUrl], cancellationToken);
                if (!pushResult.Success)
                    return new GitOperationResult(false, "add-remote", Message: $"Remote added but push URL update failed: {pushResult.Error}");
            }
            return new GitOperationResult(true, "add-remote");
        });
    }

    public async Task<GitOperationResult> UpdateRemoteAsync(Guid id, Guid userId, string name, GitRemoteRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(request.Url))
            return new GitOperationResult(false, "update-remote", Message: "Remote name and URL are required.");
        return await WithWriteLockAsync(id, async () =>
        {
            // 若新旧名不同，先重命名；同名则只是 set-url
            if (!string.Equals(name, request.Name, StringComparison.Ordinal))
            {
                var rename = await RunGitAsync(gitPath, repo.Path, ["remote", "rename", name, request.Name], cancellationToken);
                if (!rename.Success)
                    return new GitOperationResult(false, "update-remote", Message: rename.Error);
                name = request.Name;
            }
            var setUrl = await RunGitAsync(gitPath, repo.Path, ["remote", "set-url", name, request.Url], cancellationToken);
            if (!setUrl.Success)
                return new GitOperationResult(false, "update-remote", Message: setUrl.Error);
            if (!string.IsNullOrEmpty(request.PushUrl))
            {
                var setPush = await RunGitAsync(gitPath, repo.Path,
                    ["remote", "set-url", "--push", name, request.PushUrl], cancellationToken);
                if (!setPush.Success)
                    return new GitOperationResult(false, "update-remote", Message: $"Fetch URL updated but push URL update failed: {setPush.Error}");
            }
            return new GitOperationResult(true, "update-remote");
        });
    }

    public async Task<GitOperationResult> RemoveRemoteAsync(Guid id, Guid userId, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        if (string.IsNullOrWhiteSpace(name))
            return new GitOperationResult(false, "remove-remote", Message: "Remote name is required.");
        return await WithWriteLockAsync(id, async () =>
        {
            var result = await RunGitAsync(gitPath, repo.Path, ["remote", "remove", name], cancellationToken);
            return new GitOperationResult(result.Success, "remove-remote", Message: result.Success ? null : result.Error);
        });
    }

    private async Task<IReadOnlyList<GitRemoteDto>> GetRemotesAsync(string gitPath, string repoPath, CancellationToken cancellationToken)
    {
        // 注意：--no-color 是 git 全局选项，必须放在子命令之前；而 git 在 stdout 重定向
        // 时会自动禁用彩色输出，所以这里直接省略即可，避免子命令不认该参数导致命令失败。
        var result = await RunGitAsync(gitPath, repoPath, ["remote", "-v"], cancellationToken);
        if (!result.Success)
            return Array.Empty<GitRemoteDto>();

        // git remote -v 输出形如:
        //   origin  https://example.com/repo.git (fetch)
        //   origin  git@example.com:repo.git (push)
        //   upstream        https://example.com/up.git (fetch)
        //   upstream        https://example.com/up.git (push)
        // 名字与 URL 之间用制表符或多个空格分隔；行尾的 (fetch)/(push) 表示 URL 类型
        var remotes = new Dictionary<string, (string? Fetch, string? Push)>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            // 行尾标记
            const string fetchTag = "(fetch)";
            const string pushTag = "(push)";
            bool isPush = trimmed.EndsWith(pushTag, StringComparison.Ordinal);
            bool isFetch = trimmed.EndsWith(fetchTag, StringComparison.Ordinal);
            if (!isPush && !isFetch) continue;
            var body = trimmed[..^(isPush ? pushTag.Length : fetchTag.Length)].Trim();
            // 取第一个空白分隔
            var sep = -1;
            for (var i = 0; i < body.Length; i++)
            {
                if (char.IsWhiteSpace(body[i])) { sep = i; break; }
            }
            if (sep <= 0) continue;
            var remoteName = body[..sep].Trim();
            var url = body[sep..].Trim();
            if (string.IsNullOrEmpty(remoteName) || string.IsNullOrEmpty(url)) continue;

            if (!remotes.TryGetValue(remoteName, out var entry))
                entry = (null, null);
            if (isPush) entry = (entry.Fetch, url);
            else entry = (url, entry.Push);
            remotes[remoteName] = entry;
        }

        return remotes
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new GitRemoteDto(kv.Key, kv.Value.Fetch ?? string.Empty, kv.Value.Push))
            .ToArray();
    }

    // ── Helpers ──

    private string ResolveGitPath() => gitCli.ResolveGitPath() ?? "";
    private string ResolveGitPathOrThrow() => gitCli.ResolveGitPath()
        ?? throw new InvalidOperationException("Git executable not found on the host.");

    private static async Task<GitRepository> GetRepoOrThrowAsync(RemoteOsDbContext db, Guid id, Guid userId, CancellationToken cancellationToken)
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
        var status = ParseStatus(result.Output);
        var uncommitted = status.Staged.Count + status.Unstaged.Count + status.Untracked.Count + status.Conflicts.Count;
        return (status.Branch, status.Upstream, status.Ahead, status.Behind, status.IsDetached, uncommitted);
    }

    private static GitStatusDto ParseStatus(string output)
    {
        string branch = "unknown";
        string? upstream = null;
        int ahead = 0, behind = 0;
        bool isDetached = false;
        var staged = new List<GitFileChangeDto>();
        var unstaged = new List<GitFileChangeDto>();
        var untracked = new List<GitFileChangeDto>();
        var conflicts = new List<GitFileChangeDto>();

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("# branch.head"))
            {
                branch = line["# branch.head".Length..].Trim();
                if (branch == "(detached)") { isDetached = true; branch = "HEAD (detached)"; }
            }
            else if (line.StartsWith("# branch.upstream"))
            {
                upstream = line["# branch.upstream".Length..].Trim();
                if (string.IsNullOrEmpty(upstream)) upstream = null;
            }
            else if (line.StartsWith("# branch.ab"))
            {
                var abPart = line["# branch.ab".Length..].Trim();
                var abParts = abPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (abParts.Length >= 2)
                {
                    ahead = int.TryParse(abParts[0].TrimStart('+'), out var a) ? a : 0;
                    behind = int.TryParse(abParts[1].TrimStart('-'), out var b) ? b : 0;
                }
            }
            else if (line.StartsWith("u "))
            {
                // Unmerged entry: "u XY sub mH mI mW hH hI path" — path 仍为最后一段
                var (path, _) = TakePathAfterNSpaces(line, 10);
                if (!string.IsNullOrEmpty(path))
                    conflicts.Add(new GitFileChangeDto(path, Staged: false, Status: "conflicted"));
            }
            else if (line.StartsWith("1 ") || line.StartsWith("2 "))
            {
                // Type 1: "1 XY sub mH mI mW hH hI path" — 8 个空格字段 + 路径
                // Type 2: "2 XY sub mH mI mW hH hI score path⇥orig_path" — 9 个空格字段 + 路径(可能 TAB+原始路径)
                var isRename = line[0] == '2';
                var (path, origPath) = TakePathAfterNSpaces(line, isRename ? 9 : 8);
                if (string.IsNullOrEmpty(path)) continue;

                var xyText = line.Length > 3 ? line[2..4] : "  ";
                var x = xyText[0]; // staged side
                var y = xyText[1]; // unstaged side

                var status = MapStatusChar(y != '.' && y != ' ' ? y : x);

                // X = staged status: ' ' 无、'.' 无 之外的字母表示 staged 改动
                if (x != ' ' && x != '.' && x != '?')
                    staged.Add(new GitFileChangeDto(path, Staged: true, Status: MapStatusChar(x), OldPath: origPath));
                // Y = unstaged status: ' ' 无、'.' 无
                if (y != ' ' && y != '.' && y != '?')
                    unstaged.Add(new GitFileChangeDto(path, Staged: false, Status: MapStatusChar(y), OldPath: origPath));
            }
            else if (line.StartsWith("? "))
            {
                var filePath = line[2..].Trim();
                if (!string.IsNullOrEmpty(filePath))
                {
                    // Handle quoted paths (git quotes paths with special chars)
                    if (filePath.StartsWith('"') && filePath.EndsWith('"'))
                        filePath = UnquotePath(filePath);
                    untracked.Add(new GitFileChangeDto(filePath, Staged: false, Status: "untracked"));
                }
            }
        }
        return new GitStatusDto(branch, staged, unstaged, untracked, conflicts, upstream, ahead, behind, isDetached);
    }

    /// <summary>从 porcelain v2 状态行取最后一段路径（可能含空格）：跳过前 n 个空格分隔的字段，剩余即路径。
    /// Type 2（rename/copy）里路径字段格式为 path⇥orig_path，用 TAB 分隔；返回 (path, origPath_or_null)。</summary>
    private static (string Path, string? OrigPath) TakePathAfterNSpaces(string line, int spaceFieldCount)
    {
        var remaining = line.AsSpan();
        for (int i = 0; i < spaceFieldCount; i++)
        {
            // 跳过一个字段：直到空格
            var sp = remaining.IndexOf(' ');
            if (sp < 0) return ("", null); // 字段不足
            remaining = remaining[(sp + 1)..];
            // 跳过连续空格（通常只有一个）
            while (remaining.Length > 0 && remaining[0] == ' ') remaining = remaining[1..];
        }

        var tail = remaining.ToString();
        // Handle quoted paths
        if (tail.StartsWith('"') && tail.EndsWith('"'))
            tail = UnquotePath(tail);
        
        // type 2 的 rename/copy 行：路径部分是 path⇥orig_path（TAB 分隔）
        var tab = tail.IndexOf('\t');
        if (tab >= 0)
        {
            var path = tail[..tab].Trim();
            var orig = tail[(tab + 1)..].Trim();
            return (path, string.IsNullOrEmpty(orig) ? null : orig);
        }
        return (tail.Trim(), null);
    }

    /// <summary>Unquote a git path that was quoted because it contains special characters.
    /// Git uses C-style quoting with backslash escapes, and (when core.quotePath=true, the default)
    /// encodes each non-ASCII byte as a 3-digit octal escape, e.g. the en-dash in "Jaya – Cross Plat.pptx"
    /// (U+2013, UTF-8 bytes E2 80 93) is emitted as "Jaya \342\200\223 Cross Plat.pptx".
    /// We therefore collect raw bytes (resolving \a \b \f \n \r \t \\ \" and \nnn octal), then decode the
    /// resulting byte array as UTF-8 — this correctly reconstructs paths with en-dash, em-dash, CJK, emoji, etc.</summary>
    private static string UnquotePath(string path)
    {
        if (!path.StartsWith('"') || !path.EndsWith('"'))
            return path;

        var inner = path[1..^1];
        var bytes = new List<byte>(inner.Length);
        int i = 0;
        while (i < inner.Length)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                var next = inner[i + 1];
                switch (next)
                {
                    case 'a': bytes.Add(0x07); i += 2; break;
                    case 'b': bytes.Add(0x08); i += 2; break;
                    case 'f': bytes.Add(0x0C); i += 2; break;
                    case 'n': bytes.Add(0x0A); i += 2; break;
                    case 'r': bytes.Add(0x0D); i += 2; break;
                    case 't': bytes.Add(0x09); i += 2; break;
                    case '\\': bytes.Add(0x5C); i += 2; break;
                    case '"': bytes.Add(0x22); i += 2; break;
                    default:
                        // 3-digit octal escape \nnn (each n in 0..7) → single byte
                        if (IsOctDigit(next) && i + 3 < inner.Length
                            && IsOctDigit(inner[i + 2]) && IsOctDigit(inner[i + 3]))
                        {
                            bytes.Add((byte)((OctValue(next) << 6) | (OctValue(inner[i + 2]) << 3) | OctValue(inner[i + 3])));
                            i += 4;
                        }
                        else
                        {
                            // Unknown escape: keep the backslash literally
                            bytes.Add(0x5C);
                            i++;
                        }
                        break;
                }
            }
            else
            {
                // Plain char: emit its UTF-8 byte sequence
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(new[] { inner[i] }));
                i++;
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());

        static bool IsOctDigit(char c) => c >= '0' && c <= '7';
        static int OctValue(char c) => c - '0';
    }

    private static string MapStatusChar(char c) => c switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        _ => "modified"
    };

    private static (int ahead, int behind) ParseTrack(string track)
    {
        if (string.IsNullOrWhiteSpace(track)) return (0, 0);
        int ahead = 0, behind = 0;
        var aheadMatch = System.Text.RegularExpressions.Regex.Match(track, @"ahead (\d+)");
        var behindMatch = System.Text.RegularExpressions.Regex.Match(track, @"behind (\d+)");
        if (aheadMatch.Success) ahead = int.Parse(aheadMatch.Groups[1].Value);
        if (behindMatch.Success) behind = int.Parse(behindMatch.Groups[1].Value);
        return (ahead, behind);
    }

    private static (int additions, int deletions) ParseNumstat(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return (0, 0);
        var parts = output.Trim().Split('\t');
        if (parts.Length < 2) return (0, 0);
        int additions = int.TryParse(parts[0], out var a) ? a : 0;
        int deletions = int.TryParse(parts[1], out var d) ? d : 0;
        return (additions, deletions);
    }

    private static IReadOnlyList<GitFileChangeDto> ParseChangedFiles(string output)
    {
        var files = new List<GitFileChangeDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;
            var status = parts[0];
            var kind = MapStatusChar(status[0]);
            if ((status[0] is 'R' or 'C') && parts.Length >= 3)
                files.Add(new GitFileChangeDto(parts[2], parts[1], kind));
            else
                files.Add(new GitFileChangeDto(parts[1], Status: kind));
        }
        return files;
    }

    private static bool IsPathSafe(string repoRoot, string path)
    {
        if (!Path.IsPathRooted(path))
            path = Path.Combine(repoRoot, path);
        var fullRepoRoot = Path.GetFullPath(repoRoot);
        var fullTarget = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRepoRoot, fullTarget);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool ArePathsSafe(IReadOnlyList<string>? paths, out string? error)
    {
        if (paths is null || paths.Count == 0)
        {
            error = "At least one path is required.";
            return false;
        }
        if (paths.Any(string.IsNullOrWhiteSpace))
        {
            error = "Paths must not be empty.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool IsCredentialError(string error) =>
        error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("fatal: could not read", StringComparison.OrdinalIgnoreCase) ||
        HasInteractiveCredentialPrompt(error);

    private static bool HasInteractiveCredentialPrompt(string error) =>
        error.Contains("Username for '", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Password for '", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<string>?> TryGetConflictPathsAsync(string gitPath, string repoPath, CancellationToken cancellationToken)
    {
        var statusResult = await RunGitAsync(gitPath, repoPath, ["status", "--porcelain=v2", "-uall"], cancellationToken);
        if (!statusResult.Success) return null;
        var conflicts = new List<string>();
        foreach (var line in statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("u "))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 11)
                    conflicts.Add(parts[10]);
            }
        }
        return conflicts.Count > 0 ? conflicts : null;
    }

    private async Task<GitOperationResult> WithWriteLockAsync(Guid repoId, Func<Task<GitOperationResult>> operation)
    {
        var semaphore = _writeLocks.GetOrAdd(repoId, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(SemaphoreTimeout))
            return new GitOperationResult(false, "busy", Message: "Repository is busy, another operation is in progress.");
        try { return await operation(); }
        finally { semaphore.Release(); }
    }

    private async Task<CommandResult> RunGitAsync(
        string gitPath,
        string workingDir,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        GitCredentialRequest? credentials = null)
    {
        var operation = arguments.FirstOrDefault() ?? "unknown";
        try
        {
            logger.LogDebug("Starting git operation {GitOperation}.", operation);
            using var askPass = credentials is null ? null : new GitAskPassScope(credentials);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(gitPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir
                }
            };
            // A remote server has no interactive terminal to hand over to the desktop client.
            // Fail promptly when no saved/supplied credential exists; when one is available,
            // Git obtains it from the temporary askpass process below.
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            if (askPass is not null)
            {
                process.StartInfo.Environment["GIT_ASKPASS"] = askPass.Path;
                process.StartInfo.Environment["GIT_ASKPASS_REQUIRE"] = "force";
                process.StartInfo.Environment["REMOTEOS_GIT_ASKPASS_USERNAME"] = credentials!.Username;
                process.StartInfo.Environment["REMOTEOS_GIT_ASKPASS_PASSWORD"] = credentials.Password;
            }
            foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start())
                return new CommandResult(false, "", "start_failed");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            // Do not cancel the pipe reads with the operation timeout: after killing a
            // timed-out process we still need its final stderr to diagnose an interactive
            // credential prompt. Never write stderr itself to logs because it can contain
            // a username or a remote URL.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cts.Token);
                var output = await outputTask;
                var error = await errorTask;
                var success = process.ExitCode == 0;
                if (!success)
                {
                    logger.LogWarning(
                        "Git operation {GitOperation} failed with exit code {ExitCode}. InteractiveCredentialPrompt={InteractiveCredentialPrompt}",
                        operation, process.ExitCode, HasInteractiveCredentialPrompt(error));
                }
                return new CommandResult(success, output, error);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                var error = await errorTask;
                _ = await outputTask;
                logger.LogWarning(
                    "Git operation {GitOperation} was canceled or timed out. InteractiveCredentialPrompt={InteractiveCredentialPrompt}",
                    operation, HasInteractiveCredentialPrompt(error));
                return new CommandResult(false, "", "timeout");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new CommandResult(false, "", "git_not_found");
        }
    }

    /// <summary>Creates a short-lived helper used only by one child Git process. Secrets live in that
    /// process environment rather than in its command line, and the helper file is deleted immediately.</summary>
    private sealed class GitAskPassScope : IDisposable
    {
        public string Path { get; }

        public GitAskPassScope(GitCredentialRequest credentials)
        {
            var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"remoteos-git-askpass-{Guid.NewGuid():N}{extension}");
            var script = OperatingSystem.IsWindows()
                ? "@echo off\r\nset \"prompt=%~1\"\r\necho %prompt% | findstr /I /C:\"username\" >nul\r\nif not errorlevel 1 ( <nul set /p \"=%REMOTEOS_GIT_ASKPASS_USERNAME%\" & exit /b 0 )\r\necho %prompt% | findstr /I /C:\"password\" >nul\r\nif not errorlevel 1 ( <nul set /p \"=%REMOTEOS_GIT_ASKPASS_PASSWORD%\" & exit /b 0 )\r\nexit /b 1\r\n"
                : "#!/bin/sh\ncase \"$1\" in\n  *[Uu]sername*) printf '%s\\n' \"$REMOTEOS_GIT_ASKPASS_USERNAME\" ;;\n  *[Pp]assword*) printf '%s\\n' \"$REMOTEOS_GIT_ASKPASS_PASSWORD\" ;;\n  *) exit 1 ;;\nesac\n";
            File.WriteAllText(Path, script, Encoding.UTF8);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* best effort cleanup of the one-shot helper */ }
        }
    }

    private sealed record CommandResult(bool Success, string Output, string Error);
}
