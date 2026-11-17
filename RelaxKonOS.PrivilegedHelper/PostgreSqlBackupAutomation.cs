using RelaxKonOS.ManagedRuntimes.PostgreSql;
using RelaxKonOS.Protocol.Databases;

public static partial class PrivilegedOperationExecutor
{
    private static void PgValidateBackupPolicyLedger(PgLedger l)
    {
        if(l.BackupSchedule is {} policy && (policy.Id==Guid.Empty||policy.IntervalHours is <1 or >168||policy.KeepLast is <1 or >6
            ||string.IsNullOrEmpty(policy.Version)||policy.Version.Length>128||policy.NextDueAt.Offset!=TimeSpan.Zero
            ||policy.Enabled&&l.Instance is null))throw new PgFailure("database.state_invalid");
        var backups=l.Backups??[];
        if(backups.Any(x=>x.Id==Guid.Empty)||backups.Select(x=>x.Id).Distinct().Count()!=backups.Count)throw new PgFailure("database.state_invalid");
        var deletes=l.BackupDeletions??[];
        if(deletes.Any(x=>x.Id==Guid.Empty)||deletes.Select(x=>x.Id).Distinct().Count()!=deletes.Count
            ||deletes.Any(x=>(l.Backups??[]).Any(b=>b.Id==x.Id))||deletes.Count>0&&l.Pending?.Request.Maintenance is null)
            throw new PgFailure("database.state_invalid");
    }
    private static async Task PgBackupPolicyPreflight(PgLedger l,PostgreSqlMaintenanceCommand c)
    {
        if(!PostgreSqlMaintenanceValidation.Valid(c)||l.Instance!=c.InstanceId||l.Package is null
            ||c.Action!=PostgreSqlMaintenanceAction.ScheduledBackup&&l.Revision!=c.ExpectedRevision)
            throw new PgFailure("database.maintenance_invalid");
        if(c.Action==PostgreSqlMaintenanceAction.ScheduledBackup)
        {
            if(l.BackupSchedule is not {Enabled:true} policy || policy.Id!=c.ScheduleId || policy.NextDueAt!=c.DueAt || policy.NextDueAt>DateTimeOffset.UtcNow)
                throw new PgFailure("database.backup_schedule_changed");
            return;
        }
        if(c.Action==PostgreSqlMaintenanceAction.DeleteBackup)
        {
            if(l.Backups?.Any(x=>x.Id==c.BackupId)!=true)throw new PgFailure("database.backup_unverified");
            PgNoLinks(PgBackupPath(l,c.BackupId!.Value));return;
        }
        if(c.Schedule!.Enabled)
        {
            if(l.PackageRemoved||l.DataDeleted)throw new PgFailure("database.maintenance_invalid");
            await PgVerifyPackage(l);await PgVerifyConfiguration(l);
            if((l.Backups??[]).Count(x=>!x.Automatic)+c.Schedule.KeepLast+1>8)throw new PgFailure("database.backup_capacity");
        }
    }
    private static PgLedger PgScheduleFinished(PgLedger l,string outcome)
    {
        var now=DateTimeOffset.UtcNow;
        l=l with {BackupSchedule=l.BackupSchedule! with {NextDueAt=PostgreSqlBackupSchedulePolicy.NextDue(now,l.BackupSchedule!.IntervalHours),
            LastAttemptAt=now,LastOperationId=l.Pending!.Id,LastOutcome=outcome}};
        return PgPhase(l,"Verified");
    }
    private static async Task<PgLedger> PgContinueBackupPolicy(PgLedger l)
    {
        var command=l.Pending!.Request.Maintenance!;
        if(command.Action==PostgreSqlMaintenanceAction.ConfigureBackupSchedule)
        {
            if(l.Pending!.Phase=="Verified")return l;
            var requested=command.Schedule!;
            l=l with {BackupSchedule=new(l.Pending!.Id,requested.Enabled,requested.IntervalHours,requested.KeepLast,requested.AllowDowntime,l.Package!.Version,DateTimeOffset.UtcNow,l.BackupSchedule?.LastOperationId,l.BackupSchedule?.LastAttemptAt,l.BackupSchedule?.LastOutcome)};
            return PgPhase(l,"Verified");
        }
        if(command.Action==PostgreSqlMaintenanceAction.DeleteBackup)
        {
            if(l.Pending!.Phase=="Prepared")
            {
                if((l.BackupDeletions?.Count??0)==0)l=PgMarkBackupDeletion(l,[l.Backups!.Single(x=>x.Id==command.BackupId)]);
                l=PgPhase(l,"DeletingBackup");
            }
            l=PgDrainBackupDeletions(l);
            return PgPhase(l,"Verified");
        }
        if(l.BackupSchedule?.LastOperationId==l.Pending!.Id)return PgPhase(l,"Verified");
        var plan=PostgreSqlInitializationPlanner.Create(command.InstanceId, l.Port, PgMajor(l));
        if(l.Pending!.Phase=="Prepared")
        {
            try
            {
                if(l.PackageRemoved||l.DataDeleted||l.Package!.Version!=l.BackupSchedule!.Version)
                    return PgScheduleFinished(l,"database.backup_schedule_lifecycle");
                if(l.RemoteAccess is not null)return PgScheduleFinished(l,"database.backup_schedule_remote");
                var observed=(await PgRun("/usr/bin/systemctl",["show","-p","ActiveState","-p","MainPID",plan.ServiceName])).Output.Trim().Split('\n').Order().ToArray();
                var stopped=observed.SequenceEqual(new[]{"ActiveState=inactive","MainPID=0"});
                var running=await PgRunning(plan);
                if(!stopped&&!running)return PgScheduleFinished(l,"database.backup_schedule_running");
                if(running&&!l.BackupSchedule!.AllowDowntime)return PgScheduleFinished(l,"database.backup_schedule_running");
                if((l.Backups?.Count??0)>=8)return PgScheduleFinished(l,"database.backup_capacity");
                await PgVerifyPackage(l);await PgVerifyConfiguration(l,allowAdminMask:true);
                if(running)await PgVerifyConnection(l);
                await PgExclusiveInstance(l);
                l=l with {Pending=l.Pending with {RestartAfterBackup=l.Pending!.RestartAfterBackup||running}};PgWrite(l);
            }
            catch(PgFailure e)
            {
                // No destructive effect was dispatched. A prior isolation checkpoint still owns recovery.
                if(PgOwnMask(plan))throw;
                return PgScheduleFinished(l,e.Message);
            }
        }
        if(l.Pending!.Phase is not ("AutomaticBackupVerified" or "AutomaticRestartDispatched" or "AutomaticRestartVerified"))
        {
            l=await PgContinueMaintenance(l);
            l=PgPhase(l,"AutomaticBackupVerified");
        }
        if(l.Pending!.Phase=="AutomaticBackupVerified")
        {
            // Publish only verified backups; reclaim only automatic copies, after a new verified one exists.
            if((l.BackupDeletions?.Count??0)==0)
            {
                var obsolete=PostgreSqlBackupSchedulePolicy.Obsolete(l.Backups??[],l.Pending!.Id,l.BackupSchedule!.KeepLast);
                l=PgMarkBackupDeletion(l,obsolete);
            }
            l=PgDrainBackupDeletions(l);
            if(l.Pending!.RestartAfterBackup)
            {
                await PgVerifyPackage(l);await PgVerifyConfiguration(l);
                l=PgPhase(l,"AutomaticRestartDispatched");
                await PgRun("/usr/bin/systemctl",["start",plan.ServiceName]);
            }
            else return PgScheduleFinished(l,"database.backup_schedule_verified");
        }
        if(l.Pending!.Phase=="AutomaticRestartDispatched")
        {
            // Never replay an unknown StartUnit. Reconcile only the original owned service in the expected state.
            await PgVerifyPackage(l);await PgVerifyConnection(l);
            l=PgPhase(l,"AutomaticRestartVerified");
        }
        return PgScheduleFinished(l,"database.backup_schedule_verified");
    }
    private static PgLedger PgMarkBackupDeletion(PgLedger l,IReadOnlyList<PostgreSqlBackup> backups)
    {
        if((l.BackupDeletions?.Count??0)!=0)return l;
        if(backups.Count==0)return l;
        var ids=backups.Select(x=>x.Id).ToHashSet();
        // Persist exact ownership and remove public restore references before touching any directory.
        l=l with {BackupDeletions=backups,Backups=(l.Backups??[]).Where(x=>!ids.Contains(x.Id)).ToArray()};PgWrite(l);return l;
    }
    private static PgLedger PgDrainBackupDeletions(PgLedger l)
    {
        foreach(var backup in l.BackupDeletions??[])
        {
            PgDeleteOwnedTree(PgBackupPath(l,backup.Id),PgBackupRoot(l));
            l=l with {BackupDeletions=l.BackupDeletions!.Where(x=>x.Id!=backup.Id).ToArray()};PgWrite(l);
        }
        return l;
    }
}
