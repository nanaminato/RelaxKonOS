using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.Files;

public sealed partial class FileOperationService
{
    private static FileOperationItem[] NormalizeCloudItems(StartFileOperationRequest request)
    {
        var transfer = request.Cloud!;
        if (transfer.ConnectionId == Guid.Empty || transfer.Reference?.Length > 8192)
            throw new ArgumentException("Invalid cloud connection or entry reference.");
        if (request.Kind == FileOperationKind.CloudUpload)
            return request.Items.Select(item =>
            {
                var source = Normalize(item.SourcePath);
                if (item.DestinationPath is not null || Path.GetPathRoot(source) == source)
                    throw new ArgumentException("A cloud upload requires host file paths and a shared cloud destination.");
                return new FileOperationItem(source);
            }).ToArray();
        if (request.Items.Count != 1 || string.IsNullOrWhiteSpace(transfer.Reference)
            || string.IsNullOrWhiteSpace(request.Items[0].SourcePath) || request.Items[0].SourcePath.Length > 1024
            || request.Items[0].SourcePath.Any(char.IsControl))
            throw new ArgumentException("A cloud download requires one object and one host destination.");
        var destination = Normalize(request.Items[0].DestinationPath!);
        if (Path.GetPathRoot(destination) == destination) throw new ArgumentException("A download requires a file name.");
        return [new(request.Items[0].SourcePath, destination)];
    }

    private async Task ProcessCloudAsync(Job job)
    {
        using var scope = executionScopes.CreateScope();
        var module = scope.ServiceProvider.GetRequiredService<ICloudFileOperationModule>();
        var ct = job.Cancellation.Token;
        ArchiveFileSystem fs = job.ExecutionContext is not null || job.RootExecution
            ? new IdentityArchiveFileSystem(this, job) : new LocalArchiveFileSystem();
        var cloud = job.Request.Cloud!;
        var route = $"/cloud/connections/{cloud.ConnectionId:D}";
        foreach (var item in job.Request.Items)
        {
            var destination = item.DestinationPath;
            var originalName = Path.GetFileName(destination ?? item.SourcePath);
            var name = originalName;
            var reserved = new HashSet<string> { name };
            var replace = false;
            var keepBoth = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (job.Gate) { job.CurrentPath = item.SourcePath; job.Bytes = job.TotalBytes = 0; }
                try
                {
                    await module.RunAsync(job.Principal, (client, token) => job.Request.Kind == FileOperationKind.CloudUpload
                        ? UploadCloudFileAsync(job, fs, client, route, cloud.Reference, item.SourcePath, name, token)
                        : DownloadCloudFileAsync(job, fs, client, route, cloud.Reference!, destination!, replace, token), ct);
                    lock (job.Gate) job.Completed.Add(item.SourcePath);
                    job.ProcessedOne(item.SourcePath);
                    break;
                }
                catch (CloudTransferConflictException conflict)
                {
                    const string message = "Destination already exists.";
                    var decision = keepBoth && destination is not null ? FileOperationDecision.KeepBoth
                        : await AskAsync(job, "conflict", item.SourcePath, destination ?? name, message,
                            conflict.CanReplace
                                ? [FileOperationDecision.Replace, FileOperationDecision.KeepBoth, FileOperationDecision.Skip]
                                : [FileOperationDecision.KeepBoth, FileOperationDecision.Skip]);
                    if (decision == FileOperationDecision.Skip)
                    {
                        Skip(job, item.SourcePath, message);
                        break;
                    }
                    replace = decision == FileOperationDecision.Replace;
                    if (replace) continue;
                    keepBoth = true;
                    name = ReserveArchiveName(originalName, false, reserved);
                    if (destination is not null) destination = Path.Combine(Path.GetDirectoryName(destination)!, name);
                }
                catch (CloudTransferCredentialsException)
                {
                    if (await AskAsync(job, CloudStorageApi.CredentialsRejected, item.SourcePath, destination ?? name, "",
                        [FileOperationDecision.Retry, FileOperationDecision.Skip]) == FileOperationDecision.Retry)
                        continue;
                    Skip(job, item.SourcePath, CloudStorageApi.CredentialsRejected);
                    break;
                }
                catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException
                    or NotSupportedException or TimeoutException)
                {
                    if (await AskAsync(job, "io-error", item.SourcePath, destination ?? name, error.Message,
                        [FileOperationDecision.Retry, FileOperationDecision.Skip]) == FileOperationDecision.Retry)
                        continue;
                    Skip(job, item.SourcePath, error.Message);
                    break;
                }
            }
        }
    }

    private static async Task UploadCloudFileAsync(Job job, ArchiveFileSystem fs, HttpClient client, string route,
        string? reference, string source, string name, CancellationToken ct)
    {
        await FileArchiveService.RejectLinksAsync(fs, source, ct);
        if ((await fs.Info(source, ct))?.Type != FileSystemEntryType.File)
            throw new IOException("The upload source must be a regular file.");
        await using var stream = await fs.Read(source, ct);
        var length = stream.Length;
        lock (job.Gate) job.TotalBytes = length;
        using var create = await client.PostAsJsonAsync($"{route}/uploads?key={Guid.NewGuid():N}",
            new CreateUploadRequest(reference ?? "", name, length), RelaxKonOSJsonOptions.Default, ct);
        create.EnsureSuccessStatusCode();
        var session = await create.Content.ReadFromJsonAsync<UploadSessionDto>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new IOException("Missing cloud upload session.");
        if (!Guid.TryParseExact(session.UploadId, "N", out _) || session.Offset != 0 || session.Length != length
            || session.ChunkSize is <= 0 or > FileUploadProtocol.DefaultChunkSize)
            throw new IOException("Invalid cloud upload session.");
        var target = $"{route}/uploads/{session.UploadId}";
        try
        {
            var buffer = new byte[session.ChunkSize];
            long offset = 0;
            while (offset < length)
            {
                var count = (int)Math.Min(buffer.Length, length - offset);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                using var chunk = new ByteArrayContent(buffer, 0, count);
                using var response = await client.PatchAsync(FormattableString.Invariant($"{target}?offset={offset}&length={count}"), chunk, ct);
                response.EnsureSuccessStatusCode();
                if (!response.Headers.TryGetValues(FileUploadProtocol.OffsetHeader, out var offsets)
                    || !long.TryParse(offsets.FirstOrDefault(), CultureInfo.InvariantCulture, out var confirmed) || confirmed != offset + count)
                    throw new IOException("Invalid cloud upload offset.");
                offset = confirmed;
                lock (job.Gate) job.Bytes = offset;
            }
            // Module staging is complete. The provider publication has no confirmed byte counter.
            lock (job.Gate) job.TotalBytes = 0;
            while (true)
            {
                using var response = await client.PostAsJsonAsync(target + "/commit", new CommitUploadRequest(),
                    RelaxKonOSJsonOptions.Default, ct);
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(cancellationToken: ct);
                    if (problem?.Title == "cloud-upload-publish-failed") throw new CloudTransferConflictException();
                }
                await EnsureCloudTransferSuccessAsync(response, ct);
                lock (job.Gate) job.TotalBytes = length;
                return;
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                using var response = await client.DeleteAsync(target, cleanup.Token);
                if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            { job.Detail(source, "cleanup-failed", error.Message); }
        }
    }

    private async Task DownloadCloudFileAsync(Job job, ArchiveFileSystem fs, HttpClient client, string route,
        string reference, string destination, bool replace, CancellationToken ct)
    {
        await FileArchiveService.RejectLinksAsync(fs, destination, ct);
        if (await fs.Info(destination, ct) is { } existing && (!replace || existing.Type != FileSystemEntryType.File))
            throw new CloudTransferConflictException(existing.Type == FileSystemEntryType.File);
        using var response = await client.GetAsync($"{route}/download?reference={Uri.EscapeDataString(reference)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureCloudTransferSuccessAsync(response, ct);
        var expected = response.Content.Headers.ContentLength;
        lock (job.Gate) job.TotalBytes = expected ?? 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, $".cloud.{Guid.NewGuid():N}.rkup");
        await fs.CreateFile(staging, ct);
        var published = false;
        try
        {
            var buffer = new byte[1024 * 1024];
            long offset = 0;
            int count;
            while ((count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, ct)) > 0)
            {
                var confirmed = await fs.Append(staging, offset, buffer.AsMemory(0, count), ct);
                if (confirmed != offset + count) throw new IOException("Invalid host staging offset.");
                offset = confirmed;
                lock (job.Gate) job.Bytes = offset;
            }
            if (expected is not null && expected != offset) throw new IOException("Cloud download length changed.");
            try
            {
                await FileArchiveService.RejectLinksAsync(fs, destination, ct);
                if (!replace) await fs.Move(staging, destination, ct);
                else if (fs is IdentityArchiveFileSystem)
                    await ExecuteUserAsync<FileSystemEntryDto>(job, UserExecutionOperationKind.FileMove, staging,
                        destination, overwrite: true, cancellationToken: ct);
                else
                {
                    ct.ThrowIfCancellationRequested();
                    File.Move(staging, destination, overwrite: true);
                }
            }
            catch (IOException)
            {
                if (await fs.Info(destination, ct) is { } occupied && (!replace || occupied.Type != FileSystemEntryType.File))
                    throw new CloudTransferConflictException(occupied.Type == FileSystemEntryType.File);
                throw;
            }
            published = true;
        }
        finally
        {
            if (!published)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await fs.Delete(staging, cleanup.Token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
                { job.Detail(staging, "cleanup-failed", error.Message); }
            }
        }
    }

    private static async Task EnsureCloudTransferSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, ct);
                if (problem?.Type == CloudStorageApi.CredentialsRejectedProblem)
                    throw new CloudTransferCredentialsException();
            }
            catch (System.Text.Json.JsonException) { }
            catch (NotSupportedException) { }
        }
        response.EnsureSuccessStatusCode();
    }

    private sealed class CloudTransferCredentialsException : IOException;

    private sealed class CloudTransferConflictException(bool canReplace = false) : IOException
    {
        internal bool CanReplace => canReplace;
    }
}
