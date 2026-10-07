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
            switch (request.Strategy.ToLowerInvariant())
            {
                case "merge": args.Add("--no-rebase"); break;
                case "rebase": args.Add("--rebase"); break;
                case "ff-only": args.Add("--ff-only"); break;
                default: return new GitOperationResult(false, "pull", Message: "Unknown pull strategy.");
            }
            if (!string.IsNullOrEmpty(request.Remote)) args.Add(request.Remote);
            if (!string.IsNullOrEmpty(request.Refspec)) args.Add(request.Refspec);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
            var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(result.Success, "pull",
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

}
