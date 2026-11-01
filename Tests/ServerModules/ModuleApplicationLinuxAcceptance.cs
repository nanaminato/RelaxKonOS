extern alias modulehelper;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Disposable Ubuntu acceptance for the DC-04 MySQL management adapter: real MySQL accounts, root-only
/// credential files, read-only instance observation, credential rotation, old-credential rejection,
/// replay refusal, drift refusal and secret-store update. Lost-credential recovery stays unsupported.
/// </summary>
internal static class ModuleApplicationLinuxAcceptance
{
    private const string PolicyPath = "/etc/relaxkonos/module-applications.json";
    private const string SecretRoot = "/etc/relaxkonos/secrets";
    private const string AdminSecretPath = SecretRoot + "/mysql-admin.json";
    private const string AccountSecretPath = SecretRoot + "/mysql-app.json";
    private const string Socket = "/run/mysqld/mysqld.sock";
    private const string Instance = "mysql";
    private const string AdminUser = "relaxkonos_admin";
    private const string Account = "relaxkonos_app";
    private const string AdminPassword = "admin-old-password";
    private const string OldPassword = "app-old-password";
    private static readonly modulehelper::PrivilegedOperationPolicy HelperPolicy = new([], [], [], [], null);

    internal static async Task RunAsync(string[] args, Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP: application acceptance requires Linux."); return; }
        if (!Environment.IsPrivilegedProcess)
            throw new InvalidOperationException("The application acceptance must run as root.");
        foreach (var binary in new[] { "/usr/bin/apt-get", "/usr/sbin/service", "/usr/bin/mysql" })
            if (!File.Exists(binary) && !args.Contains("--install", StringComparer.Ordinal))
                throw new InvalidOperationException("Application acceptance requires " + binary + "; pass --install.");
        try
        {
            if (args.Contains("--install", StringComparer.Ordinal))
            {
                await RunAsync("/usr/bin/apt-get", ["update"], 600);
                var installed = await RunAsync("/usr/bin/apt-get", ["install", "-y", "mysql-server"], 1200);
                check(installed.ExitCode == 0 && File.Exists("/usr/bin/mysql"),
                    "real apt installs mysql-server and its client for the application adapter");
            }
            check((await RunAsync("/usr/sbin/service", ["mysql", "start"], 120)).ExitCode == 0,
                "real MySQL service starts before the application protocol acceptance");
            var provision = await RunSqlAsync(Socket, "root", "",
                $"CREATE USER IF NOT EXISTS '{AdminUser}'@'localhost' IDENTIFIED BY '{AdminPassword}';"
                + $" GRANT ALL PRIVILEGES ON *.* TO '{AdminUser}'@'localhost' WITH GRANT OPTION;"
                + $" CREATE USER IF NOT EXISTS '{Account}'@'localhost' IDENTIFIED BY '{OldPassword}';"
                + " FLUSH PRIVILEGES;");
            check(provision.ExitCode == 0, "real MySQL creates the administrative and application accounts");

            var adminRevision = Revision();
            var accountRevision = Revision();
            WriteSecret(AdminSecretPath, adminRevision, AdminUser, AdminPassword);
            WriteSecret(AccountSecretPath, accountRevision, Account, OldPassword);
            await WritePolicyAsync();
            check(modulehelper::ModuleApplicationPolicy.TryLoad(PolicyPath, out _, out _)
                && modulehelper::ModuleApplicationSecret.TryLoad(AdminSecretPath, AdminUser, out _, out _)
                && modulehelper::ModuleApplicationSecret.TryLoad(AccountSecretPath, Account, out _, out _),
                "Helper loads the application policy and both root-only credential files");

            var query = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleApplicationQuery,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleApplicationQuery: new(Instance, ModuleApplicationAction.Query)));
            var state = query.ModuleApplication;
            check(query.Success && state is { AccountExists: true, Account: Account, Protocol: "mysql-admin" }
                && state.SecretRevision == accountRevision && state.ServerVersion.Length > 0,
                "Helper observes the exact instance, account, server version and secret revision without a password");

            var rotationId = Guid.NewGuid();
            var rotation = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleApplicationApply,
                OperationId: rotationId, Correlation: CorrelationContext.Create(rotationId),
                ModuleApplicationApply: new(ModuleApplicationAction.RotateCredential, Instance, state!)));
            check(rotation.Success, "Helper rotates the real MySQL application credential through the frozen instance identity");
            check(modulehelper::ModuleApplicationSecret.TryLoad(AccountSecretPath, Account, out var rotated, out _)
                && rotated is not null && rotated.Revision != accountRevision && rotated.Password != OldPassword,
                "Helper atomically replaces the root-only account secret with a new revision and password");
            check((await RunSqlAsync(Socket, Account, OldPassword, "SELECT 1;")).ExitCode != 0,
                "the old application password is rejected by the real database after rotation");
            check((await RunSqlAsync(Socket, Account, rotated!.Password, "SELECT 1;")).ExitCode == 0,
                "the new Helper-generated application password authenticates against the real database");

            var replay = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleApplicationApply,
                OperationId: rotationId, Correlation: CorrelationContext.Create(rotationId),
                ModuleApplicationApply: new(ModuleApplicationAction.RotateCredential, Instance, state)));
            check(!replay.Success && replay.ProblemCode == PrivilegedProblemCode.RecoveryRequired,
                "replaying a completed rotation is refused as an unknown operation, never re-executed");

            var newest = (await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleApplicationQuery,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleApplicationQuery: new(Instance, ModuleApplicationAction.Query)))).ModuleApplication;
            check(newest is not null && newest.SecretRevision == rotated.Revision,
                "the Helper observes the rotated account secret revision");
            var stale = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleApplicationApply,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleApplicationApply: new(ModuleApplicationAction.RotateCredential, Instance, state)));
            check(!stale.Success && stale.ProblemCode == PrivilegedProblemCode.PlanChanged,
                "a stale rotation plan is refused as a changed plan before touching the database");

            await WritePolicyAsync(revoke: true);
            var revoked = await ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.ModuleApplicationQuery,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(),
                ModuleApplicationQuery: new(Instance, ModuleApplicationAction.Query)));
            check(!revoked.Success && revoked.ProblemCode == PrivilegedProblemCode.AccessDenied,
                "a revoked application instance no longer resolves through the root-owned policy");

            _ = await RunSqlAsync(Socket, AdminUser, AdminPassword,
                $"DROP USER IF EXISTS '{Account}'@'localhost';");
            _ = await RunSqlAsync(Socket, "root", "", $"DROP USER IF EXISTS '{AdminUser}'@'localhost';");
            File.Delete(PolicyPath);
            File.Delete(AdminSecretPath);
            File.Delete(AccountSecretPath);
        }
        finally
        {
            File.Delete(PolicyPath);
            File.Delete(AdminSecretPath);
            File.Delete(AccountSecretPath);
        }
    }

    private static async Task WritePolicyAsync(bool revoke = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
        var instances = revoke
            ? new object[]
            {
                new
                {
                    instance = "other", protocol = "mysql-admin", socket = Socket,
                    adminUser = AdminUser, adminSecret = AdminSecretPath,
                    account = Account, accountHost = "localhost", accountSecret = AccountSecretPath,
                    actions = new[] { "query", "rotate-credential" },
                },
            }
            : new object[]
            {
                new
                {
                    instance = Instance, protocol = "mysql-admin", socket = Socket,
                    adminUser = AdminUser, adminSecret = AdminSecretPath,
                    account = Account, accountHost = "localhost", accountSecret = AccountSecretPath,
                    actions = new[] { "query", "rotate-credential" },
                },
            };
        var document = new { schemaVersion = 1, instances };
        await File.WriteAllTextAsync(PolicyPath, JsonSerializer.Serialize(document));
        File.SetUnixFileMode(PolicyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void WriteSecret(string path, string revision, string user, string password)
    {
        Directory.CreateDirectory(SecretRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(SecretRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var document = new { schemaVersion = 1, revision, user, password };
        File.WriteAllText(path, JsonSerializer.Serialize(document));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request) =>
        await modulehelper::PrivilegedOperationExecutor.ExecuteAsync(request, HelperPolicy);

    private static async Task<(int ExitCode, string Output)> RunSqlAsync(string socket, string user, string password,
        string sql)
    {
        const string binary = "/usr/bin/mysql";
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (password.Length > 0) start.Environment["MYSQL_PWD"] = password;
        start.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        foreach (var argument in new[]
        {
            "--protocol=socket", "--socket=" + (socket.Length > 0 ? socket : Socket),
            "--user=" + user, "--batch", "--skip-column-names",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start mysql.");
        await process.StandardInput.WriteAsync(sql);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } }
        return (process.ExitCode, (await output) + (await error));
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] arguments, int seconds)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + file);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } }
        return (process.ExitCode, (await output) + (await error));
    }

    private static string Revision() => Guid.NewGuid().ToString("N");
}
