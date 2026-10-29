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
    /// <summary>Preview of a push to one concrete destination.
    /// <para>The ahead count cannot come from <c>%(upstream:track)</c>: a local branch that was never
    /// pushed has no upstream, so Git reports "ahead 0" no matter how many commits it holds, and a
    /// destination branch that does not exist yet yields no tracking ref at all. The push itself
    /// works in both cases (the explicit refspec creates the remote branch), so the preview resolves
    /// the destination first and then diffs against what that remote is known to contain.</para></summary>
    public async Task<GitPushPreviewDto> GetPushPreviewAsync(Guid id, Guid userId, string? localBranch = null,
        string? remote = null, string? remoteBranch = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();

        var branch = string.IsNullOrWhiteSpace(localBranch)
            ? await GetCurrentBranchAsync(gitPath, repo.Path, cancellationToken)
            : localBranch.Trim();
        if (string.IsNullOrWhiteSpace(branch) || !IsSafeRefComponent(branch))
            throw new ArgumentException("A valid local branch is required.", nameof(localBranch));

        var resolvedRemote = string.IsNullOrWhiteSpace(remote)
            ? await ResolveDefaultPushRemoteAsync(gitPath, repo.Path, branch, cancellationToken)
            : remote.Trim();
        if (!IsSafeRefComponent(resolvedRemote) && !string.Equals(resolvedRemote, ".", StringComparison.Ordinal))
            throw new ArgumentException("A valid remote name is required.", nameof(remote));

        var configuredMerge = await GetBranchConfigAsync(gitPath, repo.Path, branch, "merge", cancellationToken);
        var resolvedRemoteBranch = string.IsNullOrWhiteSpace(remoteBranch)
            ? StripHeadsPrefix(configuredMerge) ?? branch
            : remoteBranch.Trim();
        if (!IsSafeRefComponent(resolvedRemoteBranch))
            throw new ArgumentException("A valid remote branch is required.", nameof(remoteBranch));

        var localCommit = await RunGitAsync(gitPath, repo.Path,
            ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"], cancellationToken);
        if (!localCommit.Success)
        {
            // An unborn branch is a legitimate empty preview; a name that does not resolve at all is not.
            var symbolic = await RunGitAsync(gitPath, repo.Path, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
            if (symbolic.Success && string.Equals(symbolic.Output.Trim(), branch, StringComparison.Ordinal))
                return new GitPushPreviewDto(branch, resolvedRemote, resolvedRemoteBranch, false, 0, []);
            throw new ArgumentException($"Local branch '{branch}' does not exist.", nameof(localBranch));
        }

        // "." is Git's own spelling for the local repository, so its destination ref is a local branch.
        var isLocalRemote = string.Equals(resolvedRemote, ".", StringComparison.Ordinal);
        var targetRef = isLocalRemote
            ? $"refs/heads/{resolvedRemoteBranch}"
            : $"refs/remotes/{resolvedRemote}/{resolvedRemoteBranch}";
        var targetExists = (await RunGitAsync(gitPath, repo.Path,
            ["rev-parse", "--verify", "--quiet", $"{targetRef}^{{commit}}"], cancellationToken)).Success;

        // Existing destination: only the commits it lacks. New destination: everything this remote is
        // not already known to contain, which is exactly the set Git uploads when it creates the ref.
        string[] exclusion = targetExists
            ? ["--not", targetRef]
            : isLocalRemote ? ["--not", "--remotes"] : ["--not", $"--remotes={resolvedRemote}"];

        var localRef = $"refs/heads/{branch}";
        var countResult = await RunGitAsync(gitPath, repo.Path, ["rev-list", "--count", localRef, .. exclusion], cancellationToken);
        if (!countResult.Success || !int.TryParse(countResult.Output.Trim(), out var ahead))
            throw new InvalidOperationException($"The commits to push could not be read: {countResult.Error.Trim()}");

        limit = Math.Clamp(limit, 1, 500);
        IReadOnlyList<GitCommitDto> commits = [];
        if (ahead > 0)
        {
            var logResult = await RunGitAsync(gitPath, repo.Path,
                ["log", $"--pretty=tformat:{CommitLogFormat}", "--date=iso-strict", "--topo-order",
                 "-n", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), localRef, .. exclusion],
                cancellationToken);
            if (logResult.Success) commits = ParseCommitRecords(logResult.Output);
        }

        return new GitPushPreviewDto(branch, resolvedRemote, resolvedRemoteBranch, targetExists, ahead, commits);
    }

    /// <summary>Mirrors the destination choice <see cref="PushAsync"/> makes: the branch's push remote,
    /// then its fetch remote, then the only configured remote, then <c>origin</c>.</summary>
    private async Task<string> ResolveDefaultPushRemoteAsync(string gitPath, string repoPath, string branch, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { "pushRemote", "remote" })
        {
            var configured = await GetBranchConfigAsync(gitPath, repoPath, branch, key, cancellationToken);
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
        }

        var remotes = await RunGitAsync(gitPath, repoPath, ["remote"], cancellationToken);
        if (remotes.Success)
        {
            var names = remotes.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length > 0) return names[0];
        }
        return "origin";
    }
}
