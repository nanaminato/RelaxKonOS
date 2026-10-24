using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Files;

public sealed partial class FileOperationService
{
    private static readonly SemaphoreSlim ExtractionWorkers = new(2);
    private async Task ProcessArchiveAsync(Job job)
    {
        var ct = job.Cancellation.Token;
        ArchiveFileSystem fs = job.ExecutionContext is not null || job.RootExecution
            ? new IdentityArchiveFileSystem(this, job) : new LocalArchiveFileSystem();
        var destination = job.Request.Items[0].DestinationPath!;
        var sources = job.Request.Items.Select(i => i.SourcePath).ToArray();
        lock (job.Gate) { job.CurrentPath = sources[0]; job.Bytes = 0; job.TotalBytes = 0; }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var destinationInfo = await fs.Info(destination, ct);
                if (destinationInfo is not null && !(job.Request.Kind == FileOperationKind.Extract
                    && destinationInfo.Type == FileSystemEntryType.Directory))
                {
                    var decision = await AskAsync(job, "conflict", sources[0], destination, "Destination already exists.",
                        [FileOperationDecision.KeepBoth, FileOperationDecision.Skip]);
                    if (decision == FileOperationDecision.Skip) { foreach (var source in sources) Skip(job, source, "Destination already exists."); return; }
                    var name = Path.GetFileName(destination);
                    var reserved = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { name };
                    while (true)
                    {
                        var candidate = Path.Combine(Path.GetDirectoryName(destination)!, ReserveArchiveName(name, job.Request.Kind == FileOperationKind.Extract, reserved));
                        if (await fs.Info(candidate, ct) is null) { destination = candidate; break; }
                    }
                    continue;
                }
                await FileArchiveService.ExecuteAsync(fs, job.Request.Kind, sources, destination,
                    (path, bytes, total, finished) =>
                    {
                        lock (job.Gate) { job.CurrentPath = path; job.Bytes = bytes; job.TotalBytes = total; if (finished) job.Processed++; }
                    }, (path, error) => job.Detail(path, "cleanup-failed", error.Message), ct, job.Credentials);
                lock (job.Gate) job.Completed.AddRange(sources);
                foreach (var source in sources) job.Detail(source, "completed", null);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (ArchivePasswordException ex)
            {
                job.Credentials = null;
                var decision = await AskAsync(job, ex.Code, sources[0], destination, ex.Message,
                    [FileOperationDecision.Retry, FileOperationDecision.Skip]);
                if (decision == FileOperationDecision.Skip) { foreach (var source in sources) Skip(job, source, ex.Message); return; }
                lock (job.Gate) { job.Processed = 0; job.Bytes = 0; job.TotalBytes = 0; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or TimeoutException)
            {
                // Malformed archives cannot be repaired by retrying an unchanged request.
                if (ex is InvalidDataException) throw;
                var decision = await AskAsync(job, ex is UnauthorizedAccessException ? "access-denied" : "io-error",
                    sources[0], destination, ex.Message, [FileOperationDecision.Retry, FileOperationDecision.Skip]);
                if (decision == FileOperationDecision.Skip) { foreach (var source in sources) Skip(job, source, ex.Message); return; }
                lock (job.Gate) { job.Processed = 0; job.Bytes = 0; job.TotalBytes = 0; }
            }
        }
    }

    private static string ReserveArchiveName(string name, bool directory, ISet<string> reserved)
    {
        var dot = directory ? -1 : FileArchiveFormats.Detect(name) is { } format
            ? name.Length - (format == FileArchiveFormat.TarGZip && name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? 4 : FileArchiveFormats.Extension(format).Length)
            : name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;
        for (long number = 2; ; number++)
        {
            var candidate = $"{stem} ({number}){extension}";
            if (reserved.Add(candidate)) return candidate;
        }
    }

    private sealed class IdentityArchiveFileSystem(FileOperationService service, Job job) : ArchiveFileSystem
    {
        public override async Task<ArchiveExtractionResult> Extract(string source, string staging,
            Action<string, long, long, bool> report, ArchiveCredentials? credentials, CancellationToken ct)
        {
            await ExtractionWorkers.WaitAsync(ct);
            try
            {
                if (job.RootExecution) return await base.Extract(source, staging, report, credentials, ct);
                var result = await service.ExecuteUserAsync<ArchiveExtractionResult>(job, UserExecutionOperationKind.FileExtractArchive,
                    source, staging, cancellationToken: ct, archiveCredentials: credentials, archiveProgress: progress =>
                    {
                        if (progress.Entries is < 0 or > FileArchiveService.MaximumEntries || progress.Bytes < 0
                            || progress.TotalBytes < 0 || progress.TotalBytes > FileArchiveService.MaximumTotalBytes) return;
                        lock (job.Gate) { job.Processed = progress.Entries; job.Bytes = progress.Bytes; job.TotalBytes = progress.TotalBytes; }
                    });
                if (result.Entries is < 0 or > FileArchiveService.MaximumEntries) throw new InvalidDataException("Invalid extraction result.");
                lock (job.Gate)
                {
                    job.Processed = result.Entries;
                    job.CurrentPath = source;
                    job.Bytes = 0;
                    job.TotalBytes = 0;
                }
                return result;
            }
            finally { ExtractionWorkers.Release(); }
        }
        public override async Task<FileSystemEntryDto?> Info(string path, CancellationToken ct)
        {
            try { return await service.GetUserInfoAsync(job, path, ct); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        public override Task<FilePropertiesDto?> Properties(string path, CancellationToken ct) => service.ExecuteUserAsync<FilePropertiesDto?>(job, UserExecutionOperationKind.FileGetProperties, path, cancellationToken: ct);
        public override Task<DirectoryDto> List(string path, CancellationToken ct) => service.ExecuteUserAsync<DirectoryDto>(job, UserExecutionOperationKind.FileListDirectory, path, cancellationToken: ct);
        public override async Task<Stream> Read(string path, CancellationToken ct)
        {
            var info = await service.ExecuteUserAsync<UserExecutionFileRead>(job, UserExecutionOperationKind.FileRead, path,
                cancellationToken: ct, offset: 0, expectedBytes: 0);
            return new UserExecutionReadStream(info.Length, (offset, count, token) =>
                service.ExecuteUserAsync<UserExecutionFileRead>(job, UserExecutionOperationKind.FileRead, path,
                    cancellationToken: token, offset: offset, expectedBytes: count));
        }
        public override async Task CreateDirectory(string path, CancellationToken ct) => _ = await service.ExecuteUserAsync<bool>(job, UserExecutionOperationKind.FileCreateDirectory, path, cancellationToken: ct);
        public override async Task CreateFile(string path, CancellationToken ct) => _ = await service.ExecuteUserAsync<bool>(job, UserExecutionOperationKind.FileCreateStaging, path, cancellationToken: ct);
        public override Task<long> Append(string path, long offset, ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
            service.ExecuteUserAsync<long>(job, UserExecutionOperationKind.FileAppendStaging, path, cancellationToken: ct,
                offset: offset, expectedBytes: bytes.Length, content: Convert.ToBase64String(bytes.Span));
        public override async Task Move(string source, string destination, CancellationToken ct) => _ = await service.ExecuteUserAsync<FileSystemEntryDto>(job, UserExecutionOperationKind.FileMove, source, destination, cancellationToken: ct);
        public override async Task Delete(string path, CancellationToken ct) => _ = await service.ExecuteUserAsync<bool>(job, UserExecutionOperationKind.FileDelete, path, cancellationToken: ct);
    }

    private async Task<UserExecutionFileRead> ReadPrivilegedArchiveChunkAsync(PrivilegedFileAuthorizationSource source, string path, long offset, long count, CancellationToken ct)
    {
        var opened = await privileged.OpenReadAsync(source, path, ct);
        await using var stream = opened.Stream;
        return UserExecutionFileReads.Read(stream, opened.FileName, "application/octet-stream", offset, count, ct);
    }
    private async Task<bool> CreatePrivilegedArchiveDirectoryAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken ct)
    { await privileged.CreateDirectoryAsync(source, path, ct); return true; }
    private async Task<bool> CreatePrivilegedArchiveStagingAsync(PrivilegedFileAuthorizationSource source, string path, CancellationToken ct)
    { await privileged.CreateStagingAsync(source, path, ct); return true; }
    private async Task<long> AppendPrivilegedArchiveChunkAsync(PrivilegedFileAuthorizationSource source, string path, long offset, string content, CancellationToken ct)
    {
        using var bytes = new MemoryStream(Convert.FromBase64String(content), false);
        return await privileged.AppendChunkAsync(source, path, offset, bytes, ct);
    }
}
