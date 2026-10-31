using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Startup;

internal static class ModulePrivilegeChecks
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "module-privileges-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            var identity = new ModulePrivilegeIdentity("fixture", "1.0.0", "publisher", new string('a', 64), new string('b', 64),
                "revision-1", [new("service-restart", "fixture.service")], ["fixture.service"]);
            var trusted = true;
            ModulePrivilegeIdentity Resolve(string module) => trusted && module == identity.ModuleId
                ? identity : throw new ModulePrivilegeException("module.privilege_denied", 403);
            var store = new ModulePrivilegePlanStore(root, Resolve, clock);
            ModulePrivilegeRequest Request(string key, string target = "fixture.service", string kind = "service-restart") =>
                new(kind, target, "Restart the fixture service", key);
            async Task Reject(Func<Task> action, string code, string message)
            {
                try { await action(); }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_" + code)
                { check(true, message); return; }
                throw new Exception("Expected module privilege rejection: " + message);
            }
            async Task<ModulePrivilegePlan> Prepare(string key)
            {
                var prepared = await store.PrepareAsync("fixture", "session-1", Request(key));
                return await store.ReadForUserAsync(prepared.PlanId, "session-1");
            }
            static ModulePrivilegeApproval Approval(ModulePrivilegePlan plan) => new(plan.PlanHash, plan.ConfirmationToken);
            var executions = 0;
            Task<bool> Execute(Guid id, string kind, string target)
            {
                check(id != Guid.Empty && kind == "service-restart" && target == "fixture.service", "Broker executes the exact frozen typed action");
                executions++;
                return Task.FromResult(true);
            }
            var plan = await Prepare("first");
            check(plan.State == "AwaitingConfirmation" && plan.ConfirmationToken.Length == 64 && executions == 0,
                "Broker prepares without executing and issues a Core-only confirmation token");
            var same = await store.PrepareAsync("fixture", "session-1", Request("first"));
            check(same.PlanId == plan.PlanId, "Broker idempotency returns the original plan");
            await Reject(() => store.PrepareAsync("fixture", "session-1", Request("first") with { Reason = "changed" }),
                "idempotency_conflict", "Broker rejects idempotency key reuse with changed content");
            await Reject(() => store.PrepareAsync("fixture", "session-1", Request("undeclared", "other.service")),
                "not_declared", "Broker rejects undeclared targets");
            await Reject(() => store.PrepareAsync("fixture", "session-1", Request("shell", kind: "shell")),
                "unsupported", "Broker rejects arbitrary command kinds");
            await Reject(() => store.ReadForUserAsync(plan.PlanId, "other-session"), "denied", "Broker binds plans to the initiating session");
            await Reject(() => store.GetForModuleAsync(plan.PlanId, "other-module"), "denied", "Broker prevents cross-module result reads");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan) with { PlanHash = new string('0', 64) },
                () => Task.FromResult(true), Execute), "approval_invalid", "Broker rejects a changed plan hash");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan) with { ConfirmationToken = new string('0', 64) },
                () => Task.FromResult(true), Execute), "approval_invalid", "Broker rejects a forged confirmation token");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), () => Task.FromResult(false), Execute),
                "admin_required", "Broker rejects failed administrator verification without execution");
            var success = await store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), () => Task.FromResult(true), Execute);
            check(success.State == "Succeeded" && executions == 1, "Broker executes once after explicit confirmation and verification");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), () => Task.FromResult(true), Execute),
                "approval_invalid", "Broker rejects approval replay");
            var expired = await Prepare("expired");
            clock.Current = clock.Current.AddMinutes(6);
            await Reject(() => store.ApproveAsync(expired.PlanId, "session-1", Approval(expired), () => Task.FromResult(true), Execute),
                "plan_expired", "Broker rejects expired plans");
            var drifted = await Prepare("drifted");
            identity = identity with { Version = "2.0.0", ArtifactSha256 = new string('c', 64) };
            await Reject(() => store.ApproveAsync(drifted.PlanId, "session-1", Approval(drifted), () => Task.FromResult(true), Execute),
                "plan_changed", "Broker invalidates old plans after an update or policy change");
            var revoked = await Prepare("revoked");
            trusted = false;
            await Reject(() => store.ApproveAsync(revoked.PlanId, "session-1", Approval(revoked), () => Task.FromResult(true), Execute),
                "denied", "Broker rechecks revoked trust immediately before approval");
            trusted = true;
            var cancelled = await Prepare("cancelled");
            check((await store.CancelAsync(cancelled.PlanId, "session-1")).State == "Cancelled", "Broker persists user refusal");
            await Reject(() => store.ApproveAsync(cancelled.PlanId, "session-1", Approval(cancelled), () => Task.FromResult(true), Execute),
                "approval_invalid", "Broker cannot approve a refused plan");
            var concurrent = await Prepare("concurrent");
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try { return (await store.ApproveAsync(concurrent.PlanId, "session-1", Approval(concurrent), () => Task.FromResult(true), Execute)).State; }
                catch (ModulePrivilegeException error) { return error.Code; }
            }));
            check(results.Count(s => s == "Succeeded") == 1 && executions == 2, "Broker serializes concurrent approvals and executes only once");
            var interrupted = await Prepare("interrupted");
            var unknown = await store.ApproveAsync(interrupted.PlanId, "session-1", Approval(interrupted), () => Task.FromResult(true),
                (_, _, _) => throw new IOException("lost Helper response"));
            check(unknown.State == "RecoveryRequired", "Broker treats a lost Helper response as unknown, without replay");
            var reopened = new ModulePrivilegePlanStore(root, Resolve, clock);
            check((await reopened.GetForModuleAsync(plan.PlanId, "fixture")).State == "Succeeded"
                && (await reopened.GetForModuleAsync(interrupted.PlanId, "fixture")).State == "RecoveryRequired",
                "Broker retains original success and recovery facts across restart");
            check(executions == 2, "Broker queries and restart never replay privileged actions");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
