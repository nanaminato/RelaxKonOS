using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Protocol.Observability;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class TailscaleHelperControlChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Helper control assertion " + count); count++; }
        const string stopped = """{"Version":"1.test","BackendState":"Stopped","Self":{"ID":"node"},"Peer":{"secret":"not used"}}""";
        const string prefs = """{"accept-dns":true,"accept-routes":false,"hostname":"host"}""";
        var snapshot = TailscaleNativeControl.Parse(stopped, prefs, stopped);
        Check(!snapshot.Connected && snapshot.Authenticated && snapshot.Configuration == new TailscaleConfiguration(true, false));
        Check(snapshot.Preferences == new TailscalePreferences(true, false, false, null, false, false, false, false));
        Check(snapshot.Platform is "windows" or "linux");
        const string fullPrefs = """{"accept-dns":true,"accept-routes":false,"shields-up":true,"exit-node":"100.64.0.9","exit-node-allow-lan-access":true,"auto-update":true,"advertise-exit-node":true,"unattended":false}""";
        var full = TailscaleNativeControl.Parse(stopped, fullPrefs, stopped);
        Check(full.Preferences == new TailscalePreferences(true, false, true, "100.64.0.9", true, false, true, true));
        Check(snapshot.Revision == TailscaleNativeControl.Parse(stopped, """{"hostname":"host","accept-routes":false,"accept-dns":true}""", stopped).Revision);
        Check(snapshot.Revision != TailscaleNativeControl.Parse(stopped, prefs.Replace("host", "newhost"), stopped).Revision);
        Check(snapshot.Revision == TailscaleNativeControl.Parse(stopped.Replace("not used", "changed"), prefs, stopped).Revision);
        foreach (var invalid in new[] { prefs.Replace("true", "\"true\""), "[]", "{", """{"accept-dns":true,"accept-dns":false,"accept-routes":false}""",
            """{"accept-dns":true,"accept-routes":false,"exit-node":"not-an-ip"}""", """{"accept-dns":true,"accept-routes":false,"exit-node":5}""" })
        {
            try { TailscaleNativeControl.Parse(stopped, invalid, stopped); throw new Exception("Malformed preferences accepted"); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { Check(true); }
        }
        try { TailscaleNativeControl.Parse(stopped, prefs, stopped.Replace("Stopped", "Running")); throw new Exception("Changed identity accepted"); }
        catch (IOException) { Check(true); }
        var loggedOutSnapshot = TailscaleNativeControl.Parse(stopped.Replace("Stopped", "NeedsLogin"), prefs, stopped.Replace("Stopped", "NeedsLogin"));
        Check(!loggedOutSnapshot.Authenticated && !loggedOutSnapshot.CanControl && loggedOutSnapshot.Preferences is not null);
        try { TailscaleNativeControl.Parse(stopped.Replace("Stopped", "NoState"), prefs, stopped.Replace("Stopped", "NoState")); throw new Exception("Unknown backend state accepted"); }
        catch (IOException) { Check(true); }
        var target = new TailscalePrepareRequest(Guid.NewGuid(), snapshot.Revision, TailscaleAction.Connect);
        Check(TailscaleNativeControl.Arguments(target).SequenceEqual(new[] { "up" }));
        Check(TailscaleNativeControl.Arguments(target with { Action = TailscaleAction.Disconnect }).SequenceEqual(new[] { "down" }));
        Check(TailscaleNativeControl.Arguments(target with { Action = TailscaleAction.Logout }).SequenceEqual(new[] { "logout" }));
        Check(TailscaleNativeControl.Arguments(target with { Action = TailscaleAction.Configure, Configuration = new(false, true) })
            .SequenceEqual(new[] { "set", "--accept-dns=false", "--accept-routes=true" }));
        var preferences = new TailscalePreferences(true, false, true, "100.64.0.9", true, OperatingSystem.IsWindows(), true, true);
        var preferenceRequest = target with { Action = TailscaleAction.ConfigurePreferences, Preferences = preferences };
        var expectedArguments = new List<string>
        {
            "set", "--accept-dns=true", "--accept-routes=false", "--shields-up=true", "--exit-node=100.64.0.9",
            "--exit-node-allow-lan-access=true", "--auto-update=true", "--advertise-exit-node=true",
        };
        if (OperatingSystem.IsWindows()) expectedArguments.Add("--unattended=true");
        Check(TailscaleNativeControl.Arguments(preferenceRequest).SequenceEqual(expectedArguments));
        Check(TailscaleNativeControl.Arguments(preferenceRequest with { Preferences = preferences with { ExitNode = "auto:any" } })
            .Contains("--exit-node=auto:any"));
        // A preference plan commits only when the re-read snapshot reports the requested values.
        var preferenceFolder = Directory.CreateTempSubdirectory("relaxkonos-tailscale-pref-");
        try
        {
            var preferenceNative = new Native { Snapshot = full };
            var preferenceEngine = new TailscaleControlOperations(preferenceFolder.FullName, _ => { }, preferenceNative);
            var id = Guid.NewGuid();
            var plan = new TailscalePrepareRequest(id, full.Revision, TailscaleAction.ConfigurePreferences, Preferences: preferences);
            var envelope = new PrivilegedOperationRequest(PrivilegedOperationKind.TailscaleControlExecute, OperationId: id,
                Correlation: CorrelationContext.Create(id, "privileged.operation")) { TailscaleChange = plan };
            var outcome = await preferenceEngine.HandleAsync(envelope);
            Check(outcome.Success && outcome.TailscaleResult?.Completed == true && preferenceNative.Executions == 1);
            Check(preferenceNative.Snapshot.Preferences == preferences);
            var invalid = plan with { OperationId = Guid.NewGuid(), Preferences = new(true, false, false, "not-an-ip", false, false, false, false) };
            var invalidEnvelope = envelope with { OperationId = invalid.OperationId,
                Correlation = CorrelationContext.Create(invalid.OperationId, "privileged.operation"), TailscaleChange = invalid };
            Check(!(await preferenceEngine.HandleAsync(invalidEnvelope)).Success && preferenceNative.Executions == 1);
            // Logout publishes a completed receipt only after the daemon reports a signed-out node.
            var logoutId = Guid.NewGuid();
            var logoutNative = new Native { Snapshot = full with { Connected = true, Authenticated = true, CanControl = true } };
            var logoutEngine = new TailscaleControlOperations(preferenceFolder.FullName, _ => { }, logoutNative);
            var logoutPlan = new TailscalePrepareRequest(logoutId, logoutNative.Snapshot.Revision, TailscaleAction.Logout);
            var logoutEnvelope = new PrivilegedOperationRequest(PrivilegedOperationKind.TailscaleControlExecute, OperationId: logoutId,
                Correlation: CorrelationContext.Create(logoutId, "privileged.operation")) { TailscaleChange = logoutPlan };
            var loggedOut = await logoutEngine.HandleAsync(logoutEnvelope);
            Check(loggedOut.Success && loggedOut.TailscaleResult?.Completed == true && !logoutNative.Snapshot.Authenticated);
            // A logout whose result was lost can be closed from the logged-out observation.
            var recoveryId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(preferenceFolder.FullName, recoveryId.ToString("N") + ".intent.json"),
                JsonSerializer.Serialize(new TailscalePrepareRequest(recoveryId, full.Revision, TailscaleAction.Logout)));
            var recoveryRead = await logoutEngine.HandleAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.TailscaleRecoveryRead,
                OperationId: recoveryId, Correlation: CorrelationContext.Create(recoveryId, "privileged.operation")));
            Check(recoveryRead.Success && recoveryRead.TailscaleRecovery?.CanClose == true && recoveryRead.TailscaleRecovery.Observation?.CanControl == false);
            var actor = Guid.NewGuid().ToString("D");
            var recoveryClose = await logoutEngine.HandleAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.TailscaleRecoveryClose,
                OperationId: recoveryId, Correlation: CorrelationContext.Create(recoveryId, "privileged.operation"))
                { TailscaleRecovery = new(new(recoveryId, recoveryRead.TailscaleRecovery!.Observation!.Revision, true), actor) });
            Check(recoveryClose.Success && recoveryClose.TailscaleClosure?.Observation.CanControl == false);
        }
        finally { preferenceFolder.Delete(true); }
        var folder = Directory.CreateTempSubdirectory("relaxkonos-tailscale-helper-");
        try
        {
            var native = new Native { Snapshot = snapshot };
            var engine = new TailscaleControlOperations(folder.FullName, _ => { }, native);
            PrivilegedOperationRequest Envelope(PrivilegedOperationKind kind, Guid id) => new(kind, OperationId: id,
                Correlation: CorrelationContext.Create(id, "privileged.operation"));
            var execute = Envelope(PrivilegedOperationKind.TailscaleControlExecute, target.OperationId) with { TailscaleChange = target };
            foreach (var bad in new[] { execute with { Path = "arbitrary" }, execute with { ExpectedRevision = snapshot.Revision },
                execute with { TailscaleChange = target with { OperationId = Guid.NewGuid() } }, execute with { OperationId = Guid.Empty },
                execute with { Correlation = null }, execute with { Correlation = CorrelationContext.Create(Guid.NewGuid(), "privileged.operation") }, execute with { TailscaleChange = target with { Action = (TailscaleAction)55 } },
                execute with { TailscaleChange = target with { Configuration = new(true, false) } } })
                Check(!(await engine.HandleAsync(bad)).Success && native.Executions == 0);
            Check((await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlReceipt, target.OperationId))).TailscaleResult is null);
            Check((await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlRead, Guid.NewGuid()))).TailscaleControl == snapshot);
            Check(!(await engine.HandleAsync(execute with { TailscaleChange = target with { ExpectedRevision = new('C', 64) } })).Success && native.Executions == 0);
            native.OnExecute = () => Check(File.Exists(Path.Combine(folder.FullName, target.OperationId.ToString("N") + ".intent.json")));
            var result = await engine.HandleAsync(execute);
            Check(result.Success && result.TailscaleResult?.Completed == true && native.Executions == 1);
            Check((await engine.HandleAsync(execute)).TailscaleResult == result.TailscaleResult && native.Executions == 1);
            var reopened = new TailscaleControlOperations(folder.FullName, _ => { }, native);
            Check((await reopened.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlReceipt, target.OperationId))).TailscaleResult == result.TailscaleResult);
            var resultPath = Path.Combine(folder.FullName,target.OperationId.ToString("N")+".result.json");
            var originalResult = File.ReadAllText(resultPath);
            string ChangedResult(Action<JsonObject> change)
            {
                var node = JsonNode.Parse(originalResult)!.AsObject(); change(node); return node.ToJsonString();
            }
            foreach (var corrupted in new[] {
                ChangedResult(node=>node["Snapshot"]=null),
                ChangedResult(node=>node["Snapshot"]!["Configuration"]=null),
                ChangedResult(node=>node["Snapshot"]!["Revision"]="bad"),
                ChangedResult(node=>node["unexpected"]=true),
                ChangedResult(node=>node.Remove("Completed")),
                originalResult.Replace("\"Completed\":true","\"Completed\":true,\"Completed\":false",StringComparison.Ordinal) })
            {
                File.WriteAllText(resultPath,corrupted);
                Check(!(await reopened.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlReceipt,target.OperationId))).Success);
                Check(!(await reopened.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlRead,Guid.NewGuid()))).Success && native.Executions == 1);
            }
            File.WriteAllText(resultPath,originalResult);
            Check(!(await reopened.HandleAsync(execute with { TailscaleChange = target with { Action = TailscaleAction.Disconnect } })).Success && native.Executions == 1);
            native.OnExecute = null;
            // A native failure never publishes completion and does not re-run when the same ID is retried.
            var failure = new TailscalePrepareRequest(Guid.NewGuid(), native.Snapshot.Revision, TailscaleAction.Disconnect);
            native.Throw = true;
            var failedEnvelope = Envelope(PrivilegedOperationKind.TailscaleControlExecute, failure.OperationId) with { TailscaleChange = failure };
            Check((await engine.HandleAsync(failedEnvelope)).TailscaleResult?.Completed == false);
            Check((await engine.HandleAsync(failedEnvelope)).TailscaleResult?.Completed == false && native.Executions == 2);
            var blocked = (await engine.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlRead, Guid.NewGuid()))).TailscaleControl;
            Check(blocked?.CanControl == false && blocked.ProblemCode == "tailscale.recovery_required");
            // Simulate process death after publishing an intent but before any result evidence.
            var interrupted = new TailscalePrepareRequest(Guid.NewGuid(), native.Snapshot.Revision, TailscaleAction.Disconnect);
            File.WriteAllText(Path.Combine(folder.FullName, interrupted.OperationId.ToString("N") + ".intent.json"), JsonSerializer.Serialize(interrupted));
            Check((await reopened.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlReceipt, interrupted.OperationId))).TailscaleResult?.ProblemCode == "tailscale.operation.interrupted");
            var interruptedEnvelope = Envelope(PrivilegedOperationKind.TailscaleControlExecute, interrupted.OperationId) with { TailscaleChange = interrupted };
            Check((await reopened.HandleAsync(interruptedEnvelope)).TailscaleResult?.Completed == false && native.Executions == 2);
            var next = interrupted with { OperationId = Guid.NewGuid() };
            Check(!(await reopened.HandleAsync(Envelope(PrivilegedOperationKind.TailscaleControlExecute, next.OperationId) with { TailscaleChange = next })).Success && native.Executions == 2);
            Console.WriteLine($"{count} Tailscale native revision/Helper receipt checks passed.");
        }
        finally { folder.Delete(true); }
    }
    private sealed class Native : ITailscaleNativeControl
    {
        internal required TailscaleControlSnapshot Snapshot;
        internal int Executions;
        internal bool Throw;
        internal Action? OnExecute;
        public Task<bool> IsQuiescentAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<TailscaleControlSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(Snapshot);
        public Task<bool> ApplyAsync(TailscalePrepareRequest request, CancellationToken ct)
        {
            Executions++; OnExecute?.Invoke();
            if (Throw) throw new IOException("private native diagnostic");
            Snapshot = Snapshot with { Connected = request.Action == TailscaleAction.Connect,
                Authenticated = request.Action == TailscaleAction.Logout ? false : Snapshot.Authenticated,
                CanControl = request.Action == TailscaleAction.Logout ? false : Snapshot.CanControl,
                Revision = new('B', 64), Configuration = request.Configuration ?? Snapshot.Configuration,
                Preferences = request.Preferences ?? Snapshot.Preferences };
            return Task.FromResult(true);
        }
    }
}
