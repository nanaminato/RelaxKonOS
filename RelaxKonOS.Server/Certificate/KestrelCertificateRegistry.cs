using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Thread-safe SNI certificate selector for Kestrel. A fully validated new version
/// replaces all of its hostname bindings atomically; old material stays alive for connections
/// that already selected it.</summary>
internal sealed record KestrelCertificateSelectorSnapshot(bool Registered, bool IsDefault, IReadOnlyList<string> HostNames,
    string? FingerprintSha256, DateTimeOffset? NotBefore, DateTimeOffset? NotAfter);

internal sealed class KestrelCertificateRegistry
{
    private readonly Dictionary<Guid, X509Certificate2> _certificates = [];
    private readonly Dictionary<string, Guid> _hostBindings = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _defaultCertificateId;
    private readonly List<X509Certificate2> _retired = [];
    private readonly object _gate = new();

    public X509Certificate2? Select(string? hostName)
    {
        lock (_gate)
        {
            var name = CertificateUsagePolicy.NormalizeName(hostName);
            if (name is not null && _hostBindings.TryGetValue(name, out var certificateId)
                && _certificates.TryGetValue(certificateId, out var selected)) return selected;
            if (name is not null && !name.StartsWith("*.", StringComparison.Ordinal) && name.IndexOf('.') is var dot && dot > 0
                && _hostBindings.TryGetValue("*." + name[(dot + 1)..], out var wildcardId)
                && _certificates.TryGetValue(wildcardId, out var wildcard)) return wildcard;
            return _defaultCertificateId is { } fallback && _certificates.TryGetValue(fallback, out var certificate) ? certificate : null;
        }
    }

    public KestrelCertificateSelectorSnapshot Snapshot(Guid certificateId)
    {
        lock (_gate)
        {
            if (!_certificates.TryGetValue(certificateId, out var certificate)) return new(false, false, [], null, null, null);
            return new(true, _defaultCertificateId == certificateId,
                _hostBindings.Where(item => item.Value == certificateId).Select(item => item.Key).Order(StringComparer.Ordinal).ToArray(),
                certificate.GetCertHashString(HashAlgorithmName.SHA256), new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
        }
    }

    public bool IsActive(Guid certificateId)
    {
        lock (_gate) return _certificates.ContainsKey(certificateId);
    }

    public bool Activate(Guid certificateId, X509Certificate2 certificate, IReadOnlyList<string> hostNames)
    {
        var names = hostNames.Select(CertificateUsagePolicy.NormalizeName).ToArray();
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow
            || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || names.Length == 0 || names.Any(name => name is null)) return false;
        lock (_gate)
        {
            if (_certificates.Remove(certificateId, out var previous)) _retired.Add(previous);
            foreach (var host in _hostBindings.Where(item => item.Value == certificateId).Select(item => item.Key).ToArray()) _hostBindings.Remove(host);
            foreach (var host in names) _hostBindings[host!] = certificateId;
            _certificates[certificateId] = certificate;
            _defaultCertificateId ??= certificateId;
            return true;
        }
    }

    public bool Deactivate(Guid certificateId)
    {
        lock (_gate)
        {
            if (!_certificates.Remove(certificateId, out var previous)) return false;
            if (previous is not null) _retired.Add(previous);
            foreach (var host in _hostBindings.Where(item => item.Value == certificateId).Select(item => item.Key).ToArray()) _hostBindings.Remove(host);
            if (_defaultCertificateId == certificateId) _defaultCertificateId = _certificates.Count == 0 ? null : _certificates.Keys.First();
            return true;
        }
    }
}
