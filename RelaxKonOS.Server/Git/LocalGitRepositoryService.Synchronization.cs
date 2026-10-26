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
    public async Task<GitOperationResult> FetchAsync(Guid id, Guid userId, GitFetchRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            if (!ValidRemoteInput(request.Remote)) return new GitOperationResult(false, "fetch", Message: "Invalid remote.");
            var remoteUri = await GetFetchRemoteUriAsync(gitPath, repo.Path, request.Remote, null, cancellationToken);
            if (!ValidCredentials(request.Credentials)) return InvalidCredentials("fetch");
            var credentials = remoteUri is null ? null : request.Credentials ?? await GetStoredCredentialsAsync(userId, remoteUri, cancellationToken);
            var args = string.IsNullOrWhiteSpace(request.Remote) ? new[] { "fetch" } : new[] { "fetch", request.Remote };
            var result = await RunGitAsync(gitPath, repo.Path, args, cancellationToken, credentials);
            if (result.Success && remoteUri is not null && request.Credentials is { SaveCredentials: true })
                await SaveCredentialsAsync(userId, remoteUri, request.Credentials, cancellationToken);
            return new GitOperationResult(result.Success, "fetch",
                RequiresCredentials: remoteUri is not null && !result.Success && IsCredentialError(result.Error),
                CredentialHost: remoteUri?.Authority,
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
            if (!ValidRemoteInput(request.Remote) || (!string.IsNullOrEmpty(request.Refspec) && !IsSafeRefComponent(request.Refspec)))
                return new GitOperationResult(false, "pull", Message: "Invalid remote or refspec.");
            var currentBranch = await GetCurrentBranchAsync(gitPath, repo.Path, cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.Branch) &&
                !string.Equals(request.Branch, currentBranch, StringComparison.Ordinal))
                return await UpdateNonCurrentBranchAsync(userId, gitPath, repo.Path, request, cancellationToken);

            if (!string.IsNullOrEmpty(request.Refspec) && string.IsNullOrEmpty(request.Remote))
                return new GitOperationResult(false, "pull", Message: "A remote is required when a refspec is supplied.");

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
            var remoteUri = await GetFetchRemoteUriAsync(gitPath, repo.Path, request.Remote, currentBranch, cancellationToken);
            if (!ValidCredentials(request.Credentials)) return InvalidCredentials("pull");
            var credentials = remoteUri is null ? null : request.Credentials ?? await GetStoredCredentialsAsync(userId, remoteUri, cancellationToken);
            var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken, credentials);
            if (result.Success && remoteUri is not null && request.Credentials is { SaveCredentials: true })
                await SaveCredentialsAsync(userId, remoteUri, request.Credentials, cancellationToken);
            var conflicts = await TryGetConflictPathsAsync(gitPath, repo.Path, cancellationToken);
            return new GitOperationResult(result.Success, "pull",
                Conflicts: conflicts,
                RequiresCredentials: remoteUri is not null && !result.Success && IsCredentialError(result.Error),
                CredentialHost: remoteUri?.Authority,
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

            var target = await GetPushCredentialTargetAsync(gitPath, repo.Path, remote, cancellationToken);
            if (target.IsAmbiguous)
                return new GitOperationResult(false, "push", Message: "Push URLs use different credential identities. Configure a separate remote for each host or account.");
            var remoteUri = target.Uri;
            var suppliedCredentials = request?.Credentials;
            if (!ValidCredentials(suppliedCredentials))
            {
                return new GitOperationResult(false, "push", Message: "Username and access token are required.", RequiresCredentials: true);
            }

            var credentials = remoteUri is null ? null : suppliedCredentials;
            if (credentials is null && remoteUri is not null)
                credentials = await GetStoredCredentialsAsync(userId, remoteUri, cancellationToken);

            // Use an explicit refspec so a selected local branch can be pushed while another
            // branch remains checked out. This is equivalent to IDEA's Push on a non-HEAD branch.
            var result = await RunGitAsync(gitPath, repo.Path,
                ["push", remote, $"refs/heads/{localBranch}:refs/heads/{remoteBranch}"], cancellationToken, credentials);
            if (result.Success && suppliedCredentials is not null && request?.SaveCredentials != false && suppliedCredentials.SaveCredentials && remoteUri is not null)
                await SaveCredentialsAsync(userId, remoteUri, suppliedCredentials, cancellationToken);

            return new GitOperationResult(result.Success, "push",
                RequiresCredentials: remoteUri is not null && !result.Success && IsCredentialError(result.Error),
                CredentialHost: remoteUri?.Authority,
                Message: result.Success ? null : result.Error);
        });
    }

    /// <summary>Updates a local branch which is not checked out. Git cannot merge or rebase an
    /// inactive branch without changing the worktree, so this deliberately performs only a safe
    /// fast-forward after fetching its configured upstream.</summary>
    private async Task<GitOperationResult> UpdateNonCurrentBranchAsync(
        Guid userId, string gitPath, string repoPath, GitPullRequest request, CancellationToken cancellationToken)
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

        var remoteUri = await GetFetchRemoteUriAsync(gitPath, repoPath, remote, branch, cancellationToken);
        if (!ValidCredentials(request.Credentials)) return InvalidCredentials("update-branch");
        var credentials = remoteUri is null ? null : request.Credentials ?? await GetStoredCredentialsAsync(userId, remoteUri, cancellationToken);
        var fetch = await RunGitAsync(gitPath, repoPath, ["fetch", remote], cancellationToken, credentials);
        if (!fetch.Success)
            return new GitOperationResult(false, "update-branch",
                RequiresCredentials: remoteUri is not null && IsCredentialError(fetch.Error), CredentialHost: remoteUri?.Authority, Message: fetch.Error);

        if (remoteUri is not null && request.Credentials is { SaveCredentials: true })
            await SaveCredentialsAsync(userId, remoteUri, request.Credentials, cancellationToken);
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

    private static bool IsExternalGitTransport(string value) => value.Contains("::", StringComparison.Ordinal) &&
        (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git" or "file"));

    private static bool ValidRemoteInput(string? remote) => remote is null ||
        (remote.Length is > 0 and <= 4096 && !remote.StartsWith('-') && !remote.Any(char.IsControl) && !IsExternalGitTransport(remote));

    private static bool ValidCredentials(GitCredentialRequest? credentials) => credentials is null ||
        (!string.IsNullOrWhiteSpace(credentials.Username) && !string.IsNullOrWhiteSpace(credentials.Password) &&
         credentials.Username.Length <= 256 && credentials.Password.Length <= 16384 &&
         !credentials.Username.Any(char.IsControl) && !credentials.Password.Any(char.IsControl));

    private static GitOperationResult InvalidCredentials(string operation) =>
        new(false, operation, Message: "Username and access token are required.", RequiresCredentials: true);

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
    private async Task<(Uri? Uri, bool IsAmbiguous)> GetPushCredentialTargetAsync(string gitPath, string repoPath, string remoteName, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(gitPath, repoPath, ["remote", "get-url", "--push", "--all", remoteName], cancellationToken);
        if (!result.Success) return (null, false);
        var urls = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var uris = urls.Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null).ToArray();
        var https = uris.OfType<Uri>().ToArray();
        if (https.Length == 0) return (null, false);
        // Git pushes every pushurl using the same child environment. Only share an askpass token
        // when every destination has the same HTTPS authority and URL username.
        if (https.Length != urls.Length || https.Select(uri => CredentialKey(uri) + "\n" + uri.UserInfo.Split(':')[0]).Distinct(StringComparer.Ordinal).Count() != 1)
            return (null, true);
        return (https[0], false);
    }

}
