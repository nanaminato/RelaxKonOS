using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Privileged;
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
        var service = new LocalGitRepositoryService(factory, new HostGitCli(), new EphemeralDataProtectionProvider(),
            DispatchProxy.Create<IPrivilegedOperationTransport, RejectProxy>(), NullLogger<LocalGitRepositoryService>.Instance);
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

        var blocks = GitConflictBlock.Parse("before\r\n<<<<<<< HEAD\r\nours\r\n||||||| base\r\nbase\r\n=======\r\ntheirs\r\n>>>>>>> topic\r\nafter\r\n");
        Check(blocks.Count == 1 && blocks[0].Ours == "ours\r\n" && blocks[0].Theirs == "theirs\r\n" && blocks[0].Line == 2, "Diff3 block excludes base and preserves CRLF");
        Check(GitConflictBlock.Parse("<<<<<<< HEAD\na\n=======\nb\n").Count == 0, "Incomplete markers not auto-resolved");
        Console.WriteLine($"Git conflict checks passed: {count}");
    }
    private sealed class Factory(DbContextOptions<RelaxKonOSDbContext> options) : IDbContextFactory<RelaxKonOSDbContext>
    { public RelaxKonOSDbContext CreateDbContext() => new(options); }
    public class RejectProxy : DispatchProxy
    { protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(); }
}
