using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host-global profile metadata. This deliberately bypasses user/workspace repositories.</summary>
public interface IProxyProfileRepository
{
    Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken cancellationToken);
    Task<ProxyProfileDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ProxyProfileDto> UpsertAsync(Guid? id, string name, string engineId, long? expectedRevision, CancellationToken cancellationToken);
    Task<ProxyProfileDto?> SetActiveAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class SqliteProxyProfileRepository(IHostEnvironment environment, IOptions<StorageOptions> storage) : IProxyProfileRepository
{
    private readonly string _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, storage.Value.DatabasePath)}";

    public async Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT profile_id,name,engine_id,is_active,revision,created_at,updated_at FROM proxy_profiles ORDER BY name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var items = new List<ProxyProfileDto>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }
    public async Task<ProxyProfileDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT profile_id,name,engine_id,is_active,revision,created_at,updated_at FROM proxy_profiles WHERE profile_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D")); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }
    public async Task<ProxyProfileDto> UpsertAsync(Guid? id, string name, string engineId, long? expectedRevision, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || engineId != Mihomo.MihomoEngine.Id)
            throw new ProxyProfileValidationException(ProxyProblemCodes.ConfigInvalid);
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow; var profileId = id ?? Guid.NewGuid(); var existing = id is null ? null : await GetForUpdateAsync(connection, transaction, profileId, cancellationToken);
        if (existing is not null && expectedRevision != existing.Revision) throw new ProxyProfileValidationException(ProxyProblemCodes.ConfigApplyFailed);
        if (existing is null)
        {
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO proxy_profiles(profile_id,name,engine_id,is_active,revision,created_at,updated_at) VALUES($id,$name,$engine,0,1,$created,$updated);";
            Bind(insert, profileId, name.Trim(), engineId, now, now); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE proxy_profiles SET name=$name,engine_id=$engine,revision=revision+1,updated_at=$updated WHERE profile_id=$id;";
            Bind(update, profileId, name.Trim(), engineId, now, now); await update.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit(); return (await GetAsync(profileId, cancellationToken))!;
    }
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM proxy_profiles WHERE profile_id=$id AND is_active=0;"; command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) != 0;
    }
    public async Task<ProxyProfileDto?> SetActiveAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = connection.BeginTransaction();
        await using (var clear = connection.CreateCommand()) { clear.Transaction = transaction; clear.CommandText = "UPDATE proxy_profiles SET is_active=0 WHERE is_active=1;"; await clear.ExecuteNonQueryAsync(cancellationToken); }
        await using (var activate = connection.CreateCommand()) { activate.Transaction = transaction; activate.CommandText = "UPDATE proxy_profiles SET is_active=1,revision=revision+1,updated_at=$updated WHERE profile_id=$id;"; activate.Parameters.AddWithValue("$id", id.ToString("D")); activate.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O")); if (await activate.ExecuteNonQueryAsync(cancellationToken) == 0) { transaction.Rollback(); return null; } }
        transaction.Commit(); return await GetAsync(id, cancellationToken);
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) { var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken); return connection; }
    private static async Task<ProxyProfileDto?> GetForUpdateAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT profile_id,name,engine_id,is_active,revision,created_at,updated_at FROM proxy_profiles WHERE profile_id=$id;"; command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }
    private static void Bind(SqliteCommand command, Guid id, string name, string engine, DateTimeOffset created, DateTimeOffset updated)
    {
        command.Parameters.AddWithValue("$id", id.ToString("D")); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$engine", engine); command.Parameters.AddWithValue("$created", created.ToString("O")); command.Parameters.AddWithValue("$updated", updated.ToString("O"));
    }
    private static ProxyProfileDto Read(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4), DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind), DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind));
}
public sealed class ProxyProfileValidationException(string problemCode) : Exception(problemCode) { public string ProblemCode { get; } = problemCode; }
