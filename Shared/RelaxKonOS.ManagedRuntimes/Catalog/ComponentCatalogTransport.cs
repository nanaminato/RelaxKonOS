namespace RelaxKonOS.ManagedRuntimes.Catalog;

public interface IComponentCatalogTransport
{
    Task CopyAsync(ComponentCatalogSourcePolicy policy, string path, long maximumBytes, Stream destination, CancellationToken ct);
}

/// <summary>Uses the runtime transport's DNS/socket pinning, SSRF policy, bounded body, retries and per-hop checks.</summary>
public sealed class ComponentCatalogHttpsTransport : IComponentCatalogTransport
{
    public async Task CopyAsync(ComponentCatalogSourcePolicy policy, string path, long maximumBytes, Stream destination, CancellationToken ct)
    {
        policy.Validate();
        if (!RemoteCatalogRules.Path(path)) throw RemoteCatalogRules.Error("reference_invalid");
        var scope = new Scope(policy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(maximumBytes <= RemoteCatalogRules.EnvelopeLimit ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(4));
        await RuntimeHttpsTransfer.CopyAsync(scope, new Uri(new Uri(policy.BaseUri), path), maximumBytes,
            metadata: false, destination, proxy: null, deadline.Token);
    }
    private sealed class Scope(ComponentCatalogSourcePolicy policy) : IRuntimeDownloadScope
    {
        public string BaseUri => policy.BaseUri;
        public bool Enabled => policy.Enabled;
        public RuntimePrivateNetwork[] PrivateNetworks => policy.PrivateNetworks;
        public void Validate() => policy.Validate();
        public bool AllowsDownload(Uri uri) => RuntimeValidation.Https(uri.OriginalString, out _) && RuntimeValidation.Within(BaseUri, uri);
        public bool AllowsRedirect(Uri uri) => AllowsDownload(uri);
        public bool AllowsQuery(Uri uri) => false;
    }
}
