using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.MySql;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    // In-place dictionary upgrades can outlast the ordinary socket start window.
    private const int MyMaintenanceStartupAttempts = 600;
    private sealed record MyBackupRecord(MySqlBackup Backup, string ManifestHash);
    private sealed record MyBackupManifest(Guid Instance, string Version, string ConfigurationHash, string CatalogHash,
        Dictionary<string, string> Files);

    private static string MyBackupRoot(Guid instance) => $"{MyRoot}/backups/{instance:N}";
    private static string MyBackupPath(Guid instance, Guid backup) => $"{MyBackupRoot(instance)}/{backup:N}";
    private static string MyCatalogHash(MyLedger ledger) => PgHash(JsonSerializer.SerializeToUtf8Bytes(new { ledger.Databases, ledger.Accounts }));

    private static async Task MyMaintenancePreflight(MyLedger ledger, MySqlMaintenanceCommand command)
    {
        var reinstallation = command.Action == MySqlMaintenanceAction.Reinstall;
        if ((reinstallation ? !ledger.Retained || ledger.Installed : !ledger.Installed) || ledger.Instance != command.InstanceId || ledger.RemoteAccess is not null
            || (command.Action == MySqlMaintenanceAction.Update
                ? !MySqlMaintenancePolicy.CanUpdate(ledger.Version, command.Version)
                : command.Version != ledger.Version))
            throw new PgFailure("database.maintenance_invalid");
        var plan = MySqlBootstrapPolicy.Plan(command.InstanceId);
        await MyVerifyAdminData(plan);
        await MyMaintenanceInventory(plan);
        if (reinstallation)
        {
            await MyAudit();
            foreach (var path in new[] { MyUnit(plan), MySqlBootstrapPolicy.Server, "/usr/bin/mysql" })
            {
                PgNoLinks(path);
                if (File.Exists(path)) throw new PgFailure("database.external_installation");
            }
            MyReadBackup(ledger, plan, ledger.Backups?.LastOrDefault()?.Backup.Id ?? Guid.Empty);
            return;
        }
        await MyVerifyUnit(plan);
        await MyVerifyPackages(ledger.Version, command.Action != MySqlMaintenanceAction.Repair);
        if (command.Action == MySqlMaintenanceAction.Restore)
            MyReadBackup(ledger, plan, command.BackupId!.Value);
        if (command.Action is MySqlMaintenanceAction.UninstallKeepData or MySqlMaintenanceAction.UninstallDeleteData)
        {
            await PgRun("/usr/bin/dpkg", ["--no-act", "--remove", .. MySqlBootstrapPolicy.Artifacts.Select(x => x.Name)]);
            var backupRoot = MyBackupRoot(plan.Id);
            PgNoLinks(backupRoot);
            var known = (ledger.Backups ?? []).Select(x => MyBackupPath(plan.Id, x.Backup.Id)).ToHashSet(StringComparer.Ordinal);
            if (Directory.Exists(backupRoot) && Directory.EnumerateFileSystemEntries(backupRoot).Any(path => !known.Contains(path)))
                throw new PgFailure("database.external_installation");
        }
        if (await MyRunning(plan))
        {
            if (command.Action == MySqlMaintenanceAction.Repair) throw new PgFailure("database.maintenance_invalid");
            await MyVerifyConnection(plan, ledger.Version);
            await MyMaintenanceCatalog(ledger, plan);
        }
        else if (command.Action != MySqlMaintenanceAction.Repair)
            throw new PgFailure("database.connection_unverified");
        else
            MyReadBackup(ledger, plan, ledger.Backups?.LastOrDefault()?.Backup.Id ?? Guid.Empty);
    }

    private static async Task MyMaintenanceInventory(MySqlBootstrapInstance plan, string? privateUnit = null)
    {
        foreach (var path in new[] { "/var/lib/mysql", "/etc/mysql", "/etc/systemd/system/mysql.service", "/usr/lib/systemd/system/mysql.service",
            "/etc/systemd/system/mariadb.service", "/usr/lib/systemd/system/mariadb.service" })
        {
            PgNoLinks(path);
            if (File.Exists(path) || Directory.Exists(path)) throw new PgFailure("database.external_installation");
        }
        var mysqlRoot = Path.GetDirectoryName(Path.GetDirectoryName(plan.DataDirectory))!;
        PgNoLinks(mysqlRoot);
        if (Directory.Exists(mysqlRoot) && Directory.EnumerateFileSystemEntries(mysqlRoot).Any(path => path != Path.GetDirectoryName(plan.DataDirectory)))
            throw new PgFailure("database.external_installation");
        var allowedPackages = MySqlBootstrapPolicy.Artifacts.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var inventory = await PgRun("/usr/bin/dpkg-query", ["-W", "-f=${binary:Package}|${db:Status-Status}\\n", "mysql*", "mariadb*", "percona*"], allowFailure: true);
        if (inventory.Code is not (0 or 1) || inventory.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
            !allowedPackages.Contains(line.Split('|')[0]) && !line.EndsWith("|not-installed", StringComparison.Ordinal)))
            throw new PgFailure("database.external_installation");
        var allowedPids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in new[] { plan.ServiceName, privateUnit }.OfType<string>())
            allowedPids.Add((await PgRun("/usr/bin/systemctl", ["show", "-p", "MainPID", "--value", unit])).Output.Trim());
        foreach (var folder in Directory.EnumerateDirectories("/proc").Where(path => Path.GetFileName(path).All(char.IsAsciiDigit)))
        {
            string name;
            try { name = File.ReadAllText(folder + "/comm").Trim(); }
            catch (IOException) when (!Directory.Exists(folder)) { continue; }
            if (name is "mysqld" or "mysqld_safe" or "mariadbd" && !allowedPids.Contains(Path.GetFileName(folder)))
                throw new PgFailure("database.external_installation");
        }
    }

    private static async Task MyMaintenanceCatalog(MyLedger ledger, MySqlBootstrapInstance plan)
    {
        var schemas = new[] { "information_schema", "mysql", "performance_schema", "sys" }.Concat(ledger.Databases ?? []).Order(StringComparer.Ordinal);
        if (!(await MySqlQuery(plan, "SELECT schema_name FROM information_schema.schemata;")).Split('\n').Order(StringComparer.Ordinal).SequenceEqual(schemas)
            || await MySqlQuery(plan, "SELECT (SELECT COUNT(*) FROM performance_schema.replication_connection_configuration) + (SELECT COUNT(*) FROM mysql.component);") != "0")
            throw new PgFailure("database.backup_scope_unsupported");
        var tablespaces = await MySqlQuery(plan, "SELECT HEX(PATH) FROM information_schema.innodb_datafiles;");
        if (tablespaces.Length == 0 || tablespaces.Split('\n').Any(value =>
        {
            var path = Encoding.UTF8.GetString(Convert.FromHexString(value));
            return Path.IsPathRooted(path) || !Path.GetFullPath(path, plan.DataDirectory).StartsWith(plan.DataDirectory + "/", StringComparison.Ordinal);
        })) throw new PgFailure("database.backup_scope_unsupported");
        var identities = new[] { "mysql.infoschema@localhost", "mysql.session@localhost", "mysql.sys@localhost", "root@localhost" }
            .Concat((ledger.Accounts ?? []).Select(account => account.Identity)).Order(StringComparer.Ordinal);
        if (!(await MySqlQuery(plan, "SELECT CONCAT(user,'@',host) FROM mysql.user;")).Split('\n').Order(StringComparer.Ordinal).SequenceEqual(identities))
            throw new PgFailure("database.account_drift");
        foreach (var account in ledger.Accounts ?? [])
        {
            var identity = new MySqlAccountCommand(plan.Id, account.Database, account.User, account.Host);
            await MyVerifyAccountOwner(plan, identity, account.OwnershipId);
            await MyVerifyGrant(plan, identity, account.Grant);
        }
        foreach (var schema in ledger.Databases ?? [])
        {
            var names = await MySqlQuery(plan, $"SELECT HEX(TABLE_NAME) FROM information_schema.tables WHERE TABLE_SCHEMA='{schema}' AND TABLE_TYPE='BASE TABLE';");
            if (names.Length == 0) continue;
            var tables = names.Split('\n').Select(name => Encoding.UTF8.GetString(Convert.FromHexString(name))).ToArray();
            if (tables.Any(name => name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))))
                throw new PgFailure("database.backup_scope_unsupported");
            var commands = string.Join('\n', tables.Select(table => $"CHECK TABLE `{schema}`.`{table}`;"));
            var results = (await MySqlQuery(plan, commands)).Split('\n');
            if (results.Length == 0 || results.Any(line => !line.EndsWith("\tcheck\tstatus\tOK", StringComparison.Ordinal)))
                throw new PgFailure("database.backup_unverified");
        }
    }

    private static async Task MyCheckDataTree(string data)
    {
        PgNoLinks(data);
        if ((await PgRun("/usr/bin/find", [data, "-xdev", "(", "!", "-type", "d", "!", "-type", "f", "-o", "-type", "f", "-links", "+1", ")", "-print", "-quit"])).Output.Length != 0
            || File.Exists(data + "/mysqld-auto.cnf"))
            throw new PgFailure("database.backup_scope_unsupported");
    }

    private static string MyReadBackup(MyLedger ledger, MySqlBootstrapInstance plan, Guid backup)
    {
        var record = ledger.Backups?.SingleOrDefault(x => x.Backup.Id == backup && x.Backup.Version == ledger.Version)
            ?? throw new PgFailure("database.backup_unverified");
        var root = MyBackupPath(plan.Id, backup);
        var manifestPath = root + "/manifest.json";
        if (PgFileHash(manifestPath) != record.ManifestHash) throw new PgFailure("database.backup_changed");
        var manifest = JsonSerializer.Deserialize<MyBackupManifest>(File.ReadAllBytes(manifestPath), PgJson)
            ?? throw new PgFailure("database.backup_changed");
        if (manifest.Instance != plan.Id || manifest.Version != ledger.Version || manifest.CatalogHash != MyCatalogHash(ledger)
            || manifest.ConfigurationHash != PgFileHash(root + "/instance.service")
            || manifest.ConfigurationHash != PgHash(Encoding.UTF8.GetBytes(plan.UnitText)))
            throw new PgFailure("database.backup_identity_changed");
        var files = PgTreeFiles(root + "/data");
        if (files.Count != manifest.Files.Count || files.Any(x => !manifest.Files.TryGetValue(x.Key, out var hash) || x.Value != hash))
            throw new PgFailure("database.backup_changed");
        return root + "/data";
    }

    private static MySqlBootstrapInstance MyMaintenanceCopy(MySqlBootstrapInstance plan, Guid operation)
    {
        var copy = MyRecoveryPlan(plan, operation);
        return copy with { DataDirectory = $"{Path.GetDirectoryName(plan.DataDirectory)}/verify-{operation:N}" };
    }

    private static async Task MyVerifyMaintenanceCopy(MyLedger ledger, MySqlBootstrapInstance plan, string source, string version)
    {
        var copy = MyMaintenanceCopy(plan, ledger.Pending!.Id);
        var unit = MyPrivateUnit(copy);
        var text = MyRecoveryUnitText(plan, copy, null).Replace(plan.DataDirectory, copy.DataDirectory, StringComparison.Ordinal);
        await MyCleanMaintenanceCopy(copy, text);
        await PgRun("/usr/bin/cp", ["-a", "--reflink=auto", source, copy.DataDirectory], minutes: 10);
        try
        {
            PgAtomic(unit, Encoding.UTF8.GetBytes(text));
            await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
            await MyVerifyUnit(copy, unitPath: unit, unitText: text);
            await PgRun("/usr/bin/systemctl", ["start", copy.ServiceName]);
            await MyWaitSocket(copy, MyMaintenanceStartupAttempts);
            await MyVerifyProcess(copy);
            var identity = await MySqlQuery(copy, "SELECT CURRENT_USER(), VERSION(), @@datadir, @@skip_networking, plugin FROM mysql.user WHERE user='root' AND host='localhost';");
            if (identity != $"root@localhost\t{version}\t{copy.DataDirectory}/\t1\tauth_socket")
                throw new PgFailure("database.backup_unverified");
            await MyMaintenanceCatalog(ledger, copy);
        }
        finally { await MyCleanMaintenanceCopy(copy, text); }
    }

    private static async Task MyCleanMaintenanceCopy(MySqlBootstrapInstance copy, string text)
    {
        var unit = MyPrivateUnit(copy);
        if (MyOwnedText(unit, text))
        {
            await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
            await MyVerifyUnit(copy, unitPath: unit, unitText: text);
            await MyStopOwnedUnit(copy.ServiceName);
            File.Delete(unit);
            await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
        }
        if (Directory.Exists(copy.DataDirectory))
        {
            await MyCheckDataTree(copy.DataDirectory);
            PgDeleteOwnedTree(copy.DataDirectory, Path.GetDirectoryName(copy.DataDirectory)!);
        }
    }

    private static async Task<MyLedger> MyCreateMaintenanceBackup(MyLedger ledger, MySqlBootstrapInstance plan)
    {
        var operation = ledger.Pending!.Id;
        var root = MyBackupPath(plan.Id, operation);
        if (Directory.Exists(root)) PgDeleteOwnedTree(root, MyBackupRoot(plan.Id));
        await MyCheckDataTree(plan.DataDirectory);
        var files = PgTreeFiles(plan.DataDirectory);
        var bytes = files.Keys.Sum(relative => new FileInfo(Path.Combine(plan.DataDirectory, relative)).Length);
        var backupDevice = new DriveInfo(MyRoot);
        var dataDevice = new DriveInfo(plan.DataDirectory);
        if (Math.Min(backupDevice.AvailableFreeSpace, dataDevice.AvailableFreeSpace) < checked(bytes * 3 + 536870912))
            throw new PgFailure("database.backup_space_insufficient");
        Directory.CreateDirectory(root);
        await PgRun("/usr/bin/cp", ["-a", "--reflink=auto", plan.DataDirectory, root + "/data"], minutes: 10);
        var copied = PgTreeFiles(root + "/data");
        if (copied.Count != files.Count || copied.Any(x => !files.TryGetValue(x.Key, out var hash) || hash != x.Value))
            throw new PgFailure("database.backup_changed");
        var configuration = Encoding.UTF8.GetBytes(plan.UnitText);
        PgAtomic(root + "/instance.service", configuration);
        var manifest = new MyBackupManifest(plan.Id, ledger.Version, PgHash(configuration), MyCatalogHash(ledger), copied);
        var manifestPath = root + "/manifest.json";
        PgAtomic(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, PgJson));
        await PgRun("/usr/bin/sync", ["-f", root]);
        await MyVerifyMaintenanceCopy(ledger, plan, root + "/data", ledger.Version);
        var backup = new MyBackupRecord(new(operation, ledger.Version, DateTimeOffset.UtcNow), PgFileHash(manifestPath));
        return MySave(ledger with { Backups = [.. (ledger.Backups ?? []), backup] });
    }

    private static async Task MyStartMaintainedUnit(MySqlBootstrapInstance plan, string version)
    {
        var guard = MyAdminGuard(plan);
        if (MyOwnedText(guard, MyAdminGuardText(plan)))
        {
            File.Delete(guard);
            var directory = Path.GetDirectoryName(guard)!;
            LinuxDirectorySync.Flush(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                LinuxDirectorySync.Flush(Path.GetDirectoryName(directory)!);
            }
        }
        await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
        await MyVerifyUnit(plan);
        await PgRun("/usr/bin/systemctl", ["start", plan.ServiceName]);
        await MyWaitSocket(plan, MyMaintenanceStartupAttempts);
        await MyVerifyConnection(plan, version);
    }

    private static async Task<MyLedger> MyContinueMaintenance(MyLedger ledger)
    {
        var pending = ledger.Pending!;
        var command = pending.Request.Maintenance!;
        var plan = MySqlBootstrapPolicy.Plan(command.InstanceId);
        var action = command.Action;
        var operation = pending.Id;
        var copy = MyMaintenanceCopy(plan, operation);
        await MyMaintenanceInventory(plan, copy.ServiceName);

        if (pending.Phase == "Prepared")
        {
            await MyMaintenancePreflight(ledger, command);
            if (action is MySqlMaintenanceAction.Update or MySqlMaintenanceAction.Repair or MySqlMaintenanceAction.Reinstall)
                await MyPrepareArtifacts(command.Version);
            if (action == MySqlMaintenanceAction.Update) await MyPrepareArtifacts(ledger.Version);
            ledger = MyPhase(ledger, action == MySqlMaintenanceAction.Reinstall ? "ReinstallingPackages" : "Quiescing");
        }
        if (ledger.Pending!.Phase == "ReinstallingPackages")
        {
            if (MyOwnedText(MyUnit(plan), plan.UnitText))
            {
                await MyVerifyUnit(plan);
                await MyStopOwnedUnit(plan.ServiceName);
            }
            var paths = await MyPrepareArtifacts(command.Version);
            await PgRun("/usr/bin/dpkg", ["--refuse-downgrade", "-i", .. paths], minutes: 10);
            await MyVerifyPackages(command.Version);
            var backup = MyReadBackup(ledger, plan, ledger.Backups!.Last().Backup.Id);
            await MyVerifyMaintenanceCopy(ledger, plan, backup, command.Version);
            if (!MyOwnedText(MyUnit(plan), plan.UnitText)) PgAtomic(MyUnit(plan), Encoding.UTF8.GetBytes(plan.UnitText));
            ledger = MyPhase(ledger, "StartingMaintained");
        }
        if (ledger.Pending!.Phase == "Quiescing")
        {
            if (await MyRunning(plan))
            {
                await MyVerifyProcess(plan);
                await MySqlQuery(plan, "SET GLOBAL innodb_fast_shutdown=0;");
            }
            await MyFenceNormalService(plan);
            var stop = (await PgRun("/usr/bin/systemctl", ["show", "-p", "Result", "-p", "ExecMainStatus", plan.ServiceName])).Output;
            if (!stop.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet().SetEquals(["Result=success", "ExecMainStatus=0"]))
                throw new PgFailure("database.stop_unconfirmed");
            ledger = MyPhase(ledger, "StoppedForMaintenance");
        }
        if (ledger.Pending!.Phase == "StoppedForMaintenance")
        {
            await MyVerifyUnit(plan, MyAdminGuard(plan));
            await MyStopOwnedUnit(plan.ServiceName);
            if (action == MySqlMaintenanceAction.Restore)
            {
                var source = MyReadBackup(ledger, plan, command.BackupId!.Value);
                await MyVerifyMaintenanceCopy(ledger, plan, source, ledger.Version);
                var staged = plan.DataDirectory + ".restore-" + operation.ToString("N");
                if (Directory.Exists(staged)) PgDeleteOwnedTree(staged, Path.GetDirectoryName(plan.DataDirectory)!);
                await PgRun("/usr/bin/cp", ["-a", "--reflink=auto", source, staged], minutes: 10);
                await PgRun("/usr/bin/sync", ["-f", staged]);
                ledger = MyPhase(ledger, "RestoringData");
            }
            else if (action == MySqlMaintenanceAction.Repair)
                ledger = MyPhase(ledger, "ApplyingPackages");
            else
            {
                if (ledger.Backups?.Any(x => x.Backup.Id == operation) != true)
                    ledger = await MyCreateMaintenanceBackup(ledger, plan);
                MyReadBackup(ledger, plan, operation);
                ledger = MyPhase(ledger, action == MySqlMaintenanceAction.Backup ? "StartingMaintained" : "ApplyingPackages");
            }
        }
        if (ledger.Pending!.Phase == "RestoringData")
        {
            var suffix = operation.ToString("N");
            var parent = Path.GetDirectoryName(plan.DataDirectory)!;
            var staged = plan.DataDirectory + ".restore-" + suffix;
            var prior = plan.DataDirectory + ".before-" + suffix;
            foreach (var path in new[] { staged, prior, plan.DataDirectory }) PgNoLinks(path);
            if (!Directory.Exists(prior))
            {
                if (!Directory.Exists(staged)) throw new PgFailure("database.backup_changed");
                Directory.Move(plan.DataDirectory, prior);
                LinuxDirectorySync.Flush(parent);
            }
            if (!Directory.Exists(plan.DataDirectory))
            {
                Directory.Move(staged, plan.DataDirectory);
                LinuxDirectorySync.Flush(parent);
            }
            if (Directory.Exists(staged)) throw new PgFailure("database.backup_changed");
            ledger = MyPhase(ledger, "StartingMaintained");
        }
        if (ledger.Pending!.Phase == "ApplyingPackages")
        {
            await MyVerifyUnit(plan, MyAdminGuard(plan));
            await MyStopOwnedUnit(plan.ServiceName);
            if (action is MySqlMaintenanceAction.UninstallKeepData or MySqlMaintenanceAction.UninstallDeleteData)
            {
                await PgRun("/usr/bin/dpkg", ["--remove", .. MySqlBootstrapPolicy.Artifacts.Select(x => x.Name)], minutes: 10);
                await MyAudit();
                if (File.Exists(MySqlBootstrapPolicy.Server) || File.Exists("/usr/bin/mysql"))
                    throw new PgFailure("database.package_identity_changed");
                ledger = MyPhase(ledger, "RemovingInstanceRegistration");
            }
            else
            {
                var paths = await MyPrepareArtifacts(command.Version);
                await PgRun("/usr/bin/dpkg", ["--refuse-downgrade", "-i", .. paths], minutes: 10);
                await MyVerifyPackages(command.Version);
                ledger = MyPhase(ledger, "PackagesApplied");
            }
        }
        if (ledger.Pending!.Phase == "PackagesApplied")
        {
            var backup = action == MySqlMaintenanceAction.Update ? operation : ledger.Backups!.Last().Backup.Id;
            try
            {
                await MyVerifyMaintenanceCopy(ledger, plan, MyReadBackup(ledger, plan, backup), command.Version);
                ledger = MyPhase(ledger, "StartingMaintained");
            }
            catch (PgFailure error) when (action == MySqlMaintenanceAction.Update
                && error.Message is "database.backup_unverified" or "database.connection_unverified" or "database.native_failed")
            {
                ledger = MyPhase(ledger, "CompensatingPackages");
            }
        }
        if (ledger.Pending!.Phase == "CompensatingPackages")
        {
            await MyVerifyUnit(plan, MyAdminGuard(plan));
            await MyStopOwnedUnit(plan.ServiceName);
            var text = MyRecoveryUnitText(plan, copy, null).Replace(plan.DataDirectory, copy.DataDirectory, StringComparison.Ordinal);
            await MyCleanMaintenanceCopy(copy, text);
            MyReadBackup(ledger, plan, operation);
            var paths = await MyPrepareArtifacts(ledger.Version);
            await PgRun("/usr/bin/dpkg", ["--force-downgrade", "-i", .. paths], minutes: 10);
            await MyVerifyPackages(ledger.Version);
            ledger = MyPhase(ledger, "StartingCompensated");
        }
        if (ledger.Pending!.Phase == "StartingCompensated")
        {
            await MyStartMaintainedUnit(plan, ledger.Version);
            await MyMaintenanceCatalog(ledger, plan);
            return ledger with { CompensatedMaintenance = [.. (ledger.CompensatedMaintenance ?? []), operation] };
        }
        if (ledger.Pending!.Phase == "RemovingInstanceRegistration")
        {
            var unit = MyUnit(plan);
            if (MyOwnedText(unit, plan.UnitText)) File.Delete(unit);
            var guard = MyAdminGuard(plan);
            if (MyOwnedText(guard, MyAdminGuardText(plan))) File.Delete(guard);
            if (Directory.Exists(Path.GetDirectoryName(guard)!) && !Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(guard)!).Any())
                Directory.Delete(Path.GetDirectoryName(guard)!);
            LinuxDirectorySync.Flush(Path.GetDirectoryName(unit)!);
            await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
            if (action == MySqlMaintenanceAction.UninstallKeepData)
                return ledger with { Installed = false, Retained = true };
            await MyCheckDataTreeIfPresent(plan.DataDirectory);
            PgDeleteOwnedTree(plan.DataDirectory, Path.GetDirectoryName(plan.DataDirectory)!);
            PgDeleteOwnedTree(MyBackupRoot(plan.Id), MyRoot + "/backups");
            var parent = Path.GetDirectoryName(plan.DataDirectory)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            var root = Path.GetDirectoryName(parent)!;
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
                LinuxDirectorySync.Flush(Path.GetDirectoryName(root)!);
            }
            var user = await PgRun("/usr/bin/getent", ["passwd", MyUser], allowFailure: true);
            if (user.Code == 0) await PgRun("/usr/sbin/userdel", [MyUser]);
            else if (user.Code != 2) throw new PgFailure("database.state_untrusted");
            return ledger with { Installed = false, Retained = false, Instance = null, Accounts = null, Databases = null, Backups = null, Version = MySqlBootstrapPolicy.Version };
        }
        if (ledger.Pending!.Phase != "StartingMaintained") throw new PgFailure("database.recovery_required");
        try
        {
            await MyStartMaintainedUnit(plan, command.Version);
            await MyMaintenanceCatalog(ledger, plan);
        }
        catch (PgFailure)
        {
            await MyFenceNormalService(plan);
            throw;
        }
        if (action == MySqlMaintenanceAction.Restore)
            PgDeleteOwnedTree(plan.DataDirectory + ".before-" + operation.ToString("N"), Path.GetDirectoryName(plan.DataDirectory)!);
        return ledger with { Version = command.Version, Installed = true, Retained = false };
    }

    private static async Task MyCheckDataTreeIfPresent(string data)
    {
        if (Directory.Exists(data)) await MyCheckDataTree(data);
    }
}
