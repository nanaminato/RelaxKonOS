using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Server.Files;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Git;

public sealed partial class LocalGitRepositoryService
{
    public async Task<GitConflictStateDto> GetConflictStateAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        return new(await ConflictOperationNameAsync(git, repo.Path, cancellationToken),
            await TryGetConflictPathsAsync(git, repo.Path, cancellationToken) ?? []);
    }

    private async Task<string?> ConflictOperationNameAsync(string git, string root, CancellationToken ct)
    {
        foreach (var (marker, operation) in new[] { ("rebase-merge", "rebase"), ("rebase-apply", "rebase"),
                     ("MERGE_HEAD", "merge"), ("CHERRY_PICK_HEAD", "cherry-pick"), ("REVERT_HEAD", "revert") })
        {
            var result = await RunGitAsync(git, root, ["rev-parse", "--git-path", marker], ct);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            var path = Path.GetFullPath(result.Output.Trim(), root);
            if (await ConflictFileCallAsync<FileSystemEntryDto?>(UserExecutionOperationKind.FileGetInfo, path, null, null, null, ct) is not null) return operation;
        }
        return null;
    }

    private static string ConflictTarget(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || !IsPathSafe(root, path) ||
            path.Split('/', '\\').Any(p => p is ".git" or ".."))
            throw new ArgumentException("Invalid conflict path.");
        var target = Path.GetFullPath(path, root);
        return target;
    }

    public async Task<GitConflictFileDto> GetConflictAsync(Guid id, Guid userId, string path, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        return await ReadConflictAsync(ResolveGitPathOrThrow(), repo.Path, path, cancellationToken);
    }

    private async Task<GitConflictFileDto> ReadConflictAsync(string git, string root, string path, CancellationToken ct)
    {
        var target = ConflictTarget(root, path);
        var entries = await RunGitAsync(git, root, ["--literal-pathspecs", "ls-files", "-u", "-z", "--", path], ct);
        if (!entries.Success) throw new InvalidOperationException(entries.Error);
        if (entries.Output.Length == 0) throw new InvalidOperationException("This file is no longer conflicted. Refresh the repository.");
        var versions = new string?[4];
        bool editable = true;
        foreach (var entry in entries.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry[..entry.IndexOf('\t')].Split(' ');
            if (fields[0] is not ("100644" or "100755")) throw new InvalidOperationException("Resolve symbolic links and submodules with an external Git tool.");
            var size = await RunGitAsync(git, root, ["cat-file", "-s", fields[1]], ct);
            if (!size.Success || !long.TryParse(size.Output.Trim(), out var length)) throw new InvalidOperationException("Cannot read conflict object.");
            if (length > MaxDiffPatchSize) { editable = false; continue; }
            var blob = await RunGitAsync(git, root, ["cat-file", "blob", fields[1]], ct);
            if (!blob.Success) throw new InvalidOperationException(blob.Error);
            if (blob.Output.Contains('\0') || blob.Output.Contains('\uFFFD')) editable = false;
            else versions[int.Parse(fields[2])] = blob.Output;
        }
        var snapshot = await ConflictFileCallAsync<GitConflictSnapshot>(UserExecutionOperationKind.GitConflictSnapshot,
            root, path, null, null, ct);
        var working = snapshot.Content;
        var workHash = snapshot.Hash;
        editable &= snapshot.CanEdit;
        var head = await RunGitAsync(git, root, ["rev-parse", "HEAD"], ct);
        if (!head.Success) throw new InvalidOperationException("Cannot read conflict HEAD.");
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entries.Output + workHash + head.Output)));
        return new(path, revision, editable ? versions[1] : null, editable ? versions[2] : null,
            editable ? versions[3] : null, editable ? working : null, editable);
    }

    public async Task<GitOperationResult> ResolveConflictsAsync(Guid id, Guid userId, GitResolveRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            try
            {
                if (request.Choice is not ("ours" or "theirs" or "edited" or "delete"))
                    return new(false, "resolve", Message: "Unknown resolution choice.");
                var current = await ReadConflictAsync(git, repo.Path, request.Path, cancellationToken);
                if (current.Revision != request.Revision) return new(false, "resolve", Message: "File changed. Reload before resolving.");
                var target = ConflictTarget(repo.Path, request.Path);
                if (request.Choice == "edited")
                {
                    if (!current.CanEdit || request.Content is null || Encoding.UTF8.GetByteCount(request.Content) > MaxDiffPatchSize)
                        return new(false, "resolve", Message: "Only UTF-8 text up to 200 KiB can be edited.");
                    if (request.Content.Split('\n').Any(line => line.StartsWith("<<<<<<<") || line.StartsWith("=======") || line.StartsWith(">>>>>>>") || line.StartsWith("|||||||")))
                        return new(false, "resolve", Message: "Remove conflict markers before saving.");
                    var snapshot = await ConflictFileCallAsync<GitConflictSnapshot>(UserExecutionOperationKind.GitConflictSnapshot,
                        repo.Path, request.Path, null, null, cancellationToken);
                    // Recheck the revision before an ordinary-identity, conditional file write.
                    if ((await ReadConflictAsync(git, repo.Path, request.Path, cancellationToken)).Revision != current.Revision)
                        return new(false, "resolve", Message: "File changed. Reload before resolving.");
                    var bytes = new UTF8Encoding(false, true).GetBytes(request.Content);
                    if (!await ConflictFileCallAsync<bool>(UserExecutionOperationKind.GitConflictWrite, repo.Path, request.Path,
                            Convert.ToBase64String(bytes), snapshot.Hash, cancellationToken))
                        return new(false, "resolve", Message: "File changed. Reload before resolving.");
                }
                else if (request.Choice == "delete")
                {
                    var delete = await RunGitAsync(git, repo.Path, ["--literal-pathspecs", "rm", "-f", "--", request.Path], cancellationToken);
                    return new(delete.Success, "resolve", Message: delete.Success ? null : delete.Error);
                }
                else
                {
                    var stage = request.Choice == "ours" ? "2" : "3";
                    var exists = await RunGitAsync(git, repo.Path, ["cat-file", "-e", $":{stage}:{request.Path}"], cancellationToken);
                    if (!exists.Success) return new(false, "resolve", Message: "This side deleted the file. Choose Delete explicitly.");
                    var checkout = await RunGitAsync(git, repo.Path, ["--literal-pathspecs", "checkout", "--" + request.Choice, "--", request.Path], cancellationToken);
                    if (!checkout.Success) return new(false, "resolve", Message: checkout.Error);
                }
                var add = await RunGitAsync(git, repo.Path, ["--literal-pathspecs", "add", "-A", "--", request.Path], cancellationToken);
                return new(add.Success, "resolve", Message: add.Success ? null : add.Error);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            { return new(false, "resolve", Message: ex.Message); }
        });
    }

    public async Task<GitOperationResult> ConflictOperationAsync(Guid id, Guid userId, GitConflictOperationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var repo = await GetRepoOrThrowAsync(db, id, userId, cancellationToken);
        var git = ResolveGitPathOrThrow();
        return await WithWriteLockAsync(id, async () =>
        {
            var operation = await ConflictOperationNameAsync(git, repo.Path, cancellationToken);
            if (operation is null || operation != request.Operation || request.Action is not ("continue" or "abort"))
                return new(false, "resolve", Message: "Operation changed. Refresh the repository.");
            var conflicts = await TryGetConflictPathsAsync(git, repo.Path, cancellationToken);
            if (request.Action == "continue" && conflicts is not null)
                return new(false, operation, conflicts, "Resolve every conflict before continuing.");
            var result = await RunGitAsync(git, repo.Path, [operation, "--" + request.Action], cancellationToken);
            return new(result.Success, operation, await TryGetConflictPathsAsync(git, repo.Path, cancellationToken), result.Success ? null : result.Error);
        });
    }
    private async Task<T> ConflictFileCallAsync<T>(UserExecutionOperationKind operation, string root, string? relative,
        string? content, string? expectedHash, CancellationToken ct)
    {
        if (serverMode.Mode == ServerMode.System)
        {
            var principal = http.HttpContext?.User ?? throw new InvalidOperationException("Authenticated identity required.");
            UserExecutionContext context;
            using (var scope = executionScopes.CreateScope())
                context = scope.ServiceProvider.GetRequiredService<IUserExecutionContextResolver>().Resolve(principal);
            var response = await executionTransport.ExecuteAsync(new UserExecutionRequest(context.Identity, operation,
                Path: root, FileName: relative, ContentBase64: content, ExpectedSha256: expectedHash, OperationId: Guid.NewGuid()), ct);
            if (!response.Success || response.OutputBase64 is null) throw new InvalidOperationException("Conflict file access is unavailable.");
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(response.OutputBase64), RelaxKonOSJsonOptions.Default)!;
        }
        ct.ThrowIfCancellationRequested();
        object? value = operation switch
        {
            UserExecutionOperationKind.FileGetInfo => new LocalFileService(serverMode).GetInfo(root),
            UserExecutionOperationKind.GitConflictSnapshot => GitConflictFileAccess.Read(root, relative!),
            UserExecutionOperationKind.GitConflictWrite => GitConflictFileAccess.Write(root, relative!, Convert.FromBase64String(content!), expectedHash!),
            _ => throw new ArgumentException("Invalid conflict I/O operation."),
        };
        return (T)value!;
    }

}
