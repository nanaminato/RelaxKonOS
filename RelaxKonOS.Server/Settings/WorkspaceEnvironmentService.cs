using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Settings;

/// <summary>Durable encrypted overrides. The caller must resolve Workspace ownership first.</summary>
public sealed class WorkspaceEnvironmentService
{
    private readonly string _connectionString;
    private readonly IDataProtectionProvider _protection;
    private readonly bool _windows = OperatingSystem.IsWindows();

    public WorkspaceEnvironmentService(IHostEnvironment environment, IDataProtectionProvider protection)
        : this(environment.ContentRootPath, protection) { }

    public WorkspaceEnvironmentService(string contentRoot, IDataProtectionProvider protection)
    {
        _protection = protection;
        var directory = Path.Combine(contentRoot, "data", "workspace-environment");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "environment.db");
        if (!File.Exists(path))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(path, options);
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS environments (workspace TEXT PRIMARY KEY, owner TEXT NOT NULL, revision INTEGER NOT NULL, document BLOB NOT NULL)";
        command.ExecuteNonQuery();
    }

    public WorkspaceEnvironmentSnapshot Read(Workspace workspace)
    {
        using var connection = Open();
        var (revision, document) = ReadDocument(connection, workspace);
        return Snapshot(workspace, revision, document);
    }

    public WorkspaceEnvironmentSnapshot Save(Workspace workspace, WorkspaceEnvironmentUpdate request)
    {
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new SettingsException(428, "settings.revision_required");
        if (!long.TryParse(request.ExpectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) || expected < 0)
            throw new SettingsException(400, "settings.invalid_revision");
        if (!Enum.IsDefined(request.PathMode)) throw new SettingsException(400, "settings.environment.invalid_path_mode");
        if (request.Change?.Changes is not { Count: >= 0 and <= EnvironmentValidation.MaximumChanges })
            throw new SettingsException(400, "settings.environment.invalid_batch");
        if (request.Change.Changes.Count > 0 && EnvironmentValidation.Validate(request.Change, _windows) is { } error) throw new SettingsException(400, error);
        using var connection = Open();
        // Immediate transaction excludes writers across Server processes, not just within this instance.
        using var transaction = connection.BeginTransaction(deferred: false);
        var (revision, document) = ReadDocument(connection, workspace, transaction);
        if (revision != expected) throw new SettingsException(409, "settings.revision_conflict");
        if (request.PathMode != document.PathMode && document.Values.Any(value => value.Name.Equals("PATH", _windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            && !request.Change.ConfirmHighImpact) throw new SettingsException(400, "settings.environment.high_impact_confirmation_required");
        var values = document.Values.ToDictionary(value => value.Name, _windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var mutation in request.Change.Changes)
            if (mutation.Operation == EnvironmentMutationKind.Delete) values.Remove(mutation.Name);
            else values[mutation.Name] = new(mutation.Name, mutation.Value!, mutation.ValueKind);
        // Bound the entire stored snapshot, not only the current request.
        var next = new Document(values.Values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray(), request.PathMode);
        ValidateDocument(next);
        var protectedBytes = Protector(workspace).Protect(JsonSerializer.SerializeToUtf8Bytes(next, RelaxKonOSJsonOptions.Default));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO environments VALUES ($workspace, $owner, $revision, $document) ON CONFLICT(workspace) DO UPDATE SET revision = excluded.revision, document = excluded.document WHERE owner = excluded.owner";
        command.Parameters.AddWithValue("$workspace", workspace.Id.ToString("D"));
        command.Parameters.AddWithValue("$owner", workspace.UserId.ToString("D"));
        command.Parameters.AddWithValue("$revision", checked(revision + 1));
        command.Parameters.AddWithValue("$document", protectedBytes);
        if (command.ExecuteNonQuery() != 1) throw new SettingsException(404, "settings.workspace_not_found");
        transaction.Commit();
        return Snapshot(workspace, revision + 1, next);
    }

    private (long Revision, Document Document) ReadDocument(SqliteConnection connection, Workspace workspace, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT owner, revision, document FROM environments WHERE workspace = $workspace";
        command.Parameters.AddWithValue("$workspace", workspace.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, new([], EnvironmentPathMode.Append));
        if (reader.GetString(0) != workspace.UserId.ToString("D")) throw new SettingsException(404, "settings.workspace_not_found");
        try
        {
            var document = JsonSerializer.Deserialize<Document>(Protector(workspace).Unprotect((byte[])reader[2]), RelaxKonOSJsonOptions.Default)
                ?? throw new JsonException();
            ValidateDocument(document);
            return (reader.GetInt64(1), document);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or SettingsException)
        { throw new SettingsException(503, "settings.environment.stored_data_unavailable"); }
    }

    private void ValidateDocument(Document document)
    {
        if (document.Values is null || document.Values.Any(value => value is null) || !Enum.IsDefined(document.PathMode)) throw new SettingsException(400, "settings.environment.invalid_snapshot");
        if (document.Values.Count == 0) return;
        var change = new EnvironmentChangeSet(document.Values.Select(value => new EnvironmentMutation(value.Name, EnvironmentMutationKind.Set, value.Value, value.Kind)).ToArray(), true);
        if (EnvironmentValidation.Validate(change, _windows) is { } error) throw new SettingsException(400, error);
    }

    private WorkspaceEnvironmentSnapshot Snapshot(Workspace workspace, long revision, Document document)
    {
        var values = document.Values.ToDictionary(value => value.Name, value => value.Value, _windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var variables = document.Values.Select(value =>
        {
            var expansion = EnvironmentExpansion.Expand(value.Value, values, _windows);
            return new EnvironmentVariable(value.Name, value.Value, expansion.Value, value.Kind, SettingsScope.Workspace,
                EnvironmentValidation.IsPotentiallySensitive(value.Name) || expansion.ReferencedNames.Any(EnvironmentValidation.IsPotentiallySensitive), false, expansion.Warnings);
        }).ToArray();
        return new(workspace.Id, revision.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow, variables, document.PathMode);
    }

    private IDataProtector Protector(Workspace workspace) => _protection.CreateProtector("RelaxKonOS.WorkspaceEnvironment", workspace.UserId.ToString("D"), workspace.Id.ToString("D"));
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    private sealed record StoredVariable(string Name, string Value, EnvironmentValueKind Kind);
    private sealed record Document(IReadOnlyList<StoredVariable> Values, EnvironmentPathMode PathMode);
}
