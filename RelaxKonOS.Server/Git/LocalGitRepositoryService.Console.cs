using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Git;

public sealed partial class LocalGitRepositoryService
{
    private sealed record ConsoleRecord(Guid User, string Root, GitConsoleEntryDto Entry);
    private readonly ConcurrentQueue<ConsoleRecord> _consoleRecords = new();
    private long _consoleSequence;
    private static readonly HashSet<string> ConsoleCommands = ["clone", "add", "apply", "branch", "checkout", "cherry-pick", "commit", "fetch", "merge", "pull", "push", "rebase", "reset", "restore", "revert", "stash", "tag", "worktree"];
    private static string SafeConsoleText(string text, GitCredentialRequest? credentials)
    {
        if (!string.IsNullOrEmpty(credentials?.Password)) text = text.Replace(credentials.Password, "[redacted]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"https?://[^\s<>""']+", match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[remote]";
            return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        });
        return text.Length <= 8192 ? text : text[..8192] + "\n[truncated]";
    }
    private async Task<CommandResult> RunGitAsync(string gitPath, string workingDir, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, GitCredentialRequest? credentials = null)
    {
        var result = await RunGitCoreAsync(gitPath, workingDir, arguments, cancellationToken, credentials);
        var commandIndex = arguments.FirstOrDefault() == "--literal-pathspecs" ? 1 : 0;
        var principal = http.HttpContext?.User;
        var identity = principal?.FindFirst("sub")?.Value ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (commandIndex < arguments.Count && ConsoleCommands.Contains(arguments[commandIndex]) && Guid.TryParse(identity, out var user))
        {
            var command = "git " + string.Join(' ', arguments.Select(arg => arg.Any(char.IsWhiteSpace) ? JsonSerializer.Serialize(arg, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) : arg));
            var safeCommand = SafeConsoleText(command, credentials); var output = SafeConsoleText(result.Output, credentials); var error = SafeConsoleText(result.Error, credentials);
            lock (_consoleRecords)
            {
                var entry = new GitConsoleEntryDto(++_consoleSequence, DateTimeOffset.UtcNow, safeCommand, output, error, result.ExitCode);
                _consoleRecords.Enqueue(new(user, Path.GetFullPath(arguments[commandIndex] == "clone" ? arguments[^1] : workingDir), entry));
                while (_consoleRecords.Count > 500) _consoleRecords.TryDequeue(out _);
            }
        }
        return result;
    }
    public async Task<IReadOnlyList<GitConsoleEntryDto>> GetConsoleAsync(Guid id, Guid userId, long after = 0, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(repo.Path);
        return _consoleRecords.Where(record => record.User == userId && record.Entry.Sequence > after && string.Equals(record.Root, root, comparison))
            .Select(record => record.Entry).OrderBy(entry => entry.Sequence).ToArray();
    }
    public async Task<IReadOnlyList<GitGraphCommitDto>> GetGraphAsync(Guid id, Guid userId, string? reference = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        List<string> args = ["log", "--topo-order", "--format=%H%x09%P", "-n", "500"];
        var head = await RunGitAsync(git, repo.Path, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        var headSha = head.Success ? head.Output.Trim() : null;
        if (!string.IsNullOrWhiteSpace(reference)) args.Add(await CommitShaAsync(git, repo.Path, reference, cancellationToken));
        else
        {
            args.Add("--all");
            if (head.Success) args.Add("HEAD");
        }
        var result = await RunGitAsync(git, repo.Path, args, cancellationToken);
        if (!result.Success) throw new InvalidOperationException("Cannot read Git graph.");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t'))
            .Select(parts => new GitGraphCommitDto(parts[0], parts.Length > 1 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [], parts[0] == headSha)).ToArray();
    }
}
