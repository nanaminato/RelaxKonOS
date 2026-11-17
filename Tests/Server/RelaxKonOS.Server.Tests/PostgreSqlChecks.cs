using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Server.Databases;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Endpoints;

internal static class PostgreSqlChecks
{
    public static async Task RunAsync()
    {
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS PostgreSQL " + label); }
        var id = Guid.NewGuid();
        var state = new PostgreSqlState(true, true, true, "16.15", 2, Guid.NewGuid(), 55432, true, true, null, null, null);
        var transport = new Transport(r => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state))));
        var adapter = new PostgreSqlComponentAdapter(transport);
        var service = new PostgreSqlInstallationService(transport, adapter);
        var remote = new PostgreSqlRemoteCommand(state.InstanceId!.Value, state.Revision, true, "192.168.10.5", "db.example.test", "appdb", "appuser", ["192.168.10.0/24"], Guid.NewGuid(), new string('A',64));
        Check(PostgreSqlRemoteValidation.Valid(remote), "scoped TLS remote intent is valid");
        foreach(var invalid in new[] { remote with { Sources = ["0.0.0.0/0"] }, remote with { Sources = ["192.168.10.1/24"] },
            remote with { ListenAddress = "0.0.0.0" }, remote with { ListenAddress = "127.0.0.1" }, remote with { Sources = ["::/0"] },
            remote with { ServerName = "db';shell" }, remote with { CertificateFingerprint = "invalid" } })
            Check(!PostgreSqlRemoteValidation.Valid(invalid), "remote access rejects broad noncanonical or injected inputs");
        Check(InstallationEndpoints.Capability(InstallationServiceId.PostgreSqlRemoteAccess) == HostElevationCapability.DatabaseRemoteAccess,
            "remote access has dedicated elevation");
        var remoteBypass=false;
        try { service.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(new PostgreSqlInstallationRequest(true,RemoteAccess:remote)),"key"); }
        catch(InstallationException) { remoteBypass=true; }
        Check(remoteBypass,"ordinary lifecycle cannot submit remote access");
        var healthy = state;
        state = state with { Managed = false, ConnectionVerified = false, ProblemCode = "database.native_failed" };
        Check((await adapter.StateAsync(default)).State?.RecoveryRequired == true, "lost administrator opens recovery rather than classifying healthy external installation");
        state = healthy;
        var dto = new InstallationOperationDto(id, InstallationServiceId.PostgreSql, InstallationOperationKind.Configure,
            InstallationOperationState.Running, InstallationStage.Configuring, null, null, DateTimeOffset.UtcNow, null, null, false);
        Check((await service.RecoverAsync(dto, default)).State == InstallationOperationState.Interrupted, "health is not an operation receipt");
        state = state with { CommittedOperationId = id };
        Check((await service.RecoverAsync(dto, default)).State == InstallationOperationState.Succeeded, "original committed receipt reconciles response loss");
        state = state with { PendingOperationId = Guid.NewGuid() };
        Check((await service.RecoverAsync(dto, default)).State == InstallationOperationState.Interrupted, "pending transaction blocks recovered success");
        var command = new PostgreSqlInstallationRequest(true, Command: new(PostgreSqlAction.Start, state.InstanceId!.Value, state.Revision, state.Port));
        Check(service.Validate(InstallationOperationKind.Configure, JsonSerializer.SerializeToElement(command), "key") is PostgreSqlInstallationRequest, "typed configuration admitted");
        foreach (var invalid in new[] { command with { Confirmed = false }, command with { RecoverOperationId = id }, command with { Command = command.Command! with { Port = 1 } }, command with { Command = command.Command! with { InstanceId = Guid.Empty } } })
        {
            var refused = false; try { service.Validate(InstallationOperationKind.Configure, JsonSerializer.SerializeToElement(invalid), "key"); } catch (InstallationException) { refused = true; }
            Check(refused, "invalid or mixed command refused");
        }
        var badJson = false; try { service.Validate(InstallationOperationKind.Configure, JsonDocument.Parse("{\"confirmed\":true,\"shell\":\"forbidden\"}").RootElement, "key"); } catch (JsonException) { badJson = true; }
        Check(badJson, "shell field rejected by strict contract");
        Check(RelaxKonOS.ManagedRuntimes.ComponentPackages.HostPackageDefinitions.Find("postgresql") is null, "generic package executor cannot bypass database transaction");
        Check(!InstallationEndpoints.CanInstall(new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "viewer")])), InstallationServiceId.PostgreSql), "viewer cannot mutate");
        Check(InstallationEndpoints.Capability(InstallationServiceId.PostgreSql) == HostElevationCapability.NativeServiceAction && InstallationEndpoints.Target(InstallationServiceId.PostgreSql) == "postgresql", "elevation is scoped to PostgreSQL");
        transport.Reply = _ => new(true, OutputBase64: "malformed");
        Check((await service.RecoverAsync(dto, default)).State == InstallationOperationState.Interrupted, "malformed helper response cannot become success");
        transport.Reply = r => r.Operation == PrivilegedOperationKind.PostgreSqlExecute ? new(false, Error: "lost response")
            : new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state with { PendingOperationId = null, CommittedOperationId = id })));
        await service.StartAsync(InstallationOperationKind.Configure, command, "test", new Progress(id), default);
        Check(true, "native response loss is reconciled by original durable receipt");
        var root = Path.Combine(Path.GetTempPath(), "rk-db02-" + Guid.NewGuid().ToString("N"));
        try
        {
            Guid? committed = null;
            transport.Reply = r =>
            {
                if (r.Operation == PrivilegedOperationKind.PostgreSqlExecute) committed = r.OperationId;
                return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state with
                    { PendingOperationId = null, CommittedOperationId = r.OperationId == committed ? committed : null })));
            };
            var environment = new EnvironmentFixture(root);
            var protection = new EphemeralDataProtectionProvider();
            var credentials = new PostgreSqlCredentialStore(environment, protection);
            var secretRequest = new PostgreSqlCredentialRequest(Guid.NewGuid(), state.InstanceId!.Value, "Fixture-Only-Secret-29!");
            Check(credentials.Stage(secretRequest, "actor").Reference == secretRequest.Reference, "credential staging returns only an opaque reference");
            Check(credentials.Stage(secretRequest, "actor").Reference == secretRequest.Reference, "credential staging is immutable and idempotent");
            Check(new PostgreSqlCredentialStore(environment, protection).Read(secretRequest.Reference, secretRequest.InstanceId, "actor") == secretRequest.Password,
                "protected candidate survives secret-store reopening");
            Check(!Directory.GetFiles(Path.Combine(root, "data", "database-secrets")).Any(file => File.ReadAllText(file).Contains(secretRequest.Password)), "credential files contain ciphertext only");
            Check(!secretRequest.ToString().Contains(secretRequest.Password), "credential diagnostic string is redacted");
            foreach (var mismatch in new[] { (Guid.NewGuid(), "actor"), (secretRequest.InstanceId, "other-actor") })
            {
                var refused = false; try { credentials.Read(secretRequest.Reference, mismatch.Item1, mismatch.Item2); } catch (InstallationException) { refused = true; }
                Check(refused, "credential reference binds instance and staging actor");
            }
            var conflict = false; try { credentials.Stage(secretRequest with { Password = "Different-Fixture-Secret-30!" }, "actor"); } catch (InstallationException) { conflict = true; }
            Check(conflict, "credential reference cannot be rebound to another password");
            var accountService = new PostgreSqlAccountsInstallationService(transport, adapter, credentials);
            var accountRequest = new PostgreSqlInstallationRequest(true, AccountCommand: new(PostgreSqlAccountAction.CreateAccount,
                secretRequest.InstanceId, state.Revision, "appdb", "appuser", PostgreSqlPermission.ReadOnly, secretRequest.Reference));
            Check(accountService.Validate(InstallationOperationKind.Configure, JsonSerializer.SerializeToElement(accountRequest), "key") is PostgreSqlInstallationRequest,
                "typed account command uses the dedicated service");
            Check(!JsonSerializer.Serialize(accountRequest).Contains(secretRequest.Password), "installation request contains no password");
            var bypass = false; try { service.Validate(InstallationOperationKind.Configure, JsonSerializer.SerializeToElement(accountRequest), "key"); } catch (InstallationException) { bypass = true; }
            Check(bypass, "ordinary service control cannot submit account mutations");
            var originalReply = transport.Reply;
            var recoverDispatched = false;
            transport.Reply = r =>
            {
                if (r.Operation == PrivilegedOperationKind.PostgreSqlRecover) recoverDispatched = true;
                return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state with
                    { PendingOperationId = id, PendingAccount = null })));
            };
            var recoverBlocked = false;
            try { await accountService.StartAsync(InstallationOperationKind.Repair, new PostgreSqlInstallationRequest(true, RecoverOperationId: id), "actor", new Progress(Guid.NewGuid()), default); }
            catch (InstallationException) { recoverBlocked = true; }
            Check(recoverBlocked && !recoverDispatched, "account authority cannot recover a pending lifecycle transaction");
            transport.Reply = originalReply;
            var adminService = new PostgreSqlAdminRecoveryInstallationService(transport, adapter);
            var adminRequest = new PostgreSqlInstallationRequest(true, AdminRecovery: new(state.InstanceId!.Value, state.Revision));
            Check(adminService.Validate(InstallationOperationKind.Configure, JsonSerializer.SerializeToElement(adminRequest), "key") is PostgreSqlInstallationRequest,
                "administrator recovery has a typed dedicated service");
            foreach (var invalid in new[] { adminRequest with { Confirmed = false }, adminRequest with { Command = command.Command },
                adminRequest with { AdminRecovery = new(Guid.Empty, 1) }, adminRequest with { RecoverOperationId = id } })
            {
                var refused = false;
                try { adminService.Validate(InstallationOperationKind.Configure, JsonSerializer.SerializeToElement(invalid), "key"); }
                catch (InstallationException) { refused = true; }
                Check(refused, "administrator recovery rejects unconfirmed invalid or mixed intent");
            }
            Check(InstallationEndpoints.Capability(InstallationServiceId.PostgreSqlAdminRecovery) == HostElevationCapability.DatabaseAdminRecovery
                && adminService.Resources.Contains("service:PostgreSql"), "administrator recovery has separate elevation and shared instance lock");
            foreach (var ordinary in new IInstallationService[] { service, accountService })
            {
                var refused = false;
                try { ordinary.Validate(InstallationOperationKind.Configure, JsonSerializer.SerializeToElement(adminRequest), "key"); }
                catch (InstallationException) { refused = true; }
                Check(refused, "ordinary service cannot submit administrator recovery");
            }
            Check(!InstallationEndpoints.CanInstall(new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "controller"), new Claim("installation:install", "postgresqlaccounts")])), InstallationServiceId.PostgreSqlAdminRecovery),
                "account controller does not acquire administrator recovery authority");
            recoverDispatched = false;
            transport.Reply = r =>
            {
                if (r.Operation == PrivilegedOperationKind.PostgreSqlRecover) recoverDispatched = true;
                return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state with
                    { PendingOperationId = id, PendingAdminRecovery = adminRequest.AdminRecovery, PendingAccount = null })));
            };
            foreach (var ordinary in new IInstallationService[] { service, accountService })
            {
                var refused = false;
                try { await ordinary.StartAsync(InstallationOperationKind.Repair, new PostgreSqlInstallationRequest(true, RecoverOperationId: id), "actor", new Progress(Guid.NewGuid()), default); }
                catch (InstallationException) { refused = true; }
                Check(refused && !recoverDispatched, "ordinary repair cannot resume administrator recovery");
            }
            transport.Reply = originalReply;
            Check(InstallationEndpoints.Capability(InstallationServiceId.PostgreSqlAccounts) == HostElevationCapability.DatabaseAccountsManage
                && accountService.Resources.Contains("service:PostgreSql"), "account authorization is separate but instance locking is shared");
            Check(!InstallationEndpoints.CanInstall(new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "controller"), new Claim("installation:install", "postgresql")])), InstallationServiceId.PostgreSqlAccounts),
                "restricted lifecycle controller does not acquire account management");
            foreach (var name in new[] { "postgres", "pg_user", "rk_owner", "BadName", "a;DROP ROLE b", "a\\connect", "a'", new string('a', 41) })
                Check(!PostgreSqlAccountValidation.Name(name), "reserved or unsafe database identifier rejected");
            Check(PostgreSqlAccountValidation.Password("Allowed-Fixture-Password!") && !PostgreSqlAccountValidation.Password("short")
                && !PostgreSqlAccountValidation.Password("non-ascii-password-密码"), "credential normalization policy is explicit");
            var maintenanceService=new PostgreSqlMaintenanceInstallationService(transport,adapter,credentials);
            var majorTarget=new ComponentPackageTarget("postgresql-18","18.7-1.pgdg24.04+2","amd64","fixture","https://apt.postgresql.org/pub/repos/apt","noble-pgdg","main","https://apt.postgresql.org/pub/repos/apt/pool/main/p/postgresql-18/fixture.deb",new string('a',64),1,[]);
            var migration=new PostgreSqlInstallationRequest(true,Maintenance:new(PostgreSqlMaintenanceAction.MigrateMajor,state.InstanceId!.Value,state.Revision,majorTarget,MigrationSourceFingerprint:"B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8"));
            Check(maintenanceService.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(migration),"key") is PostgreSqlInstallationRequest,"major migration requires a typed target and explicit signing identity");
            foreach(var bad in new[]{migration with {Maintenance=migration.Maintenance! with {Target=null}},migration with {Maintenance=migration.Maintenance! with {MigrationSourceFingerprint=null}},migration with {Maintenance=migration.Maintenance! with {MigrationSourceFingerprint="other-key"}},migration with {Maintenance=migration.Maintenance! with {Action=PostgreSqlMaintenanceAction.Update}}})
            {
                var denied=false;try{maintenanceService.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(bad),"key");}catch(InstallationException){denied=true;}
                Check(denied,"major source confirmation cannot be omitted or attached to ordinary update");
            }
            var policyService=new PostgreSqlBackupAutomationInstallationService(transport,adapter,credentials);
            foreach(var limited in new IInstallationService[]{service,accountService,adminService,policyService})
            {
                var denied=false;try{limited.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(migration),"key");}catch(InstallationException){denied=true;}
                Check(denied,"limited database authority cannot submit major migration");
            }
            var backupRequest=new PostgreSqlInstallationRequest(true,Maintenance:new(PostgreSqlMaintenanceAction.Backup,state.InstanceId!.Value,state.Revision));
            Check(maintenanceService.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(backupRequest),"key") is PostgreSqlInstallationRequest,"maintenance validates typed command");
            Check(InstallationEndpoints.Capability(InstallationServiceId.PostgreSqlMaintenance)==HostElevationCapability.DatabaseMaintenance,"maintenance requires its own elevation");
            foreach(var ordinary in new IInstallationService[]{service,accountService,adminService})
            {
                var refused=false;try{ordinary.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(backupRequest),"key");}catch(InstallationException){refused=true;}
                Check(refused,"ordinary authority cannot submit maintenance");
            }
            foreach(var bad in new[]{backupRequest with {Command=command.Command},backupRequest with {Maintenance=backupRequest.Maintenance! with {Action=PostgreSqlMaintenanceAction.UninstallDeleteData}},backupRequest with {Maintenance=backupRequest.Maintenance! with {Action=PostgreSqlMaintenanceAction.Update}}})
            {
                var refused=false;try{maintenanceService.Validate(InstallationOperationKind.Configure,JsonSerializer.SerializeToElement(bad),"key");}catch(InstallationException){refused=true;}
                Check(refused,"maintenance rejects mixed intent, unconfirmed deletion and missing target");
            }
            recoverDispatched=false;
            transport.Reply=r=>{if(r.Operation==PrivilegedOperationKind.PostgreSqlRecover)recoverDispatched=true;return new(true,OutputBase64:Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state with {PendingOperationId=id,PendingMaintenance=backupRequest.Maintenance})));};
            foreach(var ordinary in new IInstallationService[]{service,accountService,adminService})
            {
                var refused=false;try{await ordinary.StartAsync(InstallationOperationKind.Repair,new PostgreSqlInstallationRequest(true,RecoverOperationId:id),"actor",new Progress(Guid.NewGuid()),default);}catch(InstallationException){refused=true;}
                Check(refused&&!recoverDispatched,"ordinary recovery cannot resume maintenance");
            }
            transport.Reply=originalReply;
            var deletionReference=Guid.NewGuid();var unrelatedReference=Guid.NewGuid();var unrelatedInstance=Guid.NewGuid();
            credentials.Stage(new(deletionReference,state.InstanceId.Value,"Fixture-Delete-Password!"),"actor");
            credentials.Stage(new(unrelatedReference,unrelatedInstance,"Fixture-Other-Password!"),"actor");
            credentials.DeleteInstance(state.InstanceId.Value);credentials.DeleteInstance(state.InstanceId.Value);
            var deleted=false;try{credentials.Read(deletionReference,state.InstanceId.Value);}catch(InstallationException){deleted=true;}
            Check(deleted&&credentials.Read(unrelatedReference,unrelatedInstance)=="Fixture-Other-Password!","credential removal is instance-scoped and idempotent");
            transport.Reply=r=>r.Operation==PrivilegedOperationKind.PostgreSqlExecute?new(false,Error:"database.backup_identity_changed"):originalReply(r);
            var preflightFailed=false;
            try{await maintenanceService.StartAsync(InstallationOperationKind.Configure,backupRequest,"actor",new Progress(Guid.NewGuid()),default);}
            catch(InstallationException ex){preflightFailed=ex.ProblemCode=="database.backup_identity_changed";}
            Check(preflightFailed,"maintenance preflight rejection does not invent a pending native transaction");
            transport.Reply=originalReply;
            var staleSecret=Guid.NewGuid();credentials.Stage(new(staleSecret,state.InstanceId.Value,"Fixture-Tombstone-Secret!"),"actor");
            var reinstallDispatched=false;var cleanedBeforeReinstall=false;
            transport.Reply=r=>
            {
                if(r.Operation==PrivilegedOperationKind.PostgreSqlExecute)
                {
                    try{credentials.Read(staleSecret,state.InstanceId.Value);}catch(InstallationException){cleanedBeforeReinstall=true;}
                    reinstallDispatched=true;return originalReply(r);
                }
                return reinstallDispatched?originalReply(r):new(true,OutputBase64:Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state with {PendingOperationId=null,DataDeleted=true,PackageRemoved=true,Installed=false})));
            };
            await maintenanceService.StartAsync(InstallationOperationKind.Configure,new PostgreSqlInstallationRequest(true,Maintenance:new(PostgreSqlMaintenanceAction.Reinstall,state.InstanceId.Value,state.Revision,
                new("postgresql-16","16.15-0ubuntu0.24.04.1","amd64","fixture","http://archive.ubuntu.com/ubuntu","noble","main","http://archive.ubuntu.com/ubuntu/pool/fixture.deb",new string('A',64),1,[]))),"actor",new Progress(Guid.NewGuid()),default);
            Check(cleanedBeforeReinstall,"reinstall clears interrupted Core credential cleanup before native tombstone removal");
            transport.Reply=originalReply;
            var store = new InstallationOperationStore(environment);
            var coordinator = new InstallationCoordinator(store, [service], new LifetimeFixture());
            await coordinator.StartAsync(default);
            var options = JsonSerializer.SerializeToElement(command);
            var accepted = coordinator.Start(InstallationServiceId.PostgreSql, InstallationOperationKind.Configure, options, "actor", "original-key");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (coordinator.Get(accepted.OperationId)!.Operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
            { if (DateTime.UtcNow > deadline) throw new Exception("coordinator timeout"); await Task.Delay(10); }
            Check(coordinator.Get(accepted.OperationId)!.Operation.State == InstallationOperationState.Succeeded, "shared coordinator records verified completion");
            Check(coordinator.Start(InstallationServiceId.PostgreSql, InstallationOperationKind.Configure, options, "actor", "original-key").OperationId == accepted.OperationId,
                "shared coordinator keeps original idempotency identity");
            var reopened = new InstallationOperationStore(environment);
            Check(reopened.Read().Single().Operation.State == InstallationOperationState.Succeeded, "Core ledger survives reopening");
            await coordinator.StopAsync(default);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class EnvironmentFixture(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PostgreSqlChecks";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class LifetimeFixture : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    private sealed class Progress(Guid id) : IInstallationProgress
    { public Guid OperationId => id; public Task ReportAsync(InstallationProgress progress, CancellationToken ct = default) => Task.CompletedTask; }
    private sealed class Transport(Func<PrivilegedOperationRequest, PrivilegedOperationResult> reply) : IPrivilegedOperationTransport
    {
        public Func<PrivilegedOperationRequest, PrivilegedOperationResult> Reply = reply;
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest r, CancellationToken ct = default) => Task.FromResult(Reply(r));
    }
}
