using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.ManagedRuntimes.PostgreSql;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private sealed record PgBackupManifest(Guid Instance, string Version, PgLedger Snapshot, Dictionary<string,string> Files, Dictionary<string,string> ConfigurationFiles);
    private static string PgBackupRoot(PgLedger l) => PgRoot+"/backups/"+l.Instance!.Value.ToString("N");
    private static string PgBackupPath(PgLedger l,Guid id) => PgBackupRoot(l)+"/"+id.ToString("N");
    private static async Task<ComponentPackageTarget> PgResolveMaintenance()
    {
        // Bootstrap refresh verifies the already configured Ubuntu signing authority even on an installed host.
        try { await PgResolve(); } catch(PgFailure e) when(e.Message is "database.external_installation" or "database.inventory_unknown" or "database.package_dependencies_incomplete") { }
        var policy=(await PgRun("/usr/bin/apt-cache",[..PgAptOptions(),"policy","postgresql-16"])).Output;
        if(!AptPackageResolution.TryParsePolicy(policy,"postgresql-16",out var parsed) || parsed?.CandidateVersion is not {} version
            || version!="16.15-0ubuntu0.24.04.1") throw new PgFailure("database.maintenance_version_unsupported");
        var uris=(await PgRun("/usr/bin/apt-get",[..PgAptOptions(),"-o","Dir::Cache::archives="+PgRoot+"/empty","install","--print-uris","--reinstall","--yes","--no-remove","--no-install-recommends",
            "postgresql-16="+version,"postgresql-client-16="+version,"postgresql-common","postgresql-client-common"])).Output;
        if(!AptPackageResolution.TryParseUriPlan(uris,"postgresql-16",version,out var main,out var dependencies)
            || !AptPackageResolution.TryBuildModuleResolution(parsed,main,dependencies,["postgresql-16"],out var result,out _) || result is null
            || PostgreSqlBootstrapPolicy.CheckTarget(result) is not null) throw new PgFailure("database.package_unresolved");
        return new(result.Package,result.Version,result.Architecture,result.RepositoryId,result.BaseUri,result.Suite,result.Area,result.PoolUri,result.Sha256,result.Bytes,
            result.Dependencies.Select(x=>new ComponentPackageArtifact(x.FileName,x.Uri,x.Bytes,x.Sha256)).ToArray());
    }
    private static async Task PgExclusiveInstance(PgLedger l)
    {
        var plan=PostgreSqlInitializationPlanner.Create(l.Instance!.Value,l.Port);
        foreach(var directory in new[]{"/etc/postgresql","/var/lib/postgresql"}) PgNoLinks(directory);
        if(Directory.Exists("/etc/postgresql")) foreach(var major in Directory.GetDirectories("/etc/postgresql"))
            foreach(var cluster in Directory.GetDirectories(major)) if(cluster!=plan.ConfigurationDirectory) throw new PgFailure("database.external_installation");
        if(Directory.Exists("/var/lib/postgresql"))
            foreach(var file in Directory.EnumerateFiles("/var/lib/postgresql","PG_VERSION",SearchOption.AllDirectories))throw new PgFailure("database.external_installation");
        var installed=(await PgRun("/usr/bin/dpkg-query",["-W","-f=${binary:Package}|${db:Status-Status}\n","postgresql*"],allowFailure:true)).Output;
        foreach(var line in installed.Split('\n',StringSplitOptions.RemoveEmptyEntries))
            if(line.EndsWith("|installed",StringComparison.Ordinal) && line.Split('|')[0] is not ("postgresql-16" or "postgresql-client-16" or "postgresql-common" or "postgresql-client-common")) throw new PgFailure("database.external_installation");
        if(!l.DataDeleted)
        {
            if(!Directory.Exists(plan.DataDirectory)||!Directory.Exists(plan.ConfigurationDirectory))throw new PgFailure("database.instance_invalid");
            PgTreeFiles(plan.DataDirectory);
            var bytes=Directory.EnumerateFiles(plan.DataDirectory,"*",SearchOption.AllDirectories).Sum(x=>new FileInfo(x).Length);
            if(new DriveInfo(plan.DataDirectory).AvailableFreeSpace<checked(bytes*4+536870912L))throw new PgFailure("database.backup_space_insufficient");
            if(Directory.EnumerateFileSystemEntries(plan.DataDirectory+"/pg_tblspc").Any())throw new PgFailure("database.backup_scope_unsupported");
        }
    }
    private static async Task PgMaintenancePreflight(PgLedger l,PostgreSqlMaintenanceCommand c)
    {
        if(c.Action is PostgreSqlMaintenanceAction.ConfigureBackupSchedule or PostgreSqlMaintenanceAction.ScheduledBackup or PostgreSqlMaintenanceAction.DeleteBackup)
        {await PgBackupPolicyPreflight(l,c);return;}
        if(!PostgreSqlMaintenanceValidation.Valid(c)||l.Instance!=c.InstanceId||l.Revision!=c.ExpectedRevision||l.Package is null||l.RemoteAccess is not null)throw new PgFailure("database.maintenance_invalid");
        if(c.Action==PostgreSqlMaintenanceAction.Reinstall ? !l.PackageRemoved : l.PackageRemoved)throw new PgFailure("database.maintenance_invalid");
        await PgExclusiveInstance(l);
        if(!l.PackageRemoved)
        {
            if(c.Action!=PostgreSqlMaintenanceAction.Repair)await PgVerifyPackage(l);
            else if((await PgRun("/usr/bin/dpkg-query",["-W","-f=${Status}|${Version}","postgresql-16"])).Output.Trim()!="install ok installed|"+l.Package.Version
                || PgFileHash(PostgreSqlBootstrapPolicy.ServiceTemplate)!=l.ServiceHash)throw new PgFailure("database.package_identity_changed");
            await PgVerifyConfiguration(l);
        }
        if(c.Target is {} target)
        {
            if(!AptPackageResolution.MatchesExpectation(target,await PgResolveMaintenance()))throw new PgFailure("database.plan_changed");
            var comparison=DebianVersion.Compare(target.Version,l.Package.Version);
            if(c.Action==PostgreSqlMaintenanceAction.Update && comparison<=0 || c.Action==PostgreSqlMaintenanceAction.Repair && comparison!=0
                || c.Action==PostgreSqlMaintenanceAction.Reinstall && comparison<0)throw new PgFailure("database.maintenance_invalid");
        }
        if(c.Action==PostgreSqlMaintenanceAction.Restore)
        {
            var backup=PgReadBackup(l,c.BackupId!.Value);
            // Never revive a password or permission revoked since this backup.
            if(backup.Version!=l.Package.Version || backup.Snapshot.ConfigurationHash!=l.ConfigurationHash || JsonSerializer.Serialize(backup.Snapshot.Accounts)!=JsonSerializer.Serialize(l.Accounts)
                || JsonSerializer.Serialize(backup.Snapshot.Databases)!=JsonSerializer.Serialize(l.Databases))throw new PgFailure("database.backup_identity_changed");
        }
        if((l.Backups?.Count??0)>=8 && c.Action!=PostgreSqlMaintenanceAction.UninstallDeleteData && c.Action!=PostgreSqlMaintenanceAction.Reinstall)throw new PgFailure("database.backup_capacity");
    }
    private static Dictionary<string,string> PgTreeFiles(string root)
    {
        PgNoLinks(root);
        var full=Path.GetFullPath(root).TrimEnd('/');
        foreach(var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields=line.Split(' ');if(fields.Length<5)throw new PgFailure("database.path_untrusted");
            var mount=fields[4].Replace("\\040"," ").Replace("\\011","\t").Replace("\\134","\\");
            if(mount==full||mount.StartsWith(full+"/",StringComparison.Ordinal))throw new PgFailure("database.path_untrusted");
        }
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        void Visit(string directory)
        {
            foreach(var item in Directory.EnumerateFileSystemEntries(directory))
            {
                PgNoLinks(item);
                if(Directory.Exists(item))Visit(item);else {using var stream=File.OpenRead(item);result.Add(Path.GetRelativePath(root,item),Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));}
            }
        }
        Visit(root);return result;
    }
    private static PgBackupManifest PgReadBackup(PgLedger l,Guid id,bool requireVerified=true)
    {
        var record=l.Backups?.SingleOrDefault(x=>x.Id==id&&(!requireVerified||x.RestoreVerified))??throw new PgFailure("database.backup_unverified");
        var root=PgBackupPath(l,id);var manifest=root+"/manifest.json";
        if(PgFileHash(manifest)!=record.ManifestHash)throw new PgFailure("database.backup_changed");
        var value=JsonSerializer.Deserialize<PgBackupManifest>(File.ReadAllBytes(manifest),PgJson)??throw new PgFailure("database.backup_changed");
        if(value.Instance!=l.Instance)throw new PgFailure("database.backup_identity_changed");
        var files=PgTreeFiles(root+"/data");
        if(files.Count!=value.Files.Count||files.Any(x=>!value.Files.TryGetValue(x.Key,out var hash)||hash!=x.Value))throw new PgFailure("database.backup_changed");
        var config=PgTreeFiles(root+"/config");
        if(config.Count!=value.ConfigurationFiles.Count||config.Any(x=>!value.ConfigurationFiles.TryGetValue(x.Key,out var hash)||hash!=x.Value))throw new PgFailure("database.backup_changed");
        return value;
    }
    private static void PgDeleteOwnedTree(string path,string parent)
    {
        var full=Path.GetFullPath(path);var scope=Path.GetFullPath(parent).TrimEnd('/')+"/";
        if(!full.StartsWith(scope,StringComparison.Ordinal)||full==scope.TrimEnd('/'))throw new PgFailure("database.path_untrusted");
        PgNoLinks(full);if(!Directory.Exists(full))return;PgTreeFiles(full);Directory.Delete(full,true);LinuxDirectorySync.Flush(Path.GetDirectoryName(full)!);
    }
    private static async Task PgStartPrivate(PgLedger l,string? data=null,string? socket=null)
    {
        var plan=PostgreSqlInitializationPlanner.Create(l.Instance!.Value,l.Port);data??=plan.DataDirectory;socket??=plan.SocketDirectory;
        await PgRun("/usr/bin/install",["-d","-m","700","-o","postgres","-g","postgres",socket]);
        await PgRun("/usr/sbin/runuser",["-u","postgres","--","/usr/lib/postgresql/16/bin/pg_ctl","-D",data,"-l",data+"/rk-maintenance.log","-o",
            "-c config_file="+plan.ConfigurationDirectory+"/postgresql.conf -c data_directory="+data+" -c listen_addresses='' -c unix_socket_directories="+socket+" -c unix_socket_permissions=0700 -c external_pid_file="+data+"/rk-maintenance.pid","-w","start"]);
    }
    private static async Task<string> PgPrivateSql(PgLedger l,string socket,string db,string sql)=>
        (await PgRun("/usr/sbin/runuser",["-u","postgres","--","/usr/lib/postgresql/16/bin/psql","-XqAtw","-v","ON_ERROR_STOP=1","-h",socket,"-p",l.Port.ToString(),"-d",db],input:sql)).Output.Trim();
    private static async Task PgStopCopy(string data)
    {
        if(File.Exists(data+"/postmaster.pid")) await PgRun("/usr/sbin/runuser",["-u","postgres","--","/usr/lib/postgresql/16/bin/pg_ctl","-D",data,"-m","fast","-w","stop"]);
    }
    private static async Task PgVerifyBackupCopy(PgLedger l,Guid id)
    {
        var plan=PostgreSqlInitializationPlanner.Create(l.Instance!.Value,l.Port);
        var check=Path.GetDirectoryName(plan.DataDirectory)!+"/verify-"+id.ToString("N");var data=check+"/data";var socket="/run/postgresql/rkb"+id.ToString("N");
        if(Directory.Exists(check)) { await PgStopCopy(data);PgDeleteOwnedTree(check,Path.GetDirectoryName(plan.DataDirectory)!); }
        await PgRun("/usr/bin/install",["-d","-m","700","-o","postgres","-g","postgres",check]);
        await PgRun("/usr/bin/cp",["-a","--reflink=auto",PgBackupPath(l,id)+"/data",data],minutes:10);
        try
        {
            await PgRun("/usr/sbin/runuser",["-u","postgres","--","/usr/lib/postgresql/16/bin/pg_checksums","--check","-D",data],minutes:10);
            await PgStartPrivate(l,data,socket);
            if(await PgPrivateSql(l,socket,"postgres","SELECT current_setting('listen_addresses')||'|'||current_setting('data_directory');")!="|"+data)throw new PgFailure("database.backup_unverified");
            foreach(var db in l.Databases)await PgVerifyDatabase(l,db,(database,sql)=>PgPrivateSql(l,socket,database,sql));
            foreach(var database in l.Databases.Select(x=>x.Name).Prepend("postgres").Append("template1"))
                await PgPrivateSql(l,socket,database,"SET default_transaction_read_only=on; SELECT format('SELECT count(*) FROM %I.%I;',n.nspname,c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind='r' AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%' ORDER BY n.nspname,c.relname;\n\\gexec\n");
            foreach(var account in l.Accounts)await PgVerifyAccount(l,account,(database,sql)=>PgPrivateSql(l,socket,database,sql));
        }
        finally { await PgStopCopy(data);PgDeleteOwnedTree(check,Path.GetDirectoryName(plan.DataDirectory)!);PgDeleteOwnedTree(socket,"/run/postgresql"); }
    }
    private static async Task<PgLedger> PgCreateBackup(PgLedger l, bool verify=true)
    {
        var id=l.Pending!.Id;var root=PgBackupPath(l,id);var plan=PostgreSqlInitializationPlanner.Create(l.Instance!.Value,l.Port);
        if(l.Backups?.Any(x=>x.Id==id&&x.RestoreVerified)==true){PgReadBackup(l,id);return l;}
        if(Directory.Exists(root))PgDeleteOwnedTree(root,PgBackupRoot(l));
        Directory.CreateDirectory(root);PgNoLinks(root);
        await PgRun("/usr/bin/cp",["-a","--reflink=auto",plan.DataDirectory,root+"/data"],minutes:10);
        await PgRun("/usr/bin/cp",["-a",plan.ConfigurationDirectory,root+"/config"]);
        var manifest=new PgBackupManifest(l.Instance!.Value,l.Package!.Version,l,PgTreeFiles(root+"/data"),PgTreeFiles(root+"/config"));
        PgAtomic(root+"/manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest,PgJson));
        await PgRun("/usr/bin/sync",["-f",root]);
        if(verify)await PgVerifyBackupCopy(l,id);
        var record=new PostgreSqlBackup(id,l.Package.Version,DateTimeOffset.UtcNow,PgFileHash(root+"/manifest.json"),verify,l.Pending!.Request.Maintenance?.Action==PostgreSqlMaintenanceAction.ScheduledBackup);
        l=l with { Backups=[..(l.Backups??[]).Where(x=>x.Id!=id),record] };PgWrite(l);return l;
    }
    private static async Task PgCheckMaintenanceScope(PgLedger l)
    {
        var plan=PostgreSqlInitializationPlanner.Create(l.Instance!.Value,l.Port);await PgStartPrivate(l);
        try
        {
            var expected=l.Databases.Select(x=>x.Name).Concat(["postgres","template0","template1"]).Order(StringComparer.Ordinal);
            var actual=(await PgScalar(l,"postgres","SELECT datname FROM pg_database ORDER BY datname;")).Split('\n').Order(StringComparer.Ordinal);
            if(!actual.SequenceEqual(expected))throw new PgFailure("database.backup_scope_unsupported");
            if(await PgScalar(l,"postgres","SELECT count(*) FROM pg_replication_slots;")!="0")throw new PgFailure("database.backup_scope_unsupported");
            foreach(var db in l.Databases.Select(x=>x.Name).Prepend("postgres").Append("template1"))
                if(await PgScalar(l,db,"SELECT count(*) FROM pg_extension WHERE extname<>'plpgsql';")!="0"
                    || await PgScalar(l,db,"SELECT count(*) FROM pg_subscription;")!="0")throw new PgFailure("database.backup_scope_unsupported");
            foreach(var db in l.Databases)await PgVerifyDatabase(l,db);
            foreach(var account in l.Accounts)await PgVerifyAccount(l,account);
        }
        finally {await PgStopPrivate(plan);}
    }
    private static async Task PgDownloadMaintenance(ComponentPackageTarget target)
    {
        var names=target.AlsoInstalls.Select(x=>Uri.UnescapeDataString(x.FileName).Split('_')).Select(x=>x[0]+"="+x[1]).Prepend(target.PackageName+"="+target.Version).ToArray();
        await PgRun("/usr/bin/apt-get",[..PgAptOptions(),"install","--download-only","--reinstall","--yes","--no-remove","--no-install-recommends",..names],minutes:15);
        PgVerifyMaintenanceArtifacts(target);
    }
    private static void PgVerifyMaintenanceArtifacts(ComponentPackageTarget target)
    {
        foreach(var file in target.AlsoInstalls.Prepend(new(Path.GetFileName(new Uri(target.PoolUri).AbsolutePath),target.PoolUri,target.Bytes,target.Sha256)))
        {
            var path=PgRoot+"/archives/"+Uri.UnescapeDataString(file.FileName).Replace(":","%3a",StringComparison.Ordinal);
            if(!File.Exists(path)||new FileInfo(path).Length!=file.Bytes||!PgFileHash(path).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new PgFailure("database.artifact_changed");
        }
    }
    private static async Task PgPostPatch(PgLedger l)
    {
        var plan=PostgreSqlInitializationPlanner.Create(l.Instance!.Value,l.Port);await PgStartPrivate(l);
        try
        {
            var fix="/usr/share/postgresql/16/fix-CVE-2024-4317.sql";PgNoLinks(fix);
            if(!File.Exists(fix))throw new PgFailure("database.maintenance_migration_required");
            await PgSql(l,"postgres","ALTER DATABASE template0 ALLOW_CONNECTIONS true;");
            try
            {
                foreach(var db in l.Databases.Select(x=>x.Name).Concat(["postgres","template1","template0"]))
                {
                    await PgRun("/usr/sbin/runuser",["-u","postgres","--","/usr/lib/postgresql/16/bin/psql","-XqAtw","-v","ON_ERROR_STOP=1","-h",plan.SocketDirectory,"-p",l.Port.ToString(),"-d",db,"-f",fix]);
                    await PgSql(l,db,"REINDEX DATABASE "+PgId(db)+"; ANALYZE;");
                }
            }
            finally {await PgSql(l,"postgres","ALTER DATABASE template0 ALLOW_CONNECTIONS false;");}
            foreach(var db in l.Databases)await PgVerifyDatabase(l,db);
            foreach(var account in l.Accounts)await PgVerifyAccount(l,account);
        }
        finally {await PgStopPrivate(plan);}
    }
    private static async Task<PgLedger> PgContinueMaintenance(PgLedger l)
    {
        var command=l.Pending!.Request.Maintenance!;var plan=PostgreSqlInitializationPlanner.Create(command.InstanceId,l.Port);
        if(l.Pending!.Phase=="Prepared" && command.Target is {} target)
        {if(!AptPackageResolution.MatchesExpectation(target,await PgResolveMaintenance()))throw new PgFailure("database.plan_changed");await PgDownloadMaintenance(target);}
        if(l.Instance is not null && !l.DataDeleted && l.Pending!.Phase!="RestoreDispatched"
            && !(command.Action==PostgreSqlMaintenanceAction.UninstallDeleteData && l.Pending.Phase=="PackageRemoved"))
            await PgVerifyConfiguration(l,allowAdminMask:true);
        await PgMaskAdmin(l,plan);
        await PgStopPrivate(plan);
        try
        {
            if(l.Pending!.Phase=="Prepared")l=PgPhase(l,"MaintenanceStopped");
            if(l.Pending!.Phase=="MaintenanceStopped")
            {
                await PgExclusiveInstance(l);
                if(!l.PackageRemoved)
                {
                    if(command.Action!=PostgreSqlMaintenanceAction.Repair)await PgCheckMaintenanceScope(l);
                    l=await PgCreateBackup(l,verify:command.Action!=PostgreSqlMaintenanceAction.Repair);
                }
                l=PgPhase(l,"BackupVerified");
            }
            if(command.Action is PostgreSqlMaintenanceAction.Update or PostgreSqlMaintenanceAction.Repair or PostgreSqlMaintenanceAction.Reinstall)
            {
                var package=command.Target!;
                if(l.Pending!.Phase=="BackupVerified")
                {
                    PgVerifyMaintenanceArtifacts(package);
                    await PgExclusiveInstance(l);
                    if(File.Exists(PgCreateConfig)&&File.ReadAllText(PgCreateConfig)!=PgSuppression)throw new PgFailure("database.configuration_drift");
                    PgAtomic(PgCreateConfig,Encoding.UTF8.GetBytes(PgSuppression));
                    l=PgPhase(l,"MaintenancePackageDispatched");
                    var names=package.AlsoInstalls.Select(x=>Uri.UnescapeDataString(x.FileName).Split('_')).Select(x=>x[0]+"="+x[1]).Prepend(package.PackageName+"="+package.Version).ToArray();
                    await PgRun("/usr/bin/apt-get",[..PgAptOptions(),"install","--no-download","--reinstall","--yes","--no-remove","--no-install-recommends","-o","Dpkg::Options::=--force-confold",..names],minutes:15);
                    l=PgPhase(l,"MaintenancePackageApplied");
                }
                if(l.Pending!.Phase=="MaintenancePackageDispatched")
                {
                    // No blind dpkg replay. Only the frozen target, clean audit and verified bytes can reconcile this phase.
                    var status=(await PgRun("/usr/bin/dpkg-query",["-W","-f=${Status}|${Version}","postgresql-16"])).Output.Trim();
                    if(status!="install ok installed|"+package.Version)throw new PgFailure("database.maintenance_package_uncertain");
                    l=PgPhase(l,"MaintenancePackageApplied");
                }
                if(l.Pending!.Phase=="MaintenancePackageApplied")
                {
                    l=l with { Package=package,PackageRemoved=false,BinaryHash=PgFileHash(PostgreSqlBootstrapPolicy.Executable),ServiceHash=PgFileHash(PostgreSqlBootstrapPolicy.ServiceTemplate) };
                    await PgVerifyPackage(l);
                    if(command.Action==PostgreSqlMaintenanceAction.Repair)
                    {
                        PgReadBackup(l,l.Pending!.Id,requireVerified:false);
                        await PgVerifyBackupCopy(l,l.Pending!.Id);
                        await PgCheckMaintenanceScope(l);
                        l=l with {Backups=l.Backups!.Select(x=>x.Id==l.Pending!.Id?x with {RestoreVerified=true}:x).ToArray()};PgWrite(l);
                    }
                    if(!l.DataDeleted)await PgPostPatch(l);
                    if(File.Exists(PgCreateConfig)&&File.ReadAllText(PgCreateConfig)!=PgSuppression)throw new PgFailure("database.configuration_drift");
                    File.Delete(PgCreateConfig);LinuxDirectorySync.Flush(Path.GetDirectoryName(PgCreateConfig)!);
                    
                    l=PgPhase(l,"MaintenanceVerified");
                }
            }
            else if(command.Action==PostgreSqlMaintenanceAction.Restore)
            {
                var backup=PgReadBackup(l,command.BackupId!.Value);var parent=Path.GetDirectoryName(plan.DataDirectory)!;
                var staged=parent+"/restore-"+l.Pending!.Id.ToString("N");var prior=parent+"/before-restore-"+l.Pending!.Id.ToString("N");
                if(l.Pending!.Phase=="BackupVerified")
                {
                    if(Directory.Exists(staged))PgDeleteOwnedTree(staged,parent);
                    await PgRun("/usr/bin/cp",["-a","--reflink=auto",PgBackupPath(l,command.BackupId.Value)+"/data",staged],minutes:10);
                    await PgRun("/usr/bin/sync",["-f",staged]);
                    l=PgPhase(l,"RestoreDispatched");
                }
                if(l.Pending!.Phase=="RestoreDispatched")
                {
                    if(!Directory.Exists(prior)){Directory.Move(plan.DataDirectory,prior);LinuxDirectorySync.Flush(parent);}
                    if(!Directory.Exists(plan.DataDirectory))Directory.Move(staged,plan.DataDirectory);
                    if(Directory.Exists(staged))throw new PgFailure("database.restore_uncertain");
                    LinuxDirectorySync.Flush(parent);l=PgPhase(l,"Restored");
                }
                if(l.Pending!.Phase=="Restored") {await PgCheckMaintenanceScope(l);PgDeleteOwnedTree(prior,parent);l=PgPhase(l,"MaintenanceVerified");}
            }
            else if(command.Action is PostgreSqlMaintenanceAction.UninstallKeepData or PostgreSqlMaintenanceAction.UninstallDeleteData)
            {
                if(l.Pending!.Phase=="BackupVerified")
                {
                    await PgExclusiveInstance(l);
                    l=PgPhase(l,"RemoveDispatched");await PgRun("/usr/bin/dpkg",["--remove","postgresql-16"],minutes:10);
                }
                if(l.Pending!.Phase=="RemoveDispatched")
                {
                    var status=await PgRun("/usr/bin/dpkg-query",["-W","-f=${db:Status-Status}","postgresql-16"],allowFailure:true);
                    if(status.Output.Trim() is not ("config-files" or "not-installed") || File.Exists(PostgreSqlBootstrapPolicy.Executable))throw new PgFailure("database.maintenance_package_uncertain");
                    l=l with { PackageRemoved=true };l=PgPhase(l,"PackageRemoved");
                }
                if(l.Pending!.Phase=="PackageRemoved")
                {
                    if(command.Action==PostgreSqlMaintenanceAction.UninstallDeleteData)
                    {
                        PgDeleteOwnedTree(plan.DataDirectory,Path.GetDirectoryName(plan.DataDirectory)!);
                        PgDeleteOwnedTree(plan.ConfigurationDirectory,"/etc/postgresql/16");
                        PgDeleteOwnedTree(PgBackupRoot(l),PgRoot+"/backups");
                        if(PgRemoteOwnDropIn(l)) { File.Delete(PgRemoteDropIn(l)+"/50-rk-database.conf");Directory.Delete(PgRemoteDropIn(l)); }
                        if(Directory.Exists(PgRemoteRoot(l)))
                        {
                            var tables=(await PgRun("/usr/sbin/nft",["list","tables"])).Output;
                            if(tables.Split('\n').Any(x=>x.Trim()=="table inet "+PgRemoteTable(l)))await PgRun("/usr/sbin/nft",["delete","table","inet",PgRemoteTable(l)]);
                            if((await PgRun("/usr/sbin/nft",["list","tables"])).Output.Split('\n').Any(x=>x.Trim()=="table inet "+PgRemoteTable(l)))throw new PgFailure("database.remote_isolation_unverified");
                            PgDeleteOwnedTree(PgRemoteRoot(l),PgRoot);
                        }
                        PgDeleteOwnedTree(plan.SocketDirectory,"/run/postgresql");
                        l=l with { DataDeleted=true,Backups=[],Databases=[],Accounts=[],ConfigurationHash=null };
                    }
                    l=PgPhase(l,"MaintenanceVerified");
                }
            }
            else if(command.Action is PostgreSqlMaintenanceAction.Backup or PostgreSqlMaintenanceAction.ScheduledBackup)l=PgPhase(l,"MaintenanceVerified");
            if(l.Pending!.Phase is not ("MaintenanceVerified" or "Verified"))throw new PgFailure("database.state_invalid");
            if(PgOwnMask(plan)){File.Delete(PgAdminMask(plan));LinuxDirectorySync.Flush("/etc/systemd/system");await PgRun("/usr/bin/systemctl",["daemon-reload"]);}
            if(command.Action==PostgreSqlMaintenanceAction.Reinstall&&l.DataDeleted)l=l with {Instance=null,ConfigurationHash=null,DataDeleted=false};
            return PgPhase(l,"Verified");
        }
        catch {await PgMaskAdmin(l,plan);await PgStopPrivate(plan);throw;}
    }
}
