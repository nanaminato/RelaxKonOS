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
    public async Task<IReadOnlyList<GitCommitDto>> GetLogAsync(Guid id, Guid userId, int limit = 200, int skip = 0,
        GitLogQuery? query = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var gitPath = ResolveGitPathOrThrow();
        // NUL fields preserve multiline bodies and avoid inventing records from body lines.
        var format = "%H%x00%h%x00%an%x00%ae%x00%aI%x00%s%x00%b%x00";
        limit = Math.Clamp(limit, 1, 500);
        skip = Math.Max(skip, 0);
        var args = new List<string> { "log", $"--pretty=tformat:{format}", "--date=iso-strict", "-n", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), $"--skip={skip}" };
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
        {
            var head = await RunGitAsync(gitPath, repo.Path, ["rev-parse", "--verify", "HEAD"], cancellationToken);
            var symbolic = await RunGitAsync(gitPath, repo.Path, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken);
            // An unborn branch legitimately has an empty history; an unreadable repository does not.
            if (!head.Success && head.ExitCode == 128 && symbolic.Success && query?.Reference is null)
            {
                var reference = await RunGitAsync(gitPath, repo.Path, ["show-ref", "--verify", "--quiet", symbolic.Output.Trim()], cancellationToken);
                if (reference.ExitCode == 1) return [];
            }
            throw new InvalidOperationException("Git history could not be read.");
        }

        var commits = new List<GitCommitDto>();
        var fields = result.Output.Split('\0');
        for (var i = 0; i + 6 < fields.Length; i += 7)
        {
            var sha = fields[i].Trim('\r', '\n');
            if (sha.Length is not (40 or 64) || !sha.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Invalid Git history record.");
            commits.Add(new GitCommitDto(sha, fields[i + 1], fields[i + 2], fields[i + 3], fields[i + 4], fields[i + 5],
                string.IsNullOrEmpty(fields[i + 6]) ? null : fields[i + 6]));
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

        // A commit reference means "show this commit", i.e. its patch against its parent
        // (and against the empty tree for a root commit), not a comparison to the current worktree.
        var isCommitDiff = !string.IsNullOrEmpty(@ref);
        var isUntracked = false;
        if (!isCommitDiff && !staged)
        {
            var untracked = await RunGitAsync(gitPath, repo.Path,
                ["--literal-pathspecs", "ls-files", "--others", "--exclude-standard", "--", path], cancellationToken);
            isUntracked = untracked.Success && untracked.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(candidate => string.Equals(candidate, path, StringComparison.Ordinal));
        }

        List<string> args;
        if (isCommitDiff)
            args = ["--literal-pathspecs", "show", "--format=", "--no-color", "--binary", "--no-ext-diff", "--no-textconv", "--find-renames", @ref!, "--", path];
        else if (isUntracked)
            // git diff normally excludes untracked files; compare it to an empty file instead.
            args = ["--literal-pathspecs", "diff", "--no-index", "--no-color", "--binary", "--no-ext-diff", "--no-textconv", "--", "/dev/null", path];
        else
        {
            args = ["--literal-pathspecs", "diff", "--no-color", "--binary", "--no-ext-diff", "--no-textconv"];
            if (staged) args.Add("--cached");
            args.Add("--");
            args.Add(path);
        }

        var result = await RunGitAsync(gitPath, repo.Path, [.. args], cancellationToken);
        // Git --no-index uses exit 1 for a successful comparison containing differences.
        // Other errors must not be projected as an empty, apparently unchanged file.
        if (!result.Success && (!isUntracked || result.ExitCode != 1))
            throw new InvalidOperationException("Git diff could not be read.");
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(result.Output))).ToLowerInvariant();
        var patch = result.Output;
        var truncated = false;
        if (patch.Length > MaxDiffPatchSize)
        {
            var statArgs = isCommitDiff
                ? new List<string> { "--literal-pathspecs", "show", "--no-ext-diff", "--no-textconv", "--format=", "--stat", @ref!, "--", path }
                : isUntracked
                    ? new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-index", "--stat", "--", "/dev/null", path }
                    : new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--stat" };
            if (!isCommitDiff && !isUntracked && staged) statArgs.Add("--cached");
            if (!isCommitDiff && !isUntracked) { statArgs.Add("--"); statArgs.Add(path); }
            var statResult = await RunGitAsync(gitPath, repo.Path, [.. statArgs], cancellationToken);
            if (!statResult.Success && (!isUntracked || statResult.ExitCode != 1)) throw new InvalidOperationException("Git diff statistics could not be read.");
            patch = statResult.Output;
            truncated = true;
        }

        var numstatArgs = isCommitDiff
            ? new List<string> { "--literal-pathspecs", "show", "--no-ext-diff", "--no-textconv", "--format=", "--numstat", @ref!, "--", path }
            : isUntracked
                ? new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-index", "--numstat", "--", "/dev/null", path }
                : new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--numstat" };
        if (!isCommitDiff && !isUntracked && staged) numstatArgs.Add("--cached");
        if (!isCommitDiff && !isUntracked) { numstatArgs.Add("--"); numstatArgs.Add(path); }
        var numstat = await RunGitAsync(gitPath, repo.Path, [.. numstatArgs], cancellationToken);
        if (!numstat.Success && (!isUntracked || numstat.ExitCode != 1)) throw new InvalidOperationException("Git diff statistics could not be read.");
        var (additions, deletions) = ParseNumstat(numstat.Output);

        var isBinary = result.Output.Contains("Binary files", StringComparison.OrdinalIgnoreCase) ||
                       result.Output.Contains("GIT binary patch", StringComparison.OrdinalIgnoreCase);

        return new GitDiffDto(path, version, null, isBinary ? "" : patch, additions, deletions, isBinary, truncated);
    }

}
