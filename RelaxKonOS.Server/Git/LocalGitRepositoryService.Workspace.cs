using System.Text;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Git;

public sealed partial class LocalGitRepositoryService
{
    private static string CheckedReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Length > 1024 || value.Any(char.IsControl))
            throw new ArgumentException("A valid Git reference is required.");
        return value;
    }

    private async Task<string> CommitShaAsync(string git, string root, string reference, CancellationToken ct)
    {
        var result = await RunGitAsync(git, root, ["rev-parse", "--verify", "--end-of-options", CheckedReference(reference) + "^{commit}"], ct);
        if (!result.Success || result.Output.Trim() is not { Length: 40 or 64 } sha || !sha.All(Uri.IsHexDigit))
            throw new ArgumentException("The selected revision is not a commit.");
        return sha;
    }

    public async Task<IReadOnlyList<GitTagDto>> ListTagsAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var result = await RunGitAsync(ResolveGitPathOrThrow(), repo.Path, ["for-each-ref", "--format=%(refname:strip=2)%09%(objectname)%09%(*objectname)", "refs/tags"], cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(parts => parts.Length == 3).Select(parts => new GitTagDto(parts[0], string.IsNullOrEmpty(parts[2]) ? parts[1] : parts[2])).ToArray();
    }

    public async Task<GitOperationResult> CreateTagAsync(Guid id, Guid userId, GitTagCreateRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var name = CheckedReference(request.Name);
            var valid = await RunGitAsync(git, repo.Path, ["check-ref-format", "refs/tags/" + name], cancellationToken);
            if (!valid.Success) return new(false, "tag", Message: "Invalid tag name.");
            var sha = await CommitShaAsync(git, repo.Path, request.Reference, cancellationToken);
            var result = await RunGitAsync(git, repo.Path, ["tag", "--", name, sha], cancellationToken);
            return new(result.Success, "tag", Message: result.Success ? null : result.Error);
        });
    }

    public Task<GitOperationResult> RebaseAsync(Guid id, Guid userId, GitReferenceRequest request, CancellationToken cancellationToken = default)
        => IntegrateReferenceAsync(id, userId, request, "rebase", cancellationToken);
    public Task<GitOperationResult> CherryPickAsync(Guid id, Guid userId, GitReferenceRequest request, CancellationToken cancellationToken = default)
        => IntegrateReferenceAsync(id, userId, request, "cherry-pick", cancellationToken);

    private async Task<GitOperationResult> IntegrateReferenceAsync(Guid id, Guid userId, GitReferenceRequest request, string operation, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await GetRepoOrThrowAsync(db, id, userId, ct);
        var git = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            if (await ConflictOperationNameAsync(git, repo.Path, ct) is not null)
                return new(false, operation, Message: "Finish or abort the operation already in progress first.");
            var sha = await CommitShaAsync(git, repo.Path, request.Reference, ct);
            var result = await RunGitAsync(git, repo.Path, [operation, sha], ct);
            return new(result.Success, operation, Conflicts: await TryGetConflictPathsAsync(git, repo.Path, ct), Message: result.Success ? null : result.Error);
        });
    }

    public async Task<IReadOnlyList<GitStashDto>> ListStashesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        return await ReadStashesAsync(ResolveGitPathOrThrow(), repo.Path, cancellationToken);
    }
    private async Task<IReadOnlyList<GitStashDto>> ReadStashesAsync(string git, string root, CancellationToken ct)
    {
        var result = await RunGitAsync(git, root, ["stash", "list", "--format=%gd%x09%H%x09%gs"], ct);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t', 3))
            .Where(parts => parts.Length == 3).Select(parts => new GitStashDto(parts[0], parts[1], parts[2])).ToArray();
    }
    public async Task<GitOperationResult> StashAsync(Guid id, Guid userId, GitStashRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Action is not ("save" or "apply" or "pop" or "drop")) throw new ArgumentException("Invalid stash action.");
        if (request.Message is { Length: > 1024 } || request.Message?.Any(char.IsControl) == true) throw new ArgumentException("Invalid stash message.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            if (await ConflictOperationNameAsync(git, repo.Path, cancellationToken) is not null
                || await TryGetConflictPathsAsync(git, repo.Path, cancellationToken) is { Count: > 0 })
                return new(false, "stash", Message: "Resolve the current Git operation first.");
            CommandResult result;
            if (request.Action == "save")
                result = await RunGitAsync(git, repo.Path, ["stash", "push", "--include-untracked", "--message", request.Message ?? "RelaxKonOS"], cancellationToken);
            else
            {
                var stash = (await ReadStashesAsync(git, repo.Path, cancellationToken)).FirstOrDefault(item => item.Sha == request.Sha);
                if (stash is null) return new(false, "stash", Message: "This stash no longer exists. Refresh the list.");
                if (request.Action == "drop") result = await RunGitAsync(git, repo.Path, ["stash", "drop", stash.Reference], cancellationToken);
                else
                {
                    result = await RunGitAsync(git, repo.Path, ["stash", "apply", stash.Sha], cancellationToken);
                    // Never drop the saved content when applying it failed or conflicted.
                    if (result.Success && request.Action == "pop")
                    {
                        var fresh = (await ReadStashesAsync(git, repo.Path, cancellationToken)).FirstOrDefault(item => item.Sha == stash.Sha);
                        if (fresh is not null) result = await RunGitAsync(git, repo.Path, ["stash", "drop", fresh.Reference], cancellationToken);
                    }
                }
            }
            return new(result.Success, "stash", Conflicts: await TryGetConflictPathsAsync(git, repo.Path, cancellationToken), Message: result.Success ? result.Output : result.Error);
        });
    }

    public async Task<IReadOnlyList<GitWorktreeDto>> ListWorktreesAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var result = await RunGitAsync(ResolveGitPathOrThrow(), repo.Path, ["worktree", "list", "--porcelain", "-z"], cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        var trees = new List<GitWorktreeDto>();
        string? path = null, branch = null; bool locked = false;
        foreach (var field in result.Output.Split('\0'))
        {
            if (field.StartsWith("worktree ", StringComparison.Ordinal)) { path = field[9..]; branch = null; locked = false; }
            else if (field.StartsWith("branch ", StringComparison.Ordinal)) branch = field.StartsWith("branch refs/heads/", StringComparison.Ordinal) ? field[18..] : field[7..];
            else if (field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal)) locked = true;
            else if (field.Length == 0 && path is not null) { trees.Add(new(path, branch, locked)); path = null; }
        }
        return trees;
    }
    private static string CheckedAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)
            || path.Replace('\\', '/').Split('/').Any(part => part == ".." || string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("An absolute destination outside .git is required.");
        return Path.GetFullPath(path);
    }
    public async Task<GitOperationResult> CreateWorktreeAsync(Guid id, Guid userId, GitWorktreeCreateRequest request, CancellationToken cancellationToken = default)
    {
        var path = CheckedAbsolutePath(request.Path);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var sha = await CommitShaAsync(git, repo.Path, request.Reference, cancellationToken);
            List<string> args = ["worktree", "add"];
            if (!string.IsNullOrWhiteSpace(request.NewBranch))
            {
                var branch = CheckedReference(request.NewBranch);
                var valid = await RunGitAsync(git, repo.Path, ["check-ref-format", "refs/heads/" + branch], cancellationToken);
                if (!valid.Success) return new(false, "worktree", Message: "Invalid branch name.");
                args.AddRange(["-b", branch]);
            }
            else args.Add("--detach");
            args.AddRange(["--", path, sha]);
            var result = await RunGitAsync(git, repo.Path, args, cancellationToken);
            return new(result.Success, "worktree", Message: result.Success ? null : result.Error);
        });
    }
    public async Task<GitOperationResult> RemoveWorktreeAsync(Guid id, Guid userId, GitWorktreeRemoveRequest request, CancellationToken cancellationToken = default)
    {
        var path = CheckedAbsolutePath(request.Path);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        return await WithWriteLockAsync(id, async () =>
        {
            // Git refuses the main, dirty and locked worktrees; never bypass it with --force.
            var result = await RunGitAsync(ResolveGitPathOrThrow(), repo.Path, ["worktree", "remove", "--", path], cancellationToken);
            return new(result.Success, "worktree", Message: result.Success ? null : result.Error);
        });
    }
    public async Task<GitPatchDto> ExportPatchAsync(Guid id, Guid userId, GitPatchRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        List<string> args;
        if (request.Reference is { Length: > 0 })
            args = ["show", "--format=", "--no-ext-diff", "--no-textconv", await CommitShaAsync(git, repo.Path, request.Reference, cancellationToken), "--"];
        else { args = ["diff", "--no-ext-diff", "--no-textconv"]; if (request.Staged) args.Add("--cached"); args.Add("--"); }
        var result = await RunGitAsync(git, repo.Path, args, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        if (Encoding.UTF8.GetByteCount(result.Output) > MaxDiffPatchSize || result.Output.Contains("Binary files ", StringComparison.Ordinal) || result.Output.Contains('\uFFFD'))
            throw new InvalidOperationException("This patch is too large or contains binary/non-UTF-8 content. Export it using a local Git tool.");
        return new(result.Output);
    }
    public async Task<GitOperationResult> ApplyPatchAsync(Guid id, Guid userId, GitPatchApplyRequest request, CancellationToken cancellationToken = default)
    {
        var path = CheckedAbsolutePath(request.Path);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            if (await ConflictOperationNameAsync(git, repo.Path, cancellationToken) is not null
                || await TryGetConflictPathsAsync(git, repo.Path, cancellationToken) is { Count: > 0 })
                return new(false, "apply-patch", Message: "Resolve the current Git operation first.");
            List<string> check = ["apply", "--check"];
            if (request.Stage) check.Add("--index");
            check.AddRange(["--", path]);
            var result = await RunGitAsync(git, repo.Path, check, cancellationToken);
            if (!result.Success) return new(false, "apply-patch", Message: result.Error);
            List<string> apply = ["apply"];
            if (request.Stage) apply.Add("--index");
            apply.AddRange(["--", path]);
            result = await RunGitAsync(git, repo.Path, apply, cancellationToken);
            return new(result.Success, "apply-patch", Message: result.Success ? null : result.Error);
        });
    }
    public async Task<GitCloneResult> CloneAsync(GitCloneRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var path = CheckedAbsolutePath(request.Path);
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("A destination folder is required.");
        if (string.IsNullOrWhiteSpace(request.Url) || request.Url.StartsWith('-') || request.Url.Contains("::", StringComparison.Ordinal)
            || request.Url.Any(char.IsControl) || request.Url.Length > 4096) throw new ArgumentException("Invalid clone URL.");
        // Run in an existing parent as the effective user; Git owns destination creation and rejects nonempty destinations.
        Uri? remote = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
        var credentials = request.Credentials ?? (remote is null ? null : await GetStoredCredentialsAsync(userId, remote, cancellationToken));
        var result = await RunGitAsync(ResolveGitPathOrThrow(), parent, ["clone", "--", request.Url, path], cancellationToken, credentials);
        if (!result.Success) return new(new(false, "clone", Message: result.Error, RequiresCredentials: remote is not null && IsCredentialError(result.Error)));
        if (remote is not null && credentials is { SaveCredentials: true }) await SaveCredentialsAsync(userId, remote, credentials, cancellationToken);
        var repo = await RegisterRepositoryAsync(new(Path.GetFileName(path), path), userId, cancellationToken);
        return new(new(true, "clone"), repo);
    }
}
