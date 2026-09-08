using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.WebServer;

internal sealed record NginxConfigurationSnapshot(string Id, string ContentHash);

/// <summary>Host-global metadata for detected instances and configuration transactions. Snapshot
/// content is never put in SQLite or audit records; the V1 integration changes only a RelaxKonOS-
/// owned file, so a main-configuration hash is sufficient to detect unsafe external changes.</summary>
internal sealed class WebServerMetadataRepository
{
    private readonly string _connectionString;
    private readonly bool _enabled;

    public WebServerMetadataRepository(IHostEnvironment environment, IConfiguration configuration)
    {
        var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, storage.DatabasePath)}";
        _enabled = string.Equals(storage.Provider, "sqlite", StringComparison.OrdinalIgnoreCase);
    }

    public async Task UpsertInstanceAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO webserver_instances(instance_id,provider_id,management_mode,executable_path,configuration_path,detected_at,revision)
            VALUES($id,$provider,$mode,$executable,$configuration,$detected,1)
            ON CONFLICT(instance_id) DO UPDATE SET provider_id=excluded.provider_id,management_mode=excluded.management_mode,
                executable_path=excluded.executable_path,configuration_path=excluded.configuration_path,detected_at=excluded.detected_at,
                revision=webserver_instances.revision+1;
            """;
        command.Parameters.AddWithValue("$id", instance.Id);
        command.Parameters.AddWithValue("$provider", instance.ProviderId);
        command.Parameters.AddWithValue("$mode", instance.ManagementMode.ToString());
        command.Parameters.AddWithValue("$executable", instance.ExecutablePath);
        command.Parameters.AddWithValue("$configuration", (object?)instance.ConfigurationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$detected", instance.DetectedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<NginxConfigurationSnapshot?> CreateSnapshotAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        if (instance.ConfigurationPath is null || !File.Exists(instance.ConfigurationPath)) return null;
        var contentHash = await HashFileAsync(instance.ConfigurationPath, cancellationToken);
        var snapshot = new NginxConfigurationSnapshot(Guid.NewGuid().ToString("D"), contentHash);
        if (!_enabled) return snapshot;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO webserver_config_snapshots(snapshot_id,instance_id,content_hash,external_modified,recoverable,created_at)
            VALUES($id,$instance,$hash,0,1,$created);
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$instance", instance.Id);
        command.Parameters.AddWithValue("$hash", snapshot.ContentHash);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var retention = connection.CreateCommand();
        retention.CommandText = """
            DELETE FROM webserver_config_snapshots
            WHERE instance_id=$instance AND snapshot_id NOT IN (
                SELECT snapshot_id FROM webserver_config_snapshots
                WHERE instance_id=$instance ORDER BY created_at DESC LIMIT 100
            );
            """;
        retention.Parameters.AddWithValue("$instance", instance.Id);
        await retention.ExecuteNonQueryAsync(cancellationToken);
        return snapshot;
    }

    public async Task<bool> IsSnapshotCurrentAsync(string configurationPath, NginxConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        try { return string.Equals(await HashFileAsync(configurationPath, cancellationToken), snapshot.ContentHash, StringComparison.Ordinal); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
