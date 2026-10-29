using System.Text.Json;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;

internal static class TailscaleLoginLedgerChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Login ledger assertion " + count); count++; }
        async Task Reject(Func<Task> action)
        {
            try { await action(); throw new Exception("Login ledger accepted invalid operation"); }
            catch (InvalidDataException ex) { Check(ex.Message == "tailscale.login.conflict"); }
        }
        const string url = "https://login.tailscale.com/a/0123456789abcdef";
        var frame = JsonSerializer.Serialize(new { AuthURL = url, BackendState = "NeedsLogin" });
        var folder = Directory.CreateTempSubdirectory("relaxkonos-login-ledger-");
        try
        {
            var clock = new Clock();
            var commands = new TailscaleLoginChecks.Commands(frame, url);
            var native = new Native();
            var login = new TailscaleNativeLogin(commands.Run, () => { });
            TailscaleControlOperations Engine() => new(folder.FullName, _ => { }, native, login, clock);
            var engine = Engine();
            var baseline = await login.ReadAsync(default);
            var intent = new TailscaleLoginIntent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), baseline.Revision, clock.Now.AddMinutes(4));
            string PathFor(Guid id, string suffix) => Path.Combine(folder.FullName, id.ToString("N") + "." + suffix + ".json");
            PrivilegedOperationRequest Envelope(PrivilegedOperationKind kind, Guid id) => new(kind, OperationId: id, Correlation: CorrelationContext.Create(id, "privileged.operation"));
            async Task CheckBlocked(bool blocked)
            {
                var state = (await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlRead, Guid.NewGuid()))).TailscaleControl;
                Check(state?.CanControl == !blocked);
            }
            await Reject(() => engine.BeginLoginAsync(intent, false, default));
            foreach (var invalid in new[] { intent with { OperationId = Guid.Empty }, intent with { HostId = Guid.Empty }, intent with { Actor = Guid.Empty },
                intent with { SessionId = Guid.Empty }, intent with { ExpectedRevision = "invalid" }, intent with { ExpiresAt = clock.Now.AddMinutes(-1) },
                intent with { ExpiresAt = clock.Now.AddMinutes(6) } })
                await Reject(() => engine.BeginLoginAsync(invalid, true, default));
            Check(commands.UpCalls == 0 && !Directory.EnumerateFiles(folder.FullName, "*.json").Any());
            var orphan = intent with { OperationId = Guid.NewGuid() };
            File.WriteAllText(PathFor(orphan.OperationId, "login-result"), "{}");
            await Reject(() => engine.BeginLoginAsync(orphan, true, default));
            Check(commands.UpCalls == 0 && !File.Exists(PathFor(orphan.OperationId, "login-intent")));
            File.Delete(PathFor(orphan.OperationId, "login-result"));
            native.Quiescent = false;
            await Reject(() => engine.BeginLoginAsync(intent, true, default));
            native.Quiescent = true;
            using (var held = new FileStream(Path.Combine(folder.FullName, "native.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var cancel = new CancellationTokenSource())
            {
                var waiting = engine.BeginLoginAsync(intent, true, cancel.Token);
                Check(!waiting.IsCompleted && commands.UpCalls == 0);
                cancel.Cancel();
                try { await waiting; throw new Exception("Shared gate ignored cancellation"); }
                catch (OperationCanceledException) { Check(!File.Exists(PathFor(intent.OperationId, "login-intent"))); }
            }
            var originalRun = commands;
            login = new((args, ct) =>
            {
                if (args[0] == "up") Check(File.Exists(PathFor(intent.OperationId, "login-intent")) && !File.Exists(PathFor(intent.OperationId, "login-result")));
                return originalRun.Run(args, ct);
            }, () => { });
            engine = Engine();
            var receipt = await engine.BeginLoginAsync(intent, true, default);
            Check(receipt.State == TailscaleLoginState.Issued && receipt.ChallengeFingerprint == TailscaleNativeLogin.FingerprintChallenge(url) && commands.UpCalls == 1);
            var intentBytes = File.ReadAllBytes(PathFor(intent.OperationId, "login-intent"));
            var resultBytes = File.ReadAllBytes(PathFor(intent.OperationId, "login-result"));
            Check(!Directory.EnumerateFiles(folder.FullName, "*.json").Any(path => File.ReadAllText(path).Contains("0123456789abcdef")));
            engine = Engine();
            Check(await engine.BeginLoginAsync(intent, true, default) == receipt && commands.UpCalls == 1);
            Check(await engine.ReadLoginReceiptAsync(intent, default) == receipt);
            foreach (var foreign in new[] { intent with { HostId = Guid.NewGuid() }, intent with { Actor = Guid.NewGuid() }, intent with { SessionId = Guid.NewGuid() },
                intent with { ExpectedRevision = new('F', 64) }, intent with { ExpiresAt = intent.ExpiresAt.AddSeconds(1) } })
            {
                await Reject(() => engine.BeginLoginAsync(foreign, true, default));
                await Reject(() => engine.ReadLoginChallengeAsync(foreign, default));
            }
            using (var challenge = await engine.ReadLoginChallengeAsync(intent, default))
                Check(challenge.TakeAuthenticationUrl() == url && commands.UpCalls == 1);
            await CheckBlocked(true);
            var controlId = Guid.NewGuid();
            Check(!(await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlExecute, controlId) with
            { TailscaleChange = new(controlId, new('A', 64), TailscaleAction.Connect) })).Success);
            await Reject(() => engine.BeginLoginAsync(intent with { OperationId = Guid.NewGuid() }, true, default));
            clock.Now = intent.ExpiresAt;
            await Reject(() => engine.ReadLoginChallengeAsync(intent, default));
            Check(await engine.ReadLoginReceiptAsync(intent, default) == receipt);
            commands.Complete = true;
            Check((await engine.ReadLoginReceiptAsync(intent, default))?.State == TailscaleLoginState.Issued);
            var observation = await engine.ReadLoginRecoveryAsync(intent, default);
            Check(observation.BackendState == "Running");
            await Reject(() => engine.CloseLoginAsync(intent, observation.Revision, false, default));
            await Reject(() => engine.CloseLoginAsync(intent, new('F', 64), true, default));
            native.Quiescent = false;
            await Reject(() => engine.CloseLoginAsync(intent, observation.Revision, true, default));
            native.Quiescent = true;
            var closure = await engine.CloseLoginAsync(intent, observation.Revision, true, default);
            Check(closure.Intent == intent && closure.Observation == observation);
            Check(await Engine().CloseLoginAsync(intent, observation.Revision, true, default) == closure);
            Check(await Engine().ReadLoginClosureAsync(intent, default) == closure);
            Check(File.ReadAllBytes(PathFor(intent.OperationId, "login-intent")).SequenceEqual(intentBytes)
                && File.ReadAllBytes(PathFor(intent.OperationId, "login-result")).SequenceEqual(resultBytes));
            Check(await engine.ReadLoginReceiptAsync(intent, default) == receipt && commands.UpCalls == 1);
            await CheckBlocked(false);
            await Reject(() => engine.ReadLoginChallengeAsync(intent, default));
            // Process loss after durable intent cannot turn a later Running observation into original completion.
            var interrupted = intent with { OperationId = Guid.NewGuid(), ExpiresAt = clock.Now.AddMinutes(4) };
            File.WriteAllText(PathFor(interrupted.OperationId, "login-intent"), JsonSerializer.Serialize(interrupted));
            Check((await engine.ReadLoginReceiptAsync(interrupted, default))?.State == TailscaleLoginState.Unknown);
            Check((await engine.BeginLoginAsync(interrupted, true, default))?.State == TailscaleLoginState.Unknown && commands.UpCalls == 1);
            await CheckBlocked(true);
            var observed = await engine.ReadLoginRecoveryAsync(interrupted, default);
            await engine.CloseLoginAsync(interrupted, observed.Revision, true, default);
            Check(!File.Exists(PathFor(interrupted.OperationId, "login-result")));
            await CheckBlocked(false);
            // Unknown JSON members cannot introduce an implicit secret-bearing receipt format.
            File.WriteAllText(PathFor(interrupted.OperationId, "login-result"), "{\"AuthURL\":\"private-marker\"}");
            try { await engine.ReadLoginReceiptAsync(interrupted, default); throw new Exception("Unknown receipt fields accepted"); }
            catch (JsonException) { Check(true); }
            File.Delete(PathFor(interrupted.OperationId, "login-result"));
            commands = new TailscaleLoginChecks.Commands(frame + "\n{\"BackendState\":\"Running\"}", url) { Complete = true };
            login = new(commands.Run, () => { }); engine = Engine();
            var fresh = intent with { OperationId = Guid.NewGuid(), ExpectedRevision = (await login.ReadAsync(default)).Revision, ExpiresAt = clock.Now.AddMinutes(4) };
            var complete = await engine.BeginLoginAsync(fresh, true, default);
            Check(complete.State == TailscaleLoginState.Completed && complete.ChallengeFingerprint is null && commands.UpCalls == 1);
            await CheckBlocked(false);
            await Reject(() => engine.ReadLoginChallengeAsync(fresh, default));
            await Reject(() => engine.ReadLoginRecoveryAsync(fresh, default));
            commands = new TailscaleLoginChecks.Commands(frame, url) { CaptureTimedOut = true };
            login = new(commands.Run, () => { }); engine = Engine();
            var failed = fresh with { OperationId = Guid.NewGuid(), ExpectedRevision = (await login.ReadAsync(default)).Revision };
            var unknown = await engine.BeginLoginAsync(failed, true, default);
            Check(unknown.State == TailscaleLoginState.Unknown && unknown.ChallengeFingerprint is null && commands.UpCalls == 1);
            Check(await engine.BeginLoginAsync(failed, true, default) == unknown && commands.UpCalls == 1);
            await CheckBlocked(true);
            var recovery = await engine.ReadLoginRecoveryAsync(failed, default);
            await engine.CloseLoginAsync(failed, recovery.Revision, true, default);
            Check(await engine.ReadLoginReceiptAsync(failed, default) == unknown);
            using(var caller = new CancellationTokenSource())
            {
                commands = new TailscaleLoginChecks.Commands(frame, url) { CancelAfterUp = caller };
                login = new(commands.Run, () => { }); engine = Engine();
                var disconnected = fresh with { OperationId = Guid.NewGuid(), ExpectedRevision = (await login.ReadAsync(default)).Revision };
                var issued = await engine.BeginLoginAsync(disconnected, true, caller.Token);
                Check(caller.IsCancellationRequested && issued.State == TailscaleLoginState.Issued && commands.UpCalls == 1);
                Check(await Engine().ReadLoginReceiptAsync(disconnected, default) == issued);
                var preview = await engine.ReadLoginRecoveryAsync(disconnected, default);
                await engine.CloseLoginAsync(disconnected, preview.Revision, true, default);
                await Reject(() => engine.ReadLoginChallengeAsync(disconnected, default));
                await CheckBlocked(false);
            }
            var damaged = fresh with { OperationId = Guid.NewGuid() };
            File.WriteAllText(PathFor(damaged.OperationId, "login-intent"), JsonSerializer.Serialize(damaged).Replace("\"OperationId\":", "\"OperationId\":\"" + Guid.NewGuid() + "\",\"OperationId\":"));
            try { await engine.ReadLoginReceiptAsync(damaged, default); throw new Exception("Duplicate intent identity accepted"); }
            catch (IOException ex) { Check(ex.Message == "tailscale.duplicate_preference"); }
            Console.WriteLine($"{count} Tailscale durable login intent/receipt checks passed.");
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
        internal bool Quiescent = true;
        public Task<bool> IsQuiescentAsync(CancellationToken ct) => Task.FromResult(Quiescent);
        public Task<TailscaleControlSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(new TailscaleControlSnapshot(new('A', 64), false, true, new(true, false), true));
        public Task<bool> ApplyAsync(TailscalePrepareRequest request, CancellationToken ct) => throw new Exception("Login ledger must not call control mutation");
    }
}
