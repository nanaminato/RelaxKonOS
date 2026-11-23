using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Databases;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.ServerModule.Database.Contracts;

internal static class PostgreSqlBackupAutomationChecks
{
    public static async Task RunAsync()
    {
        var count=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS PostgreSQL automation "+label);count++;}
        var executionAssembly = typeof(PostgreSqlBackupScheduler).Assembly;
        Check(executionAssembly == typeof(PostgreSqlBackupScheduleHost).Assembly,
            "scheduler belongs to the Core lifetime that owns durable backup dispatch");
        var root=Path.Combine(Path.GetTempPath(),"rk-db07-"+Guid.NewGuid().ToString("N"));
        var env=new EnvironmentFixture(root);var clock=new Clock();
        var policy=new PostgreSqlBackupSchedule(Guid.NewGuid(),true,24,2,false,"16.15",clock.Now);
        var state=new PostgreSqlState(true,true,true,"16.15",10,Guid.NewGuid(),55432,false,false,null,null,null,BackupSchedule:policy);
        var dispatched=new List<Guid>();var fail=false;
        var transport=new Transport(r=>
        {
            if(r.Operation==PrivilegedOperationKind.PostgreSqlExecute)
            {
                dispatched.Add(r.OperationId!.Value);
                if(fail)return new(false,Error:"database.backup_changed");
                state=state with {CommittedOperationId=r.OperationId};
            }
            return new(true,OutputBase64:Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state with {CommittedOperationId=r.OperationId==state.CommittedOperationId?r.OperationId:null})));
        });
        var adapter=new PostgreSqlComponentAdapter(transport);var credentials=new PostgreSqlCredentialStore(env,new EphemeralDataProtectionProvider());
        var service=new PostgreSqlBackupAutomationInstallationService(transport,adapter,credentials);
        var normal=new PostgreSqlMaintenanceInstallationService(transport,adapter,credentials);
        var configuration=new PostgreSqlInstallationRequest(true,Maintenance:new(PostgreSqlMaintenanceAction.ConfigureBackupSchedule,state.InstanceId!.Value,state.Revision,Schedule:new(true,24,2,false)));
        Check(service.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(configuration),"key") is PostgreSqlInstallationRequest,"typed ongoing grant admitted by dedicated service");
        foreach(var request in new[]{configuration,PostgreSqlBackupSchedulePolicy.Request(state.InstanceId.Value,policy)})
        {
            var rejected=false;try{normal.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(request),"key");}catch(InstallationException){rejected=true;}
            Check(rejected,"ordinary maintenance cannot configure or dispatch a schedule");
        }
        foreach(var request in new[]{configuration with {Maintenance=configuration.Maintenance! with {Schedule=new(true,0,2,false)}},configuration with {Maintenance=configuration.Maintenance! with {Schedule=new(true,24,0,false)}}})
            Check(!PostgreSqlMaintenanceValidation.Valid(request.Maintenance!),"invalid cadence or retention rejected");
        Check(RelaxKonOS.Server.Endpoints.InstallationEndpoints.Capability(InstallationServiceId.PostgreSqlBackupAutomation)==HostElevationCapability.DatabaseBackupAutomation,"ongoing authority is separately elevated");
        var current=new PostgreSqlBackup(Guid.NewGuid(),"16.15",clock.Now,"digest",true,true);
        var future=new PostgreSqlBackup(Guid.NewGuid(),"16.15",clock.Now.AddDays(3),"digest",true,true);
        var manual=current with {Id=Guid.NewGuid(),Automatic=false};
        Check(PostgreSqlBackupSchedulePolicy.Obsolete([future,current,manual],current.Id,1).Single().Id==future.Id,"clock rollback cannot evict the just-verified copy or a manual copy");
        var proofRequired=false;try{PostgreSqlBackupSchedulePolicy.Obsolete([future,manual],current.Id,1);}catch(InvalidOperationException){proofRequired=true;}
        Check(proofRequired,"retention cannot run without a newly verified automatic copy");
        var coordinator=new InstallationCoordinator(new(env),[service],new Lifetime());await coordinator.StartAsync(default);
        var backupHost = new PostgreSqlBackupScheduleHost(transport, coordinator, clock);
        using var scheduler=new PostgreSqlBackupScheduler(backupHost,NullLogger<PostgreSqlBackupScheduler>.Instance,clock);
        try
        {
            state=state with {BackupSchedule=policy with {Enabled=false}};
            Check(await scheduler.TickAsync(default) is null&&dispatched.Count==0,"disabled grant never dispatches");
            state=state with {BackupSchedule=policy with {NextDueAt=clock.Now.AddHours(1)}};
            Check(await scheduler.TickAsync(default) is null,"future slot and backward clock do not dispatch");
            state=state with {BackupSchedule=policy,PendingOperationId=Guid.NewGuid()};
            Check(await scheduler.TickAsync(default) is null,"other native pending work blocks scheduled dispatch");
            state=state with {PendingOperationId=null};
            Check(await backupHost.TryStartScheduledOperationAsync(state.InstanceId.Value, Guid.NewGuid(),
                policy.NextDueAt, default) is null && dispatched.Count == 0, "Core refuses stale schedule identity");
            var first=await scheduler.TickAsync(default);await Complete(coordinator,first!.OperationId);
            Check(coordinator.Get(first.OperationId)!.Operation.State==InstallationOperationState.Succeeded,"shared queue commits helper receipt");
            state=state with {Revision=99};
            var repeat=await scheduler.TickAsync(default);
            Check(repeat!.OperationId==first.OperationId&&dispatched.Count==1,"same UTC slot retains operation identity across ordinary revisions");
            Check(PostgreSqlObservation.Capture(state, backupHost).ToPublicState().ScheduledOperation?.OperationId==first.OperationId,
                "client projection exposes the scheduled operation through the read-only ledger");
            Check(PostgreSqlBackupSchedulePolicy.NextDue(clock.Now.AddDays(8),24)==clock.Now.AddDays(9),"missed intervals coalesce without a backlog");
            await coordinator.StopAsync(default);
            coordinator=new(new(env),[service],new Lifetime());await coordinator.StartAsync(default);
            using var reopened=new PostgreSqlBackupScheduler(new PostgreSqlBackupScheduleHost(transport, coordinator, clock),NullLogger<PostgreSqlBackupScheduler>.Instance,clock);
            Check((await reopened.TickAsync(default))!.OperationId==first.OperationId&&dispatched.Count==1,"reopened Core ledger does not duplicate accepted slot");
            fail=true;state=state with {BackupSchedule=policy with {Id=Guid.NewGuid()}};
            var failed=await reopened.TickAsync(default);await Complete(coordinator,failed!.OperationId);
            var again=await reopened.TickAsync(default);
            Check(again!.OperationId==failed.OperationId&&dispatched.Count==2,"failed slot is visible and never bypassed by automatic retries");
            state=state with {PendingOperationId=Guid.NewGuid(),PendingMaintenance=PostgreSqlBackupSchedulePolicy.Request(state.InstanceId.Value,policy).Maintenance};
            var denied=false;try{await normal.StartAsync(InstallationOperationKind.Repair,new PostgreSqlInstallationRequest(true,RecoverOperationId:state.PendingOperationId),"actor",new Progress(),default);}catch(InstallationException){denied=true;}
            Check(denied,"ordinary recovery cannot consume ongoing backup authority");
        }
        finally{await coordinator.StopAsync(default);if(Directory.Exists(root))Directory.Delete(root,true);}
        Console.WriteLine("PostgreSQL automation checks: "+count);
    }
    private static async Task Complete(InstallationCoordinator coordinator,Guid id)
    {
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(coordinator.Get(id)!.Operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
        {if(DateTime.UtcNow>deadline)throw new Exception("queue timeout");await Task.Delay(10);}
    }
    private sealed class Clock:TimeProvider{public DateTimeOffset Now=new(2026,11,17,1,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class EnvironmentFixture(string root):IHostEnvironment
    {public string EnvironmentName{get;set;}="Test";public string ApplicationName{get;set;}="DB07";public string ContentRootPath{get;set;}=root;public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();}
    private sealed class Lifetime:IHostApplicationLifetime{public CancellationToken ApplicationStarted=>default;public CancellationToken ApplicationStopping=>default;public CancellationToken ApplicationStopped=>default;public void StopApplication(){}}
    private sealed class Transport(Func<PrivilegedOperationRequest,PrivilegedOperationResult> reply):IPrivilegedOperationTransport
    {public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request,CancellationToken ct=default)=>Task.FromResult(reply(request));}
    private sealed class Progress:IInstallationProgress{public Guid OperationId=>Guid.NewGuid();public Task ReportAsync(InstallationProgress progress,CancellationToken ct=default)=>Task.CompletedTask;}
}
