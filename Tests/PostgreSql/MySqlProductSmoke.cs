using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;

internal static class MySqlProductSmoke
{
    public static async Task RunAsync(string mode)
    {
        if (File.ReadAllText("/etc/relaxkonos-db09-product-disposable").Trim() != "DB-09-product")
            throw new InvalidOperationException("Disposable fixture required");
        var transport = new NativeProcess();
        var credentials = new RelaxKonOS.Server.Databases.PostgreSqlCredentialStore(new FixtureEnvironment(), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var service = new MySqlInstallationService(transport, credentials);
        var accountService = new MySqlAccountsInstallationService(transport, credentials);
        var coordinator = new InstallationCoordinator(new(new FixtureEnvironment()), [service, accountService], new Lifetime());
        await coordinator.StartAsync(default);
        try
        {
            async Task<InstallationOperationDto> Submit(InstallationOperationKind kind, MySqlRequest request, InstallationServiceId serviceId = InstallationServiceId.MySql)
            {
                var operation = coordinator.Start(serviceId, kind, JsonSerializer.SerializeToElement(request), "fixture", Guid.NewGuid().ToString("N"));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                while (coordinator.Get(operation.OperationId)!.Operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
                    await Task.Delay(100, deadline.Token);
                return coordinator.Get(operation.OperationId)!.Operation;
            }
            async Task Execute(MySqlAction action)
            {
                var before = await service.ReadAsync(default);
                var done = await Submit(action == MySqlAction.Install ? InstallationOperationKind.Install : InstallationOperationKind.Configure,
                    new(true, action, before.Revision));
                Check(done.State == InstallationOperationState.Succeeded, action + " commits via the production queue: " + done.ProblemCode);
            }

            var initial = await service.ReadAsync(default);
            if (mode == "--accounts")
            {
                await Execute(MySqlAction.Start);
                var instance = initial.InstanceId!.Value;
                var plan = RelaxKonOS.ManagedRuntimes.MySql.MySqlBootstrapPolicy.Plan(instance);
                async Task<InstallationOperationDto> Accounts(MySqlAction action, string db, string? user = null,
                    MySqlGrant? grant = null, string? password = null, string host = "localhost")
                {
                    Guid? reference = null;
                    if (password is not null)
                    {
                        reference = Guid.NewGuid();
                        credentials.Stage(new(reference.Value, instance, password), "fixture");
                    }
                    var before = await service.ReadAsync(default);
                    return await Submit(InstallationOperationKind.Configure,
                        new(true, action, before.Revision, Account: new(instance, db, user,
                            user is null ? null : host, grant, reference)), InstallationServiceId.MySqlAccounts);
                }

                var roleContract = new MySqlAccountCommand(instance, "app", "reader", "localhost", MySqlGrant.ReadOnly, Guid.NewGuid());
                Check(roleContract.Valid(MySqlAction.CreateAccount)
                    && !(roleContract with { Host = "%" }).Valid(MySqlAction.CreateAccount)
                    && !(roleContract with { Database = "app_other" }).Valid(MySqlAction.CreateAccount),
                    "account contract rejects wildcard hosts and schema patterns");
                var suffix = Guid.NewGuid().ToString("N")[..6];
                var db = "app" + suffix;
                var other = "other" + suffix;
                var user = "reader" + suffix;
                var old = Guid.NewGuid().ToString("N");
                var next = Guid.NewGuid().ToString("N");
                async Task Success(Task<InstallationOperationDto> task, string label)
                {
                    var result = await task;
                    Check(result.State == InstallationOperationState.Succeeded, label + ": " + result.ProblemCode);
                }
                await Success(Accounts(MySqlAction.CreateDatabase, db), "create first database via product");
                await Success(Accounts(MySqlAction.CreateDatabase, other), "create second database via product");
                var root = await Sql(plan.SocketDirectory, "root", null,
                    $"CREATE TABLE {db}.witness (id int); INSERT INTO {db}.witness VALUES (84); CREATE TABLE {other}.witness (id int); CREATE USER '{user}'@'127.0.0.1' ACCOUNT LOCK;");
                Check(root.Code == 0, "fixture creates representative tables");
                await Success(Accounts(MySqlAction.CreateAccount, db, user, MySqlGrant.ReadOnly, old), "create localhost read-only account");
                var login = await Sql(plan.SocketDirectory, user, old, $"SELECT CURRENT_USER(), id FROM {db}.witness;");
                Check(login.Code == 0 && login.Output.Trim() == user + "@localhost\t84", "login resolves exact user and host and reads assigned schema");
                Check((await Sql(plan.SocketDirectory, user, old, $"SELECT * FROM {other}.witness;")).Error.Contains("ERROR 1142"),
                    "cross-schema SELECT denied");
                Check((await Sql(plan.SocketDirectory, user, old, $"INSERT INTO {db}.witness VALUES (1);")).Error.Contains("ERROR 1142"),
                    "read-only account cannot write");
                Check((await Sql(plan.SocketDirectory, user, old, "CREATE USER 'unauthorized'@'localhost';")).Code != 0,
                    "application account cannot create users");
                await Success(Accounts(MySqlAction.SetGrant, db, user, MySqlGrant.ReadWrite), "grant read-write on same schema");
                Check((await Sql(plan.SocketDirectory, user, old, $"INSERT INTO {db}.witness VALUES (85);")).Code == 0,
                    "read-write account can insert");
                Check((await Sql(plan.SocketDirectory, user, old, $"INSERT INTO {other}.witness VALUES (1);")).Error.Contains("ERROR 1142"),
                    "read-write grant still denies cross-schema writes");
                await Success(Accounts(MySqlAction.SetGrant, db, user, MySqlGrant.ReadOnly), "revoke write by switching back to read-only");
                Check((await Sql(plan.SocketDirectory, user, old, $"DELETE FROM {db}.witness;")).Error.Contains("ERROR 1142"),
                    "downgraded grant revokes write");
                using var existing = StartSql(plan.SocketDirectory, user, old);
                await existing.StandardInput.WriteLineAsync("SELECT CURRENT_USER();");
                await existing.StandardInput.FlushAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                Check(await existing.StandardOutput.ReadLineAsync(deadline.Token) == user + "@localhost", "old password session established");
                await Success(Accounts(MySqlAction.ChangePassword, db, user, password: next), "rotate existing account credential");
                Check((await Sql(plan.SocketDirectory, user, next, "SELECT CURRENT_USER();")).Code == 0, "new password authenticates on new connection");
                Check((await Sql(plan.SocketDirectory, user, old, "SELECT CURRENT_USER();")).Error.Contains("ERROR 1045"),
                    "old password rejected on new connection");
                await existing.StandardInput.WriteLineAsync($"SELECT MIN(id) FROM {db}.witness;");
                await existing.StandardInput.FlushAsync();
                Check(await existing.StandardOutput.ReadLineAsync(deadline.Token) == "84", "existing authenticated session survives password rotation");
                existing.StandardInput.Close();
                await existing.WaitForExitAsync(deadline.Token);
                var untouched = await Sql(plan.SocketDirectory, "root", null,
                    $"SELECT account_locked, authentication_string='' FROM mysql.user WHERE user='{user}' AND host='127.0.0.1';");
                Check(untouched.Code == 0 && untouched.Output.Trim() == "Y\t1",
                    "same user at a different host remains locked and unchanged");
                var final = await service.ReadAsync(default);
                Check(final.Accounts!.Single(x => x.User == user && x.Host == "localhost").CredentialRevision == 2,
                    "credential revision advances after login verification");
                await Execute(MySqlAction.Stop);
                return;
            }
            if (mode == "--after-restart")
            {
                Check(initial.InstanceId is not null && initial.Installed && !initial.Running, "Helper reads durable state after systemd restart");
                if (initial.PendingOperationId is { } pending)
                {
                    var recovery = await Submit(InstallationOperationKind.Repair, new(true, RecoverOperationId: pending));
                    var resumed = await service.ReadAsync(default, pending);
                    Check(recovery.State == InstallationOperationState.Succeeded && resumed.CommittedOperationId == pending,
                        "original interrupted Start resumes through the production queue after reboot");
                }
                else await Execute(MySqlAction.Start);
                Check((await service.ReadAsync(default)).Verified, "authenticated socket verified after systemd restart");
                await Execute(MySqlAction.Stop);
                return;
            }

            Check(!initial.Installed && initial.PendingOperationId is null, "fresh supported host");
            transport.DropInstallReceiptRead = true;
            var interrupted = await Submit(InstallationOperationKind.Install, new(true, MySqlAction.Install, initial.Revision));
            Check(interrupted.State == InstallationOperationState.Interrupted, "lost post-install response preserves Core interruption: " + interrupted.ProblemCode);
            var committed = await service.ReadAsync(default, interrupted.OperationId);
            Check(committed.Installed && committed.CommittedOperationId == interrupted.OperationId, "fresh Helper process returns original install receipt");
            var repair = await Submit(InstallationOperationKind.Repair, new(true, RecoverOperationId: interrupted.OperationId));
            Check(repair.State == InstallationOperationState.Succeeded && transport.InstallDispatches == 1,
                "user recovery reconciles install without repeating package dispatch");
            await Execute(MySqlAction.Initialize);
            Check((await service.ReadAsync(default)) is { InstanceId: not null, Running: true, Verified: true }, "initialized instance authenticates");
            Check(!Directory.Exists("/var/lib/mysql"), "no distribution default cluster");
            await Execute(MySqlAction.Verify);
            await Execute(MySqlAction.Stop);
            Check((await service.ReadAsync(default)) is { Running: false, Verified: false }, "stopped state is observed");
            await Execute(MySqlAction.Start);
            Check((await service.ReadAsync(default)).Verified, "start verifies local authentication");
            await Execute(MySqlAction.Stop);
        }
        finally { await coordinator.StopAsync(default); }
    }

    private static Process StartSql(string socket, string user, string? password)
    {
        var info = new ProcessStartInfo("/usr/bin/mysql")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "--no-defaults", "--no-login-paths", "--protocol=SOCKET", "--socket=" + socket + "/mysql.sock",
            "--user=" + user, "--batch", "--skip-column-names", "--unbuffered", "--connect-timeout=3" })
            info.ArgumentList.Add(arg);
        info.Environment.Remove("MYSQL_PWD");
        if (password is not null) info.Environment["MYSQL_PWD"] = password;
        return Process.Start(info)!;
    }

    private static async Task<(int Code, string Output, string Error)> Sql(string socket, string user, string? password, string sql)
    {
        using var client = StartSql(socket, user, password);
        var stdout = client.StandardOutput.ReadToEndAsync();
        var stderr = client.StandardError.ReadToEndAsync();
        await client.StandardInput.WriteLineAsync(sql);
        client.StandardInput.Close();
        await client.WaitForExitAsync();
        return (client.ExitCode, await stdout, await stderr);
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class NativeProcess : IPrivilegedOperationTransport
    {
        public bool DropInstallReceiptRead { get; set; }
        public int InstallDispatches { get; private set; }
        private bool dropNextQuery;

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (dropNextQuery && request.Operation == PrivilegedOperationKind.MySqlQuery)
            {
                dropNextQuery = false;
                return new(false, Error: "fixture.response_lost");
            }
            if (request.MySql?.Action == MySqlAction.Install) InstallDispatches++;
            var id = request.OperationId ?? Guid.NewGuid();
            request = request with { OperationId = id, Correlation = CorrelationContext.Create(id, "privileged.operation") };
            using var process = new Process
            {
                StartInfo = new(Environment.ProcessPath!, "helper-stdin")
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            await stderr;
            var reply = JsonSerializer.Deserialize<PrivilegedOperationResult>(await stdout)!;
            if (DropInstallReceiptRead && request.MySql?.Action == MySqlAction.Install && reply.Success)
            {
                dropNextQuery = true;
                DropInstallReceiptRead = false;
                return new(false, Error: "fixture.response_lost");
            }
            if (!reply.Success) Console.WriteLine("Helper: " + reply.Error);
            return reply;
        }
    }

    private sealed class FixtureEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "DB09";
        public string ContentRootPath { get; set; } = "/var/lib/relaxkonos/db09-product-acceptance";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}
