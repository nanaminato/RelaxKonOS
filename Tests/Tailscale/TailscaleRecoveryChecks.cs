using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Tailscale;

internal static class TailscaleRecoveryChecks
{
    internal static async Task RunAsync()
    {
        var count=0;
        void Check(bool value) { if(!value) throw new Exception("Tailscale recovery assertion " + count); count++; }
        async Task Reject(Func<Task> call,int code,string problem)
        {
            try { await call(); throw new Exception("Expected recovery rejection: " + problem); }
            catch(TailscaleControlException ex) { Check(ex.StatusCode==code && ex.ProblemCode==problem); }
        }
        var folder=Directory.CreateTempSubdirectory("relaxkonos-tailscale-recovery-");
        try
        {
            var helperRoot=Path.Combine(folder.FullName,"helper");Directory.CreateDirectory(helperRoot);
            var native=new Native();
            var engine=new TailscaleControlOperations(helperRoot,_=>{},native);
            var transport=new Transport(engine);
            var host=new HelperTailscaleControlHost(transport);
            var environment=WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath=folder.FullName }).Environment;
            var protection=new EphemeralDataProtectionProvider();
            var journal=new TailscaleOperationJournal(environment,protection);
            var grants=new StubGrants { Allowed=true };
            var coordinator=new TailscaleOperationCoordinator(journal,host,grants);
            var actor=Guid.NewGuid().ToString("D");
            var principal=new ClaimsPrincipal(new ClaimsIdentity([new("sub",actor)],"test"));
            var other=new ClaimsPrincipal(new ClaimsIdentity([new("sub",Guid.NewGuid().ToString("D"))],"test"));
            var request=new TailscalePrepareRequest(Guid.NewGuid(),native.Snapshot.Revision,TailscaleAction.Connect);
            await coordinator.PrepareAsync(principal,request,default);
            native.Fail=true;
            var unknown=await coordinator.ExecuteAsync(principal,request.OperationId,true,default);
            Check(unknown.State==TailscaleOperationState.Unknown && unknown.Closure is null && native.Executions==1);
            string Evidence(string suffix)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(helperRoot,request.OperationId.ToString("N")+"."+suffix+".json"))));
            var intentHash=Evidence("intent");var resultHash=Evidence("result");
            await Reject(()=>coordinator.ReadRecoveryAsync(new ClaimsPrincipal(new ClaimsIdentity([new("sub",Guid.Empty.ToString("D"))],"test")),request.OperationId,default),401,"tailscale.unauthenticated");
            await Reject(()=>coordinator.ReadRecoveryAsync(other,request.OperationId,default),404,"tailscale.operation.not_found");
            grants.Allowed=false;
            await Reject(()=>coordinator.ReadRecoveryAsync(principal,request.OperationId,default),403,"tailscale.elevation_required");
            grants.Allowed=true; native.Quiet=false;
            var blocked=await coordinator.ReadRecoveryAsync(principal,request.OperationId,default);
            Check(!blocked.CanClose && blocked.ProblemCode=="tailscale.recovery.cli_active" && blocked.ExpiresAt is null);
            var close=new TailscaleCloseRequest(request.OperationId,native.Snapshot.Revision,true);
            await Reject(()=>coordinator.CloseAsync(principal,close,default),409,"tailscale.recovery.preview_required");
            native.Quiet=true;native.Drift=true;
            blocked=await coordinator.ReadRecoveryAsync(principal,request.OperationId,default);
            Check(!blocked.CanClose && blocked.ProblemCode=="tailscale.recovery.observation_changed");
            native.Drift=false;
            var ready=await coordinator.ReadRecoveryAsync(principal,request.OperationId,default);
            Check(ready.CanClose && ready.Observation is not null && ready.ExpiresAt>DateTimeOffset.UtcNow && native.Executions==1);
            close=close with { ExpectedRevision=ready.Observation!.Revision };
            await Reject(()=>coordinator.CloseAsync(principal,close with { Confirmed=false },default),400,"tailscale.confirmation_required");
            await Reject(()=>coordinator.CloseAsync(principal,close with { ExpectedRevision=new('C',64) },default),409,"tailscale.recovery.preview_required");
            var stored=journal.Read(request.OperationId)!;
            journal.Save(stored with { RecoveryPlan=stored.RecoveryPlan! with { ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1) } });
            await Reject(()=>coordinator.CloseAsync(principal,close,default),409,"tailscale.recovery.preview_required");
            ready=await coordinator.ReadRecoveryAsync(principal,request.OperationId,default);
            native.OnRead=()=>grants.Allowed=false;
            await Reject(()=>coordinator.CloseAsync(principal,close,default),403,"tailscale.elevation_required");
            Check(!File.Exists(Path.Combine(helperRoot,request.OperationId.ToString("N")+".closed.json")));
            native.OnRead=null;grants.Allowed=true;
            native.Snapshot=native.Snapshot with { Revision=new('E',64) };
            await Reject(()=>coordinator.CloseAsync(principal,close,default),409,"tailscale.recovery.not_ready");
            ready=await coordinator.ReadRecoveryAsync(principal,request.OperationId,default);
            close=close with { ExpectedRevision=ready.Observation!.Revision };
            transport.BeforeClose=()=>Check(journal.Read(request.OperationId)?.ClosingRequest==close);
            transport.DropCloseReply=true;
            await Reject(()=>coordinator.CloseAsync(principal,close,default),503,"tailscale.recovery.unavailable");
            Check(native.Executions==1 && Evidence("intent")==intentHash && Evidence("result")==resultHash);
            var reads=native.Reads;
            var reopened=new TailscaleOperationCoordinator(new(environment,protection),host,grants);
            var closed=await reopened.GetAsync(principal,request.OperationId,default);
            Check(closed.State==TailscaleOperationState.Unknown && closed.ProblemCode==unknown.ProblemCode && closed.Closure?.Actor==actor);
            Check(closed.Closure?.OperationId==request.OperationId && closed.Closure.Observation.Revision==close.ExpectedRevision && native.Reads==reads);
            var closedHash=Evidence("closed");
            Check(await reopened.CloseAsync(principal,close,default)==closed && native.Reads==reads && native.Executions==1);
            Check(Evidence("closed")==closedHash);
            Check((await reopened.ReadRecoveryAsync(principal,request.OperationId,default)).Closure==closed.Closure && native.Reads==reads);
            Check((await host.ReadAsync(default)).CanControl);
            // Exact idempotency includes actor and observed revision at the protected boundary.
            PrivilegedOperationRequest Envelope(TailscaleRecoveryCommand command)=>new(PrivilegedOperationKind.TailscaleRecoveryClose,
                OperationId:request.OperationId,Correlation:CorrelationContext.Create(request.OperationId,"privileged.operation"),TailscaleRecovery:command);
            Check(!(await engine.HandleAsync(Envelope(new(close,Guid.NewGuid().ToString("D"))))).Success);
            Check(!(await engine.HandleAsync(Envelope(new(close with { ExpectedRevision=new('F',64) },actor)))).Success);
            Check(!(await engine.HandleAsync(Envelope(new(close with { Confirmed=false },actor)))).Success);
            Check(!(await engine.HandleAsync(Envelope(new(close,actor)) with { Path="arbitrary" })).Success);
            Check(!(await engine.HandleAsync(Envelope(new(null!,actor)))).Success);
            Check(!(await engine.HandleAsync(Envelope(new(close,Guid.Empty.ToString("D"))))).Success);
            Check(Evidence("intent")==intentHash && Evidence("result")==resultHash && Evidence("closed")==closedHash);
            native.Fail=false;
            var next=new TailscalePrepareRequest(Guid.NewGuid(),native.Snapshot.Revision,TailscaleAction.Connect);
            await reopened.PrepareAsync(principal,next,default);
            Check((await reopened.ExecuteAsync(principal,next.OperationId,true,default)).State==TailscaleOperationState.Applied && native.Executions==2);
            await Reject(()=>reopened.ReadRecoveryAsync(principal,next.OperationId,default),409,"tailscale.recovery.state_invalid");
            Check((await reopened.GetAsync(principal,request.OperationId,default)).State==TailscaleOperationState.Unknown);
            transport.BeforeClose=null;
            var interrupted=new TailscalePrepareRequest(Guid.NewGuid(),native.Snapshot.Revision,TailscaleAction.Configure,new(false,true));
            await reopened.PrepareAsync(principal,interrupted,default);
            stored=journal.Read(interrupted.OperationId)!;
            journal.Save(stored with { Operation=stored.Operation with { State=TailscaleOperationState.Applying } });
            File.WriteAllText(Path.Combine(helperRoot,interrupted.OperationId.ToString("N")+".intent.json"),JsonSerializer.Serialize(interrupted));
            Check((await reopened.GetAsync(principal,interrupted.OperationId,default)).State==TailscaleOperationState.Unknown);
            native.Quiet=false;
            Check(!(await reopened.ReadRecoveryAsync(principal,interrupted.OperationId,default)).CanClose);
            native.Quiet=true;
            var interruptedPreview=await reopened.ReadRecoveryAsync(principal,interrupted.OperationId,default);
            var clock=new TailscaleControlTestClock();
            var timed=new TailscaleOperationCoordinator(journal,host,grants,clock);
            var expiringPreview=await timed.ReadRecoveryAsync(principal,interrupted.OperationId,default);
            native.OnRead=()=>clock.UtcNow=expiringPreview.ExpiresAt!.Value;
            await Reject(()=>timed.CloseAsync(principal,new(interrupted.OperationId,expiringPreview.Observation!.Revision,true),default),409,"tailscale.recovery.preview_required");
            native.OnRead=null;
            Check(journal.Read(interrupted.OperationId)!.ClosingRequest is null && !File.Exists(Path.Combine(helperRoot,interrupted.OperationId.ToString("N")+".closed.json")));
            var interruptedClosed=await reopened.CloseAsync(principal,new(interrupted.OperationId,interruptedPreview.Observation!.Revision,true),default);
            Check(interruptedClosed.State==TailscaleOperationState.Unknown && interruptedClosed.Closure is not null && native.Executions==2);
            Check(!File.Exists(Path.Combine(helperRoot,interrupted.OperationId.ToString("N")+".result.json")));
            var closurePath=Path.Combine(helperRoot,request.OperationId.ToString("N")+".closed.json");
            var savedClosure=File.ReadAllBytes(closurePath);
            File.WriteAllText(closurePath,JsonSerializer.Serialize(closed.Closure! with { ClosedAt=default }));
            var corrupted=await engine.HandleAsync(new(PrivilegedOperationKind.TailscaleRecoveryReceipt,OperationId:request.OperationId,
                Correlation:CorrelationContext.Create(request.OperationId,"privileged.operation")));
            Check(!corrupted.Success && corrupted.TailscaleClosure is null);
            File.WriteAllBytes(closurePath,savedClosure);
            Console.WriteLine($"{count} Tailscale manual recovery/closure checks passed.");
        }
        finally { folder.Delete(true); }
    }
    private sealed class Native:ITailscaleNativeControl
    {
        internal TailscaleControlSnapshot Snapshot=new(new('A',64),false,true,new(true,false),true);
        internal int Reads,Executions;
        internal bool Quiet=true,Fail,Drift;
        internal Action? OnRead;
        public Task<bool> IsQuiescentAsync(CancellationToken ct)=>Task.FromResult(Quiet);
        public Task<TailscaleControlSnapshot> ReadAsync(CancellationToken ct)
        {
            Reads++;OnRead?.Invoke();
            if(Drift) Snapshot=Snapshot with { Revision=Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())) };
            return Task.FromResult(Snapshot);
        }
        public Task<bool> ApplyAsync(TailscalePrepareRequest request,CancellationToken ct)
        {
            Executions++;if(Fail) throw new IOException("private native diagnostics");
            Snapshot=Snapshot with { Connected=request.Action==TailscaleAction.Connect,Configuration=request.Configuration??Snapshot.Configuration,Revision=new('B',64) };
            return Task.FromResult(true);
        }
    }
    private sealed class Transport(TailscaleControlOperations engine):IPrivilegedOperationTransport
    {
        internal bool DropCloseReply;internal Action? BeforeClose;
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request,CancellationToken cancellationToken=default)
        {
            if(request.Operation==PrivilegedOperationKind.TailscaleRecoveryClose) BeforeClose?.Invoke();
            var result=await engine.HandleAsync(request with { Correlation=CorrelationContext.Create(request.OperationId,"privileged.operation") });
            if(DropCloseReply && request.Operation==PrivilegedOperationKind.TailscaleRecoveryClose)
            { DropCloseReply=false;return new(false,ProblemCode:PrivilegedProblemCode.HelperUnavailable); }
            return result;
        }
    }
}
