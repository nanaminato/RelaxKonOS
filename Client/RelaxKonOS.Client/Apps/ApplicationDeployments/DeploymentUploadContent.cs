using System.Diagnostics;
using System.Net;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments;

public sealed record DeploymentUploadProgress(long Bytes, long? TotalBytes, TimeSpan Elapsed);

/// <summary>Counts bytes written to the HTTP body; completion still awaits server staging.</summary>
internal sealed class DeploymentUploadContent(Stream source, IProgress<DeploymentUploadProgress>? progress) : HttpContent
{
    protected override bool TryComputeLength(out long length)
    {
        length = source.CanSeek ? source.Length - source.Position : 0;
        return source.CanSeek;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => CopyAsync(stream, cancellationToken);

    private async Task CopyAsync(Stream destination, CancellationToken token)
    {
        long? total = TryComputeLength(out var size) ? size : null;
        long sent = 0;
        var elapsed = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var buffer = new byte[81920];
        progress?.Report(new(0, total, elapsed.Elapsed));
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            sent += read;
            if (elapsed.Elapsed - lastReport < TimeSpan.FromMilliseconds(100)) continue;
            lastReport = elapsed.Elapsed;
            progress?.Report(new(sent, total, lastReport));
        }
        progress?.Report(new(sent, total ?? sent, elapsed.Elapsed));
    }
}
