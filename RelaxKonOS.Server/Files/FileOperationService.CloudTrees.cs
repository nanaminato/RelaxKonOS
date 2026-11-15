using System.Net;
using System.Net.Http.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

public sealed partial class FileOperationService
{
    private const int MaximumCloudTreeDepth = 64;

    private static void ValidateCloudChildName(string name)
    {
        if (name.Contains('/')) throw new InvalidDataException("Cloud entry names must be a single path segment.");
        FileArchiveService.ValidateName(name);
    }

    private static async Task UploadCloudTreeAsync(Job job, ArchiveFileSystem fs, HttpClient client, string route,
        string? parent, string source, string name, int depth, CancellationToken ct)
    {
        if (depth >= MaximumCloudTreeDepth) throw new IOException("Cloud directory nesting limit exceeded.");
        await FileArchiveService.RejectLinksAsync(fs, source, ct);
        if ((await fs.Info(source, ct))?.Type != FileSystemEntryType.Directory)
            throw new IOException("The upload source must be a directory.");
        var entries = await fs.List(source, ct);
        using var created = await client.PostAsync(
            $"{route}/folders?reference={Uri.EscapeDataString(parent ?? "")}&name={Uri.EscapeDataString(name)}", null, ct);
        if (created.StatusCode == HttpStatusCode.Conflict && depth == 0)
            throw new CloudTransferConflictException();
        await EnsureCloudTransferSuccessAsync(created, ct);
        var folder = await created.Content.ReadFromJsonAsync<CloudCreatedFolder>(RelaxKonOSJsonOptions.Default, ct);
        if (string.IsNullOrEmpty(folder?.Reference)) throw new IOException("Missing cloud folder reference.");

        foreach (var directory in entries.Directories)
            await UploadCloudTreeAsync(job, fs, client, route, folder.Reference, directory.Path, directory.Name, depth + 1, ct);
        foreach (var file in entries.Files)
        {
            lock (job.Gate) { job.CurrentPath = file.Path; job.Bytes = job.TotalBytes = 0; }
            try { await UploadCloudFileAsync(job, fs, client, route, folder.Reference, file.Path, file.Name, false, ct); }
            catch (CloudTransferConflictException)
            {
                throw new IOException("A file in the cloud destination directory already exists.");
            }
            job.Detail(file.Path, "completed", null);
        }
    }

    private async Task DownloadCloudTreeAsync(Job job, ArchiveFileSystem fs, HttpClient client, string route,
        string reference, string destination, CancellationToken ct)
    {
        await FileArchiveService.RejectLinksAsync(fs, destination, ct);
        if (await fs.Info(destination, ct) is not null) throw new CloudTransferConflictException();
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, $".cloud.{Guid.NewGuid():N}.rkup");
        await fs.CreateDirectory(staging, ct);
        var published = false;
        try
        {
            await DownloadCloudChildrenAsync(job, fs, client, route, reference, staging, destination, 0, ct);
            await FileArchiveService.RejectLinksAsync(fs, destination, ct);
            await fs.Move(staging, destination, ct);
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

    private async Task DownloadCloudChildrenAsync(Job job, ArchiveFileSystem fs, HttpClient client, string route,
        string reference, string staging, string destination, int depth, CancellationToken ct)
    {
        if (depth >= MaximumCloudTreeDepth) throw new IOException("Cloud directory nesting limit exceeded.");
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var query = $"reference={Uri.EscapeDataString(reference)}";
            if (cursor is not null) query += $"&cursor={Uri.EscapeDataString(cursor)}";
            using var response = await client.GetAsync($"{route}/entries?{query}", ct);
            await EnsureCloudTransferSuccessAsync(response, ct);
            var listing = await response.Content.ReadFromJsonAsync<CloudDirectoryDto>(RelaxKonOSJsonOptions.Default, ct)
                ?? throw new IOException("Missing cloud directory listing.");
            foreach (var entry in listing.Entries)
            {
                ValidateCloudChildName(entry.Name);
                var child = Path.Combine(staging, entry.Name);
                var target = Path.Combine(destination, entry.Name);
                if (entry.IsDirectory)
                {
                    await fs.CreateDirectory(child, ct);
                    await DownloadCloudChildrenAsync(job, fs, client, route, entry.Reference, child, target, depth + 1, ct);
                }
                else
                {
                    lock (job.Gate) { job.CurrentPath = target; job.Bytes = job.TotalBytes = 0; }
                    try { await DownloadCloudFileAsync(job, fs, client, route, entry.Reference, child, false, ct); }
                    catch (CloudTransferConflictException)
                    {
                        throw new IOException("Cloud entries have conflicting names on the destination filesystem.");
                    }
                }
            }
            cursor = listing.NextCursor;
            if (cursor is not null && !cursors.Add(cursor)) throw new IOException("Cloud pagination repeated a cursor.");
        } while (cursor is not null);
    }
}
