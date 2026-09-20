using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

/// <summary>
/// Host-global Docker proxy preference over the shared SQLite database. The proxy URLs are
/// DataProtection-protected before they reach disk because a proxy URL may embed a credential;
/// the bypass list and the flags are not secret and stay readable.
/// </summary>
public sealed class SqliteDockerProxySettingsRepository(IHostEnvironment environment, IOptions<StorageOptions> storage,
    IDataProtectionProvider dataProtection) : IDockerProxySettingsRepository
{
    private readonly string _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, storage.Value.DatabasePath)}";
    private readonly IDataProtector _protector = dataProtection.CreateProtector("RelaxKonOS.Docker.ProxySettings.v1");

    public async Task<DockerProxySetting?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled,source,http_proxy,https_proxy,no_proxy,apply_to_engine,apply_to_build,engine_applied,engine_problem_code,updated_at,updated_by FROM docker_proxy_settings WHERE settings_id=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new DockerProxySetting
        {
            Enabled = reader.GetInt64(0) != 0,
            Source = Enum.TryParse<DockerProxySource>(reader.GetString(1), ignoreCase: true, out var source) ? source : DockerProxySource.Custom,
            HttpProxy = Unprotect(reader.GetString(2)),
            HttpsProxy = Unprotect(reader.GetString(3)),
            NoProxy = reader.GetString(4),
            ApplyToEngine = reader.GetInt64(5) != 0,
            ApplyToBuild = reader.GetInt64(6) != 0,
            EngineApplied = reader.GetInt64(7) != 0,
            EngineProblemCode = reader.GetString(8),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
            UpdatedBy = reader.GetString(10),
        };
    }

    public async Task SaveAsync(DockerProxySetting setting, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO docker_proxy_settings(settings_id,enabled,source,http_proxy,https_proxy,no_proxy,apply_to_engine,apply_to_build,engine_applied,engine_problem_code,updated_at,updated_by)
            VALUES(1,$enabled,$source,$http,$https,$noProxy,$engine,$build,$applied,$engineProblem,$updated,$actor)
            ON CONFLICT(settings_id) DO UPDATE SET
                enabled=$enabled,source=$source,http_proxy=$http,https_proxy=$https,no_proxy=$noProxy,
                apply_to_engine=$engine,apply_to_build=$build,engine_applied=$applied,
                engine_problem_code=$engineProblem,updated_at=$updated,updated_by=$actor;
            """;
        command.Parameters.AddWithValue("$enabled", setting.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$source", setting.Source.ToString());
        command.Parameters.AddWithValue("$http", Protect(setting.HttpProxy));
        command.Parameters.AddWithValue("$https", Protect(setting.HttpsProxy));
        command.Parameters.AddWithValue("$noProxy", setting.NoProxy);
        command.Parameters.AddWithValue("$engine", setting.ApplyToEngine ? 1 : 0);
        command.Parameters.AddWithValue("$build", setting.ApplyToBuild ? 1 : 0);
        command.Parameters.AddWithValue("$applied", setting.EngineApplied ? 1 : 0);
        command.Parameters.AddWithValue("$engineProblem", setting.EngineProblemCode);
        command.Parameters.AddWithValue("$updated", setting.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$actor", setting.UpdatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM docker_proxy_settings WHERE settings_id=1;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string Protect(string value) => value.Length == 0 ? string.Empty : _protector.Protect(value);

    private string Unprotect(string value)
    {
        if (value.Length == 0) return string.Empty;
        try { return _protector.Unprotect(value); }
        // The data protection keys are gone, so the stored proxy is unusable. Reporting it is
        // better than silently pretending no proxy was ever configured.
        catch (CryptographicException exception) { throw new DockerProxySecretUnreadableException(exception); }
    }
}

/// <summary>The stored proxy values cannot be decrypted with the current data protection keys.</summary>
public sealed class DockerProxySecretUnreadableException(Exception inner) : Exception("The stored Docker proxy value cannot be decrypted.", inner);
