using System.Text.Json;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;

internal static class TailscaleLoginBridgeChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Login bridge assertion " + count); count++; }
        const string url = "https://login.tailscale.com/a/0123456789abcdef";
        var frame = JsonSerializer.Serialize(new { AuthURL = url, BackendState = "NeedsLogin" });
        var folder = Directory.CreateTempSubdirectory("relaxkonos-login-bridge-");
        try
        {
            var clock = new Clock();
            var commands = new TailscaleLoginChecks.Commands(frame, url);
            var native = new Native();
            var login = new TailscaleNativeLogin(commands.Run, () => { });
            var engine = new TailscaleControlOperations(folder.FullName, _ => { }, native, login, clock);
            var baseline = await login.ReadAsync(default);
            var intent = new TailscaleLoginIntent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                baseline.Revision, clock.Now.AddMinutes(4));
            PrivilegedOperationRequest Envelope(PrivilegedOperationKind kind, Guid id) =>
                new(kind, OperationId: id, Correlation: CorrelationContext.Create(id, "privileged.operation"));

            var begin = Envelope(PrivilegedOperationKind.TailscaleLoginBegin, intent.OperationId) with
            { TailscaleLogin = new(intent, true) };
            Check(!(await engine.HandleAsync(begin with { TailscaleLogin = new(intent, false) })).Success);
            Check(!(await engine.HandleAsync(begin with { TailscaleLogin = new(intent with { OperationId = Guid.NewGuid() }, true) })).Success);
            Check(!(await engine.HandleAsync(begin with { TailscaleLogin = null })).Success);
            Check(!(await engine.HandleAsync(begin with { TailscaleChange = new(intent.OperationId, intent.ExpectedRevision, TailscaleAction.Connect) })).Success);
            Check(commands.UpCalls == 0);
            var issued = await engine.HandleAsync(begin);
            Check(issued.Success && issued.TailscaleLoginReceipt is { State: TailscaleLoginState.Issued } receipt
                && receipt.ChallengeFingerprint == TailscaleNativeLogin.FingerprintChallenge(url) && commands.UpCalls == 1);
            Check((await engine.HandleAsync(begin)).TailscaleLoginReceipt == issued.TailscaleLoginReceipt && commands.UpCalls == 1);

            var identity = Envelope(PrivilegedOperationKind.TailscaleLoginReceipt, intent.OperationId) with { TailscaleLoginIdentity = intent };
            Check(!(await engine.HandleAsync(identity with { TailscaleLoginIdentity = intent with { SessionId = Guid.NewGuid() } })).Success);
            Check(!(await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlRead, Guid.NewGuid()) with { TailscaleLoginIdentity = intent })).Success);
            Check((await engine.HandleAsync(identity)).TailscaleLoginReceipt == issued.TailscaleLoginReceipt);

            var challenge = await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleLoginChallenge, intent.OperationId) with { TailscaleLoginIdentity = intent });
            var delivered = challenge.TailscaleLoginChallenge;
            Check(challenge.Success && delivered is not null
                && delivered.AuthenticationUrl == url && delivered.OperationId == intent.OperationId);
            Check(delivered is not null && !delivered.ToString().Contains("0123456789abcdef") && !delivered.ToString().Contains(url));
            Check((await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleLoginChallenge, intent.OperationId) with
            { TailscaleLoginIdentity = intent with { HostId = Guid.NewGuid() } })).ProblemCode == PrivilegedProblemCode.Conflict);

            var recovery = await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleLoginRecoveryRead, intent.OperationId) with { TailscaleLoginIdentity = intent });
            var preview = recovery.TailscaleLoginRecovery;
            Check(recovery.Success && preview is { CanClose: true, Observation.BackendState: "NeedsLogin" });
            var revision = preview!.Observation!.Revision;
            var close = Envelope(PrivilegedOperationKind.TailscaleLoginClose, intent.OperationId) with
            { TailscaleLoginIdentity = intent, TailscaleLoginClose = new(intent.OperationId, revision, true) };
            Check(!(await engine.HandleAsync(close with { TailscaleLoginClose = new(intent.OperationId, revision, false) })).Success);
            Check((await engine.HandleAsync(close with { TailscaleLoginClose = new(intent.OperationId, new('F', 64), true) })).ProblemCode == PrivilegedProblemCode.Conflict);
            commands.Complete = true;
            var observed = await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleLoginRecoveryRead, intent.OperationId) with { TailscaleLoginIdentity = intent });
            var runningRevision = observed.TailscaleLoginRecovery!.Observation!.Revision;
            var closure = await engine.HandleAsync(close with { TailscaleLoginClose = new(intent.OperationId, runningRevision, true) });
            Check(closure.Success && closure.TailscaleLoginClosure is { } closed && closed.Intent == intent
                && closed.Observation.BackendState == "Running" && closed.Observation.Revision == runningRevision);
            Check((await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleLoginRecoveryReceipt, intent.OperationId) with { TailscaleLoginIdentity = intent }))
                .TailscaleLoginClosure == closure.TailscaleLoginClosure);
            Check((await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleLoginChallenge, intent.OperationId) with { TailscaleLoginIdentity = intent }))
                .ProblemCode == PrivilegedProblemCode.Conflict);
            Check((await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlRead, Guid.NewGuid()))).TailscaleControl?.CanControl == true);
            Console.WriteLine($"{count} Tailscale login Helper bridge checks passed.");
        }
        finally { folder.Delete(true); }
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Native : ITailscaleNativeControl
    {
        public Task<bool> IsQuiescentAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<TailscaleControlSnapshot> ReadAsync(CancellationToken ct) =>
            Task.FromResult(new TailscaleControlSnapshot(new('A', 64), false, true, new(true, false), true));
        public Task<bool> ApplyAsync(TailscalePrepareRequest request, CancellationToken ct) => throw new Exception("Login bridge must not call control mutation");
    }
}
