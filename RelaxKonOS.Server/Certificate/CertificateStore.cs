using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

internal sealed record CertificateMaterial(Guid Id, IReadOnlyList<string> Domains, CertificateChallengeType ChallengeType,
    CertificateKeyAlgorithm KeyAlgorithm, string? ContactEmail, string CertificatePem, string PrivateKeyPem, DateTimeOffset CreatedAt, DateTimeOffset? LastRenewalAt = null, string? LastRenewalProblemCode = null,
    CertificateKind Kind = CertificateKind.Acme);

internal sealed record StoredCertificate(Guid Id, string Version, string PrimaryDomain, IReadOnlyList<string> Domains,
    CertificateChallengeType ChallengeType, CertificateKeyAlgorithm KeyAlgorithm, string? Issuer, string? SerialNumber, string? Thumbprint, DateTimeOffset NotBefore,
    DateTimeOffset NotAfter, CertificateStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ContactEmail, DateTimeOffset? RenewalWindowStart,
    DateTimeOffset? RenewalWindowEnd, DateTimeOffset? LastRenewalAt, string? LastRenewalProblemCode, CertificateKind Kind = CertificateKind.Acme,
    string? FingerprintSha256 = null);

internal interface ICertificateStore
{
    Task SaveAsync(CertificateMaterial material, CancellationToken cancellationToken);
    Task<StoredCertificate?> GetAsync(Guid certificateId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredCertificate>> ListAsync(CancellationToken cancellationToken);
    Task UpdateRenewalWindowAsync(Guid certificateId, AcmeRenewalWindow? window, CancellationToken cancellationToken);
    Task UpdateRenewalOutcomeAsync(Guid certificateId, DateTimeOffset? renewedAt, string? problemCode, CancellationToken cancellationToken);
    Task UpdateStatusAsync(Guid certificateId, CertificateStatus status, CancellationToken cancellationToken);
    Task<X509Certificate2?> LoadCurrentAsync(Guid certificateId, CancellationToken cancellationToken);
    Task<X509Certificate2?> LoadVersionAsync(Guid certificateId, string version, CancellationToken cancellationToken);
    /// <summary>Returns fixed, server-local PEM paths suitable for a web-server configuration.</summary>
    Task<(string FullChainPath, string PrivateKeyPath)?> GetNginxPathsAsync(Guid certificateId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid certificateId, CancellationToken cancellationToken);
}

/// <summary>Canonical PEM store. Metadata is separate from private material and the HTTP API only sees DTO projections.</summary>
internal sealed class FileCertificateStore : ICertificateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _versionRetention;

    private readonly CertificateMetadataRepository _metadata;

    public FileCertificateStore(IHostEnvironment environment, CertificateOptions options, CertificateMetadataRepository metadata)
    {
        _root = Path.GetFullPath(options.StorageRoot ?? DefaultRoot(environment));
        _metadata = metadata;
        _versionRetention = Math.Clamp(options.VersionRetentionCount, 2, 12);
    }

    public async Task SaveAsync(CertificateMaterial material, CancellationToken cancellationToken)
    {
        var normalizedDomains = material.Domains.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalizedDomains.Length == 0) throw new ArgumentException("At least one domain is required.", nameof(material));
        using var certificate = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);
        var version = Guid.NewGuid().ToString("N");
        var certificateRoot = CertificateRoot(material.Id);
        var versionRoot = Path.Combine(certificateRoot, "versions", version);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSafeCertificateRoot(material.Id);
            CreateProtectedDirectory(certificateRoot);
            CreateProtectedDirectory(Path.Combine(certificateRoot, "versions"));
            CreateProtectedDirectory(versionRoot);
            await WriteProtectedAsync(Path.Combine(versionRoot, "certificate.pem"), material.CertificatePem, cancellationToken);
            await WriteProtectedAsync(Path.Combine(versionRoot, "fullchain.pem"), material.CertificatePem, cancellationToken);
            await WriteProtectedAsync(Path.Combine(versionRoot, "private.key"), material.PrivateKeyPem, cancellationToken);
            // Nginx sites reference these stable files rather than a version directory. Renewal
            // can therefore prune old versions without leaving a deployed virtual host pointing
            // at a missing PEM file.
            await WriteAtomicallyAsync(Path.Combine(certificateRoot, "nginx-fullchain.pem"), material.CertificatePem, cancellationToken);
            await WriteAtomicallyAsync(Path.Combine(certificateRoot, "nginx-private.key"), material.PrivateKeyPem, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var metadata = new StoredCertificate(material.Id, version, normalizedDomains[0], normalizedDomains, material.ChallengeType, material.KeyAlgorithm,
                certificate.Issuer, certificate.SerialNumber, certificate.Thumbprint,
                new DateTimeOffset(certificate.NotBefore.ToUniversalTime()), new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
                CertificateStatus.Active, material.CreatedAt, now, material.ContactEmail, null, null, material.LastRenewalAt, material.LastRenewalProblemCode,
                material.Kind, FormatSha256Fingerprint(certificate.RawData));
            await WriteAtomicallyAsync(Path.Combine(certificateRoot, "current.json"), JsonSerializer.Serialize(metadata, Json), cancellationToken);
            await _metadata.SaveAsync(metadata, cancellationToken);
            PruneOldVersions(certificateRoot, version);
        }
        finally { _gate.Release(); }
    }

    public async Task<StoredCertificate?> GetAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        if (await _metadata.GetAsync(certificateId, cancellationToken) is { } persisted) return persisted;
        if (!IsSafeCertificateRoot(certificateId)) return null;
        var path = Path.Combine(CertificateRoot(certificateId), "current.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<StoredCertificate>(stream, Json, cancellationToken);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public async Task<IReadOnlyList<StoredCertificate>> ListAsync(CancellationToken cancellationToken)
    {
        var persisted = await _metadata.ListAsync(cancellationToken);
        if (persisted.Count != 0) return persisted;
        if (!Directory.Exists(_root)) return [];
        var results = new List<StoredCertificate>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            if (!Guid.TryParse(Path.GetFileName(directory), out var id)) continue;
            if (await GetAsync(id, cancellationToken) is { } certificate) results.Add(certificate);
        }
        return results.OrderBy(item => item.PrimaryDomain, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<X509Certificate2?> LoadCurrentAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        var metadata = await GetAsync(certificateId, cancellationToken);
        if (metadata is null) return null;
        return await LoadVersionAsync(certificateId, metadata.Version, cancellationToken);
    }

    public Task<X509Certificate2?> LoadVersionAsync(Guid certificateId, string version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length != 32 || !version.All(Uri.IsHexDigit))
            return Task.FromResult<X509Certificate2?>(null);
        if (!IsSafeCertificateRoot(certificateId)) return Task.FromResult<X509Certificate2?>(null);
        var versionRoot = Path.Combine(CertificateRoot(certificateId), "versions", version);
        var certificate = Path.Combine(versionRoot, "fullchain.pem");
        var privateKey = Path.Combine(versionRoot, "private.key");
        if (!File.Exists(certificate) || !File.Exists(privateKey)) return Task.FromResult<X509Certificate2?>(null);
        try { return Task.FromResult<X509Certificate2?>(X509Certificate2.CreateFromPemFile(certificate, privateKey)); }
        catch (CryptographicException) { return Task.FromResult<X509Certificate2?>(null); }
    }

    public async Task<(string FullChainPath, string PrivateKeyPath)?> GetNginxPathsAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        var metadata = await GetAsync(certificateId, cancellationToken);
        if (metadata is null || metadata.Status is CertificateStatus.Revoked or CertificateStatus.Expired || !IsSafeCertificateRoot(certificateId)) return null;
        var fullChain = Path.Combine(CertificateRoot(certificateId), "nginx-fullchain.pem");
        var privateKey = Path.Combine(CertificateRoot(certificateId), "nginx-private.key");
        return File.Exists(fullChain) && File.Exists(privateKey) ? (fullChain, privateKey) : null;
    }

    public async Task UpdateRenewalWindowAsync(Guid certificateId, AcmeRenewalWindow? window, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSafeCertificateRoot(certificateId);
            var current = await GetAsync(certificateId, cancellationToken);
            if (current is null) return;
            var updated = current with { RenewalWindowStart = window?.Start, RenewalWindowEnd = window?.End, UpdatedAt = DateTimeOffset.UtcNow };
            await WriteAtomicallyAsync(Path.Combine(CertificateRoot(certificateId), "current.json"), JsonSerializer.Serialize(updated, Json), cancellationToken);
            await _metadata.SaveAsync(updated, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateRenewalOutcomeAsync(Guid certificateId, DateTimeOffset? renewedAt, string? problemCode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSafeCertificateRoot(certificateId);
            var current = await GetAsync(certificateId, cancellationToken);
            if (current is null) return;
            var updated = current with
            {
                LastRenewalAt = renewedAt ?? current.LastRenewalAt,
                LastRenewalProblemCode = problemCode,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await WriteAtomicallyAsync(Path.Combine(CertificateRoot(certificateId), "current.json"), JsonSerializer.Serialize(updated, Json), cancellationToken);
            await _metadata.SaveAsync(updated, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateStatusAsync(Guid certificateId, CertificateStatus status, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSafeCertificateRoot(certificateId);
            var current = await GetAsync(certificateId, cancellationToken);
            if (current is null) return;
            var updated = current with { Status = status, UpdatedAt = DateTimeOffset.UtcNow };
            await WriteAtomicallyAsync(Path.Combine(CertificateRoot(certificateId), "current.json"), JsonSerializer.Serialize(updated, Json), cancellationToken);
            await _metadata.SaveAsync(updated, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = CertificateRoot(certificateId);
            if (Directory.Exists(root))
            {
                EnsureSafeCertificateRoot(certificateId);
                Directory.Delete(root, recursive: true);
            }
            await _metadata.DeleteAsync(certificateId, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private string CertificateRoot(Guid id) => Path.Combine(_root, id.ToString("D"));

    private static string FormatSha256Fingerprint(byte[] rawCertificate)
        => string.Join(':', Convert.ToHexString(SHA256.HashData(rawCertificate)).Chunk(2).Select(part => new string(part)));

    private void EnsureSafeCertificateRoot(Guid id)
    {
        if (!IsSafeCertificateRoot(id)) throw new CertificateOperationException("certificate.unsafe_path");
    }

    private bool IsSafeCertificateRoot(Guid id)
    {
        try
        {
            var certificateRoot = CertificateRoot(id);
            var versionsRoot = Path.Combine(certificateRoot, "versions");
            return !IsSymbolicLink(certificateRoot) && !IsSymbolicLink(versionsRoot);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsSymbolicLink(string path)
        => (Directory.Exists(path) || File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private void PruneOldVersions(string certificateRoot, string currentVersion)
    {
        try
        {
            var versionsRoot = Path.Combine(certificateRoot, "versions");
            var stale = Directory.EnumerateDirectories(versionsRoot)
                .Where(path => !Path.GetFileName(path).Equals(currentVersion, StringComparison.Ordinal))
                .OrderByDescending(Directory.GetCreationTimeUtc)
                .Skip(_versionRetention - 1)
                .ToArray();
            foreach (var path in stale)
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { /* Retention cleanup must not invalidate the newly atomically selected version. */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string DefaultRoot(IHostEnvironment environment) => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "certificates")
        : "/var/lib/relaxkonos/certificates";

    private static void CreateProtectedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WriteProtectedAsync(string path, string content, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        CreateProtectedDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteProtectedAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
