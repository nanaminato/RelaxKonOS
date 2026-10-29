using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Tailscale;
using System.Text.Json;

namespace RelaxKonOS.Server.Tailscale;

/// <summary>Encrypted receipts survive module eviction and Server restart; they are not host facts.</summary>
public sealed class TailscaleOperationJournal
{
    private readonly string _directory;
    private readonly string _connectionString;
    private readonly IDataProtector _protector;

    public TailscaleOperationJournal(IHostEnvironment environment, IDataProtectionProvider protection)
    {
        _directory = Path.Combine(environment.ContentRootPath, "data", "tailscale-operations");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = Path.Combine(_directory, "operations.db");
        if (!File.Exists(database))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var created = new FileStream(database, options);
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString();
        _protector = protection.CreateProtector("RelaxKonOS.Tailscale.Operations.v1");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS operations (id TEXT PRIMARY KEY, document BLOB NOT NULL)";
        command.ExecuteNonQuery();
    }

    public async Task<FileStream> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(_directory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, cancellationToken); }
        }
    }

    internal StoredTailscaleOperation? Read(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM operations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteScalar() is byte[] bytes
            ? JsonSerializer.Deserialize<StoredTailscaleOperation>(_protector.Unprotect(bytes), RelaxKonOSJsonOptions.Default)
                ?? throw new InvalidDataException("tailscale.operation.invalid_record") : null;
    }

    internal void Save(StoredTailscaleOperation stored)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(stored, RelaxKonOSJsonOptions.Default));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO operations(id, document) VALUES ($id, $document) ON CONFLICT(id) DO UPDATE SET document = excluded.document";
        command.Parameters.AddWithValue("$id", stored.Operation.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$document", bytes);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }
}

internal sealed record TailscaleRecoveryPlan(string ExpectedRevision, DateTimeOffset ExpiresAt);
internal sealed record StoredTailscaleOperation(string Actor, TailscaleOperation Operation, TailscaleControlSnapshot Baseline,
    TailscaleRecoveryPlan? RecoveryPlan = null, TailscaleCloseRequest? ClosingRequest = null);
