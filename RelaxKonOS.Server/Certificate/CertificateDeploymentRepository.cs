using Microsoft.Data.Sqlite;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Certificate;

internal sealed record KestrelDeploymentRecord(Guid CertificateId, string CurrentVersion, string? LastSuccessfulVersion);

/// <summary>Host-global deployment metadata. It deliberately records deployment state separately
/// from certificate issuance: a valid new certificate is not automatically proof that Kestrel is
/// serving it.</summary>
internal sealed class CertificateDeploymentRepository
{
    private const string KestrelTargetType = "kestrel";
    private const string KestrelTargetName = "remoteos";
    private readonly string _connectionString;
    private readonly bool _enabled;

    public CertificateDeploymentRepository(IHostEnvironment environment, IConfiguration configuration)
    {
        var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, storage.DatabasePath)}";
        _enabled = string.Equals(storage.Provider, "sqlite", StringComparison.OrdinalIgnoreCase);
    }

    public async Task RecordKestrelAsync(StoredCertificate certificate, bool succeeded, string? problemCode, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO certificate_deployment_records(deployment_id,certificate_id,target_type,target_name,current_version,last_successful_version,last_health_check_at,problem_code,revision,created_at,updated_at)
            VALUES($id,$certificate,$type,$name,$current,$successful,$health,$problem,1,$now,$now)
            ON CONFLICT(certificate_id,target_type,target_name) DO UPDATE SET
                current_version=CASE WHEN $succeeded THEN excluded.current_version ELSE certificate_deployment_records.current_version END,
                last_successful_version=CASE WHEN $succeeded THEN excluded.current_version ELSE certificate_deployment_records.last_successful_version END,
                last_health_check_at=CASE WHEN $succeeded THEN excluded.last_health_check_at ELSE certificate_deployment_records.last_health_check_at END,
                problem_code=excluded.problem_code, revision=certificate_deployment_records.revision+1, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", $"kestrel:{certificate.Id:D}");
        command.Parameters.AddWithValue("$certificate", certificate.Id.ToString("D"));
        command.Parameters.AddWithValue("$type", KestrelTargetType);
        command.Parameters.AddWithValue("$name", KestrelTargetName);
        command.Parameters.AddWithValue("$current", succeeded ? certificate.Version : (object)DBNull.Value);
        command.Parameters.AddWithValue("$successful", succeeded ? certificate.Version : (object)DBNull.Value);
        command.Parameters.AddWithValue("$health", succeeded ? now : (object)DBNull.Value);
        command.Parameters.AddWithValue("$problem", (object?)problemCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$succeeded", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KestrelDeploymentRecord>> ListKestrelAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) return [];
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT certificate_id,current_version,last_successful_version FROM certificate_deployment_records
            WHERE target_type=$type AND target_name=$name AND current_version IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$type", KestrelTargetType);
        command.Parameters.AddWithValue("$name", KestrelTargetName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<KestrelDeploymentRecord>();
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new KestrelDeploymentRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return records;
    }

    public async Task RemoveKestrelAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM certificate_deployment_records WHERE certificate_id=$certificate AND target_type=$type AND target_name=$name;";
        command.Parameters.AddWithValue("$certificate", certificateId.ToString("D"));
        command.Parameters.AddWithValue("$type", KestrelTargetType);
        command.Parameters.AddWithValue("$name", KestrelTargetName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
