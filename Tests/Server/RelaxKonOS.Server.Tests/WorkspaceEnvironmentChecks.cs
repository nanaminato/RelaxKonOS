using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Settings;

internal static class WorkspaceEnvironmentChecks
{
    public static void Run(string root)
    {
        var directory = Path.Combine(root, "workspace-env-checks");
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
        var service = new WorkspaceEnvironmentService(directory, protection);
        var workspace = new Workspace { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        var initial = service.Read(workspace);
        Check(initial.Revision == "0" && initial.Variables.Count == 0 && initial.PathMode == EnvironmentPathMode.Append, "Empty overrides have an explicit initial revision.");
        const string secret = "private-workspace-value-892ac49";
        var change = new EnvironmentChangeSet([new("API_TOKEN", EnvironmentMutationKind.Set, secret), new("EMPTY", EnvironmentMutationKind.Set, "")]);
        var saved = service.Save(workspace, new("0", change, EnvironmentPathMode.Append));
        Check(saved.Revision == "1" && saved.Variables.Single(v => v.Name == "API_TOKEN") is { RawValue: secret, Sensitive: true, Source: SettingsScope.Workspace }, "Authorized values and scope are preserved.");
        Check(saved.Variables.Single(v => v.Name == "EMPTY").RawValue == "", "Empty values are not deletion.");
        Reject(() => service.Save(workspace, new("0", change, EnvironmentPathMode.Append)), 409);
        Reject(() => service.Save(workspace, new("", change, EnvironmentPathMode.Append)), 428);
        Reject(() => service.Save(workspace, new("1", new([new("BAD=NAME", EnvironmentMutationKind.Set, "x")]), EnvironmentPathMode.Append)), 400);
        Reject(() => service.Save(workspace, new("1", new([new("PATH", EnvironmentMutationKind.Set, "a;;a")]), EnvironmentPathMode.Replace)), 400);
        var restarted = new WorkspaceEnvironmentService(directory, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys"))));
        Check(restarted.Read(workspace).Variables.Any(v => v.RawValue == secret), "Restart restores encrypted values with durable revision.");
        Reject(() => restarted.Read(new Workspace { Id = workspace.Id, UserId = Guid.NewGuid() }), 404);
        var winners = 0;
        Parallel.For(0, 2, i =>
        {
            try
            {
                (i == 0 ? service : restarted).Save(workspace, new("1", new([new("WRITER", EnvironmentMutationKind.Set, i.ToString())]), EnvironmentPathMode.Append));
                Interlocked.Increment(ref winners);
            }
            catch (SettingsException error) when (error.StatusCode == 409) { }
        });
        Check(winners == 1 && restarted.Read(workspace).Revision == "2", "Two independent writers cannot silently overwrite each other.");
        var deleted = service.Save(workspace, new("2", new([new("EMPTY", EnvironmentMutationKind.Delete)]), EnvironmentPathMode.Replace));
        Check(deleted.Variables.All(v => v.Name != "EMPTY") && deleted.PathMode == EnvironmentPathMode.Replace, "Explicit deletion and PATH mode are persisted atomically.");
        var modeOnly = service.Save(workspace, new("3", new([]), EnvironmentPathMode.Append));
        Check(modeOnly.Revision == "4" && modeOnly.PathMode == EnvironmentPathMode.Append && modeOnly.Variables.Count == deleted.Variables.Count, "Mode-only edits do not manufacture variables.");
        var path = Path.Combine(directory, "data", "workspace-environment", "environment.db");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(secret, StringComparison.Ordinal), "SQLite contains no plaintext secret.");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE environments SET document = x'00'";
        corrupt.ExecuteNonQuery();
        Reject(() => service.Read(workspace), 503);
        Reject(() => service.Save(workspace, new("4", change, EnvironmentPathMode.Append)), 503);
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT hex(document) FROM environments";
        Check((string)verify.ExecuteScalar()! == "00", "Corrupt data is never silently replaced with defaults.");
        Console.WriteLine("PASS: Workspace environment durable encrypted storage, empty/delete semantics, version conflicts, independent writers, identity isolation and corrupt-data preservation.");
    }

    private static void Reject(Action action, int status)
    {
        try { action(); }
        catch (SettingsException error) when (error.StatusCode == status) { return; }
        throw new Exception($"Expected settings failure {status}.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
