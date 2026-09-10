using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.FileServices;

public sealed record WindowsSmbOwnershipRecord(string Id, string Name, string PathHash, string Snapshot, bool ReconciliationRequired);
public sealed record WindowsSmbServerSecurityRecord(string SnapshotHash, bool ReconciliationRequired);
public interface IWindowsSmbOwnershipLedger
{
    Task<WindowsSmbOwnershipRecord?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyDictionary<string, WindowsSmbOwnershipRecord>> ListAsync(CancellationToken ct);
    Task UpsertAsync(WindowsSmbOwnershipRecord record, CancellationToken ct);
    Task RemoveAsync(string id, CancellationToken ct);
    Task<WindowsSmbServerSecurityRecord?> GetServerSecurityAsync(CancellationToken ct);
    Task UpsertServerSecurityAsync(WindowsSmbServerSecurityRecord record, CancellationToken ct);
}

/// <summary>Host-global ownership boundary. It records only ownership/snapshots, never a desired share configuration.</summary>
public sealed class WindowsSmbOwnershipLedger : IWindowsSmbOwnershipLedger
{
    private readonly string _connectionString;
    private readonly bool _memory;
    private readonly ConcurrentDictionary<string, WindowsSmbOwnershipRecord> _fallback = new(StringComparer.Ordinal);
    private WindowsSmbServerSecurityRecord? _securityFallback;
    public WindowsSmbOwnershipLedger(IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, options.DatabasePath)}";
        _memory = !string.Equals(options.Provider, "sqlite", StringComparison.OrdinalIgnoreCase);
    }
    public async Task<WindowsSmbOwnershipRecord?> GetAsync(string id, CancellationToken ct)
    {
        if (_memory) return _fallback.GetValueOrDefault(id);
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT share_id, share_name, path_hash, snapshot_hash, reconciliation_required FROM smb_windows_ownership_ledger WHERE share_id = $id;"; command.Parameters.AddWithValue("$id", id);
        await using var row = await command.ExecuteReaderAsync(ct); return await row.ReadAsync(ct) ? new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetBoolean(4)) : null;
    }
    public async Task<IReadOnlyDictionary<string, WindowsSmbOwnershipRecord>> ListAsync(CancellationToken ct)
    {
        if (_memory) return new Dictionary<string, WindowsSmbOwnershipRecord>(_fallback, StringComparer.Ordinal);
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand(); command.CommandText = "SELECT share_id, share_name, path_hash, snapshot_hash, reconciliation_required FROM smb_windows_ownership_ledger;";
        await using var rows = await command.ExecuteReaderAsync(ct); var result = new Dictionary<string, WindowsSmbOwnershipRecord>(StringComparer.Ordinal);
        while (await rows.ReadAsync(ct)) { var record = new WindowsSmbOwnershipRecord(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetBoolean(4)); result.Add(record.Id, record); }
        return result;
    }
    public async Task UpsertAsync(WindowsSmbOwnershipRecord record, CancellationToken ct)
    {
        if (_memory) { _fallback[record.Id] = record; return; }
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO smb_windows_ownership_ledger(share_id, share_name, path_hash, snapshot_hash, reconciliation_required, updated_at) VALUES($id,$name,$path,$snapshot,$reconcile,$now) ON CONFLICT(share_id) DO UPDATE SET share_name=$name,path_hash=$path,snapshot_hash=$snapshot,reconciliation_required=$reconcile,updated_at=$now;";
        command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$name", record.Name); command.Parameters.AddWithValue("$path", record.PathHash); command.Parameters.AddWithValue("$snapshot", record.Snapshot); command.Parameters.AddWithValue("$reconcile", record.ReconciliationRequired); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task RemoveAsync(string id, CancellationToken ct) { if (_memory) { _fallback.TryRemove(id, out _); return; } await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM smb_windows_ownership_ledger WHERE share_id=$id;"; command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(ct); }
    public async Task<WindowsSmbServerSecurityRecord?> GetServerSecurityAsync(CancellationToken ct)
    {
        if (_memory) return _securityFallback;
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_hash, reconciliation_required FROM smb_windows_server_security_ledger WHERE ledger_id=1;";
        await using var row = await command.ExecuteReaderAsync(ct); return await row.ReadAsync(ct) ? new(row.GetString(0), row.GetBoolean(1)) : null;
    }
    public async Task UpsertServerSecurityAsync(WindowsSmbServerSecurityRecord record, CancellationToken ct)
    {
        if (_memory) { _securityFallback = record; return; }
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO smb_windows_server_security_ledger(ledger_id, snapshot_hash, reconciliation_required, updated_at) VALUES(1,$snapshot,$reconcile,$now) ON CONFLICT(ledger_id) DO UPDATE SET snapshot_hash=$snapshot,reconciliation_required=$reconcile,updated_at=$now;";
        command.Parameters.AddWithValue("$snapshot", record.SnapshotHash); command.Parameters.AddWithValue("$reconcile", record.ReconciliationRequired); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(ct);
    }
    public static string PathHash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct) { var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct); return connection; }
    public static string SnapshotHash(string snapshot) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
}
