using System.Security.Claims;
using RemoteOS.Protocol.Files;
using Server.Privileged;

namespace Server.Files;

/// <summary>In-memory, identity-scoped file jobs. Never replays jobs after a server restart.</summary>
public sealed class FileOperationService(IPrivilegedFileService privileged,
    IFileElevationSessionStore elevations) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly HashSet<Job> _active = [];
    private readonly SemaphoreSlim _workers = new(2);
    private bool _disposed;
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool ContainsPath(string parent, string child) => string.Equals(parent, child, Comparison)
        || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);

    public FileOperationDto Start(string owner, ClaimsPrincipal principal, StartFileOperationRequest request)
    {
        if (request.RequestId == Guid.Empty || !Enum.IsDefined(request.Kind) || request.Items is null
            || request.Items.Count is < 1 or > 1000 || request.Items.Any(item => item is null)) throw new ArgumentException("Invalid operation request (1–1000 items required).");
        var items = request.Items.Select(item =>
        {
            var source = Normalize(item.SourcePath);
            if (Path.GetPathRoot(source) == source) throw new ArgumentException("A filesystem root cannot be operated on.");
            var destination = request.Kind == FileOperationKind.Delete ? null : Normalize(item.DestinationPath!);
            if (destination is not null && ((ContainsPath(source, destination) || ContainsPath(destination, source))
                && !(request.Kind == FileOperationKind.Copy && string.Equals(source, destination, Comparison))))
                throw new ArgumentException("Cannot move or copy an item into itself or its descendants.");
            return new FileOperationItem(source, destination);
        }).ToArray();
        if (items.Any(item => items.Any(other => other != item && ContainsPath(other.SourcePath, item.SourcePath))))
            throw new ArgumentException("Overlapping source selections must be normalized before submission.");
        if (items.Select(x => x.SourcePath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Duplicate sources.");
        if (items.Any(item => item.DestinationPath is { } destination && items.Any(other => other != item
            && (ContainsPath(other.SourcePath, destination) || ContainsPath(destination, other.SourcePath)
                || other.DestinationPath is { } d && (ContainsPath(d, destination) || ContainsPath(destination, d))))))
            throw new ArgumentException("Overlapping source and destination selections.");
        request = request with { Items = items };
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var existing = _jobs.Values.FirstOrDefault(j => j.Owner == owner && j.Request.RequestId == request.RequestId);
            if (existing is not null)
            {
                if (existing.Request.Kind != request.Kind || !existing.Request.Items.SequenceEqual(items))
                    throw new ArgumentException("Request ID was already used for a different operation.");
                return existing.Snapshot();
            }
            foreach (var old in _jobs.Values.Where(j => j.Snapshot().IsTerminal && j.CreatedAt < DateTimeOffset.UtcNow.AddHours(-1)).ToArray())
                _jobs.Remove(old.Id);
            while (_jobs.Count >= 256)
            {
                var oldest = _jobs.Values.Where(j => j.Snapshot().IsTerminal).OrderBy(j => j.CreatedAt).FirstOrDefault();
                if (oldest is null) break;
                _jobs.Remove(oldest.Id);
            }
            if (_jobs.Count >= 256 || _jobs.Values.Count(j => !j.Snapshot().IsTerminal) >= 32)
                throw new InvalidOperationException("Operation capacity reached. Clear or wait for existing operations.");
            var job = new Job(owner, new ClaimsPrincipal(principal), request);
            _jobs.Add(job.Id, job);
            _ = Task.Run(() => RunAsync(job));
            return job.Snapshot();
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Absolute paths required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public IReadOnlyList<FileOperationDto> List(string owner)
    {
        lock (_gate) return _jobs.Values.Where(j => j.Owner == owner).Select(j => j.Snapshot()).OrderBy(j => j.CreatedAt).ToArray();
    }
    private Job Find(string owner, Guid id)
    {
        lock (_gate) return _jobs.TryGetValue(id, out var job) && job.Owner == owner ? job : throw new KeyNotFoundException();
    }
    public FileOperationDto Get(string owner, Guid id) => Find(owner, id).Snapshot();
    public FileOperationDto Cancel(string owner, Guid id)
    {
        var job = Find(owner, id);
        lock (job.Gate)
        {
            if (!job.Snapshot().IsTerminal)
            {
                job.State = FileOperationState.Cancelling;
                job.Cancellation.Cancel();
            }
            return job.Snapshot();
        }
    }
    public FileOperationDto Decide(string owner, Guid id, FileOperationDecisionRequest request, ClaimsPrincipal? principal = null)
    {
        var job = Find(owner, id);
        lock (job.Gate)
        {
            if (job.LastDecision == request) return job.Snapshot();
            if (job.State != FileOperationState.WaitingForDecision || job.Decision?.Task.IsCompleted == true || job.Issue?.Id != request.IssueId
                || !job.Issue.Choices.Contains(request.Action)) throw new ArgumentException("The issue or decision is no longer valid.");
            if (principal is not null) job.Principal = new ClaimsPrincipal(principal);
            job.LastDecision = request;
            job.Decision!.TrySetResult(request);
            return job.Snapshot();
        }
    }

    private async Task RunAsync(Job job)
    {
        var ct = job.Cancellation.Token;
        var outcome = FileOperationState.Failed;
        try
        {
            // Reserve intersecting path trees before entering the bounded I/O pool.
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!_active.Any(other => Paths(job).Any(a => Paths(other).Any(b => ContainsPath(a, b) || ContainsPath(b, a)))))
                    { _active.Add(job); break; }
                }
                await Task.Delay(100, ct);
            }
            await _workers.WaitAsync(ct);
            job.HasWorker = true;
            job.SetState(FileOperationState.Running);
            foreach (var item in job.Request.Items)
            {
                ct.ThrowIfCancellationRequested();
                var destination = item.DestinationPath;
                if (destination is not null && string.Equals(item.SourcePath, destination, Comparison))
                    destination = CopyName(destination);
                if (await ProcessAsync(job, item.SourcePath, destination))
                    lock (job.Gate) job.Completed.Add(item.SourcePath);
            }
            outcome = job.Cancellation.IsCancellationRequested ? FileOperationState.Cancelled
                : job.Skipped > 0 ? FileOperationState.CompletedWithIssues : FileOperationState.Completed;
        }
        catch (OperationCanceledException) { outcome = FileOperationState.Cancelled; }
        catch (Exception ex) { job.Detail(string.Empty, "failed", ex.Message); outcome = FileOperationState.Failed; }
        finally
        {
            if (outcome is FileOperationState.Cancelled or FileOperationState.Failed)
                foreach (var item in job.Request.Items.Where(i => !job.Completed.Contains(i.SourcePath)))
                    job.Detail(item.SourcePath, "incomplete", null);
            job.SetState(outcome);
            if (job.HasWorker) _workers.Release();
            lock (_gate) _active.Remove(job);
        }
    }
    private static IEnumerable<string> Paths(Job job) => job.Request.Items.SelectMany(i =>
        // Reserve the parent for generated same-directory copies as their final name is not known yet.
        i.DestinationPath is { } d ? new[] { i.SourcePath, string.Equals(d, i.SourcePath, Comparison) ? Path.GetDirectoryName(d)! : d }
            : new[] { i.SourcePath });

    private async Task<bool> ProcessAsync(Job job, string source, string? destination)
    {
        var ct = job.Cancellation.Token;
        var replace = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (job.Gate) { job.CurrentPath = source; job.Bytes = 0; job.TotalBytes = 0; }
            var identifiedFile = false;
            try
            {
                RejectLinks(source);
                if (destination is not null) RejectLinks(destination);
                var attributes = File.GetAttributes(source);
                var directory = attributes.HasFlag(FileAttributes.Directory);
                identifiedFile = !directory;
                var exists = destination is not null && Exists(destination);
                if (exists && !(directory && Directory.Exists(destination)) && !replace)
                {
                    var choices = !directory && !Directory.Exists(destination)
                        ? new[] { FileOperationDecision.Replace, FileOperationDecision.Skip, FileOperationDecision.KeepBoth }
                        : new[] { FileOperationDecision.Skip, FileOperationDecision.KeepBoth };
                    var decision = await AskAsync(job, "conflict", source, destination, "Destination already exists.", choices);
                    if (decision == FileOperationDecision.Skip) return Skip(job, source, "Destination already exists.");
                    if (decision == FileOperationDecision.KeepBoth) destination = CopyName(destination!, directory);
                    replace = decision == FileOperationDecision.Replace;
                    continue;
                }
                if (directory)
                {
                    if (destination is not null) Directory.CreateDirectory(destination);
                    var complete = true;
                    // Snapshot children before mutations. Reparse points are rejected per child.
                    foreach (var child in Directory.GetFileSystemEntries(source))
                        complete &= await ProcessAsync(job, child, destination is null ? null : Path.Combine(destination, Path.GetFileName(child)));
                    if (job.Request.Kind != FileOperationKind.Copy && complete)
                    {
                        ct.ThrowIfCancellationRequested();
                        Directory.Delete(source, recursive: false);
                    }
                    job.ProcessedOne(source, complete);
                    return complete;
                }
                if (job.Request.Kind == FileOperationKind.Delete)
                    File.Delete(source);
                else
                {
                    ct.ThrowIfCancellationRequested();
                    if (job.Request.Kind == FileOperationKind.Move && FileOperationRename.TryMove(source, destination!, replace))
                    {
                        job.ProcessedOne(source);
                        return true;
                    }
                    // A stream copy supports cancellation within large files, across volumes as well.
                    await CopyFileAsync(job, source, destination!, replace);
                    if (job.Request.Kind == FileOperationKind.Move)
                    {
                        // Once committed, finish removing the source before honoring cancellation.
                        // A deletion failure may leave both copies; report it and never claim a completed move.
                        File.Delete(source);
                    }
                }
                job.ProcessedOne(source);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Existing privileged helper calls are opaque: no invented byte progress or forced interruption.
                if (ex is UnauthorizedAccessException && identifiedFile && IsElevated(job, source, destination))
                {
                    try
                    {
                        // Never let the legacy helper recursively replace a directory or overwrite an unconfirmed file.
                        if (destination is not null && Directory.Exists(destination)) throw new IOException("Destination is a directory.");
                        await ExecutePrivilegedAsync(job, source, destination, replace);
                        job.ProcessedOne(source);
                        return true;
                    }
                    catch (Exception elevatedError) { ex = elevatedError; }
                }
                var code = ex switch
                {
                    UnauthorizedAccessException => "access-denied",
                    NotSupportedException => "unsupported",
                    FileNotFoundException or DirectoryNotFoundException => "not-found",
                    IOException when OperatingSystem.IsWindows() && (ex.HResult & 0xffff) is 32 or 33 => "in-use",
                    IOException when !OperatingSystem.IsWindows() && (ex.HResult & 0xffff) is 11 or 16 => "in-use",
                    IOException when (ex.HResult & 0xffff) == (OperatingSystem.IsWindows() ? 112 : 28) => "disk-full",
                    _ => "io-error",
                };
                var decision = await AskAsync(job, code, source, destination, ex.Message,
                    [FileOperationDecision.Retry, FileOperationDecision.Skip]);
                if (decision == FileOperationDecision.Skip) return Skip(job, source, ex.Message);
                replace = false; // A retry always rechecks destination conflicts.
            }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new NotSupportedException("Symbolic links and reparse points are not supported by file jobs yet.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static string CopyName(string path, bool? isDirectory = null)
    {
        var name = Path.GetFileName(path);
        var dot = (isDirectory ?? Directory.Exists(path)) ? -1 : name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;
        for (long n = 2; ; n++)
        {
            var candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{stem} ({n}){extension}");
            if (!Exists(candidate)) return candidate;
        }
    }
    private static bool Skip(Job job, string path, string message)
    {
        lock (job.Gate) { job.Skipped++; job.Processed++; }
        job.Detail(path, "skipped", message);
        return false;
    }

    private async Task<FileOperationDecision> AskAsync(Job job, string code, string source, string? destination,
        string message, FileOperationDecision[] choices)
    {
        Task<FileOperationDecisionRequest> pending;
        lock (job.Gate)
        {
            if (job.Defaults.TryGetValue(code, out var automatic) && choices.Contains(automatic)) return automatic;
            job.Issue = new(Guid.NewGuid(), code, source, destination, message, choices);
            job.Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = job.Decision.Task;
            job.State = FileOperationState.WaitingForDecision;
        }
        _workers.Release();
        job.HasWorker = false;
        try
        {
            var decision = await pending.WaitAsync(job.Cancellation.Token);
            lock (job.Gate)
            {
                if (decision.ApplyToAll && decision.Action != FileOperationDecision.Retry) job.Defaults[code] = decision.Action;
            }
            await _workers.WaitAsync(job.Cancellation.Token);
            job.HasWorker = true;
            job.SetState(FileOperationState.Running);
            return decision.Action;
        }
        finally { lock (job.Gate) { job.Issue = null; job.Decision = null; } }
    }

    private static async Task CopyFileAsync(Job job, string source, string destination, bool replace)
    {
        var ct = job.Cancellation.Token;
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".remoteos-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                lock (job.Gate) job.TotalBytes = input.Length;
                var buffer = new byte[131072];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    lock (job.Gate) job.Bytes += read;
                }
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(source));
            RejectLinks(destination);
            if (Directory.Exists(destination)) throw new IOException("Destination is a directory.");
            File.Move(temporary, destination, replace);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { job.Detail(temporary, "cleanup-failed", ex.Message); }
        }
    }
    private bool IsElevated(Job job, string source, string? destination) => elevations.IsElevated(job.Principal,
        Capability(job.Request.Kind), destination is null ? [source] : [source, destination]);
    private static FileElevationCapability Capability(FileOperationKind kind) => kind switch
    {
        FileOperationKind.Copy => FileElevationCapability.Copy,
        FileOperationKind.Move => FileElevationCapability.Move,
        _ => FileElevationCapability.Delete,
    };
    private async Task ExecutePrivilegedAsync(Job job, string source, string? destination, bool replace)
    {
        // Cancellation is honored after the opaque helper finishes; passing a cancelled token would
        // only abandon the IPC response and could falsely report that its mutation was cancelled.
        lock (job.Gate) { job.Bytes = 0; job.TotalBytes = 0; }
        switch (job.Request.Kind)
        {
            case FileOperationKind.Copy: await privileged.CopyAsync(source, destination!, replace); break;
            case FileOperationKind.Move: await privileged.MoveAsync(source, destination!, replace); break;
            default: await privileged.DeleteAsync(source); break;
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var job in _jobs.Values) job.Cancellation.Cancel();
        }
    }
    private sealed class Job(string owner, ClaimsPrincipal principal, StartFileOperationRequest request)
    {
        public readonly object Gate = new();
        public Guid Id { get; } = Guid.NewGuid();
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public string Owner { get; } = owner;
        public ClaimsPrincipal Principal { get; set; } = principal;
        public StartFileOperationRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = new();
        public FileOperationState State = FileOperationState.Queued;
        public string? CurrentPath;
        public long Bytes, TotalBytes, Revision;
        public int Processed, Skipped;
        public bool HasWorker;
        public List<string> Completed { get; } = [];
        public List<FileOperationDetail> Details { get; } = [];
        public bool Truncated;
        public FileOperationIssue? Issue;
        public TaskCompletionSource<FileOperationDecisionRequest>? Decision;
        public FileOperationDecisionRequest? LastDecision;
        public Dictionary<string, FileOperationDecision> Defaults { get; } = [];
        public void SetState(FileOperationState state) { lock (Gate) State = state; }
        public void ProcessedOne(string path, bool complete = true)
        {
            lock (Gate) Processed++;
            Detail(path, complete ? "completed" : "partial", null);
        }
        public void Detail(string path, string outcome, string? message)
        {
            lock (Gate) { if (Details.Count < 200) Details.Add(new(path, outcome, message)); else Truncated = true; }
        }
        public FileOperationDto Snapshot()
        {
            lock (Gate) return new(Id, Request.Kind, State, Request.Items, CurrentPath, Bytes, TotalBytes,
                Processed, Skipped, Completed.ToArray(), Issue, Details.ToArray(), Truncated, CreatedAt, Request.RequestId, ++Revision);
        }
    }
}
