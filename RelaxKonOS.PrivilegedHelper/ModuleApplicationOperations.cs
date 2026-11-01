using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// MySQL application-management adapter. The module never supplies SQL, a socket, a user, a password
/// or a secret path: the Helper reads one root-owned policy and two root-owned credential files, and
/// executes a fixed credential-rotation protocol through the host's own mysql client.
/// </summary>
public static partial class PrivilegedOperationExecutor
{
    private const string ModuleApplicationPolicyPath = "/etc/relaxkonos/module-applications.json";
    private const string ModuleApplicationStateRoot = "/var/lib/relaxkonos/module-applications";

    private sealed record ModuleApplicationReceipt(Guid OperationId, string Instance, string Account,
        string OldRevision, string NewRevision, bool Committed);

    private static Task<PrivilegedOperationResult> QueryModuleApplicationAsync(ModuleApplicationQuery? query)
    {
        if (!OperatingSystem.IsLinux())
            return Task.FromResult(Fail(69, PrivilegedProblemCode.UnsupportedOperation, "module application host is unsupported"));
        if (query is null || !ModuleApplicationIdentity.Identifier(query.Instance) || !Enum.IsDefined(query.Action))
            return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "module application query is invalid"));
        if (!ModuleApplicationPolicy.TryLoad(ModuleApplicationPolicyPath, out var policy, out _) || policy is null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "module application policy unavailable"));
        if (!policy.TryFind(query.Instance, query.Action, out var instance) || instance is null)
            return Task.FromResult(Fail(64, PrivilegedProblemCode.AccessDenied, "module application instance is not approved"));
        var state = ReadApplicationState(instance);
        return Task.FromResult(state is null
            ? Fail(65, PrivilegedProblemCode.Conflict, "module application state is unavailable")
            : new PrivilegedOperationResult(true, ModuleApplication: state));
    }

    private static async Task<PrivilegedOperationResult> ApplyModuleApplicationTransactionAsync(
        ModuleApplicationTransaction? transaction, Guid operationId) =>
        await ApplyModuleApplicationTransactionAsync(transaction, operationId, RunMysqlAsync);

    // Internal execution seam permits lost-response fault injection without a configurable executable.
    internal static async Task<PrivilegedOperationResult> ApplyModuleApplicationTransactionAsync(
        ModuleApplicationTransaction? transaction, Guid operationId,
        Func<string, string, string, string, Task<(bool Success, string Output)>> changeCredential)
    {
        if (!OperatingSystem.IsLinux())
            return Fail(69, PrivilegedProblemCode.UnsupportedOperation, "module application host is unsupported");
        if (transaction is null || !Enum.IsDefined(transaction.Action)
            || !ModuleApplicationIdentity.Usable(transaction.Expected)
            || transaction.Instance != transaction.Expected.Instance)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "module application transaction is invalid");
        if (!ModuleApplicationPolicy.TryLoad(ModuleApplicationPolicyPath, out var policy, out _) || policy is null)
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module application policy unavailable");
        if (!policy.TryFind(transaction.Instance, transaction.Action, out var instance) || instance is null)
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module application instance is no longer approved");
        var receiptPath = Path.Combine(ModuleApplicationStateRoot, "operations", operationId.ToString("N") + ".json");
        // A rotation receipt must be checked before the current secret revision: after a successful
        // rotation the revision has necessarily moved, and a replay must still report recovery rather
        // than being mistaken for an ordinary stale plan.
        if (transaction.Action == ModuleApplicationAction.RotateCredential && File.Exists(receiptPath))
            return Fail(65, PrivilegedProblemCode.RecoveryRequired, "module application operation requires recovery");
        if (ApplicationPolicyRevision(instance) != transaction.Expected.PolicyRevision)
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module application policy or secrets changed");
        var current = ReadApplicationState(instance);
        if (current is null || !SameApplicationState(current, transaction.Expected))
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module application state changed");
        if (transaction.Action == ModuleApplicationAction.Query)
            return new PrivilegedOperationResult(true);
        if (!ModuleApplicationSecret.TryLoad(instance.AdminSecret, instance.AdminUser, out var adminSecret, out _)
            || adminSecret is null)
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module application administrative secret is unavailable");
        if (!ModuleApplicationSecret.TryLoad(instance.AccountSecret, instance.Account, out var accountSecret, out _)
            || accountSecret is null || accountSecret.Revision != transaction.Expected.SecretRevision
            || !CurrentApplicationPolicyMatches(transaction))
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module application account secret changed");
        var newRevision = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var newPassword = NewPassword();
        var receipt = new ModuleApplicationReceipt(operationId, instance.Instance, instance.Account,
            accountSecret.Revision, newRevision, false);
        try
        {
            WriteApplicationReceipt(receiptPath, receipt);
            var alter = await changeCredential(instance.Socket, instance.AdminUser, adminSecret.Password,
                $"ALTER USER '{instance.Account}'@'{instance.AccountHost}' IDENTIFIED BY '{newPassword}';");
            if (!alter.Success)
                // A non-successful client result cannot prove ALTER USER did not commit.
                return Fail(65, PrivilegedProblemCode.RecoveryRequired, "module application credential change outcome is unknown");
            if (!(await RunMysqlAsync(instance.Socket, instance.Account, newPassword, "SELECT 1;")).Success)
            {
                var rolledBack = await RollbackAsync(instance, adminSecret.Password, accountSecret.Password);
                return rolledBack
                    ? Fail(65, PrivilegedProblemCode.Conflict, "module application new credential could not be verified")
                    : Fail(65, PrivilegedProblemCode.RecoveryRequired, "module application credential verification failed and rollback failed");
            }
            // Re-read the account secret immediately before committing. If another operation changed it
            // while the database password was being changed, the database must be rolled back.
            if (!ModuleApplicationSecret.TryLoad(instance.AccountSecret, instance.Account, out var latest, out _)
                || latest is null || latest.Revision != accountSecret.Revision
                || !ModuleApplicationPolicy.TryLoad(ModuleApplicationPolicyPath, out var latestPolicy, out _)
                || latestPolicy is null || !latestPolicy.TryFind(transaction.Instance, transaction.Action, out var latestInstance)
                || latestInstance is null || ApplicationPolicyRevision(latestInstance) != transaction.Expected.PolicyRevision)
            {
                var rolledBack = await RollbackAsync(instance, adminSecret.Password, accountSecret.Password);
                return rolledBack
                    ? Fail(65, PrivilegedProblemCode.PlanChanged, "module application account secret changed during rotation")
                    : Fail(65, PrivilegedProblemCode.RecoveryRequired, "module application secret changed and rollback failed");
            }
            WriteApplicationSecret(instance.AccountSecret, newRevision, instance.Account, newPassword);
            WriteApplicationReceipt(receiptPath, receipt with { Committed = true });
            return new(true);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(65, PrivilegedProblemCode.RecoveryRequired, "module application operation could not be committed");
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return Fail(65, PrivilegedProblemCode.RecoveryRequired, "module application operation could not be committed");
        }
    }

    private static ModuleApplicationState? ReadApplicationState(ModuleApplicationInstance instance)
    {
        if (!ModuleApplicationSecret.TryLoad(instance.AdminSecret, instance.AdminUser, out var adminSecret, out _)
            || adminSecret is null)
            return null;
        if (!ModuleApplicationSecret.TryLoad(instance.AccountSecret, instance.Account, out var accountSecret, out _)
            || accountSecret is null)
            return null;
        var policyRevision = ApplicationPolicyRevision(instance, adminSecret.Revision, accountSecret.Revision);
        var query = RunMysqlAsync(instance.Socket, instance.AdminUser, adminSecret.Password,
            $"SELECT VERSION(), COUNT(*) FROM mysql.user WHERE user='{instance.Account}' AND host='{instance.AccountHost}';")
            .GetAwaiter().GetResult();
        if (!query.Success || ApplicationPolicyRevision(instance) != policyRevision) return null;
        var fields = query.Output.Trim().Split('\t', StringSplitOptions.TrimEntries);
        if (fields.Length < 2 || !int.TryParse(fields[^1], out var count))
            return null;
        return new(instance.Instance, instance.Protocol, instance.Account, instance.AccountHost,
            count == 1, accountSecret.Revision, fields[0], policyRevision);
    }

    private static async Task<bool> RollbackAsync(ModuleApplicationInstance instance, string adminPassword,
        string oldPassword)
    {
        var alter = await RunMysqlAsync(instance.Socket, instance.AdminUser, adminPassword,
            $"ALTER USER '{instance.Account}'@'{instance.AccountHost}' IDENTIFIED BY '{SqlString(oldPassword)}';");
        if (!alter.Success) return false;
        return (await RunMysqlAsync(instance.Socket, instance.Account, oldPassword, "SELECT 1;")).Success;
    }

    private static async Task<(bool Success, string Output)> RunMysqlAsync(string socket, string user,
        string password, string sql)
    {
        const string binary = "/usr/bin/mysql";
        if (!File.Exists(binary)) return (false, "");
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["MYSQL_PWD"] = password;
        foreach (var argument in new[]
        {
            "--protocol=socket", "--socket=" + socket, "--user=" + user, "--batch", "--skip-column-names",
            "--connect-timeout=10",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return (false, "");
        try
        {
            await process.StandardInput.WriteAsync(sql);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            // stderr is intentionally not surfaced: the mysql client can echo connection diagnostics.
            _ = await error;
            return (process.ExitCode == 0, (await output).Trim());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (false, "");
        }
    }

    private static string SqlString(string value) => value.Replace("\\", "\\\\").Replace("'", "''");

    private static string NewPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool CurrentApplicationPolicyMatches(ModuleApplicationTransaction transaction) =>
        ModuleApplicationPolicy.TryLoad(ModuleApplicationPolicyPath, out var policy, out _) && policy is not null
        && policy.TryFind(transaction.Instance, transaction.Action, out var instance) && instance is not null
        && ApplicationPolicyRevision(instance) == transaction.Expected.PolicyRevision;

    // Hash only non-secret policy metadata and random revisions, never credential values.
    internal static string? ApplicationPolicyRevision(ModuleApplicationInstance instance)
    {
        if (!ModuleApplicationSecret.TryLoad(instance.AdminSecret, instance.AdminUser, out var admin, out _)
            || admin is null
            || !ModuleApplicationSecret.TryLoad(instance.AccountSecret, instance.Account, out var account, out _)
            || account is null) return null;
        return ApplicationPolicyRevision(instance, admin.Revision, account.Revision);
    }

    internal static string ApplicationPolicyRevision(ModuleApplicationInstance instance, string adminRevision,
        string accountRevision) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { instance, adminRevision, accountRevision })));

    private static bool SameApplicationState(ModuleApplicationState left, ModuleApplicationState right) =>
        left.Instance == right.Instance && left.Protocol == right.Protocol && left.Account == right.Account
        && left.AccountHost == right.AccountHost && left.AccountExists == right.AccountExists
        && left.SecretRevision == right.SecretRevision && left.ServerVersion == right.ServerVersion
        && left.PolicyRevision == right.PolicyRevision;

    private static void WriteApplicationSecret(string path, string revision, string user, string password)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, revision, user, password });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteApplicationReceipt(string path, ModuleApplicationReceipt receipt)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
