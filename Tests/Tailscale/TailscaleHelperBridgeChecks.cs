using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Tailscale;
using System.Security.Claims;

internal static class TailscaleHelperBridgeChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale bridge assertion " + count); count++; }
        var folder = Directory.CreateTempSubdirectory("relaxkonos-tailscale-bridge-");
        try
        {
            var helperRoot = Path.Combine(folder.FullName, "helper"); Directory.CreateDirectory(helperRoot);
            var native = new BridgeNative();
            var engine = new TailscaleControlOperations(helperRoot, _ => { }, native);
            var transport = new BridgeTransport(engine);
            var host = new HelperTailscaleControlHost(transport);
            Check((await host.ReadAsync(default)).Revision == native.Snapshot.Revision);
            var environment = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = folder.FullName }).Environment;
            var protection = new EphemeralDataProtectionProvider();
            var journal = new TailscaleOperationJournal(environment, protection);
            var grants = new StubGrants { Allowed = true };
            var coordinator = new TailscaleOperationCoordinator(journal, host, grants);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", Guid.NewGuid().ToString())], "test"));
            var request = new TailscalePrepareRequest(Guid.NewGuid(), native.Snapshot.Revision, TailscaleAction.Connect);
            await coordinator.PrepareAsync(principal, request, default);
            transport.DropReply = true;
            Check((await coordinator.ExecuteAsync(principal, request.OperationId, true, default)).State == TailscaleOperationState.Unknown && native.Executions == 1);
            var reopened = new TailscaleOperationCoordinator(new(environment, protection), host, grants);
            var recovered = await reopened.GetAsync(principal, request.OperationId, default);
            Check(recovered.State == TailscaleOperationState.Applied && recovered.ObservedRevision == native.Snapshot.Revision && native.Executions == 1);
            Check((await reopened.ExecuteAsync(principal, request.OperationId, true, default)) == recovered && native.Executions == 1);
            Check((await host.GetResultAsync(request.OperationId, default))?.Completed == true);
            Check(await host.GetResultAsync(Guid.NewGuid(), default) is null);
            // An incompatible IPC response is never accepted as a control snapshot or receipt.
            transport.WrongVersion = true;
            Check(await host.GetResultAsync(request.OperationId, default) is null);
            try { await host.ReadAsync(default); throw new Exception("Incompatible IPC accepted"); }
            catch (TailscaleControlException ex) { Check(ex.ProblemCode == "tailscale.control_unavailable"); }
            transport.WrongVersion = false;
            var next = new TailscalePrepareRequest(Guid.NewGuid(), native.Snapshot.Revision, TailscaleAction.Disconnect);
            await coordinator.PrepareAsync(principal, next, default);
            native.Fail = true;
            Check((await coordinator.ExecuteAsync(principal, next.OperationId, true, default)).State == TailscaleOperationState.Unknown);
            // A new matching live snapshot cannot turn a failed original operation into success.
            native.Snapshot = native.Snapshot with { Connected = false };
            Check((await reopened.GetAsync(principal, next.OperationId, default)).State == TailscaleOperationState.Unknown);
            var third = new TailscalePrepareRequest(Guid.NewGuid(), native.Snapshot.Revision, TailscaleAction.Connect);
            Check(!(await host.ExecuteAsync(third, default)).Completed && native.Executions == 2);
            Console.WriteLine($"{count} Tailscale Server/Helper original-receipt bridge checks passed.");
        }
        finally { folder.Delete(true); }
    }
    private sealed class BridgeTransport(TailscaleControlOperations engine) : IPrivilegedOperationTransport
    {
        internal bool DropReply, WrongVersion;
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            var result = await engine.HandleAsync(request with { Correlation = CorrelationContext.Create(request.OperationId, "privileged.operation") });
            if (DropReply && request.Operation == PrivilegedOperationKind.TailscaleControlExecute)
            { DropReply = false; return new(false, ProblemCode: PrivilegedProblemCode.HelperUnavailable); }
            return WrongVersion ? result with { Version = "invalid" } : result;
        }
    }
    private sealed class BridgeNative : ITailscaleNativeControl
    {
        internal TailscaleControlSnapshot Snapshot = new(new('A', 64), false, true, new(true, false), true);
        internal int Executions;
        internal bool Fail;
        public Task<bool> IsQuiescentAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<TailscaleControlSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(Snapshot);
        public Task<bool> ApplyAsync(TailscalePrepareRequest request, CancellationToken ct)
        {
            Executions++;
            if (Fail) throw new IOException("private diagnostic");
            Snapshot = Snapshot with { Connected = request.Action == TailscaleAction.Connect, Configuration = request.Configuration ?? Snapshot.Configuration, Revision = new('B', 64) };
            return Task.FromResult(true);
        }
    }
}
