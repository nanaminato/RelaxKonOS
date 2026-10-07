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
            return new GitOperationResult(result.Success, "revert",
                Conflicts: conflicts, Message: result.Success ? null : result.Error);
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
            var args = new List<string> { "--literal-pathspecs", "add", "--" };
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
            var args = new List<string> { "--literal-pathspecs", "restore", "--staged", "--" };
            args.AddRange(request.Paths);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);

            // An initial repository has no HEAD, so `restore --staged` cannot obtain an index source.
            // Removing just the index entries is the equivalent safe unstage operation in that state.
            if (!result.Success && result.Error.Contains("could not resolve HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackArgs = new List<string> { "--literal-pathspecs", "rm", "--cached", "--ignore-unmatch", "--" };
                fallbackArgs.AddRange(request.Paths);
                result = await RunGitAsync(gitPath, repo.Path, [.. fallbackArgs], cancellationToken);
            }
            return new GitOperationResult(result.Success, "unstage", Message: result.Success ? null : result.Error);
        });
    }

}
