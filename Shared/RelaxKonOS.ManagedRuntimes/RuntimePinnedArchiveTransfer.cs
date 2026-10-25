using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>Network scope is separate from publisher authentication. Signed manifests and protected pins use the same bounded transport.</summary>
internal interface IRuntimeDownloadScope
{
    string BaseUri { get; }
    bool Enabled { get; }
    RuntimePrivateNetwork[] PrivateNetworks { get; }
    void Validate();
    bool AllowsDownload(Uri uri);
    bool AllowsRedirect(Uri uri);
}
public interface IPinnedArchiveTransfer
{
    Task CopyAsync(ComponentPinnedArchive pin, Stream destination, RuntimeDownloadProxy? proxy, CancellationToken ct);
}
public sealed class RuntimePinnedArchiveTransfer : IPinnedArchiveTransfer
{
    public Task CopyAsync(ComponentPinnedArchive pin, Stream destination, RuntimeDownloadProxy? proxy, CancellationToken ct)
    {
        var scope = new Scope(pin);
        scope.Validate();
        return RuntimeHttpsTransfer.CopyAsync(scope, new Uri(pin.SourceUri), pin.MaximumBytes, false, destination, proxy, ct);
    }
    private sealed class Scope(ComponentPinnedArchive pin) : IRuntimeDownloadScope
    {
        public string BaseUri => new Uri(pin.SourceUri).GetLeftPart(UriPartial.Authority) + "/";
        public bool Enabled => true;
        public RuntimePrivateNetwork[] PrivateNetworks => [];
        public void Validate()
        {
            if (pin is null || pin.MaximumBytes is <= 0 or > 128L * 1024 * 1024 || pin.Sha256 is not { Length: 64 }
                || !pin.Sha256.All(Uri.IsHexDigit) || !RuntimeValidation.Https(pin.SourceUri, out var uri) || uri.Port != 443
                || uri.Query.Length != 0 || uri.UserInfo.Length != 0
                || !(uri.Host == "nginx.org" && uri.AbsolutePath == $"/download/nginx-{pin.Version}.zip"
                    || uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/fatedier/frp/releases/download/", StringComparison.Ordinal)))
                throw new RuntimePolicyException("runtime.source_invalid");
        }
        public bool AllowsDownload(Uri uri) => uri.AbsoluteUri == pin.SourceUri;
        public bool AllowsRedirect(Uri uri)
        {
            var original = new Uri(pin.SourceUri);
            return AllowsDownload(uri) || original.Host == "github.com" && uri.Scheme == "https" && uri.Port == 443
                && uri.UserInfo.Length == 0 && uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
        }
    }
}
