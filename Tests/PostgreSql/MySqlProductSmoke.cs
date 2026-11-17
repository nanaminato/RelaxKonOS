using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
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
        var environment = new FixtureEnvironment();
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(environment.ContentRootPath, "test-keyring")));
        var credentials = new RelaxKonOS.Server.Databases.PostgreSqlCredentialStore(environment, protection);
        var service = new MySqlInstallationService(transport, credentials);
        var accountService = new MySqlAccountsInstallationService(transport, credentials);
        var adminService = new MySqlAdminRecoveryInstallationService(transport, credentials);
        var maintenanceService = new MySqlMaintenanceInstallationService(transport, credentials);
        var config = new Microsoft.Extensions.Configuration.ConfigurationManager();
        config["Storage:Provider"] = "fixture";
        var certificates = new RelaxKonOS.Server.Certificate.FileCertificateStore(environment,
            new RelaxKonOS.Server.Certificate.CertificateOptions(),
            new RelaxKonOS.Server.Certificate.CertificateMetadataRepository(environment, config));
        var remoteService = new MySqlRemoteInstallationService(transport, credentials, certificates);
        var coordinator = new InstallationCoordinator(new(new FixtureEnvironment()), [service, accountService, adminService, remoteService, maintenanceService], new Lifetime());
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
            if (mode == "--maintenance")
            {
                Check(!initial.Installed, "disposable MySQL maintenance starts without installed packages");
                var install = await Submit(InstallationOperationKind.Install,
                    new(true, MySqlAction.Install, initial.Revision, InstallVersion: "8.4.11"));
                Check(install.State == InstallationOperationState.Succeeded, "signed baseline installed via queue: " + install.ProblemCode);
                await Execute(MySqlAction.Initialize);
                var state = await service.ReadAsync(default);
                var instance = state.InstanceId!.Value;
                var plan = RelaxKonOS.ManagedRuntimes.MySql.MySqlBootstrapPolicy.Plan(instance);
                var database = "maintenancefixture";
                var reference = Guid.NewGuid();
                var password = Guid.NewGuid().ToString("N");
                async Task Account(MySqlAction action, MySqlAccountCommand target)
                {
                    var current = await service.ReadAsync(default);
                    var result = await Submit(InstallationOperationKind.Configure, new(true, action, current.Revision, Account: target), InstallationServiceId.MySqlAccounts);
                    Check(result.State == InstallationOperationState.Succeeded, "managed witness setup " + action + ": " + result.ProblemCode);
                }
                credentials.Stage(new(reference, instance, password), "fixture");
                await Account(MySqlAction.CreateDatabase, new(instance, database));
                await Account(MySqlAction.CreateAccount, new(instance, database, "reader", "localhost", MySqlGrant.ReadOnly, reference));
                Check((await Sql(plan.SocketDirectory, "root", null, $"CREATE TABLE {database}.witness(id INT) ENGINE=InnoDB; INSERT INTO {database}.witness VALUES (84);")).Code == 0,
                    "temporary maintenance witness created");
                Check(state.Verified && state.Version == "8.4.11", "maintenance starts on the qualified baseline");
                async Task<MySqlState> Maintain(MySqlMaintenanceAction action, string version, Guid? backup = null,
                    InstallationOperationState expected = InstallationOperationState.Succeeded)
                {
                    var current = await service.ReadAsync(default);
                    var change = new MySqlMaintenanceCommand(instance, action, version, backup,
                        action == MySqlMaintenanceAction.UninstallDeleteData ? instance.ToString("N") : null);
                    var result = await Submit(InstallationOperationKind.Configure, new(true, MySqlAction.Maintain, current.Revision, Maintenance: change), InstallationServiceId.MySqlMaintenance);
                    Check(result.State == expected, "maintenance " + action + " via queue: " + result.ProblemCode);
                    var verified = await service.ReadAsync(default, result.OperationId);
                    if (expected == InstallationOperationState.Succeeded)
                        Check(verified.CommittedOperationId == result.OperationId && verified.PendingOperationId is null, "maintenance receipt " + action);
                    return verified;
                }

                Check(state.Backups is null, "backup validation starts without prior snapshots");
                var external = "/var/lib/mysql";
                Check(!Directory.Exists(external), "fixture default data path initially absent");
                Directory.CreateDirectory(external);
                try
                {
                    var refused = await Maintain(MySqlMaintenanceAction.Update, "8.4.13", expected: InstallationOperationState.Failed);
                    Check(refused.Version == "8.4.11" && refused.Running && refused.PendingOperationId is null, "external data prevents dispatch without touching live instance");
                }
                finally { Directory.Delete(external); }
                transport.InterruptColdBackup = true;
                var interruptedBackup = await Maintain(MySqlMaintenanceAction.Backup, "8.4.11", expected: InstallationOperationState.Interrupted);
                var originalBackup = interruptedBackup.PendingOperationId!.Value;
                Check(transport.BackupHelperKilled && !interruptedBackup.Running, "Helper interruption leaves the original stop and backup intent fenced");
                var resumed = await Submit(InstallationOperationKind.Repair, new(true, RecoverOperationId: originalBackup), InstallationServiceId.MySqlMaintenance);
                Check(resumed.State == InstallationOperationState.Succeeded, "fresh Helper resumes original cold backup: " + resumed.ProblemCode);
                var saved = await service.ReadAsync(default, originalBackup);
                Check(saved.Verified && saved.Backups is { Count: 1 }, "standalone verified backup listed");
                Check((await Sql(plan.SocketDirectory, "root", null, $"UPDATE {database}.witness SET id=99;")).Code == 0, "live witness differs from backup");
                await Maintain(MySqlMaintenanceAction.Restore, "8.4.11", saved.Backups![0].Id);
                transport.FailCandidateProbe = true;
                var compensated = await Maintain(MySqlMaintenanceAction.Update, "8.4.13", expected: InstallationOperationState.Failed);
                Check(transport.CandidateDamaged && compensated.MaintenanceCompensated && compensated.Verified && compensated.Version == "8.4.11",
                    "candidate failure rolls back before opening live data and restores the original service");
                var upgraded = await Maintain(MySqlMaintenanceAction.Update, "8.4.13");
                Check(upgraded.Version == "8.4.13" && upgraded.Verified, "native version verified after package change");
                var loginAfter = await Sql(plan.SocketDirectory, "reader", password, $"SELECT id FROM {database}.witness;");
                Check(loginAfter.Code == 0 && loginAfter.Output.Trim() == "84", "restore and update preserve original rows and credential");
                await Maintain(MySqlMaintenanceAction.Backup, "8.4.13");
                await Execute(MySqlAction.Stop);
                await File.WriteAllTextAsync("/usr/sbin/mysqld", "disposable modified executable");
                var repaired = await Maintain(MySqlMaintenanceAction.Repair, "8.4.13");
                Check(repaired.Verified, "repair restores native executable and Unix socket");
                var retained = await Maintain(MySqlMaintenanceAction.UninstallKeepData, "8.4.13");
                Check(!retained.Installed && retained.InstanceId == instance && Directory.Exists(plan.DataDirectory)
                    && credentials.Read(reference, instance) == password, "keep data retains the exact instance and encrypted credential");
                await Maintain(MySqlMaintenanceAction.Reinstall, "8.4.13");
                var afterReinstall = await Sql(plan.SocketDirectory, "reader", password, $"SELECT id FROM {database}.witness;");
                Check(afterReinstall.Code == 0 && afterReinstall.Output.Trim() == "84", "kept instance reopens with original data and login");
                var deleted = await Maintain(MySqlMaintenanceAction.UninstallDeleteData, "8.4.13");
                Check(!deleted.Installed && deleted.InstanceId is null && !Directory.Exists(plan.DataDirectory)
                    && !Directory.Exists("/var/lib/relaxkonos/mysql-control/backups/" + instance.ToString("N"))
                    && !Directory.EnumerateFiles(Path.Combine(environment.ContentRootPath, "data", "database-secrets")).Any(),
                    "delete data removes owned instance, backups and credential reference");
                return;
            }
            if (mode is "--remote-open" or "--remote-close" or "--remote-conflict" or "--remote-resume")
            {
                var instance = initial.InstanceId!.Value;
                MySqlRemotePolicy? policy = null;
                if (mode is "--remote-open" or "--remote-conflict")
                {
                    var input = JsonSerializer.Deserialize<RemoteTestInput>(await Console.In.ReadToEndAsync())!;
                    policy = input.Policy;
                    await certificates.SaveAsync(new(policy.CertificateId, [policy.ServerName],
                        RelaxKonOS.Protocol.Certificates.CertificateChallengeType.DirectHttp01,
                        RelaxKonOS.Protocol.Certificates.CertificateKeyAlgorithm.Rsa2048,
                        null, input.CertificatePem, input.PrivateKeyPem, DateTimeOffset.UtcNow), default);
                    var owned = initial.Accounts!.Single(x => x.User == policy.User && x.Host == "localhost");
                    credentials.Stage(new(owned.CredentialReference, instance, input.Password), "fixture");
                }
                if (mode == "--remote-resume") policy = initial.PendingRemote?.Policy;
                var resume = mode == "--remote-resume" ? initial.PendingOperationId : null;
                if (mode == "--remote-resume") Check(resume is not null, "original remote transaction survived Helper exit");
                var remote = await Submit(resume is null ? InstallationOperationKind.Configure : InstallationOperationKind.Repair,
                    resume is null ? new(true, MySqlAction.ConfigureRemote, initial.Revision, Remote: new(instance, policy))
                        : new(true, RecoverOperationId: resume),
                    InstallationServiceId.MySqlRemoteAccess);
                var observed = await service.ReadAsync(default, resume ?? remote.OperationId);
                if (mode == "--remote-conflict")
                {
                    Check(remote.State == InstallationOperationState.Interrupted && observed.PendingOperationId == remote.OperationId,
                        "listen conflict leaves the original remote transaction pending: " + remote.ProblemCode);
                    return;
                }
                Check(remote.State == InstallationOperationState.Succeeded,
                    "certificate center and production queue commit remote operation: " + remote.ProblemCode);
                Check(observed.Verified && observed.CommittedOperationId == (resume ?? remote.OperationId)
                    && (observed.RemoteAccess is null) == (policy is null), "remote receipt and observed policy match");
                return;
            }
            if (mode is "--admin-loss" or "--admin-recover" or "--admin-after-restart")
            {
                var instance = initial.InstanceId!.Value;
                var plan = RelaxKonOS.ManagedRuntimes.MySql.MySqlBootstrapPolicy.Plan(instance);
                if (mode == "--admin-loss")
                {
                    if (!initial.Running) await Execute(MySqlAction.Start);
                    var setup = await Sql(plan.SocketDirectory, "root", null,
                        "CREATE DATABASE IF NOT EXISTS adminfixture; CREATE TABLE IF NOT EXISTS adminfixture.witness(id int PRIMARY KEY); INSERT IGNORE INTO adminfixture.witness VALUES (84); CREATE USER IF NOT EXISTS 'root'@'127.0.0.1' ACCOUNT LOCK; ALTER USER 'root'@'localhost' IDENTIFIED WITH caching_sha2_password BY '" + Guid.NewGuid().ToString("N") + "' ACCOUNT LOCK;");
                    Check(setup.Code == 0, "fixture loses only the local administrator identity");
                    Check((await service.ReadAsync(default)) is { Running: true, Verified: false },
                        "ordinary health no longer authenticates the locked administrator");
                    return;
                }
                var recovery = initial.PendingOperationId is { } pending
                    ? new MySqlRequest(true, RecoverOperationId: pending)
                    : new MySqlRequest(true, MySqlAction.RecoverAdministrator, initial.Revision, AdministratorInstanceId: instance);
                if (mode == "--admin-after-restart")
                {
                    Check(initial.PendingAdministratorRecovery && initial.PendingOperationId is not null && !initial.Running,
                        "original administrator recovery survives systemd restart");
                    using var start = Process.Start(new ProcessStartInfo("/usr/bin/systemctl", "start " + plan.ServiceName)
                        { UseShellExecute = false, RedirectStandardError = true })!;
                    await start.WaitForExitAsync();
                    using var observe = Process.Start(new ProcessStartInfo("/usr/bin/systemctl", "is-active " + plan.ServiceName)
                        { UseShellExecute = false, RedirectStandardOutput = true })!;
                    await observe.WaitForExitAsync();
                    Check(observe.ExitCode != 0, "persistent guard blocks ordinary service after restart");
                    var foreign = "/run/relaxkonos-mysql-recovery-" + initial.PendingOperationId!.Value.ToString("N") + ".sql";
                    File.WriteAllText(foreign, "disposable foreign content");
                    try
                    {
                        var failed = await Submit(InstallationOperationKind.Repair, recovery, InstallationServiceId.MySqlAdminRecovery);
                        var isolated = await service.ReadAsync(default);
                        Check(failed.State == InstallationOperationState.Interrupted && isolated.PendingOperationId == initial.PendingOperationId
                            && File.ReadAllText(foreign) == "disposable foreign content"
                            && File.Exists("/etc/systemd/system/" + plan.ServiceName + ".d/90-recovery.conf"),
                            "foreign temporary SQL causes safe refusal and leaves the original isolation in place");
                    }
                    finally { File.Delete(foreign); }
                }
                if (recovery.RecoverOperationId is null)
                {
                    var denied = false;
                    try { await Submit(InstallationOperationKind.Configure, recovery); }
                    catch (InstallationException) { denied = true; }
                    Check(denied, "ordinary lifecycle authority cannot dispatch administrator recovery");
                }
                var operation = await Submit(recovery.RecoverOperationId is null ? InstallationOperationKind.Configure : InstallationOperationKind.Repair,
                    recovery, InstallationServiceId.MySqlAdminRecovery);
                Check(operation.State == InstallationOperationState.Succeeded, "administrator recovery commits: " + operation.ProblemCode);
                var after = await service.ReadAsync(default, recovery.RecoverOperationId ?? operation.OperationId);
                Check(!after.Running && after.CommittedOperationId != null && after.PendingOperationId is null,
                    "recovery commits original receipt and leaves ordinary service stopped");
                Check(!Directory.Exists("/etc/systemd/system/" + plan.ServiceName + ".d")
                    && !Directory.EnumerateFileSystemEntries("/run", "relaxkonos-mysql-recovery-*").Any()
                    && !Directory.EnumerateFileSystemEntries("/run/systemd/system", "relaxkonos-mysql-recovery-*").Any(),
                    "temporary startup SQL, private unit, runtime socket and guard removed");
                await Execute(MySqlAction.Start);
                var identity = await Sql(plan.SocketDirectory, "root", null,
                    "SELECT CURRENT_USER(), (SELECT account_locked FROM mysql.user WHERE user='root' AND host='127.0.0.1'), (SELECT id FROM adminfixture.witness);");
                Check(identity.Code == 0 && identity.Output.Trim() == "root@localhost\tY\t84",
                    "normal socket authenticates local root, preserves other host and data");
                await Execute(MySqlAction.Stop);
                return;
            }
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

    private sealed record RemoteTestInput(MySqlRemotePolicy Policy, string CertificatePem, string PrivateKeyPem, string Password);

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
        public bool InterruptColdBackup { get; set; }
        public bool BackupHelperKilled { get; private set; }
        public bool FailCandidateProbe { get; set; }
        public bool CandidateDamaged { get; private set; }
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
            var interrupt = InterruptColdBackup && request.MySql?.Maintenance?.Action == MySqlMaintenanceAction.Backup;
            var failCandidate = FailCandidateProbe && request.MySql?.Maintenance?.Action == MySqlMaintenanceAction.Update;
            if (interrupt || failCandidate)
            {
                if (interrupt) InterruptColdBackup = false;
                if (failCandidate) FailCandidateProbe = false;
                const string ledger = "/var/lib/relaxkonos/mysql-control/state.json";
                var checkpoint = interrupt ? "StoppedForMaintenance" : "PackagesApplied";
                while (!process.HasExited)
                {
                    if ((await File.ReadAllTextAsync(ledger, ct)).Contains(checkpoint, StringComparison.Ordinal))
                    {
                        if (failCandidate)
                        {
                            await File.WriteAllTextAsync("/usr/sbin/mysqld", "disposable failed candidate fixture", ct);
                            CandidateDamaged = true;
                            break;
                        }
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(ct);
                        await Task.WhenAll(stdout, stderr);
                        BackupHelperKilled = true;
                        return new(false, Error: "fixture.helper_killed");
                    }
                    await Task.Delay(10, ct);
                }
            }
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
