using System.Security.Cryptography;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>Copies a previously resolved official MSI into a new seekable staging file. Does not install it.</summary>
public sealed class TailscaleMsiArtifactTransfer(
    Func<ComponentDownloadRelease, Stream, RuntimeDownloadProxy?, CancellationToken, Task>? copy = null)
{
    public const long MaximumBytes = 128L * 1024 * 1024;
    public async Task<long> CopyAsync(ComponentDownloadRelease release, Stream destination, RuntimeDownloadProxy? proxy, CancellationToken ct)
    {
        Validate(release);
        if (proxy is not null) RuntimeDownloadPolicy.Validate(proxy);
        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek || destination.Position != 0 || destination.Length != 0)
            throw new RuntimePolicyException("component.tailscale_staging_invalid");
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        try
        {
            if (copy is null) await RuntimeHttpsTransfer.CopyAsync(new Scope(release),new Uri(release.DownloadUrl),MaximumBytes,false,destination,proxy,token);
            else await copy(release,destination,proxy,token);
            token.ThrowIfCancellationRequested();
            if (destination.Length is <= 0 or > MaximumBytes) throw Invalid();
            await destination.FlushAsync(token);
            destination.Position = 0;
            var hash = await SHA256.HashDataAsync(destination,token);
            if (!CryptographicOperations.FixedTimeEquals(hash,Convert.FromHexString(release.Sha256!))) throw Invalid();
            token.ThrowIfCancellationRequested();
            destination.Position = 0;
            return destination.Length;
        }
        catch
        {
            destination.SetLength(0); destination.Position = 0;
            throw;
        }
    }
    public static void Validate(ComponentDownloadRelease release)
    {
        if (release is null || release.Rid is not ("win-x64" or "win-arm64") || release.Format != "msi"
            || release.Sha256 is not { Length: 64 } || !release.Sha256.All(Uri.IsHexDigit)) throw Invalid();
        var architecture = release.Rid == "win-x64" ? "amd64" : "arm64";
        // Reuse the strict stable-version and exact filename parser; a client-provided URL is never a source.
        var metadata = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            MSIsVersion = release.Version,
            MSIs = new Dictionary<string,string> { [architecture] = release.FileName }
        });
        var expected = TailscaleOfficialReleases.ParseMetadata(metadata,release.Rid,release.Version);
        if (release.DownloadUrl != expected.DownloadUrl || release.FileName != expected.FileName) throw Invalid();
    }
    private static RuntimePolicyException Invalid() => new("component.tailscale_artifact_invalid");
    private sealed class Scope(ComponentDownloadRelease release) : IRuntimeDownloadScope
    {
        public string BaseUri => TailscaleOfficialReleases.Root;
        public bool Enabled => true;
        public RuntimePrivateNetwork[] PrivateNetworks => [];
        public void Validate() => TailscaleMsiArtifactTransfer.Validate(release);
        public bool AllowsDownload(Uri uri) => uri.OriginalString == release.DownloadUrl;
        public bool AllowsRedirect(Uri uri) => false;
        public bool AllowsQuery(Uri uri) => false;
    }
}
