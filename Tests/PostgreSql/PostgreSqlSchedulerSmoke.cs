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
internal static class PostgreSqlSchedulerSmoke
{
    public static async Task RunAsync()
    {
        if(!OperatingSystem.IsLinux()||File.ReadAllText("/etc/relaxkonos-db02-disposable").Trim()!="DB-02")throw new Exception("Disposable Linux fixture required");
        var count=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS Native scheduler "+label);count++;}
        var transport=new Native();var component=new PostgreSqlComponentAdapter(transport);
        var environment=new EnvironmentFixture();var credentials=new PostgreSqlCredentialStore(environment,new EphemeralDataProtectionProvider());
        var service=new PostgreSqlBackupAutomationInstallationService(transport,component,credentials);
        var coordinator=new InstallationCoordinator(new(environment),[service],new Lifetime());await coordinator.StartAsync(default);
        using var scheduler=new PostgreSqlBackupScheduler(new PostgreSqlBackupScheduleHost(transport, coordinator),NullLogger<PostgreSqlBackupScheduler>.Instance);
        try
        {
            var before=await component.ReadAsync(default);Check(before.Running&&before.PendingOperationId is null,"owned instance starts running");
            var configure=new PostgreSqlInstallationRequest(true,Maintenance:new(PostgreSqlMaintenanceAction.ConfigureBackupSchedule,before.InstanceId!.Value,before.Revision,Schedule:new(true,24,2,true)));
            var grant=coordinator.Start(InstallationServiceId.PostgreSqlBackupAutomation,InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(configure),"fixture-admin",Guid.NewGuid().ToString("N"));
            await Wait(coordinator,grant.OperationId);Check(coordinator.Get(grant.OperationId)!.Operation.State==InstallationOperationState.Succeeded,"configuration uses real service and root policy receipt");
            await scheduler.StartAsync(default);
            var deadline=DateTime.UtcNow.AddMinutes(2);PostgreSqlState after;
            do
            {
                after=await component.ReadAsync(default);
                if(after.BackupSchedule?.LastOperationId is {} last&&last!=before.BackupSchedule?.LastOperationId)break;
                if(DateTime.UtcNow>deadline)throw new Exception("real scheduler timeout");await Task.Delay(100);
            }while(true);
            var operation=after.BackupSchedule!.LastOperationId!.Value;await Wait(coordinator,operation);
            Check(coordinator.Get(operation)!.Operation.State==InstallationOperationState.Succeeded&&after.BackupSchedule.LastOutcome=="database.backup_schedule_verified","background worker and shared queue complete real verified backup");
            Check(after.Running&&after.ConnectionVerified,"authorized downtime restores native running state");
            Check(after.Backups!.Any(x=>x.Id==operation&&x.Automatic&&x.RestoreVerified),"root backup and server receipt share operation identity");
            Check(await scheduler.TickAsync(default) is null,"completed slot is not immediately repeated");
            await scheduler.StopAsync(default);await coordinator.StopAsync(default);
            coordinator=new(new(environment),[service],new Lifetime());await coordinator.StartAsync(default);
            Check(coordinator.Get(operation)!.Operation.State==InstallationOperationState.Succeeded,"real Core installation ledger survives reopening");
            var latest=await component.ReadAsync(default);
            var disable=new PostgreSqlInstallationRequest(true,Maintenance:new(PostgreSqlMaintenanceAction.ConfigureBackupSchedule,latest.InstanceId!.Value,latest.Revision,Schedule:new(false,24,2,false)));
            var revoked=coordinator.Start(InstallationServiceId.PostgreSqlBackupAutomation,InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(disable),"fixture-admin",Guid.NewGuid().ToString("N"));
            await Wait(coordinator,revoked.OperationId);Check((await component.ReadAsync(default)).BackupSchedule?.Enabled==false,"explicit policy revocation reaches real root authority");
        }
        finally{await scheduler.StopAsync(default);await coordinator.StopAsync(default);}
        Console.WriteLine("Native scheduler checks: "+count);
    }
    public static async Task DeleteInstanceAsync()
    {
        if (File.ReadAllText("/etc/relaxkonos-db07c-disposable").Trim() != "DB-07C")
            throw new InvalidOperationException("Disposable instance fixture required");
        var transport = new Native();
        var component = new PostgreSqlComponentAdapter(transport);
        var primary = await component.ReadAsync(default);
        var secondaryId = primary.Instances!.Single(x => x.Id != primary.InstanceId).Id;
        var secondary = await component.ReadAsync(default, instance: secondaryId);
        var environment = new EnvironmentFixture { ContentRootPath = "/var/lib/relaxkonos/db07c-delete-acceptance-" + Guid.NewGuid().ToString("N") };
        var credentials = new PostgreSqlCredentialStore(environment, new EphemeralDataProtectionProvider());
        const string password = "Disposable-Acceptance-Password!";
        foreach (var cluster in new[] { primary, secondary })
            credentials.Stage(new(cluster.Accounts!.Single().CredentialReference, cluster.InstanceId!.Value, password), "fixture");

        var service = new PostgreSqlMaintenanceInstallationService(transport, component, credentials);
        var coordinator = new InstallationCoordinator(new(environment), [service], new Lifetime());
        await coordinator.StartAsync(default);
        try
        {
            var command = new PostgreSqlMaintenanceCommand(PostgreSqlMaintenanceAction.DeleteInstance,
                secondaryId, secondary.Revision, DeleteConfirmation: secondaryId.ToString("N"));
            var operation = coordinator.Start(service.Id, InstallationOperationKind.Configure,
                JsonSerializer.SerializeToElement(new PostgreSqlInstallationRequest(true, Maintenance: command)),
                "fixture", Guid.NewGuid().ToString("N"));
            await Wait(coordinator, operation.OperationId);
            if (coordinator.Get(operation.OperationId)!.Operation.State != InstallationOperationState.Succeeded)
                throw new InvalidOperationException("Instance deletion failed: " + coordinator.Get(operation.OperationId)!.Operation.ProblemCode);

            var removed = false;
            try { credentials.Read(secondary.Accounts!.Single().CredentialReference, secondaryId); }
            catch (InstallationException) { removed = true; }
            if (!removed || credentials.Read(primary.Accounts!.Single().CredentialReference, primary.InstanceId!.Value) != password)
                throw new InvalidOperationException("Credentials were not cleaned by instance");
            Console.WriteLine("PASS real Server queue removes only secondary credentials");

            var receipt = await component.ReadAsync(default, operation.OperationId);
            if (!receipt.DataDeleted || receipt.PackageRemoved || receipt.InstanceId != secondaryId
                || receipt.CommittedOperationId != operation.OperationId || receipt.Accounts!.Count != 0 || receipt.Backups!.Count != 0)
                throw new InvalidOperationException("Deletion receipt is not scoped");
            if ((await service.RecoverAsync(coordinator.Get(operation.OperationId)!.Operation, default)).State != InstallationOperationState.Succeeded)
                throw new InvalidOperationException("Deletion receipt did not reconcile");
            Console.WriteLine("PASS secondary tombstone reconciles without removing shared package");
        }
        finally { await coordinator.StopAsync(default); }
    }

    private static async Task Wait(InstallationCoordinator coordinator,Guid id)
    {
        var deadline=DateTime.UtcNow.AddMinutes(2);
        while(coordinator.Get(id)?.Operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
        {if(DateTime.UtcNow>deadline)throw new Exception("queue timeout");await Task.Delay(50);}
    }
    private sealed class Native:IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request,CancellationToken ct=default)
        {
            var id=request.OperationId??Guid.NewGuid();request=request with {OperationId=id,Correlation=RelaxKonOS.Protocol.Observability.CorrelationContext.Create(id,"privileged.operation")};
            return PrivilegedOperationExecutor.ExecuteAsync(request,new([],[],[],[]));
        }
    }
    private sealed class EnvironmentFixture:IHostEnvironment
    {public string EnvironmentName{get;set;}="Test";public string ApplicationName{get;set;}="DB07";public string ContentRootPath{get;set;}="/var/lib/relaxkonos/db07-scheduler-acceptance";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();}
    private sealed class Lifetime:IHostApplicationLifetime{public CancellationToken ApplicationStarted=>default;public CancellationToken ApplicationStopping=>default;public CancellationToken ApplicationStopped=>default;public void StopApplication(){}}
}
