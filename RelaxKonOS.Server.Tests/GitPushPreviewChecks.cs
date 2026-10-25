using System.Diagnostics;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;

/// <summary>A local branch that was never pushed has no upstream, so <c>%(upstream:track)</c> reports
/// "ahead 0" and <c>refs/remotes/&lt;remote&gt;/&lt;branch&gt;</c> does not exist. The push itself creates the
/// remote branch; the dialog must therefore preview the commits that push transfers instead of
/// claiming there is nothing to push.</summary>
public static class GitPushPreviewChecks
{
    public static async Task RunAsync(string root, string databaseFile)
    {
        var git = new HostGitCli().ResolveGitPath() ?? throw new Exception("Git is required for push preview checks.");
        var dbPath = Path.Combine(root, databaseFile);
        var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={dbPath};Pooling=False").Options;
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
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine($"PASS GIT PUSH {++count}: {name}"); }
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
            if (process.ExitCode != 0) throw new Exception(await error);
            return text;
        }

        // A repository whose only remote is a local bare clone: pushing there needs no credentials,
        // so the preview and the real push can be compared directly.
        var dir = Path.Combine(root, "push-new-branch");
        var bare = Path.Combine(root, "push-new-branch.git");
        Directory.CreateDirectory(dir);
        await Run(dir, "init", "-b", "main");
        await Run(dir, "config", "user.email", "test@example.invalid");
        await Run(dir, "config", "user.name", "Push Preview Tests");
        await Run(dir, "config", "core.autocrlf", "false");
        await Run(root, "init", "--bare", bare);
        await File.WriteAllTextAsync(Path.Combine(dir, "base.txt"), "base\n");
        await Run(dir, "add", ".");
        await Run(dir, "commit", "-m", "base");
        await Run(dir, "remote", "add", "origin", bare);
        await Run(dir, "push", "origin", "main");

        // The branch the user pushes exists only locally: no upstream, no remote-tracking ref.
        await Run(dir, "checkout", "-b", "aaa");
        await File.WriteAllTextAsync(Path.Combine(dir, "Program.cs.txt"), "changed\n");
        await Run(dir, "add", ".");
        await Run(dir, "commit", "-m", "test");
        var repoId = Guid.Parse((await service.RegisterRepositoryAsync(new("push-new-branch", dir), user)).Id);

        var status = await service.GetStatusAsync(repoId, user);
        Check(status.Upstream is null && status.Ahead == 0,
            "A branch without an upstream reports no upstream and no ahead count");

        var preview = await service.GetPushPreviewAsync(repoId, user, "aaa", "origin", "aaa");
        Check(!preview.RemoteBranchExists && preview.AheadCount == 1 && preview.Commits.Count == 1,
            "Unpushed branch previews its only commit even though the destination ref does not exist");
        Check(preview.Commits[0].Subject == "test" && preview.LocalBranch == "aaa" && preview.RemoteBranch == "aaa",
            "Push preview names the destination and returns the exact commit");
        Check((await service.GetPushPreviewAsync(repoId, user, "aaa")).AheadCount == 1,
            "Preview falls back to the repository's remote and the local branch name");

        var pushed = await service.PushAsync(repoId, user, new(LocalBranch: "aaa", Remote: "origin", RemoteBranch: "aaa"));
        Check(pushed.Success, $"Push creates the missing remote branch ({pushed.Message})");
        Check((await Run(bare, "rev-parse", "--verify", "aaa")).Trim().Length is 40 or 64,
            "The remote branch exists after the push that had nothing to build on");

        var synced = await service.GetPushPreviewAsync(repoId, user, "aaa", "origin", "aaa");
        Check(synced.RemoteBranchExists && synced.AheadCount == 0 && synced.Commits.Count == 0,
            "After the push the same destination previews nothing to push");

        await File.WriteAllTextAsync(Path.Combine(dir, "Program.cs.txt"), "changed again\n");
        await Run(dir, "commit", "-am", "second");
        var ahead = await service.GetPushPreviewAsync(repoId, user, "aaa", "origin", "aaa");
        Check(ahead.RemoteBranchExists && ahead.AheadCount == 1 && ahead.Commits.Single().Subject == "second",
            "A tracked destination previews only the commit it lacks");

        // A different destination on the same repository has a different ahead set: main already
        // exists on the remote, so its preview is empty while aaa still has one commit pending.
        var mainPreview = await service.GetPushPreviewAsync(repoId, user, "main", "origin", "main");
        Check(mainPreview.RemoteBranchExists && mainPreview.AheadCount == 0,
            "Preview is scoped to the selected destination branch, not the checked-out one");

        try
        {
            await service.GetPushPreviewAsync(repoId, user, "no-such-branch", "origin", "no-such-branch");
            throw new Exception("Preview of an unknown local branch should fail");
        }
        catch (ArgumentException) { Check(true, "Unknown local branches are rejected instead of previewing zero"); }

        try
        {
            await service.GetPushPreviewAsync(repoId, user, "aaa", "origin", "-oops");
            throw new Exception("Unsafe destination name should fail");
        }
        catch (ArgumentException) { Check(true, "Unsafe destination ref names are rejected"); }

        var otherUserPreviewFailed = false;
        try { await service.GetPushPreviewAsync(repoId, Guid.NewGuid(), "aaa", "origin", "aaa"); }
        catch (InvalidOperationException) { otherUserPreviewFailed = true; }
        Check(otherUserPreviewFailed, "Repository ownership is enforced for the preview");

        // An unborn branch has nothing to transfer, and that is a preview, not an error.
        var unborn = Path.Combine(root, "push-unborn");
        Directory.CreateDirectory(unborn);
        await Run(unborn, "init", "-b", "main");
        var unbornId = Guid.Parse((await service.RegisterRepositoryAsync(new("push-unborn", unborn), user)).Id);
        var unbornPreview = await service.GetPushPreviewAsync(unbornId, user, "main");
        Check(!unbornPreview.RemoteBranchExists && unbornPreview.AheadCount == 0 && unbornPreview.Commits.Count == 0,
            "An unborn branch previews an empty push instead of failing");

        Console.WriteLine($"Git push preview checks passed: {count}");
    }

    private sealed class Factory(DbContextOptions<RelaxKonOSDbContext> options) : IDbContextFactory<RelaxKonOSDbContext>
    { public RelaxKonOSDbContext CreateDbContext() => new(options); }
}
