using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Tailscale;
using System.Security.Claims;

internal static class TailscaleControlChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale control assertion " + count); count++; }
        async Task Reject(Func<Task> action, int code, string problem)
        {
            try { await action(); throw new Exception("Expected rejection: " + problem); }
            catch (TailscaleControlException ex) { Check(ex.StatusCode == code && ex.ProblemCode == problem); }
        }
        var folder = Directory.CreateTempSubdirectory("relaxkonos-tailscale-");
        try
        {
            var environment = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = folder.FullName }).Environment;
            var protection = new EphemeralDataProtectionProvider();
            var journal = new TailscaleOperationJournal(environment, protection);
            var host = new ControlHost();
            var grants = new StubGrants { Allowed = true };
            var coordinator = new TailscaleOperationCoordinator(journal, host, grants);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", Guid.NewGuid().ToString())], "test"));
            var other = new ClaimsPrincipal(new ClaimsIdentity([new("sub", Guid.NewGuid().ToString())], "test"));
            TailscalePrepareRequest Request(TailscaleAction action = TailscaleAction.Connect) =>
                new(Guid.NewGuid(), host.Snapshot.Revision, action);
            await Reject(() => coordinator.PrepareAsync(new ClaimsPrincipal(), Request(), default), 401, "tailscale.unauthenticated");
            await Reject(() => coordinator.PrepareAsync(principal, Request() with { OperationId = Guid.Empty }, default), 400, "tailscale.invalid_request");
            await Reject(() => coordinator.PrepareAsync(principal, Request() with { ExpectedRevision = "bad" }, default), 400, "tailscale.invalid_request");
            await Reject(() => coordinator.PrepareAsync(principal, Request() with { Action = (TailscaleAction)99 }, default), 400, "tailscale.invalid_request");
            await Reject(() => coordinator.PrepareAsync(principal, Request() with { Configuration = new(true, true) }, default), 400, "tailscale.invalid_request");
            await Reject(() => coordinator.PrepareAsync(principal, Request(TailscaleAction.Configure), default), 400, "tailscale.invalid_request");
            grants.Allowed = false;
            await Reject(() => coordinator.PrepareAsync(principal, Request(), default), 403, "tailscale.elevation_required");
            Check(host.Reads == 0 && host.Executions == 0);
            grants.Allowed = true;
            await Reject(() => coordinator.PrepareAsync(principal, Request() with { ExpectedRevision = new('B', 64) }, default), 409, "tailscale.revision_conflict");
            host.Snapshot = host.Snapshot with { Authenticated = false };
            await Reject(() => coordinator.PrepareAsync(principal, Request(), default), 409, "tailscale.login_required");
            host.Snapshot = host.Snapshot with { Authenticated = true, CanControl = false };
            await Reject(() => coordinator.PrepareAsync(principal, Request(), default), 409, "tailscale.control_unavailable");
            host.Snapshot = host.Snapshot with { CanControl = true };
            var revokeDuringRead = Request();
            await coordinator.PrepareAsync(principal, revokeDuringRead, default);
            host.OnRead = () => grants.Allowed = false;
            await Reject(() => coordinator.ExecuteAsync(principal, revokeDuringRead.OperationId, true, default), 403, "tailscale.elevation_required");
            Check(host.Executions == 0 && journal.Read(revokeDuringRead.OperationId)!.Operation.State == TailscaleOperationState.Prepared);
            host.OnRead = null;
            grants.Allowed = true;
            using (var held = await journal.AcquireAsync(default))
            using (var cancelled = new CancellationTokenSource(50))
            {
                try { await coordinator.PrepareAsync(principal, Request(), cancelled.Token); throw new Exception("Lock wait ignored cancellation"); }
                catch (OperationCanceledException) { Check(host.Executions == 0); }
            }
            var request = Request();
            var prepared = await coordinator.PrepareAsync(principal, request, default);
            Check(prepared.State == TailscaleOperationState.Prepared && host.Executions == 0);
            Check(await coordinator.PrepareAsync(principal, request, default) == prepared);
            await Reject(() => coordinator.PrepareAsync(principal, request with { Action = TailscaleAction.Disconnect }, default), 409, "tailscale.idempotency_conflict");
            await Reject(() => coordinator.GetAsync(other, request.OperationId, default), 404, "tailscale.operation.not_found");
            await Reject(() => coordinator.ExecuteAsync(principal, request.OperationId, false, default), 400, "tailscale.confirmation_required");
            grants.Allowed = false;
            await Reject(() => coordinator.ExecuteAsync(principal, request.OperationId, true, default), 403, "tailscale.elevation_required");
            Check(host.Executions == 0);
            grants.Allowed = true;
            host.Snapshot = host.Snapshot with { Revision = new('B', 64) };
            await Reject(() => coordinator.ExecuteAsync(principal, request.OperationId, true, default), 409, "tailscale.revision_conflict");
            host.Snapshot = host.Snapshot with { Revision = request.ExpectedRevision };
            using var cancellation = new CancellationTokenSource();
            host.OnExecute = () =>
            {
                // Prove the durability ordering using a separately opened database connection.
                Check(journal.Read(request.OperationId)?.Operation.State == TailscaleOperationState.Applying);
                cancellation.Cancel();
            };
            var applied = await coordinator.ExecuteAsync(principal, request.OperationId, true, cancellation.Token);
            Check(applied.State == TailscaleOperationState.Applied && host.Executions == 1 && !host.LastToken.CanBeCanceled);
            Check(await coordinator.ExecuteAsync(principal, request.OperationId, true, default) == applied && host.Executions == 1);
            var reopened = new TailscaleOperationCoordinator(new(environment, protection), host, grants);
            Check(await reopened.GetAsync(principal, request.OperationId, default) == applied);
            Check(await reopened.ExecuteAsync(principal, request.OperationId, true, default) == applied && host.Executions == 1);
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder.FullName, "data", "tailscale-operations", "operations.db"))).Contains(principal.FindFirst("sub")!.Value));
            host.OnExecute = null;
            // Two equal concurrent submissions share one mutation and one immutable result.
            var concurrent = Request(TailscaleAction.Disconnect);
            await coordinator.PrepareAsync(principal, concurrent, default);
            var results = await Task.WhenAll(coordinator.ExecuteAsync(principal, concurrent.OperationId, true, default),
                reopened.ExecuteAsync(principal, concurrent.OperationId, true, default));
            Check(results[0] == results[1] && host.Executions == 2);
            // Lost replies stay unknown even if a later read appears to match the requested intent.
            var unknown = Request();
            await coordinator.PrepareAsync(principal, unknown, default);
            host.Throw = true;
            Check((await coordinator.ExecuteAsync(principal, unknown.OperationId, true, default)).State == TailscaleOperationState.Unknown);
            var executions = host.Executions;
            Check((await reopened.ExecuteAsync(principal, unknown.OperationId, true, default)).State == TailscaleOperationState.Unknown && host.Executions == executions);
            host.Throw = false;
            var interrupted = Request();
            await coordinator.PrepareAsync(principal, interrupted, default);
            var stored = journal.Read(interrupted.OperationId)!;
            journal.Save(stored with { Operation = stored.Operation with { State = TailscaleOperationState.Applying } });
            var receipt = await reopened.GetAsync(principal, interrupted.OperationId, default);
            Check(receipt.State == TailscaleOperationState.Unknown && receipt.ProblemCode == "tailscale.operation.interrupted");
            Check((await coordinator.ExecuteAsync(principal, interrupted.OperationId, true, default)).State == TailscaleOperationState.Unknown && host.Executions == executions);
            var expired = Request();
            await coordinator.PrepareAsync(principal, expired, default);
            stored = journal.Read(expired.OperationId)!;
            journal.Save(stored with { Operation = stored.Operation with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } });
            Check((await reopened.ExecuteAsync(principal, expired.OperationId, true, default)).ProblemCode == "tailscale.plan_expired" && host.Executions == executions);
            var mismatched = Request();
            await coordinator.PrepareAsync(principal, mismatched, default);
            host.WrongIdentity = true;
            Check((await coordinator.ExecuteAsync(principal, mismatched.OperationId, true, default)).State == TailscaleOperationState.Unknown);
            host.WrongIdentity = false;
            var config = Request(TailscaleAction.Configure) with { Configuration = new(false, true) };
            await coordinator.PrepareAsync(principal, config, default);
            Check((await coordinator.ExecuteAsync(principal, config.OperationId, true, default)).State == TailscaleOperationState.Applied && host.Snapshot.Configuration == config.Configuration);
            // The extended official preference set shares the same revision and verified readback.
            var preferences = new TailscalePreferences(true, false, true, "100.64.0.9", true, false, true, false);
            var preferenceRequest = Request(TailscaleAction.ConfigurePreferences) with { Preferences = preferences };
            await coordinator.PrepareAsync(principal, preferenceRequest, default);
            Check((await coordinator.ExecuteAsync(principal, preferenceRequest.OperationId, true, default)).State == TailscaleOperationState.Applied
                && host.Snapshot.Preferences == preferences);
            await Reject(() => coordinator.PrepareAsync(principal, Request(TailscaleAction.ConfigurePreferences), default), 400, "tailscale.invalid_request");
            await Reject(() => coordinator.PrepareAsync(principal, Request(TailscaleAction.Configure) with
                { Configuration = new(true, true), Preferences = preferences }, default), 400, "tailscale.invalid_request");
            await Reject(() => coordinator.PrepareAsync(principal, Request(TailscaleAction.ConfigurePreferences) with
                { Preferences = preferences with { ExitNode = "not-an-ip" } }, default), 400, "tailscale.invalid_request");
            var wrongReadback = Request(TailscaleAction.Configure) with { Configuration = new(true, false) };
            await coordinator.PrepareAsync(principal, wrongReadback, default);
            host.WrongReadback = true;
            Check((await coordinator.ExecuteAsync(principal, wrongReadback.OperationId, true, default)).State == TailscaleOperationState.Unknown);
            host.WrongReadback = false;
            var unrelated = Request(TailscaleAction.Disconnect);
            await coordinator.PrepareAsync(principal, unrelated, default);
            host.ChangeUnrelated = true;
            Check((await coordinator.ExecuteAsync(principal, unrelated.OperationId, true, default)).State == TailscaleOperationState.Unknown);
            var clock = new TailscaleControlTestClock();
            var timed = new TailscaleOperationCoordinator(journal,host,grants,clock);
            host.ChangeUnrelated = false; host.Snapshot = host.Snapshot with { Connected = false };
            var expiresDuringRead = Request();
            var expiring = await timed.PrepareAsync(principal,expiresDuringRead,default);
            var executionsBeforeExpiry = host.Executions;
            host.OnRead = () => clock.UtcNow = expiring.ExpiresAt;
            var expiredAfterRead = await timed.ExecuteAsync(principal,expiring.OperationId,true,default);
            host.OnRead = null;
            Check(expiredAfterRead.State == TailscaleOperationState.Rejected && expiredAfterRead.ProblemCode == "tailscale.plan_expired" && host.Executions == executionsBeforeExpiry);
            Check((await timed.GetAsync(principal,expiring.OperationId,default)).State == TailscaleOperationState.Rejected);
            // Logout is a closed action that proves the signed-out post-state before completing.
            host.Snapshot = host.Snapshot with { Connected = false, Authenticated = true, CanControl = true };
            var logout = Request(TailscaleAction.Logout);
            await coordinator.PrepareAsync(principal, logout, default);
            var loggedOut = await coordinator.ExecuteAsync(principal, logout.OperationId, true, default);
            Check(loggedOut.State == TailscaleOperationState.Applied && !host.Snapshot.Authenticated && !host.Snapshot.CanControl);
            await Reject(() => coordinator.PrepareAsync(principal, Request(TailscaleAction.Logout), default), 409, "tailscale.control_unavailable");
            Console.WriteLine($"{count} Tailscale durable control checks passed.");
        }
        finally { folder.Delete(recursive: true); }
    }

    private sealed class ControlHost : ITailscaleControlHost
    {
        public TailscaleControlSnapshot Snapshot = new(new('A', 64), false, true, new(true, false), true,
            Preferences: new(true, false, false, null, false, false, false, false), Platform: "linux");
        public int Reads, Executions;
        public CancellationToken LastToken;
        public Action? OnExecute, OnRead;
        public bool Throw, WrongIdentity, WrongReadback, ChangeUnrelated;
        public Task<TailscaleControlResult?> GetResultAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult<TailscaleControlResult?>(null);
        public Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TailscaleRecoveryClosure> CloseAsync(TailscaleCloseRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TailscaleRecoveryClosure?> GetClosureAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult<TailscaleRecoveryClosure?>(null);
        public Task<TailscaleControlSnapshot> ReadAsync(CancellationToken cancellationToken)
        { Reads++; OnRead?.Invoke(); return Task.FromResult(Snapshot); }
        public Task<TailscaleControlResult> ExecuteAsync(TailscalePrepareRequest request, CancellationToken cancellationToken)
        {
            Executions++; LastToken = cancellationToken; OnExecute?.Invoke();
            if (Throw) throw new IOException("private native diagnostic");
            if (!WrongReadback) Snapshot = Snapshot with {
                Connected = request.Action is TailscaleAction.Configure or TailscaleAction.ConfigurePreferences ? Snapshot.Connected : request.Action == TailscaleAction.Connect,
                Authenticated = request.Action == TailscaleAction.Logout ? false : Snapshot.Authenticated,
                CanControl = request.Action == TailscaleAction.Logout ? false : Snapshot.CanControl,
                Configuration = request.Configuration ?? Snapshot.Configuration,
                Preferences = request.Preferences ?? Snapshot.Preferences,
                Revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())) };
            if (ChangeUnrelated) Snapshot = Snapshot with { Configuration = new(!Snapshot.Configuration.AcceptDns, !Snapshot.Configuration.AcceptRoutes) };
            return Task.FromResult(new TailscaleControlResult(WrongIdentity ? Guid.NewGuid() : request.OperationId, true, Snapshot));
        }
    }
}

internal sealed class TailscaleControlTestClock : TimeProvider
{
    internal DateTimeOffset UtcNow = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
