using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>
/// The compiled identity of the one runtime catalog RelaxKonOS itself publishes. The private half of
/// this keypair lives with the publisher and never enters a Server or Helper process, so a host can
/// verify a catalog but never mint one. Everything except the base URI is fixed here: an operator
/// supplies only where the signed file is hosted, and the store still records the whole policy.
/// </summary>
public static class OfficialRuntimeCatalog
{
    public const string KeyId = "relaxkonos-runtime-catalog-2026";
    public const string Publisher = "RelaxKonOS";
    /// <summary>Catalogs are re-signed per release; the host refuses anything older than this window.</summary>
    public const int MaximumValiditySeconds = 30 * 24 * 60 * 60;

    /// <summary>Closed set of runtimes this publisher signs catalogs for.</summary>
    public static readonly IReadOnlyList<string> RuntimeIds = ["mihomo", "sing-box"];

    /// <summary>ECDSA P-256 subject public key info of the publisher key. Public by design.</summary>
    public const string PublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEuuXhx8UWefExRNRHnrVCQ5oJr54BdZCdPXj4gHtnPbh4nrGz3LlS9cPH7D6uoEg2cdsR5v98A5risxdZDN8I9g==";

    public static string SourceId(string runtimeId) => "relaxkonos-official-" + runtimeId;

    /// <summary>Each runtime owns its own signed file, so one publisher key can be rotated per runtime.</summary>
    public static string ManifestPath(string runtimeId) => runtimeId + "/catalog.json";

    /// <summary>
    /// Builds the host policy an administrator confirms once. Only artifacts served from the
    /// runtime's own release location are downloadable; the catalog itself may live on any host.
    /// </summary>
    public static RuntimeSource Policy(string runtimeId, string baseUri, long trustRevision = 0)
    {
        if (!RuntimeIds.Contains(runtimeId, StringComparer.Ordinal) || !baseUri.EndsWith('/')
            || !RuntimeValidation.Https(baseUri, out _))
            throw new RuntimePolicyException("runtime.source_invalid");
        var releasePrefix = runtimeId == "mihomo"
            ? "https://github.com/MetaCubeX/mihomo/releases/download/"
            : "https://github.com/SagerNet/sing-box/releases/download/";
        var policy = new RuntimeSource(SourceId(runtimeId), runtimeId, Publisher, RuntimeSourceKind.HttpsManifest,
            baseUri, ManifestPath(runtimeId), KeyId, PublicKeySpki, true, trustRevision,
            [RuntimeChannel.Stable], 4 * 1024 * 1024, MaximumValiditySeconds,
            [releasePrefix],
            // GitHub serves release assets from a redirect target; both hops stay on the publisher.
            ["https://release-assets.githubusercontent.com/", "https://objects.githubusercontent.com/"],
            []);
        policy.Validate();
        return policy;
    }
}
