using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.ManagedRuntimes;

public interface IRuntimeArchiveTransport
{
    Task CopyAsync(RuntimeSource source, ManifestRelease release, Stream destination, CancellationToken ct);
}

public sealed class RuntimeArchiveTransport : IRuntimeArchiveTransport
{
    public Task CopyAsync(RuntimeSource source, ManifestRelease release, Stream destination, CancellationToken ct) =>
        RuntimeHttpsTransfer.CopyAsync(source, new Uri(release.DownloadUri), release.Artifact.MaximumBytes,
            metadata: false, destination, ct);
}

public sealed record PreparedRuntimeArtifact(AuthorizedRuntimeRelease Authorization, VerifiedRuntimeArchive Archive);

/// <summary>Helper preparation gate. Does not execute a binary or imply a successful installation.</summary>
public sealed class RuntimeArtifactPreparer(RuntimeSourceStore sources, IRuntimeArchiveTransport transport,
    Func<InstallationStage, Task>? progress = null)
{
    public async Task<PreparedRuntimeArtifact> PrepareAsync(string sourceId, string releaseId,
        long expectedCatalogRevision, long expectedTrustRevision, RuntimeTarget target,
        bool allowPrerelease, string protectedStagingRoot, CancellationToken ct)
    {
        var authorization = sources.AuthorizeRelease(sourceId, releaseId, expectedCatalogRevision,
            expectedTrustRevision, target, allowPrerelease, DateTimeOffset.UtcNow);
        var root = Path.GetFullPath(protectedStagingRoot);
        RuntimeSourceStore.RequireNoLinks(root);
        if (!Directory.Exists(root)) throw new RuntimePolicyException("runtime.staging_root_missing");
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".archive");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        VerifiedRuntimeArchive? extracted = null;
        try
        {
            await using (var archive = new FileStream(temporary, options))
            {
                await NotifyAsync(InstallationStage.Downloading);
                // A transport is not an authorization authority: the Helper rechecks size, digest and contents.
                await transport.CopyAsync(authorization.Source, authorization.Release,
                    new BoundedDestination(archive, authorization.Release.Artifact.MaximumBytes), ct);
                archive.Flush(true);
                await NotifyAsync(InstallationStage.Extracting);
                extracted = await RuntimeArchiveExtractor.ExtractAsync(archive, authorization.Release.Artifact, root, ct);
            }
            ct.ThrowIfCancellationRequested();
            sources.AuthorizeRelease(sourceId, releaseId, expectedCatalogRevision, expectedTrustRevision, target, allowPrerelease, DateTimeOffset.UtcNow);
            return new(authorization, extracted);
        }
        catch
        {
            if (extracted is not null) RuntimeArchiveExtractor.Discard(extracted, root);
            throw;
        }
        finally { RuntimeSourceStore.RequireNoLinks(temporary); File.Delete(temporary); }
    }

    private async Task NotifyAsync(InstallationStage stage)
    {
        if (progress is null) return;
        try { await progress(stage); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private sealed class BoundedDestination(Stream output, long limit) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
        private void Reserve(int count)
        {
            if (count > limit - written) throw new RuntimePolicyException("runtime.archive_too_large");
            written += count;
        }
        public override void Write(byte[] buffer, int offset, int count) { Reserve(count); output.Write(buffer, offset, count); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Reserve(buffer.Length); await output.WriteAsync(buffer, ct); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
