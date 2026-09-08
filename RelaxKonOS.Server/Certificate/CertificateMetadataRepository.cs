using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Certificate;

/// <summary>SQLite source of truth for certificate metadata. Private PEM remains file-only.</summary>
internal sealed class CertificateMetadataRepository
{
    private readonly string _connectionString;
    private readonly bool _enabled;

    public CertificateMetadataRepository(IHostEnvironment environment, IConfiguration configuration)
    {
        var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        var databasePath = Path.Combine(environment.ContentRootPath, storage.DatabasePath);
        _connectionString = $"Data Source={databasePath}";
        _enabled = string.Equals(storage.Provider, "sqlite", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SaveAsync(StoredCertificate certificate, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO certificate_records (certificate_id, primary_domain, domains_json, challenge_type, key_algorithm, issuer, serial_number, thumbprint, not_before, not_after, current_version, previous_version, status, revision, created_at, updated_at, contact_email, renewal_window_start, renewal_window_end, last_renewal_at, last_renewal_problem_code, kind, fingerprint_sha256)
            VALUES ($id, $domain, $domains, $challenge, $algorithm, $issuer, $serial, $thumbprint, $notBefore, $notAfter, $version, NULL, $status, 1, $created, $updated, $email, $renewalStart, $renewalEnd, $lastRenewalAt, $lastRenewalProblem, $kind, $fingerprint)
            ON CONFLICT(certificate_id) DO UPDATE SET
                primary_domain=excluded.primary_domain, domains_json=excluded.domains_json, challenge_type=excluded.challenge_type, key_algorithm=excluded.key_algorithm,
                issuer=excluded.issuer, serial_number=excluded.serial_number, thumbprint=excluded.thumbprint,
                not_before=excluded.not_before, not_after=excluded.not_after, previous_version=certificate_records.current_version,
                current_version=excluded.current_version, status=excluded.status, contact_email=excluded.contact_email,
                renewal_window_start=excluded.renewal_window_start, renewal_window_end=excluded.renewal_window_end,
                last_renewal_at=excluded.last_renewal_at, last_renewal_problem_code=excluded.last_renewal_problem_code,
                kind=excluded.kind, fingerprint_sha256=excluded.fingerprint_sha256,
                revision=certificate_records.revision+1, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", certificate.Id.ToString("D"));
        command.Parameters.AddWithValue("$domain", certificate.PrimaryDomain);
        command.Parameters.AddWithValue("$domains", System.Text.Json.JsonSerializer.Serialize(certificate.Domains));
        command.Parameters.AddWithValue("$challenge", certificate.ChallengeType.ToString());
        command.Parameters.AddWithValue("$algorithm", certificate.KeyAlgorithm.ToString());
        command.Parameters.AddWithValue("$issuer", (object?)certificate.Issuer ?? DBNull.Value);
        command.Parameters.AddWithValue("$serial", (object?)certificate.SerialNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$thumbprint", (object?)certificate.Thumbprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$notBefore", certificate.NotBefore.ToString("O"));
        command.Parameters.AddWithValue("$notAfter", certificate.NotAfter.ToString("O"));
        command.Parameters.AddWithValue("$version", certificate.Version);
        command.Parameters.AddWithValue("$status", certificate.Status.ToString());
        command.Parameters.AddWithValue("$created", certificate.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", certificate.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$email", (object?)certificate.ContactEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("$renewalStart", certificate.RenewalWindowStart?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$renewalEnd", certificate.RenewalWindowEnd?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lastRenewalAt", certificate.LastRenewalAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lastRenewalProblem", (object?)certificate.LastRenewalProblemCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", certificate.Kind.ToString());
        command.Parameters.AddWithValue("$fingerprint", (object?)certificate.FingerprintSha256 ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredCertificate?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_enabled) return null;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT certificate_id, current_version, primary_domain, domains_json, challenge_type, key_algorithm, issuer, serial_number, thumbprint, not_before, not_after, status, created_at, updated_at, contact_email, renewal_window_start, renewal_window_end, last_renewal_at, last_renewal_problem_code, kind, fingerprint_sha256 FROM certificate_records WHERE certificate_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<StoredCertificate>> ListAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) return [];
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT certificate_id, current_version, primary_domain, domains_json, challenge_type, key_algorithm, issuer, serial_number, thumbprint, not_before, not_after, status, created_at, updated_at, contact_email, renewal_window_start, renewal_window_end, last_renewal_at, last_renewal_problem_code, kind, fingerprint_sha256 FROM certificate_records ORDER BY primary_domain;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<StoredCertificate>();
        while (await reader.ReadAsync(cancellationToken)) results.Add(Read(reader));
        return results;
    }

    public async Task UpsertAcmeAccountAsync(Uri directoryUrl, string contactEmail, string keyReference, string? accountUrl, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        var accountId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directoryUrl.AbsoluteUri)))[..32].ToLowerInvariant();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO acme_account_records(account_id,directory_url,account_url,contact_email,key_reference,revision,created_at,updated_at)
            VALUES($id,$directory,$accountUrl,$email,$key,1,$now,$now)
            ON CONFLICT(account_id) DO UPDATE SET account_url=excluded.account_url,contact_email=excluded.contact_email,key_reference=excluded.key_reference,revision=acme_account_records.revision+1,updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$directory", directoryUrl.AbsoluteUri);
        command.Parameters.AddWithValue("$accountUrl", (object?)accountUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$email", contactEmail);
        command.Parameters.AddWithValue("$key", keyReference);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM certificate_records WHERE certificate_id=$id;";
        command.Parameters.AddWithValue("$id", certificateId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static StoredCertificate Read(SqliteDataReader reader)
    {
        var domains = System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
        return new StoredCertificate(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), domains,
            Enum.Parse<CertificateChallengeType>(reader.GetString(4)), reader.IsDBNull(5) ? CertificateKeyAlgorithm.EcdsaP256 : Enum.Parse<CertificateKeyAlgorithm>(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9)), DateTimeOffset.Parse(reader.GetString(10)), Enum.Parse<CertificateStatus>(reader.GetString(11)),
            DateTimeOffset.Parse(reader.GetString(12)), DateTimeOffset.Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)), reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17)), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? CertificateKind.Acme : Enum.Parse<CertificateKind>(reader.GetString(19)),
            reader.IsDBNull(20) ? null : reader.GetString(20));
    }
}
