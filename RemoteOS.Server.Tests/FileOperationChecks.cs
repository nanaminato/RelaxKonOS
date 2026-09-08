using System.Reflection;
using System.Security.Claims;
using RemoteOS.Protocol.Files;
using Server.Files;
using Server.Privileged;

public static class FileOperationChecks
{
    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "file-jobs");
        Directory.CreateDirectory(directory);
        using var service = new FileOperationService(DispatchProxy.Create<IPrivilegedFileService, RejectProxy>(),
            DispatchProxy.Create<IFileElevationSessionStore, RejectProxy>());
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        const string owner = "one";
        var count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            Console.WriteLine($"PASS FILE JOB {++count}: {message}");
        }
        string Write(string relative, string content)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
        FileOperationDto Start(FileOperationKind kind, params FileOperationItem[] items)
            => service.Start(owner, principal, new(Guid.NewGuid(), kind, items));
        async Task<FileOperationDto> Wait(Guid id, Func<FileOperationDto, bool>? predicate = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var dto = service.Get(owner, id);
                if (predicate?.Invoke(dto) ?? dto.IsTerminal) return dto;
                await Task.Delay(5, timeout.Token);
            }
        }
        var a = Write("source/a.txt", "new");
        var b = Write("destination/a.txt", "old");
        var copy = Start(FileOperationKind.Copy, new FileOperationItem(a, b));
        var issue = (await Wait(copy.Id, d => d.Issue is not null)).Issue!;
        Check(issue.Choices.Contains(FileOperationDecision.Replace), "Existing file asks before overwrite");
        Check(File.ReadAllText(b) == "old", "Destination unchanged while waiting");
        var independent = Start(FileOperationKind.Copy, new FileOperationItem(a, Path.Combine(directory, "independent.txt")));
        // Same source is deliberately serialized even for read-only copies.
        await Task.Delay(30);
        Check(service.Get(owner, independent.Id).State == FileOperationState.Queued, "Overlapping source jobs queue");
        var otherSource = Write("other/source.txt", "parallel");
        var parallel = Start(FileOperationKind.Copy, new FileOperationItem(otherSource, Path.Combine(directory, "parallel.txt")));
        Check((await Wait(parallel.Id)).State == FileOperationState.Completed, "Independent job completes while another waits for a decision");
        var staleRejected = false;
        try { service.Decide(owner, copy.Id, new(Guid.NewGuid(), FileOperationDecision.Replace)); }
        catch (ArgumentException) { staleRejected = true; }
        Check(staleRejected, "Stale decisions rejected");
        service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.Replace));
        Check((await Wait(copy.Id)).State == FileOperationState.Completed && File.ReadAllText(b) == "new", "Explicit replace commits new content");
        Check(service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.Replace)).Id == copy.Id,
            "Repeating the accepted decision is idempotent");
        var changedDecisionRejected = false;
        try { service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.Skip)); }
        catch (ArgumentException) { changedDecisionRejected = true; }
        Check(changedDecisionRejected, "An accepted decision cannot be replaced by a later click");
        Check((await Wait(independent.Id)).State == FileOperationState.Completed, "Queued intersecting job resumes after completion");

        var duplicate = new StartFileOperationRequest(Guid.NewGuid(), FileOperationKind.Copy, [new(a, b)]);
        var original = service.Start(owner, principal, duplicate);
        Check(service.Start(owner, principal, duplicate).Id == original.Id, "Idempotency key prevents duplicate jobs");
        var reusedRejected = false;
        try { service.Start(owner, principal, duplicate with { Kind = FileOperationKind.Delete }); }
        catch (ArgumentException) { reusedRejected = true; }
        Check(reusedRejected, "An idempotency key cannot change its operation");
        Check(service.List("other").Count == 0, "Task listing is identity scoped");
        var isolated = false;
        try { service.Cancel("other", original.Id); } catch (KeyNotFoundException) { isolated = true; }
        Check(isolated, "Another identity cannot cancel the task");
        service.Cancel(owner, original.Id);
        Check((await Wait(original.Id)).State == FileOperationState.Cancelled, "Cancellation wakes a queued or waiting task");

        Write("destination/a (2).txt", "occupied");
        copy = Start(FileOperationKind.Copy, new FileOperationItem(a, b));
        issue = (await Wait(copy.Id, d => d.Issue is not null)).Issue!;
        service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.KeepBoth));
        await Wait(copy.Id);
        Check(File.ReadAllText(Path.Combine(directory, "destination/a (3).txt")) == "new", "Keep both preserves extension and skips occupied names");
        var same = Start(FileOperationKind.Copy, new FileOperationItem(a, a));
        Check((await Wait(same.Id)).State == FileOperationState.Completed && File.Exists(Path.Combine(directory, "source/a (2).txt")), "Same-directory copy creates a numbered sibling");

        var typeTarget = Path.Combine(directory, "type-conflict.txt");
        Directory.CreateDirectory(typeTarget);
        copy = Start(FileOperationKind.Copy, new FileOperationItem(a, typeTarget));
        issue = (await Wait(copy.Id, d => d.Issue is not null)).Issue!;
        Check(!issue.Choices.Contains(FileOperationDecision.Replace), "File and directory type conflicts cannot replace a directory tree");
        service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.KeepBoth));
        await Wait(copy.Id);
        Check(Directory.Exists(typeTarget) && File.ReadAllText(Path.Combine(directory, "type-conflict (2).txt")) == "new",
            "Keep both retains the source extension even when the target is a directory");
        var ancestorRejected = false;
        try { Start(FileOperationKind.Copy, new FileOperationItem(Path.GetDirectoryName(a)!, directory)); }
        catch (ArgumentException) { ancestorRejected = true; }
        Check(ancestorRejected, "Copying onto an ancestor is rejected to avoid recursive self-merges");

        var from = Path.Combine(directory, "merge-source");
        var to = Path.Combine(directory, "merge-target");
        Write("merge-source/keep.txt", "keep source");
        Write("merge-source/move.txt", "move source");
        Write("merge-target/keep.txt", "keep target");
        Write("merge-target/extra.txt", "unrelated");
        var move = Start(FileOperationKind.Move, new FileOperationItem(from, to));
        issue = (await Wait(move.Id, d => d.Issue is not null)).Issue!;
        service.Decide(owner, move.Id, new(issue.Id, FileOperationDecision.Skip));
        var result = await Wait(move.Id);
        Check(result.State == FileOperationState.CompletedWithIssues && result.CompletedSources.Count == 0, "Partially moved directory is not reported as complete");
        Check(File.Exists(Path.Combine(from, "keep.txt")) && !File.Exists(Path.Combine(from, "move.txt")), "Skipped sources remain while successful move sources are removed");
        Check(File.ReadAllText(Path.Combine(to, "extra.txt")) == "unrelated", "Directory merge preserves unrelated target files");

        var one = Write("all-source/one", "1");
        var two = Write("all-source/two", "2");
        var oneTarget = Write("all-target/one", "old");
        var twoTarget = Write("all-target/two", "old");
        copy = Start(FileOperationKind.Copy, new FileOperationItem(one, oneTarget), new(two, twoTarget));
        issue = (await Wait(copy.Id, d => d.Issue is not null)).Issue!;
        service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.Skip, true));
        Check((await Wait(copy.Id)).SkippedItems == 2, "Apply to all skips later conflicts in this job");

        var missing = Path.Combine(directory, "later.txt");
        copy = Start(FileOperationKind.Copy, new FileOperationItem(missing, Path.Combine(directory, "retry.txt")));
        issue = (await Wait(copy.Id, d => d.Issue is not null)).Issue!;
        Check(issue.Code == "not-found" && !issue.Choices.Contains(FileOperationDecision.Replace), "I/O errors do not advertise overwrite as a fix");
        File.WriteAllText(missing, "retry");
        service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.Retry));
        Check((await Wait(copy.Id)).State == FileOperationState.Completed, "Explicit retry resumes after the underlying error is fixed");

        var lockedSource = Write("locked-source.txt", "locked");
        var lockedTarget = Path.Combine(directory, "locked-target.txt");
        using (var locked = new FileStream(lockedSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            copy = Start(FileOperationKind.Copy, new FileOperationItem(lockedSource, lockedTarget));
            issue = (await Wait(copy.Id, d => d.Issue is not null)).Issue!;
            Check(issue.Choices.Contains(FileOperationDecision.Retry) && !File.Exists(lockedTarget),
                "An exclusive source lock prompts without leaving a partial target");
        }
        service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.Retry));
        Check((await Wait(copy.Id)).State == FileOperationState.Completed && File.ReadAllText(lockedTarget) == "locked",
            "Retry succeeds after the source lock is released");

        var delete = Start(FileOperationKind.Delete, new FileOperationItem(Path.Combine(directory, "all-source")));
        Check((await Wait(delete.Id)).State == FileOperationState.Completed && !Directory.Exists(Path.Combine(directory, "all-source")), "Recursive delete completes and removes the directory");
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(directory, "link");
            File.CreateSymbolicLink(link, a);
            delete = Start(FileOperationKind.Delete, new FileOperationItem(link));
            issue = (await Wait(delete.Id, d => d.Issue is not null)).Issue!;
            Check(issue.Code == "unsupported" && File.Exists(a), "Symbolic links do not traverse or delete their target");
            service.Cancel(owner, delete.Id);
            await Wait(delete.Id);
        }

        // Sparse input is large enough to reliably observe an in-flight buffered copy without filling the source disk.
        var large = Path.Combine(directory, "large.bin");
        await using (var stream = File.Create(large)) stream.SetLength(512L * 1024 * 1024);
        var largeTarget = Write("large-target.bin", "original");
        copy = Start(FileOperationKind.Copy, new FileOperationItem(large, largeTarget));
        issue = (await Wait(copy.Id, d => d.Issue is not null)).Issue!;
        service.Decide(owner, copy.Id, new(issue.Id, FileOperationDecision.Replace));
        await Wait(copy.Id, d => d.CurrentBytes > 0 || d.IsTerminal);
        service.Cancel(owner, copy.Id);
        result = await Wait(copy.Id);
        Check(result.State == FileOperationState.Cancelled, "Large file copy cancels during streaming");
        Check(File.ReadAllText(largeTarget) == "original", "Cancelling a replacement preserves original target content");
        Check(!Directory.EnumerateFiles(directory, ".remoteos-*.tmp", SearchOption.AllDirectories).Any(), "Cancellation removes uncommitted temporary files");
        var secondLarge = Path.Combine(directory, "large-two.bin");
        await using (var stream = File.Create(secondLarge)) stream.SetLength(512L * 1024 * 1024);
        var firstParallel = Start(FileOperationKind.Copy, new FileOperationItem(large, Path.Combine(directory, "parallel-large-one.bin")));
        var secondParallel = Start(FileOperationKind.Copy, new FileOperationItem(secondLarge, Path.Combine(directory, "parallel-large-two.bin")));
        await Wait(firstParallel.Id, d => d.CurrentBytes > 0 && service.Get(owner, secondParallel.Id).CurrentBytes > 0);
        Check(service.Get(owner, firstParallel.Id).State == FileOperationState.Running
            && service.Get(owner, secondParallel.Id).State == FileOperationState.Running, "Two independent jobs stream concurrently");
        service.Cancel(owner, firstParallel.Id);
        service.Cancel(owner, secondParallel.Id);
        await Wait(firstParallel.Id);
        await Wait(secondParallel.Id);
        Check(!File.Exists(Path.Combine(directory, "parallel-large-one.bin"))
            && !File.Exists(Path.Combine(directory, "parallel-large-two.bin")), "Concurrent jobs can each be cancelled without committing partial targets");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            var nativeSource = Path.Combine(directory, "native-move.bin");
            var nativeTarget = Path.Combine(directory, "native-moved.bin");
            await using (var stream = File.Create(nativeSource)) stream.SetLength(512L * 1024 * 1024);
            move = Start(FileOperationKind.Move, new FileOperationItem(nativeSource, nativeTarget));
            result = await Wait(move.Id);
            Check(result.State == FileOperationState.Completed && result.CurrentTotalBytes == 0
                && !File.Exists(nativeSource) && new FileInfo(nativeTarget).Length == 512L * 1024 * 1024,
                "Same-volume file moves use rename without streaming the file contents");
        }
        if (Environment.GetEnvironmentVariable("REMOTEOS_FILE_JOB_SECONDARY_ROOT") is { Length: > 0 } secondaryRoot)
        {
            var secondary = Path.Combine(secondaryRoot, "file-job-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(secondary);
            try
            {
                var crossSource = Write("cross-source.txt", "cross-volume content");
                var crossTarget = Path.Combine(secondary, "cross-target.txt");
                move = Start(FileOperationKind.Move, new FileOperationItem(crossSource, crossTarget));
                result = await Wait(move.Id);
                Check(result.State == FileOperationState.Completed && !File.Exists(crossSource)
                    && File.ReadAllText(crossTarget) == "cross-volume content", "Move into the secondary filesystem completes before deleting its source");
            }
            finally { Directory.Delete(secondary, recursive: true); }
        }
        Console.WriteLine($"{count} file operation checks passed.");
    }
}

public class RejectProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.ReturnType == typeof(bool)) return false;
        throw new NotSupportedException("Privileged operations must not be used in ordinary-file checks.");
    }
}
