using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Tailscale;
using System.Security.Claims;

internal static class TailscaleLoginCoordinatorChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Login coordinator assertion " + count); count++; }
        async Task Reject(Func<Task> action, int code, string problem)
        {
            try { await action(); throw new Exception("Expected login rejection: " + problem); }
            catch (TailscaleControlException ex) { Check(ex.StatusCode == code && ex.ProblemCode == problem); }
        }
        var folder = Directory.CreateTempSubdirectory("relaxkonos-login-coordinator-");
        try
        {
            var environment = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = folder.FullName }).Environment;
            var protection = new EphemeralDataProtectionProvider();
            var journal = new TailscaleLoginJournal(environment, protection);
            var settings = new ServerRuntimeSettingsStore(Path.Combine(folder.FullName, "settings.json"));
            var host = new LoginHost();
            var grants = new Grants { Allowed = true };
            var clock = new Clock();
            var coordinator = new TailscaleLoginCoordinator(journal, host, grants, settings, clock);
            var actor = Guid.NewGuid();
            var session = Guid.NewGuid();
            ClaimsPrincipal Principal(Guid? subject = null, Guid? loginSession = null) => new(new ClaimsIdentity(
                [new("sub", (subject ?? actor).ToString("D")), new("sid", (loginSession ?? session).ToString("D"))], "test"));
            var principal = Principal();
            var other = Principal(Guid.NewGuid());
            TailscaleLoginPrepareRequest Request(Guid? id = null) => new(id ?? Guid.NewGuid());

            await Reject(() => coordinator.PrepareAsync(new ClaimsPrincipal(), Request(), default), 401, "tailscale.unauthenticated");
            await Reject(() => coordinator.PrepareAsync(new ClaimsPrincipal(new ClaimsIdentity([new("sub", actor.ToString("D"))], "test")), Request(), default),
                401, "tailscale.unauthenticated");
            grants.Allowed = false;
            await Reject(() => coordinator.PrepareAsync(principal, Request(), default), 403, "tailscale.elevation_required");
            grants.Allowed = true;
            host.Observation = host.Observation with { BackendState = "Running", CanStart = false };
            await Reject(() => coordinator.PrepareAsync(principal, Request(), default), 409, "tailscale.login.not_available");
            host.Observation = host.Observation with { BackendState = "NeedsLogin", CanStart = true };
            var observes = host.Observes;
            var request = Request();
            var prepared = await coordinator.PrepareAsync(principal, request, default);
            Check(prepared.State == TailscaleLoginState.Prepared && prepared.OperationId == request.OperationId
                && prepared.ExpectedRevision == host.Observation.Revision && host.Observes == observes + 1);
            Check((await coordinator.PrepareAsync(principal, request, default)) == prepared && host.Observes == observes + 1);
            await Reject(() => coordinator.PrepareAsync(Principal(actor, Guid.NewGuid()), request, default), 404, "tailscale.operation.not_found");

            await Reject(() => coordinator.ExecuteAsync(principal, request.OperationId, false, default), 400, "tailscale.confirmation_required");
            Check(host.Begins == 0);
            host.Observation = host.Observation with { Revision = new('C', 64) };
            var changed = await coordinator.ExecuteAsync(principal, request.OperationId, true, default);
            Check(changed.State == TailscaleLoginState.Rejected && changed.ProblemCode == "tailscale.login.state_changed" && host.Begins == 0);
            host.Observation = host.Observation with { Revision = prepared.ExpectedRevision };

            var expiredRequest = Request();
            var expiring = await coordinator.PrepareAsync(principal, expiredRequest, default);
            var expiredStored = journal.Read(expiring.OperationId)!;
            journal.Save(expiredStored with { Operation = expiredStored.Operation with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } });
            Check((await coordinator.ExecuteAsync(principal, expiredRequest.OperationId, true, default)).State == TailscaleLoginState.Rejected
                && host.Begins == 0);
            var slowRequest = Request();
            await coordinator.PrepareAsync(principal, slowRequest, default);
            host.OnObserve = () => clock.Offset = TimeSpan.FromMinutes(6);
            var slow = await coordinator.ExecuteAsync(principal, slowRequest.OperationId, true, default);
            Check(slow.State == TailscaleLoginState.Rejected && slow.ProblemCode == "tailscale.plan_expired" && host.Begins == 0);
            host.OnObserve = null;
            clock.Offset = TimeSpan.Zero;

            var successRequest = Request();
            var issuedPrepared = await coordinator.PrepareAsync(principal, successRequest, default);
            var issued = await coordinator.ExecuteAsync(principal, successRequest.OperationId, true, default);
            Check(issued.State == TailscaleLoginState.Issued && host.Begins == 1 && host.LastCommand is { Confirmed: true } command
                && command.Intent.HostId == settings.Current.InstanceId && command.Intent.Actor == actor && command.Intent.SessionId == session
                && command.Intent.ExpectedRevision == issuedPrepared.ExpectedRevision && command.Intent.ExpiresAt == issuedPrepared.ExpiresAt);
            Check((await coordinator.ExecuteAsync(principal, successRequest.OperationId, true, default)).State == TailscaleLoginState.Issued && host.Begins == 1);
            var reconnected = Principal(actor, Guid.NewGuid());
            Check((await coordinator.GetAsync(reconnected, successRequest.OperationId, default)).State == TailscaleLoginState.Issued);
            await Reject(() => coordinator.ExecuteAsync(reconnected, successRequest.OperationId, true, default), 404, "tailscale.operation.not_found");
            await Reject(() => coordinator.ReadChallengeAsync(reconnected, successRequest.OperationId, default), 404, "tailscale.operation.not_found");
            await Reject(() => coordinator.GetAsync(other, successRequest.OperationId, default), 404, "tailscale.operation.not_found");

            grants.Allowed = false;
            await Reject(() => coordinator.ReadChallengeAsync(principal, successRequest.OperationId, default), 403, "tailscale.elevation_required");
            grants.Allowed = true;
            var challenge = await coordinator.ReadChallengeAsync(principal, successRequest.OperationId, default);
            Check(challenge.OperationId == successRequest.OperationId && challenge.AuthenticationUrl == LoginHost.Url
                && challenge.ExpiresAt == issuedPrepared.ExpiresAt && host.Challenges == 1);
            Check(!challenge.ToString().Contains(LoginHost.Url, StringComparison.Ordinal));
            host.ChallengeOverride = new(Guid.NewGuid(), LoginHost.Url, host.Observation);
            await Reject(() => coordinator.ReadChallengeAsync(principal, successRequest.OperationId, default), 409, "tailscale.login.challenge_unavailable");
            host.ChallengeOverride = new(successRequest.OperationId, "https://example.com/a/0123456789", host.Observation);
            await Reject(() => coordinator.ReadChallengeAsync(principal, successRequest.OperationId, default), 409, "tailscale.login.challenge_unavailable");
            host.ChallengeOverride = new(successRequest.OperationId, LoginHost.Url, host.Observation with { PreferencesRevision = new('F', 64) });
            await Reject(() => coordinator.ReadChallengeAsync(principal, successRequest.OperationId, default), 409, "tailscale.login.challenge_unavailable");
            host.ChallengeOverride = new(successRequest.OperationId, LoginHost.Url, host.Observation with { BackendState = "Running" });
            await Reject(() => coordinator.ReadChallengeAsync(principal, successRequest.OperationId, default), 409, "tailscale.login.challenge_unavailable");
            host.ChallengeOverride = null;
            host.OnChallenge = () => clock.Offset = TimeSpan.FromMinutes(6);
            await Reject(() => coordinator.ReadChallengeAsync(principal, successRequest.OperationId, default), 409, "tailscale.plan_expired");
            host.OnChallenge = null;
            clock.Offset = TimeSpan.Zero;

            host.Observation = new(new('D', 64), new('B', 64), "Running", false);
            var recovery = await coordinator.ReadRecoveryAsync(reconnected, successRequest.OperationId, default);
            Check(recovery.CanClose && recovery.Observation == host.Observation && recovery.ExpiresAt > DateTimeOffset.UtcNow);
            host.OnRecovery = () => clock.Offset = TimeSpan.FromMinutes(6);
            await Reject(() => coordinator.CloseAsync(principal, new(successRequest.OperationId, host.Observation.Revision, true), default),
                409, "tailscale.login.recovery.preview_required");
            Check(host.Closes == 0);
            host.OnRecovery = null;
            clock.Offset = TimeSpan.Zero;
            await Reject(() => coordinator.CloseAsync(principal, new(successRequest.OperationId, recovery.Observation!.Revision, false), default),
                400, "tailscale.confirmation_required");
            await Reject(() => coordinator.CloseAsync(principal, new(successRequest.OperationId, new('F', 64), true), default),
                409, "tailscale.login.recovery.preview_required");
            var closure = await coordinator.CloseAsync(reconnected, new(successRequest.OperationId, host.Observation.Revision, true), default);
            Check(closure.State == TailscaleLoginState.Issued && closure.Closure is { } accepted
                && accepted.Observation.Revision == host.Observation.Revision && accepted.Intent.Actor == actor && accepted.Intent.SessionId == session && host.Closes == 1);
            Check((await coordinator.GetAsync(principal, successRequest.OperationId, default)).Closure == closure.Closure);
            var closedRecovery = await coordinator.ReadRecoveryAsync(principal, successRequest.OperationId, default);
            Check(!closedRecovery.CanClose && closedRecovery.Closure == closure.Closure && host.Recoveries == 3);
            Check((await coordinator.CloseAsync(principal, new(successRequest.OperationId, host.Observation.Revision, true), default)).Closure == closure.Closure
                && host.Closes == 1);
            await Reject(() => coordinator.ReadChallengeAsync(principal, successRequest.OperationId, default), 409, "tailscale.login.challenge_unavailable");

            // An interrupted Applying record is never replayed and is reported as unknown.
            host.Observation = host.Observation with { BackendState = "NeedsLogin", CanStart = true };
            var interruptedRequest = Request();
            var interrupted = await coordinator.PrepareAsync(principal, interruptedRequest, default);
            var storedInterrupted = journal.Read(interrupted.OperationId)!;
            journal.Save(storedInterrupted with { Operation = storedInterrupted.Operation with { State = TailscaleLoginState.Applying } });
            var normalized = await coordinator.GetAsync(principal, interruptedRequest.OperationId, default);
            Check(normalized.State == TailscaleLoginState.Unknown && normalized.ProblemCode == "tailscale.operation.interrupted");
            Check((await coordinator.ExecuteAsync(principal, interruptedRequest.OperationId, true, default)).State == TailscaleLoginState.Unknown && host.Begins == 1);
            // A Helper receipt recorded before the Server could save it is honoured once on read, not replayed.
            var recoveredRequest = Request();
            await coordinator.PrepareAsync(principal, recoveredRequest, default);
            var recoveredStored = journal.Read(recoveredRequest.OperationId)!;
            journal.Save(recoveredStored with { Operation = recoveredStored.Operation with { State = TailscaleLoginState.Applying } });
            host.StoredReceipt = new(Guid.NewGuid(), TailscaleLoginState.Completed, DateTimeOffset.UtcNow);
            Check((await coordinator.GetAsync(principal, recoveredRequest.OperationId, default)).State == TailscaleLoginState.Unknown);
            host.StoredReceipt = new(recoveredRequest.OperationId, TailscaleLoginState.Completed, default);
            Check((await coordinator.GetAsync(principal, recoveredRequest.OperationId, default)).State == TailscaleLoginState.Unknown);
            host.StoredReceipt = new(recoveredRequest.OperationId, TailscaleLoginState.Issued, DateTimeOffset.UtcNow,
                host.Observation.PreferencesRevision, host.Observation.Revision, new('C', 64));
            var reconciled = await coordinator.GetAsync(principal, recoveredRequest.OperationId, default);
            Check(reconciled.State == TailscaleLoginState.Issued && host.Receipts > 0);
            host.StoredReceipt = null;
            // A prepared plan that never reached a native command has no original evidence to reconcile.
            var neverRequest = Request();
            await coordinator.PrepareAsync(principal, neverRequest, default);
            await Reject(() => coordinator.ReadRecoveryAsync(principal, neverRequest.OperationId, default), 409, "tailscale.login.recovery.state_invalid");

            // A native submission whose result was lost stays unknown, and its closure is idempotent.
            var failingRequest = Request();
            await coordinator.PrepareAsync(principal, failingRequest, default);
            host.FailBegin = true;
            var unknown = await coordinator.ExecuteAsync(principal, failingRequest.OperationId, true, default);
            Check(unknown.State == TailscaleLoginState.Unknown && unknown.ProblemCode == "tailscale.login.outcome_unknown" && host.Begins == 2);
            var unknownPreview = await coordinator.ReadRecoveryAsync(principal, failingRequest.OperationId, default);
            var unknownClosure = await coordinator.CloseAsync(principal, new(failingRequest.OperationId, unknownPreview.Observation!.Revision, true), default);
            Check(unknownClosure.State == TailscaleLoginState.Unknown && unknownClosure.Closure is not null && host.Closes == 2);

            // An issued challenge is not re-delivered after the plan expires.
            host.FailBegin = false;
            var lateRequest = Request();
            await coordinator.PrepareAsync(principal, lateRequest, default);
            Check((await coordinator.ExecuteAsync(principal, lateRequest.OperationId, true, default)).State == TailscaleLoginState.Issued);
            var lateStored = journal.Read(lateRequest.OperationId)!;
            journal.Save(lateStored with { Operation = lateStored.Operation with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } });
            await Reject(() => coordinator.ReadChallengeAsync(principal, lateRequest.OperationId, default), 409, "tailscale.plan_expired");

            // Approval-required is also an unresolved Helper intent and must have a closure path.
            host.ReceiptState = TailscaleLoginState.ApprovalRequired;
            var approval = Request();
            await coordinator.PrepareAsync(principal, approval, default);
            Check((await coordinator.ExecuteAsync(principal, approval.OperationId, true, default)).State == TailscaleLoginState.ApprovalRequired);
            var approvalPreview = await coordinator.ReadRecoveryAsync(principal, approval.OperationId, default);
            Check(approvalPreview.CanClose);
            var approvalClosed = await coordinator.CloseAsync(principal, new(approval.OperationId, approvalPreview.Observation!.Revision, true), default);
            Check(approvalClosed.State == TailscaleLoginState.ApprovalRequired && approvalClosed.Closure is not null);

            // Server journal is encrypted and contains neither identity nor the browser URL.
            var database = Path.Combine(folder.FullName, "data", "tailscale-login", "operations.db");
            var bytes = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database));
            Check(!bytes.Contains(actor.ToString("D")) && !bytes.Contains(session.ToString("D")) && !bytes.Contains("login.tailscale.com"));
            Console.WriteLine($"{count} Tailscale login coordinator checks passed.");
        }
        finally { folder.Delete(true); }
    }

    private sealed class LoginHost : ITailscaleLoginHost
    {
        internal const string Url = "https://login.tailscale.com/a/0123456789abcdef";
        internal TailscaleLoginObservation Observation = new(new('A', 64), new('B', 64), "NeedsLogin", true);
        internal int Observes, Begins, Challenges, Recoveries, Closes, Receipts;
        internal bool FailBegin;
        internal TailscaleLoginCommand? LastCommand;
        internal TailscaleLoginReceipt? StoredReceipt;
        internal TailscaleLoginState ReceiptState = TailscaleLoginState.Issued;
        internal TailscaleLoginChallenge? ChallengeOverride;
        internal Action? OnChallenge;
        internal Action? OnObserve;
        internal Action? OnRecovery;

        public Task<TailscaleLoginObservation> ObserveAsync(CancellationToken cancellationToken)
        { Observes++; OnObserve?.Invoke(); return Task.FromResult(Observation); }

        public Task<TailscaleLoginReceipt> BeginAsync(TailscaleLoginCommand command, CancellationToken cancellationToken)
        {
            Begins++; LastCommand = command;
            if (FailBegin) throw new TailscaleControlException(503, "tailscale.login.unavailable");
            return Task.FromResult(new TailscaleLoginReceipt(command.Intent.OperationId, ReceiptState, DateTimeOffset.UtcNow,
                Observation.PreferencesRevision, Observation.Revision, new('C', 64)));
        }

        public Task<TailscaleLoginReceipt?> GetReceiptAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken)
        {
            Receipts++;
            return Task.FromResult(StoredReceipt);
        }

        public Task<TailscaleLoginChallenge> ReadChallengeAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken)
        { Challenges++; OnChallenge?.Invoke(); return Task.FromResult(ChallengeOverride ?? new TailscaleLoginChallenge(intent.OperationId, Url, Observation)); }

        public Task<TailscaleLoginObservation> ReadRecoveryAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken)
        { Recoveries++; OnRecovery?.Invoke(); return Task.FromResult(Observation); }

        public Task<TailscaleLoginClosure> CloseAsync(TailscaleLoginIntent intent, TailscaleLoginCloseRequest request, CancellationToken cancellationToken)
        { Closes++; return Task.FromResult(new TailscaleLoginClosure(intent, Observation, DateTimeOffset.UtcNow)); }

        public Task<TailscaleLoginClosure?> GetClosureAsync(TailscaleLoginIntent intent, CancellationToken cancellationToken) =>
            Task.FromResult<TailscaleLoginClosure?>(null);
    }

    private sealed class Grants : ITailscaleControlAuthorization
    {
        internal bool Allowed;
        public bool IsGranted(ClaimsPrincipal principal) => Allowed;
    }
    private sealed class Clock : TimeProvider
    {
        internal TimeSpan Offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.Add(Offset);
    }
}
