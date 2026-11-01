using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Startup;
using System.Text.Json.Nodes;

internal static class ModulePrivilegeChecks
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await ModulePrivilegeConsentChecks.RunAsync(check);
        var root = Path.Combine(Path.GetTempPath(), "module-privileges-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            var identity = new ModulePrivilegeIdentity("fixture", "1.0.0", "publisher", new string('a', 64), new string('b', 64),
                "revision-1", [new("service-restart", "fixture.service")], ["fixture.service"]);
            var trusted = true;
            static Task<ModulePrivilegeAdministrator?> Verify() => Task.FromResult<ModulePrivilegeAdministrator?>(new("linux:1000", "linux-pam-administrator"));
            ModulePrivilegeIdentity Resolve(string module) => trusted && module == identity.ModuleId
                ? identity : throw new ModulePrivilegeException("module.privilege_denied", 403);
            var store = new ModulePrivilegePlanStore(root, Resolve, clock);
            Task<ModulePrivilegePrepared> PrepareRequest(ModulePrivilegeRequest request) => store.PrepareAsync("fixture", "session-1", request, "test-user");
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
                var prepared = await PrepareRequest(Request(key));
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
            var same = await PrepareRequest(Request("first"));
            check(same.PlanId == plan.PlanId, "Broker idempotency returns the original plan");
            await Reject(() => PrepareRequest(Request("first") with { Reason = "changed" }),
                "idempotency_conflict", "Broker rejects idempotency key reuse with changed content");
            await Reject(() => PrepareRequest(Request("undeclared", "other.service")),
                "not_declared", "Broker rejects undeclared targets");
            await Reject(() => PrepareRequest(Request("shell", kind: "shell")),
                "unsupported", "Broker rejects arbitrary command kinds");
            await Reject(() => store.ReadForUserAsync(plan.PlanId, "other-session"), "denied", "Broker binds plans to the initiating session");
            await Reject(() => store.GetForModuleAsync(plan.PlanId, "other-module"), "denied", "Broker prevents cross-module result reads");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan) with { PlanHash = new string('0', 64) },
                Verify, Execute), "approval_invalid", "Broker rejects a changed plan hash");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan) with { ConfirmationToken = new string('0', 64) },
                Verify, Execute), "approval_invalid", "Broker rejects a forged confirmation token");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), () => Task.FromResult<ModulePrivilegeAdministrator?>(null), Execute),
                "admin_required", "Broker rejects failed administrator verification without execution");
            var success = await store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), Verify, Execute);
            check(success.State == "Succeeded" && executions == 1, "Broker executes once after explicit confirmation and verification");
            await Reject(() => store.ApproveAsync(plan.PlanId, "session-1", Approval(plan), Verify, Execute),
                "approval_invalid", "Broker rejects approval replay");
            var expired = await Prepare("expired");
            clock.Current = clock.Current.AddMinutes(6);
            await Reject(() => store.ApproveAsync(expired.PlanId, "session-1", Approval(expired), Verify, Execute),
                "plan_expired", "Broker rejects expired plans");
            var drifted = await Prepare("drifted");
            identity = identity with { Version = "2.0.0", ArtifactSha256 = new string('c', 64) };
            await Reject(() => store.ApproveAsync(drifted.PlanId, "session-1", Approval(drifted), Verify, Execute),
                "plan_changed", "Broker invalidates old plans after an update or policy change");
            var revoked = await Prepare("revoked");
            trusted = false;
            await Reject(() => store.ApproveAsync(revoked.PlanId, "session-1", Approval(revoked), Verify, Execute),
                "denied", "Broker rechecks revoked trust immediately before approval");
            trusted = true;
            var cancelled = await Prepare("cancelled");
            check((await store.CancelAsync(cancelled.PlanId, "session-1")).State == "Cancelled", "Broker persists user refusal");
            await Reject(() => store.ApproveAsync(cancelled.PlanId, "session-1", Approval(cancelled), Verify, Execute),
                "approval_invalid", "Broker cannot approve a refused plan");
            var concurrent = await Prepare("concurrent");
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try { return (await store.ApproveAsync(concurrent.PlanId, "session-1", Approval(concurrent), Verify, Execute)).State; }
                catch (ModulePrivilegeException error) { return error.Code; }
            }));
            check(results.Count(s => s == "Succeeded") == 1 && executions == 2, "Broker serializes concurrent approvals and executes only once");
            var interrupted = await Prepare("interrupted");
            var unknown = await store.ApproveAsync(interrupted.PlanId, "session-1", Approval(interrupted), Verify,
                (_, _, _) => throw new IOException("lost Helper response"));
            check(unknown.State == "RecoveryRequired", "Broker treats a lost Helper response as unknown, without replay");
            var reopened = new ModulePrivilegePlanStore(root, Resolve, clock);
            check((await reopened.GetForModuleAsync(plan.PlanId, "fixture")).State == "Succeeded"
                && (await reopened.GetForModuleAsync(interrupted.PlanId, "fixture")).State == "RecoveryRequired",
                "Broker retains original success and recovery facts across restart");
            var originalUnknown = await reopened.PrepareAsync("fixture", "session-1", Request("interrupted"), "test-user");
            check(originalUnknown.PlanId == interrupted.PlanId && originalUnknown.State == "RecoveryRequired",
                "Broker idempotent prepare retains an unknown result instead of creating a retry");
            var recoveryView = await reopened.ReadForUserAsync(interrupted.PlanId, "session-1");
            check(recoveryView.ConfirmationToken == "", "Broker does not issue confirmation tokens for unknown operations");
            await Reject(() => reopened.ApproveAsync(interrupted.PlanId, "session-1", Approval(interrupted),
                Verify, Execute), "approval_invalid", "Broker rejects replay after a lost Helper response and restart");
            var crash = await Prepare("crash-before-response");
            await store.ApproveAsync(crash.PlanId, "session-1", Approval(crash), Verify, async (_, _, _) =>
            {
                // Reopen while the durable record is Executing, before any completion response exists.
                var recovering = new ModulePrivilegePlanStore(root, Resolve, clock);
                check((await recovering.GetForModuleAsync(crash.PlanId, "fixture")).State == "RecoveryRequired",
                    "Broker recovers a durable Executing record as unknown before receiving any Helper result");
                await Reject(() => recovering.ApproveAsync(crash.PlanId, "session-1", Approval(crash),
                    Verify, Execute), "approval_invalid", "Broker recovery never reuses an in-flight confirmation");
                throw new IOException("simulated process interruption");
            });
            check(executions == 2, "Broker queries and restart never replay privileged actions");
            var persisted = Path.Combine(root, "data", "module-management", "privilege-plans.json");
            var original = File.ReadAllText(persisted);
            var records = JsonNode.Parse(original)!["plans"]!.AsArray();
            var approvedAudit = records.Single(p => p!["id"]!.GetValue<Guid>() == plan.PlanId)!["audit"]!;
            check(approvedAudit["initiatingUser"]!.GetValue<string>() == "test-user"
                && approvedAudit["approvingUser"]!.GetValue<string>() == "test-user"
                && approvedAudit["administrator"]!["identity"]!.GetValue<string>() == "linux:1000"
                && approvedAudit["administrator"]!["authenticationMethod"]!.GetValue<string>() == "linux-pam-administrator"
                && approvedAudit["confirmedAt"] is not null && approvedAudit["attemptCompletedAt"] is not null,
                "Broker durably records initiating and approving users, actual administrator identity and attempt timestamps");
            var unknownAudit = records.Single(p => p!["id"]!.GetValue<Guid>() == interrupted.PlanId)!["audit"]!;
            check(unknownAudit["administrator"]!["identity"]!.GetValue<string>() == "linux:1000"
                && unknownAudit["confirmedAt"] is not null && unknownAudit["attemptCompletedAt"] is not null,
                "Broker retains the verified administrator and attempt evidence when the Helper response is unknown");
            void RejectCorruption(Action<JsonObject> mutate, string message)
            {
                var document = JsonNode.Parse(original)!.AsObject();
                mutate(document);
                File.WriteAllText(persisted, document.ToJsonString());
                try
                {
                    _ = new ModulePrivilegePlanStore(root, Resolve, clock);
                    throw new Exception("Expected persisted plan corruption rejection");
                }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_recovery_required") { check(true, message); }
                finally { File.WriteAllText(persisted, original); }
            }
            RejectCorruption(d => d["plans"]![0]!["request"]!["target"] = "different.service",
                "Broker rejects a persisted request changed without its frozen hash");
            RejectCorruption(d => d["plans"]![0]!["identity"]!["version"] = "9.0.0",
                "Broker rejects a persisted module identity changed without its frozen hash");
            RejectCorruption(d => d["plans"]![0]!["hash"] = new string('0', 64),
                "Broker rechecks the composite plan hash on recovery");
            RejectCorruption(d => d["plans"]![0]!["state"] = "UnknownState",
                "Broker rejects an unrecognized persisted lifecycle state");
            RejectCorruption(d => d["plans"]![0]!["tokenHash"] = new string('a', 64),
                "Broker rejects a persisted confirmation token on a completed operation");
            RejectCorruption(d => d["plans"] = null, "Broker rejects a null persisted plan collection");
            RejectCorruption(d => d["plans"]![0]!["audit"]!["initiatingUser"] = "changed-user",
                "Broker binds the initiating audit user into the frozen plan hash");
            RejectCorruption(d => d["plans"]![0]!["audit"]!["approvingUser"] = "different-user",
                "Broker rejects a persisted approval attributed to another user");
            RejectCorruption(d => d["plans"]![0]!["audit"]!["administrator"] = null,
                "Broker rejects a completed action missing its verified administrator evidence");
            RejectCorruption(d => d["schemaVersion"] = 2, "Broker rejects the previous audit-incomplete internal store format");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
