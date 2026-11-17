using RelaxKonOS.Protocol.Privileged;
using System.Text;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.ManagedRuntimes.PostgreSql;
using RelaxKonOS.Protocol.Databases;
public static partial class PrivilegedOperationExecutor
{
    private static async Task PgVerifyMajorPackages(RelaxKonOS.Protocol.ManagedRuntimes.ComponentPackageTarget target)
    {
        var files=target.AlsoInstalls.Select(x=>x.FileName).Prepend(Path.GetFileName(new Uri(target.PoolUri).AbsolutePath));
        foreach(var file in files)
        {
            var parts=Uri.UnescapeDataString(file).Split('_');
            if(parts.Length!=3||!parts[2].EndsWith(".deb",StringComparison.Ordinal))throw new PgFailure("database.package_unresolved");
            var status=(await PgRun("/usr/bin/dpkg-query",["-W","-f=${Status}|${Version}|${Architecture}",parts[0]])).Output.Trim();
            if(status!="install ok installed|"+parts[1]+"|"+parts[2][..^4])throw new PgFailure("database.migration_uncertain");
        }
    }
    private static async Task PgMajorPreflight(PgLedger l, PostgreSqlMaintenanceCommand c)
    {
        if(!PostgreSqlMaintenanceValidation.Valid(c)||l.Instance!=c.InstanceId||l.Revision!=c.ExpectedRevision
            ||l.PackageRemoved||l.DataDeleted||l.Package?.Version!="16.15-0ubuntu0.24.04.1"||PgMajor(l)!=16||l.RemoteAccess is not null)
            throw new PgFailure("database.migration_invalid");
        await PgVerifyPackage(l);await PgVerifyConfiguration(l);await PgExclusiveInstance(l);
        if((l.Backups?.Count??0)>=8)throw new PgFailure("database.backup_capacity");
        var next=PostgreSqlInitializationPlanner.Create(c.InstanceId,l.Port,18);
        if(Directory.Exists(next.ConfigurationDirectory)||File.Exists(PgBin(18,"postgres")))throw new PgFailure("database.external_installation");
        if(!AptPackageResolution.MatchesExpectation(c.Target,await PgResolveMajor()))throw new PgFailure("database.plan_changed");
    }
    private static async Task<PgLedger> PgContinueMajor(PgLedger l)
    {
        // A durable verified phase already completed mask cleanup; receipt recovery must not re-isolate it.
        if(l.Pending!.Phase=="Verified")return l;
        var c=l.Pending.Request.Maintenance!;var id=l.Pending.Id;
        var old=PostgreSqlInitializationPlanner.Create(c.InstanceId,l.Port,16);
        var next=PostgreSqlInitializationPlanner.Create(c.InstanceId,l.Port,18);
        var parent=Path.GetDirectoryName(old.DataDirectory)!;
        var staged=parent+"/major-"+id.ToString("N");var prior=parent+"/before-major-"+id.ToString("N");
        var work=parent+"/upgrade-"+id.ToString("N");
        if(l.Pending.Phase=="Prepared")
        {
            if(!AptPackageResolution.MatchesExpectation(c.Target,await PgResolveMajor()))throw new PgFailure("database.plan_changed");
            await PgDownloadMaintenance(c.Target!);l=PgPhase(l,"MajorDownloaded");
        }
        // Both ordinary services remain isolated until all migration and cleanup responsibilities finish.
        await PgMaskAdmin(l,old);await PgMaskAdmin(l,next);
        await PgStopPrivate(old);
        if(l.Pending!.Phase=="MajorDownloaded")
        {
            await PgVerifyConfiguration(l,allowAdminMask:true);await PgCheckMaintenanceScope(l);
            l=await PgCreateBackup(l);l=PgPhase(l,"MajorBackupVerified");
        }
        if(l.Pending!.Phase=="MajorBackupVerified")
        {
            PgVerifyMaintenanceArtifacts(c.Target!);
            if(File.Exists(PgCreateConfig)&&File.ReadAllText(PgCreateConfig)!=PgSuppression)throw new PgFailure("database.configuration_drift");
            PgAtomic(PgCreateConfig,Encoding.UTF8.GetBytes(PgSuppression));
            l=PgPhase(l,"MajorPackageDispatched");
            var names=c.Target!.AlsoInstalls.Select(x=>Uri.UnescapeDataString(x.FileName).Split('_')).Select(x=>x[0]+"="+x[1]).Prepend(c.Target.PackageName+"="+c.Target.Version).ToArray();
            await PgRun("/usr/bin/apt-get",[..PgAptOptions(),"install","--no-download","--reinstall","--yes","--no-remove","--no-install-recommends","-o","Dpkg::Options::=--force-confold",..names],minutes:15);
        }
        if(l.Pending!.Phase=="MajorPackageDispatched")
        {
            await PgVerifyMajorPackages(c.Target!);
            var candidate=l with {Package=c.Target,BinaryHash=PgFileHash(PgBin(18,"postgres")),ServiceHash=PgFileHash(PostgreSqlBootstrapPolicy.ServiceTemplate)};
            await PgVerifyPackage(candidate);
            // Shared package changes are accepted only after the exact frozen installation was verified.
            l=l with {ServiceHash=candidate.ServiceHash};await PgVerifyPackage(l);
            if(File.Exists(PgCreateConfig))
            {if(File.ReadAllText(PgCreateConfig)!=PgSuppression)throw new PgFailure("database.configuration_drift");File.Delete(PgCreateConfig);LinuxDirectorySync.Flush(Path.GetDirectoryName(PgCreateConfig)!);}
            l=PgPhase(l,"MajorPackagesVerified");
        }
        if(l.Pending!.Phase=="MajorPackagesVerified")
        {
            foreach(var path in new[]{staged,next.ConfigurationDirectory,work})
            {PgNoLinks(path);if(Directory.Exists(path)||File.Exists(path))throw new PgFailure("database.path_untrusted");}
            await PgRun("/usr/bin/install",["-d","-m","700","-o","postgres","-g","postgres",work]);
            l=PgPhase(l,"MajorInitializeDispatched");
            await PgRun("/usr/bin/pg_createcluster",next.CreateArguments.Select(x=>x=="--datadir="+next.DataDirectory?"--datadir="+staged:x).ToArray(),minutes:3);
            PgAtomic(next.ConfigurationDirectory+"/pg_hba.conf",Encoding.UTF8.GetBytes(next.HbaConfiguration));
            await PgRun("/usr/bin/chown",["postgres:postgres",next.ConfigurationDirectory+"/pg_hba.conf"]);
            l=PgPhase(l,"MajorInitialized");
        }
        if(l.Pending!.Phase=="MajorInitialized")
        {
            var arguments=new[]{"-b",Path.GetDirectoryName(PgBin(16,"postgres"))!,"-B",Path.GetDirectoryName(PgBin(18,"postgres"))!,
                "-d",old.ConfigurationDirectory,"-D",next.ConfigurationDirectory,"-U","postgres","-p",l.Port.ToString(),"-P",l.Port.ToString(),
                "-s",old.SocketDirectory,"-o","-c listen_addresses='' -c unix_socket_permissions=0700","-O","-c listen_addresses='' -c unix_socket_permissions=0700"};
            await PgRun("/usr/sbin/runuser",["-u","postgres","--",PgBin(18,"pg_upgrade"),..arguments,"--check"],minutes:10,workingDirectory:work);
            l=PgPhase(l,"MajorUpgradeDispatched");
            await PgRun("/usr/sbin/runuser",["-u","postgres","--",PgBin(18,"pg_upgrade"),..arguments],minutes:15,workingDirectory:work);
            await PgRun("/usr/bin/sync",["-f",staged]);l=PgPhase(l,"MajorUpgraded");
        }
        if(l.Pending!.Phase=="MajorUpgraded")
        {
            var candidate=l with {Package=c.Target,BinaryHash=PgFileHash(PgBin(18,"postgres"))};
            await PgStopCopy(staged);
            await PgRun("/usr/sbin/runuser",["-u","postgres","--",PgBin(18,"pg_checksums"),"--check","-D",staged],minutes:10);
            await PgStartPrivate(candidate,staged);
            try
            {
                foreach(var db in l.Databases)await PgVerifyDatabase(candidate,db);
                foreach(var account in l.Accounts)await PgVerifyAccount(candidate,account);
                foreach(var db in l.Databases.Select(x=>x.Name).Prepend("postgres").Append("template1"))
                    await PgSql(candidate,db,"REINDEX DATABASE "+PgId(db)+"; ANALYZE;");
            }
            finally {await PgStopCopy(staged);}
            l=PgPhase(l,"MajorSwitchDispatched");
        }
        if(l.Pending!.Phase=="MajorSwitchDispatched")
        {
            if(!Directory.Exists(prior)){Directory.Move(old.DataDirectory,prior);LinuxDirectorySync.Flush(parent);}
            if(!Directory.Exists(next.DataDirectory)){Directory.Move(staged,next.DataDirectory);LinuxDirectorySync.Flush(parent);}
            if(Directory.Exists(staged)||PgDataMajor(next.DataDirectory)!=18||PgDataMajor(prior)!=16)throw new PgFailure("database.migration_uncertain");
            var config=next.ConfigurationDirectory+"/postgresql.conf";PgNoLinks(config);
            var text=File.ReadAllText(config);text=text.Replace(staged,next.DataDirectory,StringComparison.Ordinal);
            PgAtomic(config,Encoding.UTF8.GetBytes(text));await PgRun("/usr/bin/chown",["postgres:postgres",config]);
            l=l with {Package=c.Target,BinaryHash=PgFileHash(PgBin(18,"postgres")),ConfigurationHash=PgConfigurationHash(next),BackupSchedule=l.BackupSchedule is {} policy?policy with {Enabled=false,LastOutcome="database.backup_schedule_lifecycle"}:null};
            l=PgPhase(l,"MajorSwitched");
        }
        if(l.Pending!.Phase=="MajorSwitched")
        {
            await PgVerifyPackage(l);await PgVerifyConfiguration(l,allowAdminMask:true);await PgCheckMaintenanceScope(l);
            // The verified pre-migration physical backup remains; old live directories are not a rollback API.
            PgDeleteOwnedTree(prior,parent);PgDeleteOwnedTree(work,parent);
            PgDeleteOwnedTree(old.ConfigurationDirectory,"/etc/postgresql/16");
            l=PgPhase(l,"MajorRemoveOldDispatched");
            await PgRun("/usr/bin/dpkg",["--remove","postgresql-16"],minutes:10);
        }
        if(l.Pending!.Phase=="MajorRemoveOldDispatched")
        {
            var status=(await PgRun("/usr/bin/dpkg-query",["-W","-f=${db:Status-Status}","postgresql-16"],allowFailure:true)).Output.Trim();
            if(status is not ("config-files" or "not-installed")||File.Exists(PgBin(16,"postgres")))throw new PgFailure("database.migration_uncertain");
            await PgExclusiveInstance(l);await PgVerifyPackage(l);
            foreach(var plan in new[]{old,next})
            {if(PgOwnMask(plan)){File.Delete(PgAdminMask(plan));LinuxDirectorySync.Flush("/etc/systemd/system");}}
            await PgRun("/usr/bin/systemctl",["daemon-reload"]);l=PgPhase(l,"Verified");
        }
        if(l.Pending!.Phase!="Verified")throw new PgFailure("database.migration_uncertain");
        return l;
    }
}
