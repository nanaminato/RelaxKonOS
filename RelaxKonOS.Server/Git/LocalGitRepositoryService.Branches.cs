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

public sealed partial class LocalGitRepositoryService
{
    public async Task<IReadOnlyList<GitBranchDto>> ListBranchesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        // Keep the full refname in addition to its display name.  A branch name is
        // allowed to contain '/', so it cannot tell us whether the ref is local or
        // remote (for example, a perfectly valid local branch is feature/login).
        var fmt = "%(refname)%09%(refname:short)%09%(upstream:short)%09%(upstream:track)%09%(HEAD)%09%(objectname)";
        var result = await RunGitAsync(gitPath, repo.Path,
            ["for-each-ref", $"--format={fmt}", "refs/heads", "refs/remotes"], cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"git for-each-ref failed: {result.Error}");

        var branches = new List<GitBranchDto>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 6) throw new InvalidOperationException("Invalid Git branch record.");
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
            branches.Add(new GitBranchDto(name, parts[5].Trim(), isRemote, isHead, false, upstream, ahead, behind));
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

}
