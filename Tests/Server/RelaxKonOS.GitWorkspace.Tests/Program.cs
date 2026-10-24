using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.UserExecution;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var git = new HostGitCli().ResolveGitPath() ?? throw new Exception("Git is required.");
var factory = new Factory(new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={Path.Combine(root, "git.sqlite")};Pooling=False").Options);
await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
using var scopes = new ServiceCollection().BuildServiceProvider();
var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
var service = new LocalGitRepositoryService(factory, new HostGitCli(), new EphemeralDataProtectionProvider(), NullLogger<LocalGitRepositoryService>.Instance,
    scopes.GetRequiredService<IServiceScopeFactory>(), DispatchProxy.Create<IUserExecutionTransport, RejectProxy>(), new UserMode(), new UserExecutionBackendSelection(UserExecutionBackend.Helper), http);
var user = Guid.NewGuid();
http.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", user.ToString())], "test")) };
var count = 0;
void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine($"PASS GIT WORKSPACE {++count}: {message}"); }
async Task<string> Run(string dir, params string[] args)
{
    using var process = new Process { StartInfo = new(git) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
    UserExecutionGitPolicy.ApplySafeEnvironment(process.StartInfo);
    foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
    process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var text = await output; if (process.ExitCode != 0) throw new Exception(await error);
    return text.TrimEnd('\r', '\n');
}
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
async Task<(string Path, Guid Id)> Setup(string name)
{
    var path = Path.Combine(root, name); Directory.CreateDirectory(path);
    await Run(path, "init", "-b", "main");
    await Run(path, "config", "user.name", "Workspace Tests");
    await Run(path, "config", "user.email", "test@example.invalid");
    await Run(path, "config", "core.autocrlf", "false");
    await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "base\n");
    await Run(path, "add", "."); await Run(path, "commit", "-m", "base");
    return (path, Guid.Parse((await service.RegisterRepositoryAsync(new(name, path), user)).Id));
}
var (path, id) = await Setup("workspace");
Check((await service.CreateTagAsync(id, user, new("release/测试", "HEAD"))).Success, "Creates Unicode slash-separated tag at verified commit");
Check((await service.ListTagsAsync(id, user)).Single().Name == "release/测试", "Lists exact tag names");
Check(!(await service.CreateTagAsync(id, user, new("release/测试"))).Success, "Duplicate tag cannot overwrite existing tag");
await Reject(async () => { await service.CreateTagAsync(id, user, new("--force")); }, "Rejects tag option injection");
Check(!(await service.CreateTagAsync(id, user, new("bad..tag"))).Success, "Rejects invalid ref syntax");
await Reject(async () => { await service.ListTagsAsync(id, Guid.NewGuid()); }, "Tag listing enforces repository owner");
await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "changed\n");
await File.WriteAllTextAsync(Path.Combine(path, "new 文件.txt"), "untracked\n");
Check(!(await service.CheckoutAsync(id, user, new("file.txt"))).Success && await File.ReadAllTextAsync(Path.Combine(path, "file.txt")) == "changed\n", "Revision checkout rejects a file path instead of discarding its changes");
var patch = await service.ExportPatchAsync(id, user, new());
Check(patch.Patch.Contains("+changed") && patch.Patch.Contains("-base"), "Exports complete unstaged unified patch");
var patchPath = Path.Combine(root, "changes.patch"); await File.WriteAllTextAsync(patchPath, patch.Patch);
await Run(path, "checkout", "--", "file.txt");
Check((await service.ApplyPatchAsync(id, user, new(patchPath))).Success && await File.ReadAllTextAsync(Path.Combine(path, "file.txt")) == "changed\n", "Applies exported patch to the worktree");
Check(!(await service.ApplyPatchAsync(id, user, new(patchPath))).Success && await File.ReadAllTextAsync(Path.Combine(path, "file.txt")) == "changed\n", "A rejected patch preserves existing working files");

await File.WriteAllBytesAsync(Path.Combine(path, "file.txt"), [0, 1, 0]);
await Reject(async () => { await service.ExportPatchAsync(id, user, new()); }, "Binary patches cannot be exported as incomplete text");
await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), new string('字', 100_000));
await Reject(async () => { await service.ExportPatchAsync(id, user, new()); }, "Patch size limit counts UTF-8 bytes rather than characters");
await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "changed\n");
await Run(path, "add", "file.txt");
Check(!(await service.ResetAsync(id, user, new("file.txt"))).Success && (await service.ExportPatchAsync(id, user, new(Staged: true))).Patch.Contains("+changed"), "HEAD reset rejects file names instead of silently unstaging them");
Check((await service.ExportPatchAsync(id, user, new(Staged: true))).Patch.Contains("+changed"), "Exports index independently from worktree");
Check((await service.StashAsync(id, user, new("save", Message: "Unicode 测试"))).Success, "Stash saves tracked and untracked files");
var stash = (await service.ListStashesAsync(id, user)).Single();
Check(!File.Exists(Path.Combine(path, "new 文件.txt")) && await File.ReadAllTextAsync(Path.Combine(path, "file.txt")) == "base\n", "Stash cleans worktree without losing saved content");
Check(!(await service.StashAsync(id, user, new("drop", new string('0', 40)))).Success, "Stale stash selection cannot drop another entry");
Check((await service.StashAsync(id, user, new("apply", stash.Sha))).Success && (await service.ListStashesAsync(id, user)).Count == 1, "Apply restores content and keeps stash");
await Run(path, "reset", "--hard", "HEAD"); File.Delete(Path.Combine(path, "new 文件.txt"));
Check((await service.StashAsync(id, user, new("pop", stash.Sha))).Success && (await service.ListStashesAsync(id, user)).Count == 0, "Pop removes stash only after successful apply");
await Run(path, "reset", "--hard", "HEAD"); File.Delete(Path.Combine(path, "new 文件.txt"));
await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "stashed\n");
await service.StashAsync(id, user, new("save")); stash = (await service.ListStashesAsync(id, user)).Single();
await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "committed\n"); await Run(path, "commit", "-am", "advance");
var pop = await service.StashAsync(id, user, new("pop", stash.Sha));
Check(!pop.Success && pop.Conflicts?.Contains("file.txt") == true && (await service.ListStashesAsync(id, user)).Count == 1, "Conflicted pop retains stash and reports exact conflict");
await Run(path, "reset", "--hard", "HEAD");
Check((await service.StashAsync(id, user, new("drop", stash.Sha))).Success, "Explicit stash drop removes selected SHA");
var linked = Path.Combine(root, "linked 文件");
Check((await service.CreateWorktreeAsync(id, user, new(linked, "HEAD", "feature/linked"))).Success, "Creates linked worktree with a new branch");
Check((await service.ListWorktreesAsync(id, user)).Any(tree => tree.Path.Replace('\\', '/') == linked.Replace('\\', '/') && tree.Branch == "feature/linked"), "Lists linked path and branch with spaces and Unicode");
await File.WriteAllTextAsync(Path.Combine(linked, "file.txt"), "dirty\n");
Check(!(await service.RemoveWorktreeAsync(id, user, new(linked))).Success && Directory.Exists(linked), "Dirty worktree removal is refused without deleting files");
await Run(linked, "reset", "--hard", "HEAD");
await Run(path, "worktree", "lock", linked);
Check(!(await service.RemoveWorktreeAsync(id, user, new(linked))).Success, "Locked worktree removal is refused");
await Run(path, "worktree", "unlock", linked);
Check((await service.RemoveWorktreeAsync(id, user, new(linked))).Success && !Directory.Exists(linked), "Clean linked worktree can be removed");
Check(!(await service.RemoveWorktreeAsync(id, user, new(path))).Success, "Main worktree cannot be removed");
await Reject(async () => { await service.CreateWorktreeAsync(id, user, new("relative")); }, "Relative worktree destinations are rejected");
await Reject(async () => { await service.CreateWorktreeAsync(id, user, new(Path.Combine(path, ".git", "bad"))); }, "Destinations inside .git are rejected");
await Reject(async () => { await service.CreateWorktreeAsync(id, user, new(Path.Combine(path, ".GIT", "bad"))); }, "Git metadata path protection is case-insensitive");
var clonePath = Path.Combine(root, "clone");
var clone = await service.CloneAsync(new(path, clonePath), user);
Check(clone.Result.Success && Directory.Exists(Path.Combine(clonePath, ".git")) && (await service.ListRepositoriesAsync(user)).Any(repo => repo.Id == clone.Repository!.Id), "Clone creates repository and registers it for its owner");
await Reject(async () => { await service.CloneAsync(new("ext::anything", Path.Combine(root, "unsafe")), user); }, "Clone rejects external command transports");
Check(!(await service.CloneAsync(new(path, clonePath), user)).Result.Success, "Clone refuses a nonempty destination");
var (integrate, integrateId) = await Setup("integrate");
await Run(integrate, "checkout", "-b", "topic"); await File.WriteAllTextAsync(Path.Combine(integrate, "topic.txt"), "topic\n"); await Run(integrate, "add", "."); await Run(integrate, "commit", "-m", "topic");
var topic = await Run(integrate, "rev-parse", "HEAD");
await Run(integrate, "checkout", "main"); await File.WriteAllTextAsync(Path.Combine(integrate, "main.txt"), "main\n"); await Run(integrate, "add", "."); await Run(integrate, "commit", "-m", "main");
Check((await service.RebaseAsync(integrateId, user, new("topic"))).Success && File.Exists(Path.Combine(integrate, "topic.txt")), "Rebase actually reapplies current commits onto selected branch");
await Run(integrate, "checkout", "-b", "pick", "main~1");
await File.WriteAllTextAsync(Path.Combine(integrate, "pick.txt"), "picked\n"); await Run(integrate, "add", "."); await Run(integrate, "commit", "-m", "pick");
var pick = await Run(integrate, "rev-parse", "HEAD"); await Run(integrate, "checkout", "main");
Check((await service.CherryPickAsync(integrateId, user, new(pick))).Success && File.Exists(Path.Combine(integrate, "pick.txt")), "Cherry-pick creates a real commit and worktree change");
Check((await service.ExportPatchAsync(integrateId, user, new(pick))).Patch.Contains("+picked"), "Exports selected commit patch");
await Reject(async () => { await service.RebaseAsync(integrateId, user, new("--exec=bad")); }, "Rejects rebase option injection");
await Reject(async () => { await service.RebaseAsync(integrateId, Guid.NewGuid(), new("main")); }, "Rebase enforces repository owner");
var (conflictPath, conflictId) = await Setup("rebase-conflict");
await Run(conflictPath, "checkout", "-b", "topic"); await File.WriteAllTextAsync(Path.Combine(conflictPath, "file.txt"), "theirs\n"); await Run(conflictPath, "commit", "-am", "theirs");
await Run(conflictPath, "checkout", "main"); await File.WriteAllTextAsync(Path.Combine(conflictPath, "file.txt"), "ours\n"); await Run(conflictPath, "commit", "-am", "ours");
var rebased = await service.RebaseAsync(conflictId, user, new("topic"));
Check(!rebased.Success && rebased.Conflicts?.Single() == "file.txt" && (await service.GetConflictStateAsync(conflictId, user)).Operation == "rebase", "Rebase conflict feeds the existing resolver lifecycle");
Check(!(await service.CherryPickAsync(conflictId, user, new("topic"))).Success, "New integration cannot overwrite an in-progress operation");
Check((await service.ConflictOperationAsync(conflictId, user, new("rebase", "abort"))).Success && await File.ReadAllTextAsync(Path.Combine(conflictPath, "file.txt")) == "ours\n", "Abort restores pre-rebase worktree");
Check(!(await service.ResetAsync(id, user, new("HEAD", "hard"))).Success, "UI reset contract never permits hard reset");
foreach (var command in new[] { "stash", "tag", "worktree", "apply", "check-ref-format" }) Check(UserExecutionGitPolicy.IsAllowed([command, "--help"]), "Shared Helper allowlist supports " + command);
Check(UserExecutionGitPolicy.IsAllowed(["clone", "--", "https://example.invalid/repo", "/tmp/repo"]) && !UserExecutionGitPolicy.IsAllowed(["clone", "-u", "evil", "url", "path"]) && !UserExecutionGitPolicy.IsAllowed(["apply", "--unsafe-paths"]) && !UserExecutionGitPolicy.IsAllowed(["clone", "--upload-pack=evil", "url", "path"]) && !UserExecutionGitPolicy.IsAllowed(["clone", "ext::evil", "path"]), "Shared execution policy rejects arbitrary transport commands");
var (graphPath, graphId) = await Setup("graph");
await Run(graphPath, "checkout", "-b", "feature/ui");
await File.WriteAllTextAsync(Path.Combine(graphPath, "feature.txt"), "feature\n"); await Run(graphPath, "add", "."); await Run(graphPath, "commit", "-m", "feature");
var featureSha = await Run(graphPath, "rev-parse", "HEAD");
await Run(graphPath, "checkout", "main"); await File.WriteAllTextAsync(Path.Combine(graphPath, "main.txt"), "main\n"); await Run(graphPath, "add", "."); await Run(graphPath, "commit", "-m", "main");
var mainSha = await Run(graphPath, "rev-parse", "HEAD");
await Run(graphPath, "merge", "--no-ff", "feature/ui", "-m", "merge");
var mergeSha = await Run(graphPath, "rev-parse", "HEAD");
var graph = await service.GetGraphAsync(graphId, user);
Check(graph.Single(item => item.Sha == mergeSha).Parents.SequenceEqual(new[] { mainSha, featureSha }), "Graph API preserves both real merge parents in Git order");
var featureGraph = await service.GetGraphAsync(graphId, user, "feature/ui");
Check(featureGraph.Any(item => item.Sha == featureSha) && featureGraph.All(item => item.Sha != mainSha && item.Sha != mergeSha), "Each graph can be scoped to its own branch reference");
await Run(graphPath, "checkout", "--detach", featureSha);
Check((await service.GetGraphAsync(graphId, user)).Single(item => item.IsHead).Sha == featureSha, "Graph marks the actual detached HEAD independently from branch labels");
await Run(graphPath, "checkout", "main");
await Run(graphPath, "tag", "-a", "annotated", "-m", "annotation");
Check((await service.ListTagsAsync(graphId, user)).Single().Sha == mergeSha, "Annotated tags are peeled to the commit used by graph labels");
await Run(graphPath, "checkout", "--orphan", "independent"); await Run(graphPath, "commit", "--allow-empty", "-m", "independent");
var independentSha = await Run(graphPath, "rev-parse", "HEAD"); await Run(graphPath, "checkout", "main");
Check((await service.GetLogAsync(graphId, user)).Any(item => item.Sha == independentSha) && (await service.GetLogAsync(graphId, user, query: new(Reference: "main"))).All(item => item.Sha != independentSha), "All-branch log includes an unmerged root while a branch-filtered log excludes it");
await Reject(async () => { await service.GetGraphAsync(graphId, Guid.NewGuid()); }, "Graph data enforces repository ownership");
await Reject(async () => { await service.GetGraphAsync(graphId, user, "--all"); }, "Graph reference cannot inject command options");
await service.CreateTagAsync(graphId, user, new("console-trace"));
await service.CreateTagAsync(graphId, user, new("console-trace"));
var transcript = await service.GetConsoleAsync(graphId, user);
Check(transcript.Any(item => item.Command.Contains("tag -- console-trace") && item.ExitCode == 0) && transcript.Any(item => item.ExitCode != 0 && item.Error.Length > 0), "Console contains actual successful and failed Git commands, exit codes and stderr");
Check(transcript.All(item => !item.Command.Contains("stash")) && (await service.GetConsoleAsync(graphId, user, transcript.Max(item => item.Sequence))).Count == 0, "Console is repository-scoped and supports an incremental cursor");
await Reject(async () => { await service.GetConsoleAsync(graphId, Guid.NewGuid()); }, "Console data cannot cross user boundaries");
await service.StashAsync(id, user, new("save", Message: "https://name:trace-secret@example.invalid/repo?token=private-token"));
var safe = await service.GetConsoleAsync(id, user);
Check(safe.Any(item => item.Command.Contains("https://example.invalid/repo")) && safe.All(item => !(item.Command + item.Output + item.Error).Contains("trace-secret") && !(item.Command + item.Output + item.Error).Contains("private-token")), "Console removes URL passwords and query tokens from real command records");
Check((await service.GetConsoleAsync(Guid.Parse(clone.Repository!.Id), user)).Any(item => item.Command.StartsWith("git clone --")), "Successful clone transcript follows the newly registered destination repository");
Console.WriteLine($"Git workspace integration checks passed: {count}. Temporary repositories: {root}");

sealed class Factory(DbContextOptions<RelaxKonOSDbContext> options) : IDbContextFactory<RelaxKonOSDbContext>
{ public RelaxKonOSDbContext CreateDbContext() => new(options); }
sealed class UserMode : IServerModeResolver
{
    public ServerMode Mode => ServerMode.User;
    public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    public bool Supports(ServerHostFeature feature) => false;
}
public class RejectProxy : DispatchProxy
{ protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new Exception("User-mode tests must not use Helper: " + method?.Name); }
