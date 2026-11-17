using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
namespace RelaxKonOS.Client.Apps.Databases;
public sealed partial class PostgreSqlMaintenanceViewModel(IPostgreSqlClient client, InstallationTaskViewModel installation):ObservableObject,IDisposable
{
    public InstallationTaskViewModel Installation=>installation;
    public InstallationTaskViewModel? ScheduleInstallation {get;set;}
    public Func<PostgreSqlBackupScheduleRequest,Task<bool>>? ConfirmScheduleAsync {get;set;}
    private PostgreSqlInstallationRequest? policyPending;
    [ObservableProperty] private bool scheduleEnabled;
    [ObservableProperty] private int intervalHours=24;
    [ObservableProperty] private int keepLast=3;
    [ObservableProperty] private bool allowDowntime;
    public string NextDue=>Snapshot?.BackupSchedule is {Enabled:true} policy?policy.NextDueAt.ToLocalTime().ToString("g"):"—";
    public LocalizedStatus ScheduleOutcome=>LocalizedStatus.Key(Snapshot?.BackupSchedule?.LastOutcome??"database.backup_schedule_no_run");
    public string ScheduledTask=>Snapshot?.ScheduledOperation is {} operation?$"{operation.OperationId} · {LocalizedText.Get("installation.stage."+operation.Stage)}":"—";
    public bool CanConfigureSchedule=> (Snapshot?.Instances?.FirstOrDefault()?.Id == Snapshot?.InstanceId || Snapshot?.Instances is null)&&!invalid&&!Busy&&CanManage&&!installation.IsActive&&ScheduleInstallation is {IsActive:false}&&OtherBusy?.Invoke()!=true&&!HasPending
        &&Snapshot is {Supported:true,Managed:true,InstanceId:not null,PendingOperationId:null};
    public bool CanRecoverSchedule=>!invalid&&!Busy&&CanManage&&!installation.IsActive&&ScheduleInstallation is {IsActive:false}&&OtherBusy?.Invoke()!=true&&!HasPending
        &&Snapshot?.PendingMaintenance?.Action is PostgreSqlMaintenanceAction.ConfigureBackupSchedule or PostgreSqlMaintenanceAction.ScheduledBackup;
    public bool CanResumeSchedule=>!invalid&&!Busy&&CanManage&&ScheduleInstallation is {IsActive:false}&&policyPending is not null;
    public bool CanDeleteBackup=>Ready&&SelectedBackup is not null;

    public Func<PostgreSqlMaintenanceCommand?,Task<bool>>? ConfirmAsync {get;set;}
    public Func<bool>? OtherBusy {get;set;}
    public bool CanManage {get;set;}
    private bool invalid;
    private bool migrationPlan;
    [ObservableProperty] private bool migrationSourceAccepted;
    private bool SharedPackageWritable => Snapshot?.Instances?.Count is not > 1;
    public bool CanMigrate=>SharedPackageWritable&&CanBackup&&HasPlan&&migrationPlan&&MigrationSourceAccepted&&Snapshot!.Version=="16.15-0ubuntu0.24.04.1"&&Plan!.Candidate!.PackageName=="postgresql-18";
    public string PlanDetails=>Plan?.Candidate is {} target?target.BaseUri+" · "+target.Suite+"\n"+string.Join("\n",target.AlsoInstalls.Select(x=>Uri.UnescapeDataString(x.FileName))):"";
    partial void OnMigrationSourceAcceptedChanged(bool value)=>Notify();
    private PostgreSqlInstallationRequest? pending;
    [ObservableProperty] private PostgreSqlState? snapshot;
    [ObservableProperty] private PostgreSqlState? plan;
    [ObservableProperty] private PostgreSqlBackup? selectedBackup;
    [ObservableProperty] private string deleteConfirmation="";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private LocalizedStatus notice=LocalizedStatus.Literal("");
    public bool HasPending=>pending is not null||policyPending is not null;
    public IEnumerable<PostgreSqlBackup> Backups=>Snapshot?.Backups??[];
    public string Candidate=>Plan?.Candidate?.Version??"—";
    private bool Ready=>!invalid&&!Busy&&CanManage&&!installation.IsActive&&ScheduleInstallation?.IsActive!=true&&OtherBusy?.Invoke()!=true&&!HasPending&&Snapshot is {Supported:true,Managed:true,InstanceId:not null,PendingOperationId:null,RemoteAccess:null};
    public bool CanBackup=>Ready&&Snapshot is {Installed:true,ProblemCode:null};
    public bool CanRestore=>CanBackup&&SelectedBackup is {RestoreVerified:true} b&&b.Version==Snapshot!.Version;
    private bool HasPlan=>Plan?.Revision==Snapshot?.Revision&&Plan?.Candidate is not null;
    public bool CanUpdate=>SharedPackageWritable&&CanBackup&&HasPlan&&!migrationPlan&&Plan!.Candidate!.Version!=Snapshot!.Version;
    public bool CanRepair=>SharedPackageWritable&&Ready&&Snapshot?.Installed==true&&HasPlan&&!migrationPlan&&Plan!.Candidate!.Version==Snapshot.Version;
    public bool CanReinstall=>SharedPackageWritable&&Ready&&Snapshot?.PackageRemoved==true&&HasPlan&&!migrationPlan;
    public bool CanKeepData=>SharedPackageWritable&&CanBackup;
    public bool CanDelete=>SharedPackageWritable&&CanBackup&&DeleteConfirmation==Snapshot!.InstanceId!.Value.ToString("N");
    public bool CanRecover=>!invalid&&!Busy&&CanManage&&!installation.IsActive&&ScheduleInstallation?.IsActive!=true&&OtherBusy?.Invoke()!=true&&!HasPending&&Snapshot?.PendingMaintenance is {Action:not (PostgreSqlMaintenanceAction.ConfigureBackupSchedule or PostgreSqlMaintenanceAction.ScheduledBackup)};
    public bool CanResume=>!invalid&&!Busy&&CanManage&&!installation.IsActive&&pending is not null;
    partial void OnSnapshotChanged(PostgreSqlState? value)
    {
        migrationPlan=false;MigrationSourceAccepted=false;Plan=null;var policy=value?.BackupSchedule;ScheduleEnabled=policy?.Enabled??false;IntervalHours=policy?.IntervalHours??24;KeepLast=policy?.KeepLast??3;AllowDowntime=policy?.AllowDowntime??false;Notify();
    }
    partial void OnPlanChanged(PostgreSqlState? value)=>Notify();
    partial void OnBusyChanged(bool value)=>Notify();
    partial void OnDeleteConfirmationChanged(string value)=>Notify();
    partial void OnSelectedBackupChanged(PostgreSqlBackup? value)=>Notify();
    public void Notify(){foreach(var name in new[]{nameof(CanMigrate),nameof(CanKeepData),nameof(PlanDetails),nameof(CanConfigureSchedule),nameof(CanRecoverSchedule),nameof(CanResumeSchedule),nameof(CanDeleteBackup),nameof(NextDue),nameof(ScheduleOutcome),nameof(ScheduledTask),nameof(HasPending),nameof(Backups),nameof(Candidate),nameof(CanBackup),nameof(CanRestore),nameof(CanUpdate),nameof(CanRepair),nameof(CanReinstall),nameof(CanDelete),nameof(CanRecover),nameof(CanResume)})OnPropertyChanged(name);}
    [RelayCommand] private async Task PlanAsync()
    {
        if(!Ready)return;Busy=true;
        try{var result=await client.MaintenancePlanAsync(default);if(!invalid){migrationPlan=false;Plan=result;Notice=LocalizedStatus.Literal("");}}
        catch(InstallationApiException error){Notice=LocalizedStatus.Key(error.ProblemCode);}
        catch{Notice=LocalizedStatus.Key("database.maintenance_plan_failed");}
        finally{Busy=false;}
    }
    [RelayCommand] private async Task MigrationPlanAsync()
    {
        if(!Ready)return;Busy=true;MigrationSourceAccepted=false;
        try{var result=await client.MigrationPlanAsync(default);if(!invalid){migrationPlan=true;Plan=result;Notice=LocalizedStatus.Literal("");}}
        catch(InstallationApiException error){Notice=LocalizedStatus.Key(error.ProblemCode);}
        catch{Notice=LocalizedStatus.Key("database.maintenance_plan_failed");}
        finally{Busy=false;}
    }
    [RelayCommand] private Task MigrateAsync()=>Submit(PostgreSqlMaintenanceAction.MigrateMajor,CanMigrate);
    [RelayCommand] private async Task SaveScheduleAsync()
    {
        if(!CanConfigureSchedule)return;
        var policy=new PostgreSqlBackupScheduleRequest(ScheduleEnabled,IntervalHours,KeepLast,AllowDowntime);
        if(IntervalHours is <1 or >168||KeepLast is <1 or >6){Notice=LocalizedStatus.Key("database.backup_schedule_invalid");return;}
        var before=Snapshot!;Busy=true;
        try{if(ConfirmScheduleAsync is null||!await ConfirmScheduleAsync(policy)||invalid)return;
            policyPending=new(true,Maintenance:new(PostgreSqlMaintenanceAction.ConfigureBackupSchedule,before.InstanceId!.Value,before.Revision,Schedule:policy));await SendPolicy();}
        finally{Busy=false;}
    }
    [RelayCommand] private async Task RecoverScheduleAsync()
    {
        if(!CanRecoverSchedule)return;var original=Snapshot!.PendingOperationId;Busy=true;
        try{if(ConfirmAsync is null||!await ConfirmAsync(null)||invalid)return;policyPending=new(true,RecoverOperationId:original);await SendPolicy();}finally{Busy=false;}
    }
    [RelayCommand] private async Task ResumeScheduleAsync(){if(!CanResumeSchedule)return;Busy=true;try{await SendPolicy();}finally{Busy=false;}}
    private async Task SendPolicy()
    {
        var task=ScheduleInstallation!;var previous=task.Operation?.OperationId;
        await task.SubmitAsync(policyPending!.RecoverOperationId is null?InstallationOperationKind.Configure:InstallationOperationKind.Repair,policyPending);
        if(!task.HasPendingSubmission||task.Operation?.OperationId is {} accepted&&accepted!=previous)policyPending=null;Notify();
    }
    [RelayCommand] private Task DeleteBackupAsync()=>Submit(PostgreSqlMaintenanceAction.DeleteBackup,CanDeleteBackup);
    [RelayCommand] private Task UpdateAsync()=>Submit(PostgreSqlMaintenanceAction.Update,CanUpdate);
    [RelayCommand] private Task RepairAsync()=>Submit(PostgreSqlMaintenanceAction.Repair,CanRepair);
    [RelayCommand] private Task BackupAsync()=>Submit(PostgreSqlMaintenanceAction.Backup,CanBackup);
    [RelayCommand] private Task RestoreAsync()=>Submit(PostgreSqlMaintenanceAction.Restore,CanRestore);
    [RelayCommand] private Task ReinstallAsync()=>Submit(PostgreSqlMaintenanceAction.Reinstall,CanReinstall);
    [RelayCommand] private Task KeepDataAsync()=>Submit(PostgreSqlMaintenanceAction.UninstallKeepData,CanKeepData);
    [RelayCommand] private Task DeleteDataAsync()=>Submit(PostgreSqlMaintenanceAction.UninstallDeleteData,CanDelete);
    private async Task Submit(PostgreSqlMaintenanceAction action,bool allowed)
    {
        if(!allowed)return;
        var command=new PostgreSqlMaintenanceCommand(action,Snapshot!.InstanceId!.Value,Snapshot.Revision,
            action is PostgreSqlMaintenanceAction.Update or PostgreSqlMaintenanceAction.Repair or PostgreSqlMaintenanceAction.Reinstall or PostgreSqlMaintenanceAction.MigrateMajor?Plan!.Candidate:null,
            action is PostgreSqlMaintenanceAction.Restore or PostgreSqlMaintenanceAction.DeleteBackup?SelectedBackup!.Id:null,
            action==PostgreSqlMaintenanceAction.UninstallDeleteData?DeleteConfirmation:null,
            MigrationSourceFingerprint:action==PostgreSqlMaintenanceAction.MigrateMajor?"B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8":null);
        Busy=true;
        try{if(ConfirmAsync is null||!await ConfirmAsync(command)||invalid)return;pending=new(true,Maintenance:command);await Send();}finally{Busy=false;}
    }
    [RelayCommand] private async Task RecoverAsync()
    {
        if(!CanRecover)return;var original=Snapshot!.PendingOperationId;Busy=true;
        try{if(ConfirmAsync is null||!await ConfirmAsync(null)||invalid)return;pending=new(true,RecoverOperationId:original);await Send();}finally{Busy=false;}
    }
    [RelayCommand] private async Task ResumeAsync(){if(!CanResume)return;Busy=true;try{await Send();}finally{Busy=false;}}
    private async Task Send()
    {
        var previous=installation.Operation?.OperationId;
        await installation.SubmitAsync(pending!.RecoverOperationId is null?InstallationOperationKind.Configure:InstallationOperationKind.Repair,pending);
        if(!installation.HasPendingSubmission||installation.Operation?.OperationId is {} accepted&&accepted!=previous)pending=null;
        Notify();
    }
    public void Invalidate(){invalid=true;Snapshot=null;pending=null;policyPending=null;Notify();}
    public void Dispose(){Invalidate();installation.Dispose();ScheduleInstallation?.Dispose();}
}
