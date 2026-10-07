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
            throw new InvalidOperationException("Git remotes could not be read.");

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

}
