using Microsoft.Data.Sqlite;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Certificate;

internal sealed record KestrelDeploymentRecord(Guid CertificateId, string CurrentVersion, string? LastSuccessfulVersion, bool IsDefault, string? ChainSha256);

/// <summary>Host-global deployment metadata. It deliberately records deployment state separately
/// from certificate issuance: a valid new certificate is not automatically proof that Kestrel is
/// serving it.</summary>
internal sealed class CertificateDeploymentRepository
{
    private const string KestrelTargetType = "kestrel";
    private const string KestrelTargetName = "relaxkonos";
    private const string KestrelDefaultType = "kestrel-default";
    private readonly string _connectionString;
    private readonly bool _enabled;
    internal bool IsPersistent => _enabled;

    public CertificateDeploymentRepository(IHostEnvironment environment, IConfiguration configuration)
    {
        var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, storage.DatabasePath)}";
        _enabled = string.Equals(storage.Provider, "sqlite", StringComparison.OrdinalIgnoreCase);
    }

    public async Task RecordKestrelAsync(StoredCertificate certificate, bool succeeded, string? problemCode, CancellationToken cancellationToken, bool makeDefault = false)
    {
        if (!_enabled) return;
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO certificate_deployment_records(deployment_id,certificate_id,target_type,target_name,current_version,last_successful_version,last_health_check_at,problem_code,revision,created_at,updated_at)
            VALUES($id,$certificate,$type,$name,$current,$successful,$health,$problem,1,$now,$now)
            ON CONFLICT(certificate_id,target_type,target_name) DO UPDATE SET
                current_version=CASE WHEN $succeeded THEN excluded.current_version ELSE certificate_deployment_records.current_version END,
                last_successful_version=CASE WHEN $succeeded THEN excluded.current_version ELSE certificate_deployment_records.last_successful_version END,
                last_health_check_at=CASE WHEN $succeeded THEN excluded.last_health_check_at ELSE certificate_deployment_records.last_health_check_at END,
                kestrel_chain_sha256=CASE WHEN $succeeded THEN NULL ELSE certificate_deployment_records.kestrel_chain_sha256 END,
                problem_code=excluded.problem_code, revision=certificate_deployment_records.revision+1, updated_at=excluded.updated_at;
            INSERT INTO certificate_deployment_records(deployment_id,certificate_id,target_type,target_name,current_version,last_successful_version,last_health_check_at,problem_code,revision,created_at,updated_at)
            SELECT 'kestrel-default:relaxkonos',$certificate,$defaultType,$name,$current,$successful,$health,NULL,1,$now,$now
            WHERE $succeeded AND $makeDefault
            ON CONFLICT(deployment_id) DO UPDATE SET certificate_id=excluded.certificate_id,
                current_version=excluded.current_version,last_successful_version=excluded.last_successful_version,
                last_health_check_at=excluded.last_health_check_at,revision=certificate_deployment_records.revision+1,updated_at=excluded.updated_at;
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
        command.Parameters.AddWithValue("$makeDefault", makeDefault ? 1 : 0);
        command.Parameters.AddWithValue("$defaultType", KestrelDefaultType);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<KestrelDeploymentRecord>> ListKestrelAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) return [];
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT binding.certificate_id,binding.current_version,binding.last_successful_version,
                EXISTS(SELECT 1 FROM certificate_deployment_records selection
                    WHERE selection.target_type=$defaultType AND selection.target_name=$name AND selection.certificate_id=binding.certificate_id),
                binding.kestrel_chain_sha256
            FROM certificate_deployment_records binding
            WHERE binding.target_type=$type AND binding.target_name=$name AND binding.current_version IS NOT NULL
            ORDER BY binding.last_health_check_at, binding.deployment_id;
            """;
        command.Parameters.AddWithValue("$type", KestrelTargetType);
        command.Parameters.AddWithValue("$name", KestrelTargetName);
        command.Parameters.AddWithValue("$defaultType", KestrelDefaultType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<KestrelDeploymentRecord>();
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new KestrelDeploymentRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return records;
    }

    internal bool TryCommitKestrelRecovery(Guid certificateId, RenewalRecoveryBaseline expected, string version, string chainSha256)
    {
        if (!_enabled) return false;
        // This compare-and-set is serialized with the in-memory SNI check. Bound SQLite lock wait.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { DefaultTimeout = 1 }.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE certificate_deployment_records
            SET current_version=$version,last_successful_version=$version,last_health_check_at=$now,
                kestrel_chain_sha256=$chain,problem_code=NULL,revision=revision+1,updated_at=$now
            WHERE certificate_id=$id AND target_type='kestrel' AND target_name='relaxkonos'
                AND current_version=$expected AND last_successful_version=$expected
                AND EXISTS(SELECT 1 FROM certificate_records WHERE certificate_id=$id AND current_version=$expected
                    AND upper(replace(fingerprint_sha256,':',''))=$fingerprint AND status!='Revoked')
                AND EXISTS(SELECT 1 FROM certificate_deployment_records WHERE deployment_id='kestrel-default:relaxkonos'
                    AND certificate_id=$id)=$default;
            """;
        command.Parameters.AddWithValue("$id", certificateId.ToString("D"));
        command.Parameters.AddWithValue("$expected", expected.Version);
        command.Parameters.AddWithValue("$fingerprint", expected.Fingerprint);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$chain", chainSha256);
        command.Parameters.AddWithValue("$default", expected.IsDefault);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1) return false;
        if (expected.IsDefault)
        {
            command.CommandText = """
                UPDATE certificate_deployment_records SET current_version=$version,last_successful_version=$version,
                    last_health_check_at=$now,revision=revision+1,updated_at=$now
                WHERE deployment_id='kestrel-default:relaxkonos' AND certificate_id=$id;
                """;
            if (command.ExecuteNonQuery() != 1) return false;
        }
        transaction.Commit();
        return true;
    }

    public async Task RemoveKestrelAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM certificate_deployment_records WHERE certificate_id=$certificate AND target_type IN ($type,$defaultType) AND target_name=$name;";
        command.Parameters.AddWithValue("$certificate", certificateId.ToString("D"));
        command.Parameters.AddWithValue("$type", KestrelTargetType);
        command.Parameters.AddWithValue("$defaultType", KestrelDefaultType);
        command.Parameters.AddWithValue("$name", KestrelTargetName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task RecordNginxRenewalAsync(PendingNginxRenewal renewal, string instanceId, Guid? operationId,
        bool succeeded, string? problemCode, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO certificate_deployment_records(deployment_id,certificate_id,target_type,target_name,current_version,
                last_successful_version,last_health_check_at,problem_code,revision,created_at,updated_at,renewal_operation_id,webserver_operation_id)
            VALUES($id,$certificate,'nginx',$instance,$version,
                CASE WHEN $succeeded THEN $version END,CASE WHEN $succeeded THEN $now END,$problem,1,$now,$now,$renewal,$operation)
            ON CONFLICT(certificate_id,target_type,target_name) DO UPDATE SET
                current_version=excluded.current_version,
                last_successful_version=COALESCE(excluded.last_successful_version,certificate_deployment_records.last_successful_version),
                last_health_check_at=COALESCE(excluded.last_health_check_at,certificate_deployment_records.last_health_check_at),
                problem_code=excluded.problem_code,renewal_operation_id=excluded.renewal_operation_id,
                webserver_operation_id=excluded.webserver_operation_id,revision=certificate_deployment_records.revision+1,updated_at=excluded.updated_at
            WHERE certificate_deployment_records.current_version IS NOT excluded.current_version
                OR certificate_deployment_records.problem_code IS NOT excluded.problem_code
                OR certificate_deployment_records.webserver_operation_id IS NOT excluded.webserver_operation_id
                OR ($succeeded AND certificate_deployment_records.last_successful_version IS NOT excluded.last_successful_version);
            """;
        command.Parameters.AddWithValue("$id", $"nginx:{renewal.CertificateId:D}:{instanceId}");
        command.Parameters.AddWithValue("$certificate", renewal.CertificateId.ToString("D"));
        command.Parameters.AddWithValue("$instance", instanceId);
        command.Parameters.AddWithValue("$version", renewal.Version);
        command.Parameters.AddWithValue("$renewal", renewal.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$operation", (object?)operationId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$succeeded", succeeded);
        command.Parameters.AddWithValue("$problem", (object?)problemCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
}
