using System.Security.Cryptography.X509Certificates;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Thread-safe SNI certificate selector for Kestrel. A fully validated new version
/// replaces all of its hostname bindings atomically; old material stays alive for connections
/// that already selected it.</summary>
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
            if (!string.IsNullOrWhiteSpace(hostName) && _hostBindings.TryGetValue(hostName.TrimEnd('.'), out var certificateId)
                && _certificates.TryGetValue(certificateId, out var selected)) return selected;
            return _defaultCertificateId is { } fallback && _certificates.TryGetValue(fallback, out var certificate) ? certificate : null;
        }
    }

    public bool IsActive(Guid certificateId)
    {
        lock (_gate) return _certificates.ContainsKey(certificateId);
    }

    public bool Activate(Guid certificateId, X509Certificate2 certificate, IReadOnlyList<string> hostNames)
    {
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || hostNames.Count == 0) return false;
        lock (_gate)
        {
            if (_certificates.Remove(certificateId, out var previous)) _retired.Add(previous);
            foreach (var host in _hostBindings.Where(item => item.Value == certificateId).Select(item => item.Key).ToArray()) _hostBindings.Remove(host);
            foreach (var host in hostNames) _hostBindings[host.TrimEnd('.')] = certificateId;
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
