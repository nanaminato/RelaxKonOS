using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Client.Apps.Git;

public static class GitConflictChecks
{
    public static async Task RunAsync(string root)
    {
        var git = new HostGitCli().ResolveGitPath() ?? throw new Exception("Git is required for conflict integration tests.");
        var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={Path.Combine(root, "git.sqlite")};Pooling=False").Options;
        var factory = new Factory(options);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        using var executionScopeProvider = new ServiceCollection()
            .AddSingleton(DispatchProxy.Create<IUserExecutionContextResolver, RejectProxy>())
            .BuildServiceProvider();
        var service = new LocalGitRepositoryService(factory, new HostGitCli(), new EphemeralDataProtectionProvider(),
            NullLogger<LocalGitRepositoryService>.Instance,
            executionScopeProvider.GetRequiredService<IServiceScopeFactory>(),
            DispatchProxy.Create<IUserExecutionTransport, RejectProxy>(),
            new TestUserModeResolver(), new UserExecutionBackendSelection(UserExecutionBackend.Helper),
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        var user = Guid.NewGuid();
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine($"PASS GIT {++count}: {name}"); }
        async Task<string> Run(string dir, params string[] args)
        {
            using var process = new Process { StartInfo = new(git) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.Environment["GIT_EDITOR"] = "true";
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var text = await output;
            if (process.ExitCode != 0 && args[0] is not ("merge" or "rebase" or "revert" or "cherry-pick")) throw new Exception(await error);
            return text;
        }
        async Task<(string Dir, Guid Id)> Setup(string name, bool binary = false, bool delete = false)
        {
            var dir = Path.Combine(root, name); Directory.CreateDirectory(dir);
            await Run(dir, "init", "-b", "main");
            await Run(dir, "config", "user.email", "test@example.invalid");
            await Run(dir, "config", "user.name", "Conflict Tests");
            await Run(dir, "config", "core.autocrlf", "false");
            await File.WriteAllTextAsync(Path.Combine(dir, "a space 文件.txt"), binary ? "base\0data" : "base\n");
            await Run(dir, "add", "."); await Run(dir, "commit", "-m", "base");
            await Run(dir, "checkout", "-b", "topic");
            await File.WriteAllTextAsync(Path.Combine(dir, "a space 文件.txt"), binary ? "theirs\0data" : "theirs\n");
            await Run(dir, "commit", "-am", "topic");
            await Run(dir, "checkout", "main");
            if (delete) await Run(dir, "rm", "--", "a space 文件.txt");
            else await File.WriteAllTextAsync(Path.Combine(dir, "a space 文件.txt"), binary ? "ours\0data" : "ours\n");
            await Run(dir, "commit", "-am", "main");
            var repo = await service.RegisterRepositoryAsync(new(name, dir), user);
            return (dir, Guid.Parse(repo.Id));
        }
        const string path = "a space 文件.txt";
        var (dir, id) = await Setup("merge");
        var merge = await service.MergeBranchAsync(id, user, new("topic"));
        Check(!merge.Success && merge.Conflicts?.Single() == path, "Merge failure returns exact Unicode/space conflict path");
        var state = await service.GetConflictStateAsync(id, user);
        Check(state.Operation == "merge" && state.Paths.Single() == path, "Merge state detected");
        var file = await service.GetConflictAsync(id, user, path);
        Check(file.CanEdit && file.BaseVersion == "base\n" && file.OursVersion == "ours\n" && file.TheirsVersion == "theirs\n", "Three index stages preserved");
        Check(!(await service.ConflictOperationAsync(id, user, new("merge", "continue"))).Success, "Continue blocked while unresolved");
        Check(!(await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "edited", file.Result))).Success, "Unresolved markers rejected");
        await File.WriteAllTextAsync(Path.Combine(dir, path), "external edit\n");
        Check(!(await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "edited", "resolved\n"))).Success, "External edit rejects stale revision");
        Check(await File.ReadAllTextAsync(Path.Combine(dir, path)) == "external edit\n", "Stale resolution leaves worktree intact");
        file = await service.GetConflictAsync(id, user, path);
        Check((await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "edited", "resolved\n"))).Success, "Edited result saved and staged");
        state = await service.GetConflictStateAsync(id, user);
        Check(state.Paths.Count == 0 && state.Operation == "merge", "Pending merge remains visible after last resolution");
        Check((await service.ConflictOperationAsync(id, user, new("merge", "continue"))).Success, "Merge continues without interactive editor");
        Check((await service.GetConflictStateAsync(id, user)).Operation is null, "Merge completion clears state");
        Check(!(await service.ConflictOperationAsync(id, user, new("rebase", "continue"))).Success, "No blind rebase continuation");
        var unsafeRejected = false;
        try { await service.GetConflictAsync(id, user, "../outside"); } catch (ArgumentException) { unsafeRejected = true; }
        Check(unsafeRejected, "Traversal rejected");
        bool isolated = false;
        try { await service.GetConflictStateAsync(id, Guid.NewGuid()); } catch (InvalidOperationException) { isolated = true; }
        Check(isolated, "Repository ownership enforced");

        (dir, id) = await Setup("rebase");
        await Run(dir, "rebase", "topic");
        Check((await service.GetConflictStateAsync(id, user)).Operation == "rebase", "Rebase state detected");
        file = await service.GetConflictAsync(id, user, path);
        Check(file.OursVersion == "theirs\n" && file.TheirsVersion == "ours\n", "Rebase stage semantics verified");
        Check((await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "edited", "combined\n"))).Success, "Rebase file resolved");
        Check((await service.ConflictOperationAsync(id, user, new("rebase", "continue"))).Success, "Rebase continues");

        (dir, id) = await Setup("worktree");
        var worktree = Path.Combine(root, "linked");
        await Run(dir, "worktree", "add", "-b", "linked", worktree, "main");
        id = Guid.Parse((await service.RegisterRepositoryAsync(new("linked", worktree), user)).Id);
        await service.MergeBranchAsync(id, user, new("topic"));
        Check((await service.GetConflictStateAsync(id, user)).Operation == "merge", "Worktree .git file supported");
        Check(!(await service.ConflictOperationAsync(id, user, new("rebase", "abort"))).Success, "Stale operation type rejected");
        Check((await service.ConflictOperationAsync(id, user, new("merge", "abort"))).Success, "Worktree merge abort works");
        Check(await File.ReadAllTextAsync(Path.Combine(worktree, path)) == "ours\n", "Abort restores original contents");

        (dir, id) = await Setup("binary", binary: true);
        await service.MergeBranchAsync(id, user, new("topic"));
        file = await service.GetConflictAsync(id, user, path);
        Check(!file.CanEdit, "Binary text editing disabled");
        Check((await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "theirs"))).Success, "Binary side can be accepted");
        Check((await File.ReadAllBytesAsync(Path.Combine(dir, path))).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("theirs\0data")), "Binary selection preserves bytes");

        (dir, id) = await Setup("delete", delete: true);
        await service.MergeBranchAsync(id, user, new("topic"));
        file = await service.GetConflictAsync(id, user, path);
        Check(file.OursVersion is null && file.TheirsVersion is not null, "Missing side is distinct from empty content");
        Check(!(await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "ours"))).Success, "Missing side requires explicit delete");
        Check((await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "delete"))).Success, "Delete conflict resolved");
        Check(!File.Exists(Path.Combine(dir, path)), "Deleted result stays deleted");

        foreach (var strategy in new[] { "merge", "rebase" })
        {
            (dir, id) = await Setup("pull-" + strategy);
            // Opposite config proves the explicit strategy, rather than host config, controls pull.
            await Run(dir, "config", "pull.rebase", strategy == "merge" ? "true" : "false");
            var pull = await service.PullAsync(id, user, new(strategy, ".", "topic"));
            Check(!pull.Success && pull.Conflicts?.Single() == path, $"Pull {strategy} returns conflicts");
            Check((await service.GetConflictStateAsync(id, user)).Operation == strategy, $"Pull honors {strategy} strategy");
            Check((await service.ConflictOperationAsync(id, user, new(strategy, "abort"))).Success, $"Pull {strategy} aborts");
        }
        (dir, id) = await Setup("squash");
        await service.MergeBranchAsync(id, user, new("topic", "squash"));
        state = await service.GetConflictStateAsync(id, user);
        Check(state.Operation is null && state.Paths.Count == 1, "Squash conflicts do not invent a continue operation");
        file = await service.GetConflictAsync(id, user, path);
        Check((await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "ours"))).Success, "Squash conflict can be staged independently");

        (dir, id) = await Setup("cherry-pick");
        await Run(dir, "cherry-pick", "topic");
        Check((await service.GetConflictStateAsync(id, user)).Operation == "cherry-pick", "Cherry-pick state detected");
        Check((await service.ConflictOperationAsync(id, user, new("cherry-pick", "abort"))).Success, "Cherry-pick abort supported");

        (dir, id) = await Setup("revert");
        await service.RevertAsync(id, user, new("topic"));
        Check((await service.GetConflictStateAsync(id, user)).Operation == "revert", "Revert state detected");
        Check((await service.ConflictOperationAsync(id, user, new("revert", "abort"))).Success, "Revert abort supported");

        (dir, id) = await Setup("multi-rebase");
        await File.WriteAllTextAsync(Path.Combine(dir, path), "second local change\n");
        await Run(dir, "commit", "-am", "second");
        await Run(dir, "rebase", "topic");
        file = await service.GetConflictAsync(id, user, path);
        await service.ResolveConflictsAsync(id, user, new(path, file.Revision, "edited", "first resolution\n"));
        var continuation = await service.ConflictOperationAsync(id, user, new("rebase", "continue"));
        Check(!continuation.Success && continuation.Conflicts?.Single() == path, "Rebase continue returns next commit conflicts");
        var next = await service.GetConflictAsync(id, user, path);
        Check(next.Revision != file.Revision, "Next rebase step has a new file revision");
        await service.ConflictOperationAsync(id, user, new("rebase", "abort"));

        // The desktop diff viewer relies on the same endpoint for a committed file
        // and an untracked file. The latter needs an explicit empty-file comparison.
        (dir, id) = await Setup("diff-preview");
        var rootSha = (await Run(dir, "rev-list", "--max-parents=0", "HEAD")).Trim();
        var rootDiff = await service.GetDiffAsync(id, user, path, @ref: rootSha);
        Check(rootDiff.Patch.Contains("+base", StringComparison.Ordinal) && rootDiff.Additions == 1,
            "Root commit diff is compared with the empty tree");
        Check((await service.GetCommitDetailAsync(id, user, rootSha)).ChangedFiles.Single().Path == path,
            "Commit details decode exact Unicode filenames");
        var (renameDir, renameId) = await Setup("rename-paths");
        const string oldName = " old 文件.txt", newName = " new 文件.txt";
        await File.WriteAllTextAsync(Path.Combine(renameDir, oldName), "rename content\n");
        await Run(renameDir, "add", "--", oldName); await Run(renameDir, "commit", "-m", "rename base");
        await Run(renameDir, "mv", "--", oldName, newName);
        var renamed = (await service.GetStatusAsync(renameId, user)).Staged.Single();
        Check(renamed.Path == newName && renamed.OldPath == oldName, "Status preserves quoted Unicode rename pairs and leading whitespace");
        await service.CommitAsync(renameId, user, new("rename", []));
        var renameSha = (await Run(renameDir, "rev-parse", "HEAD")).Trim();
        var renameDetail = (await service.GetCommitDetailAsync(renameId, user, renameSha)).ChangedFiles.Single();
        Check(renameDetail.Path == newName && renameDetail.OldPath == oldName, "Commit detail preserves exact old and new rename names");
        const string untrackedPath = "new preview.txt";
        await File.WriteAllTextAsync(Path.Combine(dir, untrackedPath), "new file\n");
        var untrackedDiff = await service.GetDiffAsync(id, user, untrackedPath);
        Check(untrackedDiff.Patch.Contains("+new file", StringComparison.Ordinal) && untrackedDiff.Additions == 1,
            "Untracked file diff is compared with an empty file");
        Check(untrackedDiff.Version.Length == 64 && untrackedDiff.Version == (await service.GetDiffAsync(id, user, untrackedPath)).Version,
            "Diff versions are stable full-patch hashes");
        await File.WriteAllTextAsync(Path.Combine(dir, untrackedPath), "changed file\n");
        Check((await service.GetDiffAsync(id, user, untrackedPath)).Version != untrackedDiff.Version,
            "Diff version changes when contents change without changing status");
        Check((await service.StageAsync(id, user, new([untrackedPath]))).Success, "Explicit staging succeeds");
        var stagedDiff = await service.GetDiffAsync(id, user, untrackedPath, staged: true);
        await File.WriteAllTextAsync(Path.Combine(dir, untrackedPath), "unstaged content\n");
        Check((await service.GetDiffAsync(id, user, untrackedPath, staged: true)).Version == stagedDiff.Version,
            "Staged patch ignores later unstaged content");
        var commitMessage = "snapshot commit\n\nfirst body line\n" + new string('f', 40) + "\x01not a record\nlast body line";
        Check((await service.CommitAsync(id, user, new(commitMessage, []))).Success, "Empty commit paths commit only the index");
        Check((await Run(dir, "show", $"HEAD:{untrackedPath}")).Trim() == "changed file", "Committed content is the staged version");
        Check((await File.ReadAllTextAsync(Path.Combine(dir, untrackedPath))) == "unstaged content\n", "Unstaged contents survive commit");
        var history = await service.GetLogAsync(id, user, 1);
        Check(history.Count == 1 && history[0].Subject == "snapshot commit" && history[0].Body!.Contains("not a record\nlast body line"),
            "Multiline history body preserves separators without inventing commits");
        var secondPage = await service.GetLogAsync(id, user, 1, 1);
        Check(secondPage.Count == 1 && secondPage[0].Sha != history[0].Sha, "History limit and skip work");
        Check((await service.GetCommitDetailAsync(id, user, history[0].Sha)).ChangedFiles.Single().Path == untrackedPath,
            "Commit detail reads the exact file identity");
        var branchRows = await service.ListBranchesAsync(id, user);
        Check(branchRows.Single(row => row.IsCurrent).Sha == history[0].Sha, "Branch rows carry exact ref tip versions");
        foreach (var (dto, field) in new (object Dto, string Field)[] {
                     (await service.GetStatusAsync(id, user), "configVersion"), (branchRows[0], "sha"), (rootDiff, "version") })
        {
            var json = System.Text.Json.JsonSerializer.SerializeToNode(dto, dto.GetType(), RelaxKonOSJsonOptions.Default)!.AsObject();
            json.Remove(field);
            try { System.Text.Json.JsonSerializer.Deserialize(json.ToJsonString(), dto.GetType(), RelaxKonOSJsonOptions.Default); throw new Exception("Missing version should fail"); }
            catch (System.Text.Json.JsonException) { Check(true, "Shared DTO rejects a missing current version field without a legacy default"); }
        }
        var statusBeforeRemote = await service.GetStatusAsync(id, user);
        await Run(dir, "remote", "add", "origin", "https://user:private-token@example.invalid/repo");
        var statusWithRemote = await service.GetStatusAsync(id, user);
        Check(statusBeforeRemote.ConfigVersion != statusWithRemote.ConfigVersion && statusBeforeRemote.Branch == statusWithRemote.Branch,
            "Remote destinations change the config version even when branch and status do not change");
        Check(!System.Text.Json.JsonSerializer.Serialize(statusWithRemote, RelaxKonOSJsonOptions.Default).Contains("private-token"),
            "Status exposes only the config digest, never credentialed remote URLs");
        await Run(dir, "config", "--add", "remote.origin.pushurl", "https://example.invalid/first");
        await Run(dir, "config", "--add", "remote.origin.pushurl", "https://example.invalid/second");
        var multiplePush = await service.GetStatusAsync(id, user);
        await Run(dir, "config", "--replace-all", "remote.origin.pushurl", "https://example.invalid/changed", "https://example.invalid/first");
        Check((await service.GetStatusAsync(id, user)).ConfigVersion != multiplePush.ConfigVersion,
            "All push destinations participate in the version, including a non-final URL");
        Check(UserExecutionGitPolicy.IsAllowed(["config", "--list", "--null"]) && !UserExecutionGitPolicy.IsAllowed(["config", "remote.origin.url", "replacement"]),
            "Config fingerprint command is read-only and the closed Git policy still denies writes");
        Check((await service.StageAsync(id, user, new([untrackedPath]))).Success, "Working-tree edit can be staged independently");
        Check((await service.UnstageAsync(id, user, new([untrackedPath]))).Success, "Explicit unstage succeeds");
        Check((await service.GetStatusAsync(id, user)).Staged.Count == 0 &&
              await File.ReadAllTextAsync(Path.Combine(dir, untrackedPath)) == "unstaged content\n", "Unstage preserves the working tree");
        var binaryPath = "binary data.bin";
        await File.WriteAllBytesAsync(Path.Combine(dir, binaryPath), [0, 1, 2, 3]);
        var binaryDiff = await service.GetDiffAsync(id, user, binaryPath);
        await File.WriteAllBytesAsync(Path.Combine(dir, binaryPath), [0, 1, 2, 4]);
        var changedBinary = await service.GetDiffAsync(id, user, binaryPath);
        Check(binaryDiff.Binary && changedBinary.Binary && binaryDiff.Patch == "" && binaryDiff.Version != changedBinary.Version,
            "Binary versions protect content hidden from textual presentation");
        await File.WriteAllTextAsync(Path.Combine(dir, "large.txt"), string.Concat(Enumerable.Repeat("large content line\n", 15000)));
        var large = await service.GetDiffAsync(id, user, "large.txt");
        await File.AppendAllTextAsync(Path.Combine(dir, "large.txt"), "changed tail\n");
        Check(large.Truncated && (await service.GetDiffAsync(id, user, "large.txt")).Version != large.Version,
            "Truncated patches retain a full-content version");
        try { await service.GetDiffAsync(id, user, path, @ref: "missing-reference"); throw new Exception("Invalid diff should fail"); }
        catch (InvalidOperationException) { Check(true, "Unreadable diff never becomes empty success"); }
        var unborn = Path.Combine(root, "unborn"); Directory.CreateDirectory(unborn); await Run(unborn, "init", "-b", "main");
        var unbornId = (await service.RegisterRepositoryAsync(new("unborn", unborn), user)).Id;
        Check((await service.GetLogAsync(Guid.Parse(unbornId), user)).Count == 0, "Unborn branches legitimately have empty history");
        await File.WriteAllTextAsync(Path.Combine(unborn, ".git", "config"), "[broken config");
        foreach (var read in new Func<Task>[] {
                     async () => { await service.GetStatusAsync(Guid.Parse(unbornId), user); },
                     async () => { await service.GetLogAsync(Guid.Parse(unbornId), user); },
                     async () => { await service.ListRemotesAsync(Guid.Parse(unbornId), user); } })
        {
            try { await read(); throw new Exception("Corrupt config should fail"); }
            catch (InvalidOperationException) { Check(true, "Unreadable config fails closed instead of projecting empty repository facts"); }
        }
        var snapshot = GitConflictFileAccess.Read(dir, untrackedPath);
        Check(snapshot.CanEdit && snapshot.Content == "unstaged content\n", "Ordinary conflict snapshot carries content and raw-byte hash");
        Check(!GitConflictFileAccess.Write(dir, untrackedPath, System.Text.Encoding.UTF8.GetBytes("new"), new string('0', 64)),
            "Conditional conflict write rejects a stale raw-byte hash");
        Check(GitConflictFileAccess.Write(dir, "missing.txt", System.Text.Encoding.UTF8.GetBytes("created"), "deleted"),
            "A missing conflict result is created without overwrite");
        Check(!GitConflictFileAccess.Write(dir, "missing.txt", System.Text.Encoding.UTF8.GetBytes("overwrite"), "deleted"),
            "A concurrently created conflict result is preserved");
        Check(GitConflictFileAccess.Read(dir, "missing-parent/missing.txt").Hash == "deleted", "A missing parent is a deleted working-tree result");
        Check(GitConflictFileAccess.Write(dir, "missing-parent/missing.txt", System.Text.Encoding.UTF8.GetBytes("created"), "deleted"),
            "A deleted nested result can be created under the ordinary identity");
        Check(!GitConflictFileAccess.Read(dir, binaryPath).CanEdit && !GitConflictFileAccess.Read(dir, "large.txt").CanEdit,
            "Snapshot refuses binary and oversized editable content");
        foreach (var operation in new[] { UserExecutionOperationKind.GitConflictSnapshot, UserExecutionOperationKind.GitConflictWrite })
        {
            var request = new UserExecutionRequest(new(HostPlatformKind.Linux, "1000", "alice", "/home/alice"), operation,
                Path: "/repo", FileName: "a 文件.txt", ContentBase64: operation == UserExecutionOperationKind.GitConflictWrite ? "YQ==" : null,
                ExpectedSha256: operation == UserExecutionOperationKind.GitConflictWrite ? "deleted" : null, OperationId: Guid.NewGuid());
            Check(UserExecutionRequestPolicy.IsValid(request, false) && !UserExecutionRequestPolicy.IsValid(request with { FileName = "../outside" }, false),
                "Conflict Helper request has a closed repository-relative path");
            Check(!UserExecutionRequestPolicy.IsValid(request with { Version = "1.4" }, false) &&
                  !UserExecutionRequestPolicy.IsValid(request with { GitArguments = ["status"] }, false), "Conflict Helper request rejects old versions and unrelated fields");
        }

        var blocks = GitConflictBlock.Parse("before\r\n<<<<<<< HEAD\r\nours\r\n||||||| base\r\nbase\r\n=======\r\ntheirs\r\n>>>>>>> topic\r\nafter\r\n");
        Check(blocks.Count == 1 && blocks[0].Ours == "ours\r\n" && blocks[0].Theirs == "theirs\r\n" && blocks[0].Line == 2, "Diff3 block excludes base and preserves CRLF");
        Check(GitConflictBlock.Parse("<<<<<<< HEAD\na\n=======\nb\n").Count == 0, "Incomplete markers not auto-resolved");
        Console.WriteLine($"Git conflict checks passed: {count}");
    }
    private sealed class Factory(DbContextOptions<RelaxKonOSDbContext> options) : IDbContextFactory<RelaxKonOSDbContext>
    { public RelaxKonOSDbContext CreateDbContext() => new(options); }
}
