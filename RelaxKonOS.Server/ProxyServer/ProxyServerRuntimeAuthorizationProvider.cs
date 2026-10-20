using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.Server.ProxyServer;

/// <summary>
/// Reads the published state afresh for each Helper challenge. The lifecycle caller holds the domain
/// gate; this reader must not reacquire it or reconcile a publication by calling back into Helper.
/// </summary>
public sealed class ProxyServerRuntimeAuthorizationProvider(ProxyServerStore store,
    IProxyServerCertificateSource certificates, TimeProvider? time = null)
{
    public async Task<SingBoxCurrentAuthorization> ReadAsync(CancellationToken ct)
    {
        var clock = time ?? TimeProvider.System;
        var now = clock.GetUtcNow();
        var until = now.AddSeconds(30);
        var state = await store.ReadAsync(ct);
        if (state.PendingPublication is not null) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        if (state.Published is not { } published) return new(null, until);

        foreach (var secret in published.Credentials)
        {
            var grants = state.Credentials.Where(c => c.Id == secret.Id).ToArray();
            if (grants.Length != 1 || !grants[0].Enabled || grants[0].Value != secret.Value
                || secret.InboundIds.Any(id => !grants[0].InboundIds.Contains(id))
                || grants[0].ExpiresAt is { } expiry && expiry <= now)
                throw new RuntimePolicyException("runtime.credentials_revoked");
            if (grants[0].ExpiresAt is { } expires && expires < until) until = expires;
        }

        foreach (var inbound in published.Configuration.Inbounds.Where(i => i.Enabled && ProxyServerPresets.RequiresCertificate(i.Protocol)))
        {
            var applied = (published.Certificates ?? []).Where(c => c.InboundId == inbound.Id).ToArray();
            if (applied.Length != 1) throw new RuntimePolicyException("runtime.certificate_unavailable");
            ProxyServerTlsMaterial current;
            try { current = await certificates.ReadAsync(inbound, ct); }
            catch (ProxyServerException) { throw new RuntimePolicyException("runtime.certificate_unavailable"); }
            // A binary switch cannot silently publish a renewed certificate or restore a retired copy.
            if (current != applied[0]) throw new RuntimePolicyException("runtime.certificate_changed");
            try
            {
                using var leaf = X509Certificate2.CreateFromPem(current.CertificatePem);
                var validFrom = new DateTimeOffset(leaf.NotBefore.ToUniversalTime());
                var validTo = new DateTimeOffset(leaf.NotAfter.ToUniversalTime());
                if (validFrom > now || validTo <= now) throw new RuntimePolicyException("runtime.certificate_unavailable");
                if (validTo < until) until = validTo;
            }
            catch (System.Security.Cryptography.CryptographicException)
            { throw new RuntimePolicyException("runtime.certificate_unavailable"); }
        }

        // Validate only the current published model. Draft edits/new credentials stay unpublished.
        SingBoxConfiguration.Render(published);
        if (until <= clock.GetUtcNow()) throw new RuntimePolicyException("runtime.authorization_unavailable");
        return new(published, until);
    }
}
