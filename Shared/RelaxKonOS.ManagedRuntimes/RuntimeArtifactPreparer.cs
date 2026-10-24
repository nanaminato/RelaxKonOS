using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.ManagedRuntimes;

public interface IRuntimeArchiveTransport
{
    /// <summary>The route is resolved by the caller for this operation; it is never part of the frozen plan.</summary>
    Task CopyAsync(RuntimeSource source, ManifestRelease release, RuntimeDownloadProxy? proxy, Stream destination, CancellationToken ct);
}

public sealed class RuntimeArchiveTransport : IRuntimeArchiveTransport
{
    public Task CopyAsync(RuntimeSource source, ManifestRelease release, RuntimeDownloadProxy? proxy, Stream destination, CancellationToken ct) =>
        RuntimeHttpsTransfer.CopyAsync(source, new Uri(release.DownloadUri), release.Artifact.MaximumBytes,
            metadata: false, destination, proxy, ct);
}

public sealed record PreparedRuntimeArtifact(AuthorizedRuntimeRelease Authorization, VerifiedRuntimeArchive Archive);

/// <summary>Helper preparation gate. Does not execute a binary or imply a successful installation.</summary>
public sealed class RuntimeArtifactPreparer(RuntimeSourceStore sources, IRuntimeArchiveTransport transport,
    Func<InstallationStage, Task>? progress = null)
{
    public async Task<PreparedRuntimeArtifact> PrepareAsync(string sourceId, string releaseId,
        long expectedCatalogRevision, long expectedTrustRevision, RuntimeTarget target,
        bool allowPrerelease, string protectedStagingRoot, RuntimeDownloadProxy? proxy, CancellationToken ct)
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
                await transport.CopyAsync(authorization.Source, authorization.Release, proxy,
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

    /// <summary>
    /// Fails a body that grows past the declared maximum while still allowing the transport to rewind to
    /// a previous offset, so a repeated download attempt writes one body instead of appending to a partial one.
    /// </summary>
    private sealed class BoundedDestination(Stream output, long limit) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => output.CanSeek;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position
        {
            get => written;
            set
            {
                if (!output.CanSeek || value < 0 || value > written) throw new NotSupportedException();
                output.Position = value;
                written = value;
            }
        }
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
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => written + offset,
                SeekOrigin.End => written + offset,
                _ => throw new NotSupportedException()
            };
            Position = target;
            return written;
        }
        public override void SetLength(long value)
        {
            if (!output.CanSeek || value < 0 || value > written) throw new NotSupportedException();
            output.SetLength(value);
            written = value;
        }
    }
}
